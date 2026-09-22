// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Linux;

/// <summary>The fields of <c>/proc/&lt;pid&gt;/stat</c> the process tools use.</summary>
internal sealed record LinuxProcStat
{
    /// <summary>The process id.</summary>
    public required int Pid { get; init; }

    /// <summary>The executable name the kernel records, without the surrounding parentheses.</summary>
    public string? Name { get; init; }

    /// <summary>The parent's process id.</summary>
    public int? ParentPid { get; init; }

    /// <summary>Cumulative user plus system time, in clock ticks.</summary>
    public long? CpuTicks { get; init; }

    /// <summary>Cumulative minor plus major page faults.</summary>
    public long? PageFaults { get; init; }

    /// <summary>Threads currently in the process.</summary>
    public int? ThreadCount { get; init; }

    /// <summary>Reserved virtual address space, in bytes.</summary>
    public long? VirtualBytes { get; init; }
}

/// <summary>The fields of <c>/proc/&lt;pid&gt;/status</c> the process tools use.</summary>
internal sealed record LinuxProcStatus
{
    /// <summary>The real user id the process runs as.</summary>
    public int? Uid { get; init; }

    /// <summary>Reserved virtual address space, in bytes.</summary>
    public long? VirtualBytes { get; init; }

    /// <summary>Resident set size, in bytes.</summary>
    public long? ResidentBytes { get; init; }

    /// <summary>
    /// Anonymous (private, non-file-backed) resident bytes — the closest Linux equivalent of the
    /// private committed memory Windows reports. Absent on kernels older than 4.5.
    /// </summary>
    public long? PrivateBytes { get; init; }

    /// <summary>Threads currently in the process.</summary>
    public int? ThreadCount { get; init; }
}
