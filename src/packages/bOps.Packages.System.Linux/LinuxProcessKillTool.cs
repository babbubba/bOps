// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects <c>process.kill</c> data on Linux via <see cref="Process.Kill()"/> (SIGKILL).</summary>
public sealed class LinuxProcessKillTool() : ProcessKillToolBase("linux")
{
    protected override Task<ToolCallResult> KillAsync(int pid, CancellationToken ct)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            return Task.FromResult(ToolCallResult.Success($"Sent a forced kill to process {pid}."));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(ToolCallResult.Failure($"No process with id {pid} is running."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not kill process {pid}: {ex.Message}"));
        }
    }
}
