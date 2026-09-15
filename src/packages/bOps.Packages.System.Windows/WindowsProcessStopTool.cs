// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Collects <c>process.stop</c> data on Windows via <see cref="Process.CloseMainWindow"/> — the
/// only generic, no-extra-privilege "please close" mechanism the Win32 API offers, and it only
/// works for a process with a message loop and a main window. A console or service process has
/// neither: Windows has no generic SIGTERM equivalent that reaches an arbitrary process the way
/// POSIX's <c>kill(pid, SIGTERM)</c> does. That gap is reported honestly as
/// <see cref="ToolOutcome.Failure"/>, not papered over by silently escalating to a forced kill.
/// </summary>
public sealed class WindowsProcessStopTool() : ProcessStopToolBase("windows")
{
    protected override Task<ToolCallResult> RequestStopAsync(int pid, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(ToolCallResult.Failure($"No process with id {pid} is running."));
        }

        using (process)
        {
            try
            {
                if (process.CloseMainWindow())
                {
                    return Task.FromResult(ToolCallResult.Success($"Sent a close request to process {pid}'s main window."));
                }

                return Task.FromResult(ToolCallResult.Failure(
                    $"Process {pid} has no main window to close gracefully — Windows has no generic " +
                    "graceful-stop mechanism for a process without one. Use process.kill for a forced stop."));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return Task.FromResult(ToolCallResult.Failure($"Could not request a graceful stop of process {pid}: {ex.Message}"));
            }
        }
    }
}
