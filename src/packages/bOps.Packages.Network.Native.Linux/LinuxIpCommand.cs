// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>
/// Runs exactly the two fixed, argument-listed invocations the V1.3-D task spec permits —
/// <c>ip -j route show [-6]</c> and <c>ip -j neighbor show [-6]</c> — and nothing else. Arguments
/// are a fixed array passed through <see cref="ProcessStartInfo.ArgumentList"/>
/// (<c>UseShellExecute = false</c>, no shell), never string-concatenated, and no argument is
/// ever model-supplied (rule S1: this is not a general process-execution surface).
/// </summary>
internal static class LinuxIpCommand
{
    internal static async Task<string?> RunAsync(string[] arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ip",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return null; // "ip" is not installed or not on PATH: an honest gap, not a failure to surface.
        }

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0 ? output : null;
    }
}
