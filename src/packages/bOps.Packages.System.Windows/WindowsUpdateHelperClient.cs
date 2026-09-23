// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Runs the fixed WUA read in a child process because WUA search has no reliable cancellation boundary.</summary>
internal sealed class WindowsUpdateHelperClient : IWindowsUpdateCollector
{
    internal const string HelperFileName = "bOps.Packages.System.Windows.Updates.Helper.exe";
    internal const int TimeoutSeconds = 20;
    private const int MaximumOutputBytes = 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string HelperPath => Path.Combine(AppContext.BaseDirectory, HelperFileName);

    public async Task<MaintenanceSnapshot<UpdateRecord>> CollectAsync(string kind, int limit, CancellationToken ct)
    {
        if (!File.Exists(HelperPath))
        {
            return Failure("windows-update-agent.helper-unavailable");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(HelperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("pending-updates");
        process.StartInfo.ArgumentList.Add(kind);
        process.StartInfo.ArgumentList.Add(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            if (!process.Start()) return Failure("windows-update-agent.helper-start-failed");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            var output = ReadBoundedAsync(process.StandardOutput, MaximumOutputBytes, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, MaximumErrorBytes, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillAndWait(process);
                return Failure("windows-update-agent.timeout");
            }

            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            if (stdout is null || stderr is null) return Failure("windows-update-agent.helper-response-too-large");
            if (process.ExitCode != 0) return Failure("windows-update-agent.helper-failed");
            try
            {
                var snapshot = JsonSerializer.Deserialize<MaintenanceSnapshot<UpdateRecord>>(stdout, JsonOptions);
                return snapshot is null ? Failure("windows-update-agent.helper-invalid-response") : snapshot;
            }
            catch (JsonException)
            {
                return Failure("windows-update-agent.helper-invalid-response");
            }
        }
        catch (Win32Exception)
        {
            return Failure("windows-update-agent.helper-start-failed");
        }
    }

    internal static async Task<(int ProcessId, bool Exited, bool TimedOut)> RunTestChildAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var processId = process.Id;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return (processId, process.HasExited, false);
        }
        catch (OperationCanceledException)
        {
            KillAndWait(process);
            return (processId, process.HasExited, true);
        }
    }

    private static MaintenanceSnapshot<UpdateRecord> Failure(string warning) =>
        new([], [new("windows-update-agent", InventorySourceStatus.Unavailable, warning)], [warning]);

    private static void KillAndWait(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        process.WaitForExit();
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, int maximumBytes, CancellationToken ct)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) return output.ToString();
            output.Append(buffer, 0, count);
            if (Encoding.UTF8.GetByteCount(output.ToString()) > maximumBytes) return null;
        }
    }
}
