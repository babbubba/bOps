// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>Basic identification of the machine: OS, hostname, and how long it has been running.</summary>
public sealed record SystemInfoResult(
    string OsDescription,
    string HostName,
    TimeSpan Uptime,
    string? HardwareModel = null);

/// <summary>Current CPU utilization, sampled over a short interval.</summary>
public sealed record CpuUsageResult(double Percent);

/// <summary>Physical memory totals, in megabytes.</summary>
public sealed record MemoryUsageResult(long TotalMb, long AvailableMb)
{
    /// <summary>Percentage of total memory currently in use.</summary>
    public double UsedPercent => TotalMb == 0 ? 0 : 100.0 * (TotalMb - AvailableMb) / TotalMb;
}

/// <summary>One mounted volume's space usage, in megabytes.</summary>
public sealed record DiskUsageResult(string Name, long TotalMb, long FreeMb);

/// <summary>One running process, for listing by memory usage.</summary>
public sealed record ProcessSummary(int Pid, string Name, long WorkingSetMb);

/// <summary>Swap (paging file) totals, in megabytes.</summary>
public sealed record SwapUsageResult(long TotalMb, long UsedMb)
{
    /// <summary>Percentage of total swap currently in use.</summary>
    public double UsedPercent => TotalMb == 0 ? 0 : 100.0 * UsedMb / TotalMb;
}

/// <summary>One device's disk I/O throughput, in kilobytes per second, sampled over a short interval.</summary>
public sealed record IoUsageResult(string DeviceName, double ReadKbPerSec, double WriteKbPerSec);

/// <summary>
/// A detailed, single-PID observation — including a definite absence when the process is not
/// running, reported the same way <c>fs.stat</c> reports a missing path: a successful observation
/// of a negative fact, not a failure. A field is <c>null</c> when the process exists but that one
/// property could not be read (exited mid-read, another user's process, a protected system
/// process) rather than losing the whole observation.
/// <para>
/// The environment of a process is never part of this shape and never will be: an environment
/// block routinely carries credentials, and an unfiltered environment dump is permanently out of
/// scope (agentic/00-project-spec.md; rule S1's sibling).
/// </para>
/// </summary>
public sealed record ProcessInspectResult(
    int Pid, bool Exists, string? Name, long? WorkingSetMb, int? ThreadCount, DateTimeOffset? StartTimeUtc)
{
    /// <summary>The parent's process id (V1.3-C, ADR-0034).</summary>
    public int? ParentPid { get; init; }

    /// <summary>The full path of the executable image.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// The command line the process was started with. Untrusted text: it can carry anything the
    /// launcher chose to put there, and it is bounded before it reaches the model.
    /// </summary>
    public string? CommandLine { get; init; }

    /// <summary>The identity the process runs as.</summary>
    public string? User { get; init; }

    /// <summary>Private (non-shared) committed memory, in megabytes.</summary>
    public long? PrivateMemoryMb { get; init; }

    /// <summary>Reserved virtual address space, in megabytes.</summary>
    public long? VirtualMemoryMb { get; init; }

    /// <summary>Open kernel handles (Windows) or file descriptors (Linux).</summary>
    public int? HandleOrFdCount { get; init; }

    /// <summary>Cumulative processor time across every thread since the process started, in milliseconds.</summary>
    public long? CpuTotalMs { get; init; }

    /// <summary>Cumulative bytes read since the process started.</summary>
    public long? IoReadBytes { get; init; }

    /// <summary>Cumulative bytes written since the process started.</summary>
    public long? IoWriteBytes { get; init; }
}
