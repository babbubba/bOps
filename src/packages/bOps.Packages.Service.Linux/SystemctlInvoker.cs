// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace bOps.Packages.Service.Linux;

/// <summary>
/// Runs <c>systemctl</c> with a fixed argument list — never a composed shell string — and returns
/// its standard output. Both <c>service.list</c> and <c>service.status</c> use this; the
/// invocation shape each one passes is hardcoded in the calling tool, never assembled from model
/// input beyond a single argv element (ADR-0021).
/// </summary>
internal static class SystemctlInvoker
{
    internal sealed record ExecutionResult(int ExitCode, string StandardOutput, string StandardError);

    public static async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await RunDetailedAsync(arguments, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"systemctl exited with code {result.ExitCode}: {result.StandardError}");
        }

        return result.StandardOutput;
    }

    public static async Task<ExecutionResult> RunDetailedAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("systemctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start systemctl.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new ExecutionResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
