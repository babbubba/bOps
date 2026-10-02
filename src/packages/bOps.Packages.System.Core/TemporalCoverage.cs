// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>What kind of history-bearing store backs a source (ADR-0032 HARDEN-7 amendment §5).</summary>
public enum CoverageBasis
{
    /// <summary>A Windows Event Log channel: its oldest live record and configured maximum size can be read.</summary>
    EventLog,

    /// <summary>The Linux journal, as visible to the host identity, in the scope of the source it backs.</summary>
    Journal,

    /// <summary>A directory of evidence files: it has no retention guarantee, so its coverage is always unknown.</summary>
    Directory,
}

/// <summary>Whether a time is the instant the described thing happened or the instant evidence about it was recorded (ADR-0041 §6).</summary>
public enum EvidenceTimestampKind
{
    /// <summary>By the documented semantics of the source, the time is when the described thing happened.</summary>
    Occurred,

    /// <summary>The time the system recorded, processed or wrote evidence about it, which may be later. The default when not proven.</summary>
    Reported,
}

/// <summary>
/// One history-bearing data store behind a tool's sources (ADR-0032 HARDEN-7 amendment §5). A store is listed in a result only when
/// at least one of <paramref name="BackedSources"/> appears in that result with a status other than <c>notApplicable</c>.
/// </summary>
/// <param name="Name">The store name, for example <c>windows.channel.System</c> or <c>linux.journald</c>.</param>
/// <param name="Basis">The kind of store.</param>
/// <param name="OldestAvailableUtc">The oldest record time the store holds (directories: the oldest item observed), or <c>null</c> when it could not be determined.</param>
/// <param name="LogMaximumBytes">The configured maximum size of an Event Log channel, or <c>null</c>.</param>
/// <param name="BackedSources">The names of the result sources this store holds the history of.</param>
public sealed record CoverageStore(
    string Name,
    CoverageBasis Basis,
    DateTimeOffset? OldestAvailableUtc,
    long? LogMaximumBytes,
    IReadOnlyList<string> BackedSources);

/// <summary>
/// Evaluates and writes the temporal <c>coverage</c> object shared by <c>system.events</c>, <c>system.crashes</c> and
/// <c>system.stability</c> (ADR-0032 HARDEN-7 amendment §5). Coverage answers only "how far back does each store reach, compared with
/// what was asked"; it never says whether a read succeeded, which is the sources' status, <c>truncated</c> and the typed
/// completeness.
/// </summary>
public static class TemporalCoverage
{
    /// <summary>The wire value of a coverage or store state that reaches the start of the request.</summary>
    public const string Complete = "complete";

    /// <summary>The wire value of a coverage or store state whose history starts after the start of the request.</summary>
    public const string Partial = "partial";

    /// <summary>The wire value of a coverage or store state that could not be determined.</summary>
    public const string Unknown = "unknown";

    /// <summary>The wire name of a timestamp kind.</summary>
    public static string ToWireValue(EvidenceTimestampKind kind) => kind switch
    {
        EvidenceTimestampKind.Occurred => "occurred",
        EvidenceTimestampKind.Reported => "reported",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown timestamp kind."),
    };

    /// <summary>The wire name of a store basis.</summary>
    public static string ToWireValue(CoverageBasis basis) => basis switch
    {
        CoverageBasis.EventLog => "eventLog",
        CoverageBasis.Journal => "journal",
        CoverageBasis.Directory => "directory",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, "Unknown coverage basis."),
    };

    /// <summary>
    /// The state of one store: a directory is always <c>unknown</c>; an Event Log channel or the journal is <c>complete</c> when its
    /// oldest record is not after <paramref name="requestedFromUtc"/>, <c>partial</c> when it is, and <c>unknown</c> when it could not
    /// be determined.
    /// </summary>
    public static string StoreState(CoverageStore store, DateTimeOffset requestedFromUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Basis == CoverageBasis.Directory || store.OldestAvailableUtc is not { } oldest)
        {
            return Unknown;
        }

        return oldest <= requestedFromUtc ? Complete : Partial;
    }

    /// <summary>The stores a result lists: those backing at least one listed source that is not <c>notApplicable</c>, ordered by name.</summary>
    public static IReadOnlyList<CoverageStore> Listed(
        IEnumerable<CoverageStore> stores, IReadOnlyDictionary<string, InventorySourceStatus> sourceStatuses)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(sourceStatuses);
        return stores
            .Where(store => store.BackedSources.Any(source =>
                sourceStatuses.TryGetValue(source, out var status) && status != InventorySourceStatus.NotApplicable))
            .GroupBy(store => store.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(store => store.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(store => store.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The global state, over the Event Log and journal stores only: <c>partial</c> if any is partial; else <c>unknown</c> if any is
    /// unknown or there is none; else <c>complete</c>. Directory stores never change it.
    /// </summary>
    public static string GlobalState(IReadOnlyList<CoverageStore> listed, DateTimeOffset requestedFromUtc)
    {
        ArgumentNullException.ThrowIfNull(listed);
        var states = listed
            .Where(store => store.Basis != CoverageBasis.Directory)
            .Select(store => StoreState(store, requestedFromUtc))
            .ToArray();
        if (states.Contains(Partial, StringComparer.Ordinal))
        {
            return Partial;
        }

        return states.Length == 0 || states.Contains(Unknown, StringComparer.Ordinal) ? Unknown : Complete;
    }

    /// <summary>Writes the <c>coverage</c> object for the listed stores of a window.</summary>
    public static JsonObject ToJson(IReadOnlyList<CoverageStore> listed, DateTimeOffset requestedFromUtc, DateTimeOffset requestedToUtc)
    {
        ArgumentNullException.ThrowIfNull(listed);
        return new JsonObject
        {
            ["requestedFromUtc"] = EvidenceTime.Format(requestedFromUtc),
            ["requestedToUtc"] = EvidenceTime.Format(requestedToUtc),
            ["state"] = GlobalState(listed, requestedFromUtc),
            ["stores"] = new JsonArray(listed.Select(store => (JsonNode)new JsonObject
            {
                ["name"] = SystemInventoryFormatting.Bounded(store.Name, 128),
                ["basis"] = ToWireValue(store.Basis),
                ["oldestAvailableUtc"] = EvidenceTime.Format(store.OldestAvailableUtc),
                ["logMaximumBytes"] = store.LogMaximumBytes,
                ["state"] = StoreState(store, requestedFromUtc),
            }).ToArray()),
        };
    }
}

/// <summary>The one timestamp format of the evidence tools: UTC, seven fractional digits, <c>Z</c>.</summary>
public static class EvidenceTime
{
    private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>Formats <paramref name="value"/> as UTC, or returns <c>null</c>.</summary>
    public static string? Format(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    /// <summary>Formats <paramref name="value"/> as UTC.</summary>
    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);
}
