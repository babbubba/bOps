// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The bounded, deterministic JSON of <c>system.stability</c>, schema 1 (ADR-0041 §5, §8): all eight categories with honest counts
/// (exact, lower bound, or <c>null</c> when unknown — never an unknown zero), signature groups, a dense bucketed timeline per category
/// and timestamp kind, the minidump inventory, source status and temporal coverage, within a fixed 32 KiB budget.
/// </summary>
public static class StabilityFormatting
{
    /// <summary>The result schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The fixed category order.</summary>
    public static readonly IReadOnlyList<StabilityCategory> CategoryOrder =
    [
        StabilityCategory.UnexpectedShutdown,
        StabilityCategory.KernelCrash,
        StabilityCategory.KernelFault,
        StabilityCategory.HardwareError,
        StabilityCategory.DisplayFault,
        StabilityCategory.StorageError,
        StabilityCategory.MemoryExhaustion,
        StabilityCategory.Minidump,
    ];

    /// <summary>The wire name of a category.</summary>
    public static string ToWireValue(StabilityCategory category) => category switch
    {
        StabilityCategory.UnexpectedShutdown => "unexpectedShutdown",
        StabilityCategory.KernelCrash => "kernelCrash",
        StabilityCategory.KernelFault => "kernelFault",
        StabilityCategory.HardwareError => "hardwareError",
        StabilityCategory.DisplayFault => "displayFault",
        StabilityCategory.StorageError => "storageError",
        StabilityCategory.MemoryExhaustion => "memoryExhaustion",
        StabilityCategory.Minidump => "minidump",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown stability category."),
    };

    /// <summary>The wire name of an applicability.</summary>
    public static string ToWireValue(CategoryApplicability applicability) => applicability switch
    {
        CategoryApplicability.Applicable => "applicable",
        CategoryApplicability.NotApplicable => "notApplicable",
        CategoryApplicability.NotCollected => "notCollected",
        _ => throw new ArgumentOutOfRangeException(nameof(applicability), applicability, "Unknown applicability."),
    };

    /// <summary>
    /// The time buckets of a window (ADR-0041 §8): hours for 1–2 days, days for 3–31, Monday-aligned weeks for 32–180. The first
    /// bucket starts at the window start rounded down to the alignment; the last one contains the window end.
    /// </summary>
    public static StabilityBuckets Buckets(StabilityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var from = query.FromUtc.UtcDateTime;
        string width;
        TimeSpan length;
        DateTime start;
        if (query.WindowDays <= 2)
        {
            (width, length) = ("hour", TimeSpan.FromHours(1));
            start = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc);
        }
        else if (query.WindowDays <= 31)
        {
            (width, length) = ("day", TimeSpan.FromDays(1));
            start = from.Date;
        }
        else
        {
            (width, length) = ("week", TimeSpan.FromDays(7));
            start = from.Date.AddDays(-(((int)from.DayOfWeek + 6) % 7));
        }

        var first = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc));
        var count = (int)((query.ToUtc - first).Ticks / length.Ticks) + 1;
        return new StabilityBuckets(width, first, count, length);
    }

    /// <summary>Formats <paramref name="snapshot"/> for <paramref name="query"/>.</summary>
    public static string Format(StabilitySnapshot snapshot, StabilityQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        var statuses = SystemEventFormatting.StatusByName(snapshot.Sources);
        var buckets = Buckets(query);
        var minidumpFiles = snapshot.Minidumps.Applicability == CategoryApplicability.Applicable
            ? snapshot.Minidumps.Files.Where(file => InWindow(file.FileTimeUtc, query)).ToArray()
            : [];
        var evidence = snapshot.Evidence
            .Where(record => record.Category != StabilityCategory.Minidump && InWindow(record.TimestampUtc, query))
            .Concat(minidumpFiles.Select(file => new StabilityEvidence(
                StabilityCategory.Minidump, null, null, null, null, null, EvidenceTimestampKind.Reported, file.FileTimeUtc)))
            .ToArray();

        var status = SystemInventoryFormatting.GetStatus(snapshot.Sources);
        var stores = TemporalCoverage.Listed(snapshot.Stores, statuses);
        var coverage = TemporalCoverage.ToJson(stores, query.FromUtc, query.ToUtc);
        var coverageState = coverage["state"]!.GetValue<string>();
        var groups = Groups(evidence);
        var selected = groups.Take(query.Limit).ToArray();

        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["status"] = status,
            ["complete"] = false,
            ["truncated"] = false,
            ["window"] = new JsonObject { ["fromUtc"] = EvidenceTime.Format(query.FromUtc), ["toUtc"] = EvidenceTime.Format(query.ToUtc) },
            ["bucket"] = new JsonObject
            {
                ["width"] = buckets.Width,
                ["firstStartUtc"] = EvidenceTime.Format(buckets.FirstStartUtc),
                ["count"] = buckets.Count,
            },
            ["coverage"] = coverage,
            ["sources"] = SystemEventFormatting.Sources(snapshot.Sources.Select(source => source with
            {
                Detail = SystemInventoryFormatting.Bounded(source.Detail, StabilityLimits.DetailCharacters),
            })),
            ["categories"] = Categories(snapshot.Categories, statuses, evidence),
            ["context"] = Context(snapshot.Context, statuses),
            ["timeline"] = Timeline(evidence, buckets),
            ["observedGroups"] = groups.Length,
            ["returnedGroups"] = selected.Length,
            ["groups"] = new JsonArray(selected.Select(group => (JsonNode)group).ToArray()),
            ["minidumps"] = Minidumps(snapshot.Minidumps, statuses, minidumpFiles),
        };

        var truncated = snapshot.CollectionTruncated || selected.Length < groups.Length;
        SystemEventFormatting.SetCompleteness(root, status, truncated, coverageState);

        var output = root.ToJsonString();
        var groupArray = root["groups"]!.AsArray();
        var fileArray = root["minidumps"]!["files"]!.AsArray();
        while (Encoding.UTF8.GetByteCount(output) > StabilityLimits.OutputBytes && (groupArray.Count > 0 || fileArray.Count > 0))
        {
            if (groupArray.Count > 0)
            {
                groupArray.RemoveAt(groupArray.Count - 1);
                root["returnedGroups"] = groupArray.Count;
            }
            else
            {
                fileArray.RemoveAt(fileArray.Count - 1);
                root["minidumps"]!["returned"] = fileArray.Count;
            }

            SystemEventFormatting.SetCompleteness(root, status, truncated: true, coverageState);
            output = root.ToJsonString();
        }

        if (Encoding.UTF8.GetByteCount(output) > StabilityLimits.OutputBytes)
        {
            // The envelope is bounded by construction (8 categories, at most 8 sources, 3 stores, 16 timeline rows of at most 49
            // integers); not fitting it is an internal error, never a silently shortened envelope (ADR-0041 §5).
            throw new InvalidOperationException("The stability envelope exceeds the fixed output budget.");
        }

        return output;
    }

    /// <summary>
    /// The status of an applicable category from its sources: all available → available; all not applicable → notApplicable; at least
    /// one available or partial → partial; otherwise unavailable. A source the snapshot does not list counts as unavailable.
    /// </summary>
    internal static InventorySourceStatus CategoryStatus(IReadOnlyList<string> sources, IReadOnlyDictionary<string, InventorySourceStatus> statuses)
    {
        var values = sources.Select(name => statuses.TryGetValue(name, out var status) ? status : InventorySourceStatus.Unavailable).ToArray();
        if (values.Length == 0)
        {
            return InventorySourceStatus.Unavailable;
        }

        if (values.All(value => value == InventorySourceStatus.Available))
        {
            return InventorySourceStatus.Available;
        }

        if (values.All(value => value == InventorySourceStatus.NotApplicable))
        {
            return InventorySourceStatus.NotApplicable;
        }

        return values.Any(value => value is InventorySourceStatus.Available or InventorySourceStatus.Partial)
            ? InventorySourceStatus.Partial
            : InventorySourceStatus.Unavailable;
    }

    private static bool InWindow(DateTimeOffset time, StabilityQuery query) => time >= query.FromUtc && time <= query.ToUtc;

    private static JsonArray Categories(
        IReadOnlyList<StabilityCategoryPlan> plans,
        IReadOnlyDictionary<string, InventorySourceStatus> statuses,
        IReadOnlyList<StabilityEvidence> evidence)
    {
        var array = new JsonArray();
        foreach (var category in CategoryOrder)
        {
            var plan = plans.FirstOrDefault(candidate => candidate.Category == category)
                ?? throw new InvalidOperationException($"The collector did not describe the category {ToWireValue(category)}.");
            string? status = null;
            int? count = null;
            if (plan.Applicability == CategoryApplicability.Applicable)
            {
                var derived = CategoryStatus(plan.Sources, statuses);
                status = SystemInventoryFormatting.ToWireValue(derived);
                count = derived switch
                {
                    // Unknown evidence is never reported as absence of evidence (ADR-0041 §5, review finding R2).
                    InventorySourceStatus.Unavailable or InventorySourceStatus.Unsupported => null,
                    InventorySourceStatus.NotApplicable => 0,
                    _ => evidence.Count(record => record.Category == category),
                };
            }

            array.Add(new JsonObject
            {
                ["category"] = ToWireValue(category),
                ["applicability"] = ToWireValue(plan.Applicability),
                ["status"] = status,
                ["detail"] = SystemInventoryFormatting.Bounded(plan.Detail, StabilityLimits.DetailCharacters),
                ["count"] = count,
            });
        }

        return array;
    }

    private static JsonObject Context(StabilityContext context, Dictionary<string, InventorySourceStatus> statuses)
    {
        if (context.Applicability != CategoryApplicability.Applicable || context.Source is null)
        {
            return new JsonObject
            {
                ["applicability"] = ToWireValue(context.Applicability),
                ["status"] = null,
                ["detail"] = null,
                ["boots"] = null,
                ["cleanShutdowns"] = null,
            };
        }

        var status = statuses.TryGetValue(context.Source, out var value) ? value : InventorySourceStatus.Unavailable;
        var known = status is not (InventorySourceStatus.Unavailable or InventorySourceStatus.Unsupported);
        return new JsonObject
        {
            ["applicability"] = ToWireValue(context.Applicability),
            ["status"] = SystemInventoryFormatting.ToWireValue(status),
            ["detail"] = null,
            ["boots"] = known ? context.Boots : null,
            ["cleanShutdowns"] = known ? context.CleanShutdowns : null,
        };
    }

    private static JsonArray Timeline(IReadOnlyList<StabilityEvidence> evidence, StabilityBuckets buckets)
    {
        var rows = new JsonArray();
        foreach (var category in CategoryOrder)
        {
            foreach (var kind in new[] { EvidenceTimestampKind.Occurred, EvidenceTimestampKind.Reported })
            {
                var members = evidence.Where(record => record.Category == category && record.TimestampKind == kind).ToArray();
                if (members.Length == 0)
                {
                    continue;
                }

                var counts = new int[buckets.Count];
                foreach (var record in members)
                {
                    var index = (int)((record.TimestampUtc - buckets.FirstStartUtc).Ticks / buckets.Length.Ticks);
                    counts[Math.Clamp(index, 0, buckets.Count - 1)]++;
                }

                rows.Add(new JsonObject
                {
                    ["category"] = ToWireValue(category),
                    ["timestampKind"] = TemporalCoverage.ToWireValue(kind),
                    ["counts"] = new JsonArray(counts.Select(value => (JsonNode?)value).ToArray()),
                });
            }
        }

        return rows;
    }

    private static JsonObject[] Groups(IReadOnlyList<StabilityEvidence> evidence) =>
        evidence
            .GroupBy(record => (record.Category, record.Provider, record.EventId, record.Code, record.Component, record.SeverityClass, record.TimestampKind))
            .Select(group => (group.Key, Count: group.Count(), First: group.Min(record => record.TimestampUtc), Last: group.Max(record => record.TimestampUtc)))
            .OrderBy(group => group.Key.Category)
            .ThenByDescending(group => group.Count)
            .ThenByDescending(group => group.Last)
            .ThenBy(group => group.Key.Provider, KeyComparer.Instance)
            .ThenBy(group => group.Key.EventId, KeyComparer.Instance)
            .ThenBy(group => group.Key.Code, KeyComparer.Instance)
            .ThenBy(group => group.Key.Component, KeyComparer.Instance)
            .ThenBy(group => group.Key.SeverityClass, KeyComparer.Instance)
            .ThenBy(group => TemporalCoverage.ToWireValue(group.Key.TimestampKind), StringComparer.Ordinal)
            .Select(group => new JsonObject
            {
                ["category"] = ToWireValue(group.Key.Category),
                ["provider"] = SystemInventoryFormatting.Bounded(group.Key.Provider, 128),
                ["eventId"] = SystemInventoryFormatting.Bounded(group.Key.EventId, 16),
                ["code"] = SystemInventoryFormatting.Bounded(group.Key.Code, StabilityLimits.CodeCharacters),
                ["component"] = SystemInventoryFormatting.Bounded(group.Key.Component, StabilityLimits.ComponentCharacters),
                ["severityClass"] = group.Key.SeverityClass,
                ["timestampKind"] = TemporalCoverage.ToWireValue(group.Key.TimestampKind),
                ["count"] = group.Count,
                ["firstSeenUtc"] = EvidenceTime.Format(group.First),
                ["lastSeenUtc"] = EvidenceTime.Format(group.Last),
            })
            .ToArray();

    private static JsonObject Minidumps(
        MinidumpInventory inventory, Dictionary<string, InventorySourceStatus> statuses, MinidumpFile[] inWindow)
    {
        if (inventory.Applicability != CategoryApplicability.Applicable || inventory.Source is null)
        {
            return new JsonObject
            {
                ["applicability"] = ToWireValue(inventory.Applicability),
                ["status"] = null,
                ["directory"] = null,
                ["observed"] = null,
                ["returned"] = null,
                ["totalBytes"] = null,
                ["files"] = new JsonArray(),
            };
        }

        var status = statuses.TryGetValue(inventory.Source, out var value) ? value : InventorySourceStatus.Unavailable;
        var root = new JsonObject
        {
            ["applicability"] = ToWireValue(inventory.Applicability),
            ["status"] = SystemInventoryFormatting.ToWireValue(status),
            ["directory"] = SystemInventoryFormatting.Bounded(inventory.Directory, SystemMaintenanceLimits.PathCharacters),
        };

        if (status is InventorySourceStatus.Unavailable or InventorySourceStatus.Unsupported)
        {
            // The directory could not be read at all: the counters are unknown, never zero (ADR-0041 §5, review finding R2).
            root["observed"] = null;
            root["returned"] = null;
            root["totalBytes"] = null;
            root["files"] = new JsonArray();
            return root;
        }

        var listed = inWindow
            .OrderByDescending(file => file.FileTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .Take(StabilityLimits.ListedMinidumps)
            .Select(file => (JsonNode)new JsonObject
            {
                ["name"] = SystemInventoryFormatting.Bounded(file.Name, StabilityLimits.FileNameCharacters),
                ["sizeBytes"] = file.SizeBytes,
                ["fileTimeUtc"] = EvidenceTime.Format(file.FileTimeUtc),
                ["timestampKind"] = TemporalCoverage.ToWireValue(EvidenceTimestampKind.Reported),
                ["fileNameLocalDate"] = file.FileNameLocalDate,
            })
            .ToArray();
        root["observed"] = inWindow.Length;
        root["returned"] = listed.Length;
        root["totalBytes"] = inWindow.Sum(file => file.SizeBytes);
        root["files"] = new JsonArray(listed);
        return root;
    }
}

/// <summary>The derived time buckets of a <c>system.stability</c> result.</summary>
/// <param name="Width">The bucket width: <c>hour</c>, <c>day</c> or <c>week</c>.</param>
/// <param name="FirstStartUtc">The start of the first bucket.</param>
/// <param name="Count">The number of buckets, up to and including the one containing the window end.</param>
/// <param name="Length">The length of one bucket.</param>
public sealed record StabilityBuckets(string Width, DateTimeOffset FirstStartUtc, int Count, TimeSpan Length);
