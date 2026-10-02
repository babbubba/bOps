// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>The eight fixed categories of <c>system.stability</c>, in their fixed order (ADR-0041 §3).</summary>
public enum StabilityCategory
{
    /// <summary>The machine restarted without a clean shutdown.</summary>
    UnexpectedShutdown,

    /// <summary>The kernel stopped the machine (a bugcheck or panic).</summary>
    KernelCrash,

    /// <summary>A kernel fault the machine survived (oops, BUG, general protection fault).</summary>
    KernelFault,

    /// <summary>A hardware error reported by the platform (WHEA, MCE, EDAC).</summary>
    HardwareError,

    /// <summary>Graphics-stack fault evidence: a timeout detection and recovery or a display-kernel live dump; not proof of a reset.</summary>
    DisplayFault,

    /// <summary>A storage device or controller error or reset.</summary>
    StorageError,

    /// <summary>The kernel killed a process for memory.</summary>
    MemoryExhaustion,

    /// <summary>Kernel crash dump files present now.</summary>
    Minidump,
}

/// <summary>Whether this platform has typed evidence for a category (ADR-0041 §3).</summary>
public enum CategoryApplicability
{
    /// <summary>This platform has typed evidence for the category and this tool collects it.</summary>
    Applicable,

    /// <summary>This platform has no typed evidence that means this category; bOps does not infer one.</summary>
    NotApplicable,

    /// <summary>This platform has related evidence that this version does not read; absence of findings means nothing.</summary>
    NotCollected,
}

/// <summary>The <c>severityClass</c> vocabulary of hardware-error evidence; anything not proven is <see cref="Unknown"/> (review note R9).</summary>
public static class StabilitySeverityClasses
{
    /// <summary>The platform states that the error was corrected.</summary>
    public const string Corrected = "corrected";

    /// <summary>The platform states that the error was not corrected (or was fatal).</summary>
    public const string Uncorrected = "uncorrected";

    /// <summary>Neither is proven.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Finite limits of <c>system.stability</c> (ADR-0041 §2, §4, §5).</summary>
public static class StabilityLimits
{
    /// <summary>Default look-back window, in days.</summary>
    public const int DefaultWindowDays = 30;

    /// <summary>Largest look-back window, in days.</summary>
    public const int MaximumWindowDays = 180;

    /// <summary>Default number of groups returned.</summary>
    public const int DefaultGroups = 50;

    /// <summary>Largest number of groups a caller may request.</summary>
    public const int MaximumGroups = 200;

    /// <summary>The fixed UTF-8 output budget.</summary>
    public const int OutputBytes = 32_768;

    /// <summary>Most native records one Windows Event Log source examines.</summary>
    public const int EventLogRecordCeiling = 5_000;

    /// <summary>Most entries the minidump enumeration examines.</summary>
    public const int MinidumpEntryCeiling = 256;

    /// <summary>Most kernel journal records the Linux source examines.</summary>
    public const int JournalRecordCeiling = 10_000;

    /// <summary>Most minidump files listed by name (the newest by last-write time).</summary>
    public const int ListedMinidumps = 16;

    /// <summary>Longest <c>code</c>.</summary>
    public const int CodeCharacters = 32;

    /// <summary>Longest <c>component</c>.</summary>
    public const int ComponentCharacters = 128;

    /// <summary>Longest <c>detail</c>.</summary>
    public const int DetailCharacters = 256;

    /// <summary>Longest minidump file name.</summary>
    public const int FileNameCharacters = 128;

    /// <summary>The bound on one call's reads, coverage probes included: shorter than the runner's 30-second tool timeout.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);
}

/// <summary>
/// One machine-level evidence record of <c>system.stability</c>, already typed by the OS package that read it. No message text:
/// <see cref="Code"/> and <see cref="Component"/> have matched their fixed patterns or are <c>null</c>.
/// </summary>
/// <param name="Category">The category.</param>
/// <param name="Provider">The native provider (Windows), <c>kernel</c> (Linux), or <c>null</c>.</param>
/// <param name="EventId">The native event id (Windows), or <c>null</c>.</param>
/// <param name="Code">The canonical code, or <c>null</c>.</param>
/// <param name="Component">The device, driver or process the evidence names, or <c>null</c>.</param>
/// <param name="SeverityClass">For hardware errors: <see cref="StabilitySeverityClasses"/>; otherwise <c>null</c>.</param>
/// <param name="TimestampKind">Whether <paramref name="TimestampUtc"/> is an occurrence or a reporting time (ADR-0041 §6).</param>
/// <param name="TimestampUtc">The record time.</param>
public sealed record StabilityEvidence(
    StabilityCategory Category,
    string? Provider,
    string? EventId,
    string? Code,
    string? Component,
    string? SeverityClass,
    EvidenceTimestampKind TimestampKind,
    DateTimeOffset TimestampUtc);

/// <summary>How one category is collected on this platform.</summary>
/// <param name="Category">The category.</param>
/// <param name="Applicability">Whether the platform has typed evidence for it.</param>
/// <param name="Detail">A fixed package text for <c>notApplicable</c>, <c>notCollected</c> or a known visibility limitation; else <c>null</c>.</param>
/// <param name="Sources">The sources whose status makes the category status (empty unless applicable).</param>
public sealed record StabilityCategoryPlan(
    StabilityCategory Category,
    CategoryApplicability Applicability,
    string? Detail,
    IReadOnlyList<string> Sources);

/// <summary>Boot context (not instability): boots and clean shutdowns in the window.</summary>
/// <param name="Applicability">Whether the platform collects it.</param>
/// <param name="Source">The source the counts come from, or <c>null</c>.</param>
/// <param name="Boots">Boots observed in the window.</param>
/// <param name="CleanShutdowns">Clean shutdowns observed in the window.</param>
public sealed record StabilityContext(CategoryApplicability Applicability, string? Source, int Boots, int CleanShutdowns);

/// <summary>One minidump file: name, size and last-write time only; the file is never opened.</summary>
/// <param name="Name">The file name, already matched against its fixed pattern.</param>
/// <param name="SizeBytes">The size in bytes.</param>
/// <param name="FileTimeUtc">The last-write time, which is a reporting time (ADR-0041 §6).</param>
/// <param name="FileNameLocalDate">The date a <c>MMDDYY-n-n.dmp</c> name carries, as <c>yyyy-MM-dd</c> in the local calendar; descriptive only.</param>
public sealed record MinidumpFile(string Name, long SizeBytes, DateTimeOffset FileTimeUtc, string? FileNameLocalDate);

/// <summary>The minidump inventory of the platform.</summary>
/// <param name="Applicability">Whether the platform collects it.</param>
/// <param name="Source">The source the inventory comes from, or <c>null</c>.</param>
/// <param name="Directory">The resolved directory, or <c>null</c>.</param>
/// <param name="Files">Every valid file the enumeration saw, in any order; the formatter keeps those in the window.</param>
public sealed record MinidumpInventory(CategoryApplicability Applicability, string? Source, string? Directory, IReadOnlyList<MinidumpFile> Files);

/// <summary>Everything one <c>system.stability</c> call collected.</summary>
/// <param name="Evidence">The evidence records (the minidump category is derived from <paramref name="Minidumps"/>).</param>
/// <param name="Sources">Every source of the platform with its status and scan reach.</param>
/// <param name="Categories">All eight categories, as this platform collects them.</param>
/// <param name="Context">Boot context.</param>
/// <param name="Minidumps">The minidump inventory.</param>
/// <param name="CollectionTruncated">True when a ceiling or the time bound stopped a source.</param>
public sealed record StabilitySnapshot(
    IReadOnlyList<StabilityEvidence> Evidence,
    IReadOnlyList<InventorySourceResult> Sources,
    IReadOnlyList<StabilityCategoryPlan> Categories,
    StabilityContext Context,
    MinidumpInventory Minidumps,
    bool CollectionTruncated)
{
    /// <summary>The history-bearing stores behind <see cref="Sources"/>.</summary>
    public IReadOnlyList<CoverageStore> Stores { get; init; } = [];
}

/// <summary>A validated <c>system.stability</c> request.</summary>
/// <param name="WindowDays">The look-back window in days.</param>
/// <param name="FromUtc">Start of the window, inclusive.</param>
/// <param name="ToUtc">End of the window, inclusive.</param>
/// <param name="Limit">Groups returned.</param>
public sealed record StabilityQuery(int WindowDays, DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit);

/// <summary>Reads <c>system.stability</c> arguments (ADR-0041 §2): no mode, no cross-field rule; invalid values are rejected, never clamped.</summary>
public static class StabilityArguments
{
    /// <summary>Validates <paramref name="arguments"/> for a window that ends at <paramref name="now"/>.</summary>
    public static bool TryRead(ToolArguments arguments, DateTimeOffset now, out StabilityQuery? query, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        query = null;
        var json = arguments.ToJson();
        if (!TryInteger(json, arguments, "windowDays", StabilityLimits.DefaultWindowDays, StabilityLimits.MaximumWindowDays, out var days, out error)
            || !TryInteger(json, arguments, "limit", StabilityLimits.DefaultGroups, StabilityLimits.MaximumGroups, out var limit, out error))
        {
            return false;
        }

        var to = now.ToUniversalTime();
        query = new StabilityQuery(days, to.AddMinutes(-days * 1_440d), to, limit);
        return true;
    }

    private static bool TryInteger(
        System.Text.Json.Nodes.JsonObject json, ToolArguments arguments, string name, int fallback, int maximum, out int value, out string? error)
    {
        value = fallback;
        error = null;
        if (!json.ContainsKey(name) || json[name] is null)
        {
            return true;
        }

        if (!arguments.TryGet<int>(name, out value))
        {
            error = $"{name} must be an integer.";
            return false;
        }

        if (value < 1 || value > maximum)
        {
            error = $"{name} must be between 1 and {maximum}.";
            return false;
        }

        return true;
    }
}
