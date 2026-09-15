// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>Basic identification of the machine: OS, hostname, and how long it has been running.</summary>
public sealed record SystemInfoResult(string OsDescription, string HostName, TimeSpan Uptime);

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
