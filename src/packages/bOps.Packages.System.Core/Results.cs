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
/// </summary>
public sealed record ProcessInspectResult(
    int Pid, bool Exists, string? Name, long? WorkingSetMb, int? ThreadCount, DateTimeOffset? StartTimeUtc);
