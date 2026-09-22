// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Collects <c>process.inspect</c> data on Windows via <see cref="Process"/>, <c>Win32_Process</c>
/// and two kernel32 counters. Every field is read independently: one that this identity may not
/// read becomes <c>null</c> and the rest of the observation still stands (ADR-0034).
/// </summary>
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
            ct.ThrowIfCancellationRequested();
            var identity = WindowsProcessInformation.TryRead(pid);
            var result = new ProcessInspectResult(
                pid,
                Exists: true,
                WindowsProcessCounters.Text(() => process.ProcessName),
                WindowsProcessCounters.Value(() => process.WorkingSet64 / BytesPerMb),
                WindowsProcessCounters.Value(() => process.Threads.Count),
                WindowsProcessCounters.Value(() => (DateTimeOffset)process.StartTime.ToUniversalTime()))
            {
                ParentPid = identity?.ParentPid,
                ExecutablePath = identity?.ExecutablePath,
                CommandLine = identity?.CommandLine,
                User = WindowsProcessInformation.TryReadOwner(pid),
                PrivateMemoryMb = WindowsProcessCounters.Value(() => process.PrivateMemorySize64 / BytesPerMb),
                VirtualMemoryMb = WindowsProcessCounters.Value(() => process.VirtualMemorySize64 / BytesPerMb),
                HandleOrFdCount = WindowsProcessCounters.HandleCount(process),
                CpuTotalMs = WindowsProcessCounters.Value(() => (long)process.TotalProcessorTime.TotalMilliseconds),
                IoReadBytes = WindowsProcessCounters.ReadTransferBytes(process),
                IoWriteBytes = WindowsProcessCounters.WriteTransferBytes(process),
            };
            return Task.FromResult(result);
        }
    }
}
