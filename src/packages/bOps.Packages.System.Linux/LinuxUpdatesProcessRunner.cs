// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;

namespace bOps.Packages.Sys.Linux;

/// <summary>Bounded process lifecycle used only by the fixed Linux update-query recipes.</summary>
internal static class LinuxUpdatesProcessRunner
{
    private const int MaxOutput = 1_048_576;
    internal static async Task<Result> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, Action<ProcessStartInfo>? configure = null, Action<int>? started = null)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        configure?.Invoke(process.StartInfo);
        if (!process.Start()) return new(false, false, false, 0, null, null, null);
        started?.Invoke(process.Id);
        process.StandardInput.Close();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        var stdout = LinuxUpdatesTool.ReadBounded(process.StandardOutput, MaxOutput, linked.Token);
        var stderr = LinuxUpdatesTool.ReadBounded(process.StandardError, 16_384, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            return new(true, true, true, process.Id, null, null, null);
        }
        return new(true, false, true, process.Id, process.ExitCode, await stdout, await stderr);
    }

    internal sealed record Result(bool Started, bool TimedOut, bool Exited, int ProcessId, int? ExitCode, string? StandardOutput, string? StandardError);
}
