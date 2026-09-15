// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects <c>process.inspect</c> data on Windows via <see cref="Process"/>.</summary>
public sealed class WindowsProcessInspectTool() : ProcessInspectToolBase("windows")
{
    private const int BytesPerMb = 1024 * 1024;

    protected override Task<ProcessInspectResult> CollectAsync(int pid, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new ProcessInspectResult(pid, Exists: false, null, null, null, null));
        }

        using (process)
        {
            var result = new ProcessInspectResult(
                pid,
                Exists: true,
                SafeName(process),
                SafeWorkingSetMb(process),
                SafeThreadCount(process),
                SafeStartTimeUtc(process));
            return Task.FromResult(result);
        }
    }

    // A process can exit, or be another user's / a protected system process, between
    // GetProcessById() and reading its properties — degrade the affected field to null rather
    // than losing the whole observation (mirrors WindowsProcessListTool's SafeXxx helpers).
    private static string? SafeName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static long? SafeWorkingSetMb(Process process)
    {
        try
        {
            return process.WorkingSet64 / BytesPerMb;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static int? SafeThreadCount(Process process)
    {
        try
        {
            return process.Threads.Count;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset? SafeStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
