// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects <c>process.kill</c> data on Windows via <see cref="Process.Kill()"/> (<c>TerminateProcess</c>).</summary>
public sealed class WindowsProcessKillTool() : ProcessKillToolBase("windows")
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
