// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Collects <c>process.stop</c> data on Linux via <c>kill(pid, SIGTERM)</c> — a single libc call,
/// not a subprocess: unlike <c>bOps.Packages.Service.Linux</c>'s <c>systemctl</c> shell-out
/// (ADR-0021), a direct P/Invoke has no process-spawning surface to reason about at all.
/// </summary>
public sealed partial class LinuxProcessStopTool() : ProcessStopToolBase("linux")
{
    private const int Sigterm = 15;

    [LibraryImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int kill(int pid, int sig);

    protected override Task<ToolCallResult> RequestStopAsync(int pid, CancellationToken ct)
    {
        if (kill(pid, Sigterm) == 0)
        {
            return Task.FromResult(ToolCallResult.Success($"Sent SIGTERM to process {pid}."));
        }

        var error = Marshal.GetLastPInvokeError();
        return Task.FromResult(ToolCallResult.Failure(
            $"Could not send SIGTERM to process {pid}: {new Win32Exception(error).Message}"));
    }
}
