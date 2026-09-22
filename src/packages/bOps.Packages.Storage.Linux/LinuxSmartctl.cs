// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Linux;

internal static class LinuxSmartctl
{
    private const int MaximumJsonCharacters = 1_048_576;

    public static string? FindExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "smartctl");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static async Task<StorageHealth?> ReadAsync(string executable, string device, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-j");
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(device);

        using var process = Process.Start(startInfo);
        if (process is null) return null;
        var outputTask = ReadBoundedAsync(process.StandardOutput, MaximumJsonCharacters, ct);
        var errorTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        await errorTask;
        return output is null ? null : Parse(device, output);
    }

    internal static StorageHealth? Parse(string device, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var passed = Boolean(root, "smart_status", "passed");
            var temperature = Number(root, "temperature", "current") ?? Number(root, "nvme_smart_health_information_log", "temperature");
            var hours = Integer(root, "power_on_time", "hours") ?? Integer(root, "power_on_hours");
            var mediaErrors = Integer(root, "nvme_smart_health_information_log", "media_errors");
            var wear = Number(root, "nvme_smart_health_information_log", "percentage_used");
            long? reallocated = null;
            if (root.TryGetProperty("ata_smart_attributes", out var attributes)
                && attributes.TryGetProperty("table", out var table) && table.ValueKind == JsonValueKind.Array)
            {
                foreach (var attribute in table.EnumerateArray())
                {
                    var name = attribute.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                    if (name?.Contains("Reallocated", StringComparison.OrdinalIgnoreCase) != true) continue;
                    if (attribute.TryGetProperty("raw", out var raw) && raw.TryGetProperty("value", out var value) && value.TryGetInt64(out var count)) reallocated = count;
                }
            }

            var smartAvailable = passed is not null || temperature is not null || hours is not null || mediaErrors is not null || reallocated is not null || wear is not null;
            return new StorageHealth(
                device, passed switch { true => "healthy", false => "critical", null => "unknown" },
                passed switch { true => "passed", false => "failed", null => null }, temperature, hours,
                mediaErrors, reallocated, wear, smartAvailable, "smartctl", passed is null ? "SMART status was not reported." : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken ct)
    {
        var buffer = new char[8_192];
        var output = new StringBuilder(Math.Min(maximumCharacters, 65_536));
        var exceeded = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return exceeded ? null : output.ToString();
            if (exceeded || output.Length + count > maximumCharacters)
            {
                exceeded = true;
                continue;
            }

            output.Append(buffer, 0, count);
        }
    }

    private static JsonElement? PropertyPath(JsonElement root, string first, string second)
    {
        if (!root.TryGetProperty(first, out var one) || !one.TryGetProperty(second, out var two)) return null;
        return two;
    }

    private static bool? Boolean(JsonElement root, string first, string second) => PropertyPath(root, first, second) is { } value && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static double? Number(JsonElement root, string first, string second) => PropertyPath(root, first, second) is { } value && value.TryGetDouble(out var number) ? number : null;
    private static long? Integer(JsonElement root, string first, string second) => PropertyPath(root, first, second) is { } value && value.TryGetInt64(out var number) ? number : null;
    private static long? Integer(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
}
