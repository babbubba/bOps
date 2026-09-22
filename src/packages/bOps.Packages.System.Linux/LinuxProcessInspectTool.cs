// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Collects <c>process.inspect</c> data on Linux: the existing fields via <see cref="Process"/>,
/// the V1.3-C additions by reading <c>/proc/&lt;pid&gt;</c> directly (ADR-0034). Every file is read
/// on its own, so one the identity may not open costs its own fields and nothing else.
/// </summary>
public sealed class LinuxProcessInspectTool() : ProcessInspectToolBase("linux")
{
    private const int BytesPerMb = 1024 * 1024;

    protected override async Task<ProcessInspectResult> CollectAsync(int pid, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return new ProcessInspectResult(pid, Exists: false, null, null, null, null);
        }

        using (process)
        {
            var stat = await LinuxProcReader.ReadStatAsync(pid, ct);
            var status = await LinuxProcReader.ReadStatusAsync(pid, ct);
            var io = await LinuxProcReader.ReadIoAsync(pid, ct);
            var names = await LinuxUserNames.ReadAsync(ct);

            return new ProcessInspectResult(
                pid,
                Exists: true,
                SafeName(process) ?? stat?.Name,
                SafeWorkingSetMb(process) ?? status?.ResidentBytes / BytesPerMb,
                SafeThreadCount(process) ?? status?.ThreadCount ?? stat?.ThreadCount,
                SafeStartTimeUtc(process))
            {
                ParentPid = stat?.ParentPid,
                ExecutablePath = LinuxProcReader.ReadExecutablePath(pid),
                CommandLine = await LinuxProcReader.ReadCommandLineAsync(pid, ct),
                User = LinuxUserNames.Resolve(names, status?.Uid),
                PrivateMemoryMb = status?.PrivateBytes / BytesPerMb,
                VirtualMemoryMb = (status?.VirtualBytes ?? stat?.VirtualBytes) / BytesPerMb,
                HandleOrFdCount = LinuxProcReader.CountFileDescriptors(pid),
                CpuTotalMs = stat?.CpuTicks is { } ticks ? ticks * 1000 / LinuxProcReader.ClockTicksPerSecond : null,
                IoReadBytes = io.Read,
                IoWriteBytes = io.Write,
            };
        }
    }

    // A process can exit, or be another user's, between GetProcessById() and reading its
    // properties — degrade the affected field to null rather than losing the whole observation
    // (mirrors LinuxProcessListTool's SafeXxx helpers).
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
