// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>Samples interface counters from <c>/proc/net/dev</c> plus <c>/sys/class/net</c> (V1.3-D, ADR-0035).</summary>
internal static class LinuxInterfaceStatsCollector
{
    internal static async Task<IReadOnlyList<InterfaceCounterSample>> SampleAsync(string? interfaceName, CancellationToken ct)
    {
        const string devPath = "/proc/net/dev";
        if (!File.Exists(devPath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(devPath, ct);
        var results = new List<InterfaceCounterSample>();

        foreach (var line in lines.Skip(2))
        {
            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            if (interfaceName is not null && !string.Equals(name, interfaceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 16)
            {
                continue;
            }

            var bytesReceived = ParseLong(fields[0]);
            var packetsReceived = ParseLong(fields[1]);
            var errorsReceived = ParseLong(fields[2]);
            var dropsReceived = ParseLong(fields[3]);
            var bytesSent = ParseLong(fields[8]);
            var packetsSent = ParseLong(fields[9]);
            var errorsSent = ParseLong(fields[10]);
            var dropsSent = ParseLong(fields[11]);

            results.Add(new InterfaceCounterSample(
                name, bytesReceived, bytesSent, packetsReceived, packetsSent, errorsReceived, errorsSent, dropsReceived, dropsSent,
                ReadSpeedMbps(name), ReadOperationalStatus(name)));
        }

        return results;
    }

    private static long? ParseLong(string field) =>
        long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? ReadSpeedMbps(string interfaceName)
    {
        try
        {
            var path = $"/sys/class/net/{interfaceName}/speed";
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var speed) && speed > 0 ? speed : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadOperationalStatus(string interfaceName)
    {
        try
        {
            var path = $"/sys/class/net/{interfaceName}/operstate";
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "unknown";
        }
        catch (IOException)
        {
            return "unknown";
        }
        catch (UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}
