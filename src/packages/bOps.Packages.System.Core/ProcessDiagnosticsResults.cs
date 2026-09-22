// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>
/// One instantaneous reading of a process's cumulative counters, taken by an OS package.
/// <c>process.metrics</c> takes two of these and turns the difference into rates, so the rate
/// arithmetic — and with it the host normalization of CPU — is written once, in the shared
/// library, rather than once per operating system (agentic/01-architecture-rules.md, rule A8).
/// A counter the identity may not read is <c>null</c>, never zero: "not visible" and "zero" lead
/// to opposite conclusions about a process.
/// </summary>
public sealed record ProcessSample
{
    /// <summary>Whether the process existed at the instant this sample was taken.</summary>
    public required bool Exists { get; init; }

    /// <summary>Cumulative processor time across every thread, since the process started.</summary>
    public TimeSpan? CpuTotal { get; init; }

    /// <summary>Resident (working set) bytes.</summary>
    public long? WorkingSetBytes { get; init; }

    /// <summary>Private (non-shared) committed bytes.</summary>
    public long? PrivateMemoryBytes { get; init; }

    /// <summary>Reserved virtual address space, in bytes.</summary>
    public long? VirtualMemoryBytes { get; init; }

    /// <summary>Threads currently in the process.</summary>
    public int? ThreadCount { get; init; }

    /// <summary>Open kernel handles (Windows) or file descriptors (Linux).</summary>
    public int? HandleOrFdCount { get; init; }

    /// <summary>Cumulative bytes read by the process since it started.</summary>
    public long? ReadBytes { get; init; }

    /// <summary>Cumulative bytes written by the process since it started.</summary>
    public long? WriteBytes { get; init; }

    /// <summary>Cumulative page faults since the process started.</summary>
    public long? PageFaults { get; init; }
}

/// <summary>
/// The rates and levels <c>process.metrics</c> reports for one PID. A field is <c>null</c> when the
/// underlying counter could not be read; <see cref="Partial"/> then says so explicitly, so an
/// absent number is never mistaken for a quiet process.
/// </summary>
public sealed record ProcessMetricsResult
{
    /// <summary>The PID that was sampled.</summary>
    public required int Pid { get; init; }

    /// <summary>Whether the process was still present for both samples.</summary>
    public required bool Exists { get; init; }

    /// <summary>The interval between the two samples that produced the rates, in milliseconds.</summary>
    public required int SampleMilliseconds { get; init; }

    /// <summary>Processor use over the interval, normalized across every host processor, 0–100.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Resident memory at the end of the interval, in megabytes.</summary>
    public long? WorkingSetMb { get; init; }

    /// <summary>Private committed memory at the end of the interval, in megabytes.</summary>
    public long? PrivateMemoryMb { get; init; }

    /// <summary>Reserved virtual address space at the end of the interval, in megabytes.</summary>
    public long? VirtualMemoryMb { get; init; }

    /// <summary>Threads at the end of the interval.</summary>
    public int? ThreadCount { get; init; }

    /// <summary>Open handles (Windows) or file descriptors (Linux) at the end of the interval.</summary>
    public int? HandleOrFdCount { get; init; }

    /// <summary>Bytes read per second over the interval.</summary>
    public double? ReadBytesPerSec { get; init; }

    /// <summary>Bytes written per second over the interval.</summary>
    public double? WriteBytesPerSec { get; init; }

    /// <summary>Page faults per second over the interval.</summary>
    public double? PageFaultsPerSec { get; init; }

    /// <summary>True when the process exited mid-sample or any reported field could not be read.</summary>
    public required bool Partial { get; init; }
}

/// <summary>One process as a tree collector saw it: its own PID, its parent's, and its name.</summary>
/// <param name="Pid">The process id.</param>
/// <param name="ParentPid">The parent's process id, or <c>null</c> when it could not be read.</param>
/// <param name="Name">The process name, or <c>null</c> when it could not be read.</param>
public sealed record ProcessTreeEntry(int Pid, int? ParentPid, string? Name);

/// <summary>
/// Every process a tree collector could observe, plus how many it could not. A process that is
/// running but invisible to this identity is counted, never silently dropped: a tree with a
/// missing branch that claims to be complete is worse than no tree.
/// </summary>
/// <param name="Entries">The processes the collector read, in any order.</param>
/// <param name="Skipped">Processes that existed but could not be read at all.</param>
/// <param name="CollectionTruncated">True when the collector stopped at its scan ceiling.</param>
public sealed record ProcessTreeSnapshot(
    IReadOnlyList<ProcessTreeEntry> Entries,
    int Skipped = 0,
    bool CollectionTruncated = false);

/// <summary>One selected row of <c>process.tree</c>: a process and how far below the root it sits.</summary>
/// <param name="Pid">The process id.</param>
/// <param name="ParentPid">The parent's process id, or <c>null</c> when it could not be read.</param>
/// <param name="Name">The process name, or <c>null</c> when it could not be read.</param>
/// <param name="Depth">Generations below the root of this walk; a root is 0.</param>
public sealed record ProcessTreeRow(int Pid, int? ParentPid, string? Name, int Depth);

/// <summary>The rows <c>process.tree</c> will report, and what the walk had to leave out.</summary>
/// <param name="Rows">The selected rows, already in the order they are reported.</param>
/// <param name="RootFound">False when a requested <c>rootPid</c> was not among the observed processes.</param>
/// <param name="ObservedProcesses">Processes reachable from the roots within the requested depth.</param>
/// <param name="Truncated">True when the row limit, the depth or the collector's scan ceiling cut the walk.</param>
public sealed record ProcessTreeSelection(
    IReadOnlyList<ProcessTreeRow> Rows,
    bool RootFound,
    int ObservedProcesses,
    bool Truncated);

/// <summary>One loaded module (Windows DLL/EXE image, Linux file-backed mapping).</summary>
/// <param name="Name">The file name of the module.</param>
/// <param name="Path">The full path the module was loaded from; the identity modules are unique by.</param>
/// <param name="BaseAddress">The lowest mapped address, as lower-case hexadecimal with an <c>0x</c> prefix, or <c>null</c>.</param>
/// <param name="SizeBytes">The mapped size in bytes, or <c>null</c> when it could not be read.</param>
/// <param name="Version">The file version, where the platform records one.</param>
public sealed record ProcessModuleEntry(
    string Name,
    string Path,
    string? BaseAddress,
    long? SizeBytes,
    string? Version);

/// <summary>
/// The modules of one process plus the explicit status of the read, so "this process loaded
/// nothing" and "this identity may not look" stay distinguishable.
/// </summary>
/// <param name="Exists">Whether the process existed when the read was attempted.</param>
/// <param name="Modules">The modules the collector read, in any order.</param>
/// <param name="Status">Whether the module list could be read completely.</param>
/// <param name="Detail">Why the read was not complete, when it was not.</param>
/// <param name="CollectionTruncated">True when the collector stopped at its scan ceiling.</param>
public sealed record ProcessModuleSnapshot(
    bool Exists,
    IReadOnlyList<ProcessModuleEntry> Modules,
    InventorySourceStatus Status = InventorySourceStatus.Available,
    string? Detail = null,
    bool CollectionTruncated = false);
