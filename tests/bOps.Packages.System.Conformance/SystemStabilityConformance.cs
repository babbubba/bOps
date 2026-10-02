// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Xunit;

namespace bOps.Packages.Sys.Conformance;

/// <summary>
/// The cross-platform conformance suite for <c>system.stability</c> (ADR-0041) and the schema-2 <c>system.crashes</c> envelope
/// (ADR-0032 HARDEN-7 amendment §4). Windows and Linux share one manifest (apart from <c>Platforms</c>), one argument set, one
/// vocabulary and one set of bounds; platform differences appear only as data — a category's applicability and status.
/// </summary>
public static partial class SystemToolConformance
{
    /// <summary>The <c>system.stability</c> parameters both operating systems must declare, in order, all optional.</summary>
    public static readonly IReadOnlyList<(string Name, ToolParameterType Type, double Minimum, double Maximum)> StabilityParameters =
    [
        ("windowDays", ToolParameterType.Integer, 1, 180),
        ("limit", ToolParameterType.Integer, 1, 200),
    ];

    /// <summary>The fixed category order.</summary>
    public static readonly IReadOnlyList<string> StabilityCategories =
        ["unexpectedShutdown", "kernelCrash", "kernelFault", "hardwareError", "displayFault", "storageError", "memoryExhaustion", "minidump"];

    /// <summary>The <c>system.crashes</c> parameters both operating systems must declare, in order, all optional.</summary>
    public static readonly IReadOnlyList<(string Name, ToolParameterType Type)> CrashesParameters =
    [
        ("mode", ToolParameterType.Enum),
        ("sinceMinutes", ToolParameterType.Integer),
        ("sinceDays", ToolParameterType.Integer),
        ("limit", ToolParameterType.Integer),
    ];

    private static readonly string[] Applicabilities = ["applicable", "notApplicable", "notCollected"];
    private static readonly string[] CategoryStatuses = ["available", "partial", "unavailable", "notApplicable"];
    private static readonly string[] TimestampKinds = ["occurred", "reported"];
    private static readonly string[] SeverityClasses = ["corrected", "uncorrected", "unknown"];

    /// <summary>Asserts that a manifest is the shared <c>system.stability</c> contract for <paramref name="platform"/>.</summary>
    public static void AssertStabilityManifest(ToolManifest manifest, string platform)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        AssertManifestIsWellFormed(manifest, platform, "system.stability");
        Assert.Equal(
            StabilityParameters,
            manifest.Parameters.Select(parameter => (parameter.Name, parameter.Type, parameter.Minimum ?? double.NaN, parameter.Maximum ?? double.NaN)).ToArray());
        Assert.All(manifest.Parameters, parameter => Assert.False(parameter.Required));
        Assert.All(manifest.Parameters, parameter => Assert.False(parameter.Sensitive));
        Assert.Equal([platform], manifest.Platforms);
        Assert.DoesNotContain(manifest.Parameters, parameter => parameter.Name is "mode" or "provider" or "eventId" or "channel" or "query" or "path");
    }

    /// <summary>Asserts that a manifest is the shared schema-2 <c>system.crashes</c> contract for <paramref name="platform"/>.</summary>
    public static void AssertCrashesManifest(ToolManifest manifest, string platform)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        AssertManifestIsWellFormed(manifest, platform, "system.crashes");
        Assert.Equal(CrashesParameters, manifest.Parameters.Select(parameter => (parameter.Name, parameter.Type)).ToArray());
        Assert.Equal(["aggregate", "raw"], manifest.Parameters.Single(parameter => parameter.Name == "mode").AllowedValues);
        Assert.Equal((1d, 10080d), Bounds(manifest, "sinceMinutes"));
        Assert.Equal((1d, 180d), Bounds(manifest, "sinceDays"));
        Assert.Equal((1d, 1000d), Bounds(manifest, "limit"));
    }

    /// <summary>
    /// Runs <paramref name="tool"/> against the real operating system and asserts the shared manifest and envelope. Category
    /// applicability is platform data; everything else is the same contract.
    /// </summary>
    public static async Task<JsonObject> AssertStabilityConformsAsync(ITool tool, string platform, int windowDays = 30)
    {
        ArgumentNullException.ThrowIfNull(tool);
        AssertStabilityManifest(tool.Manifest, platform);
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["windowDays"] = windowDays }));
        Assert.True(result.Succeeded, result.ErrorMessage);
        return AssertStabilityEnvelope(result.Output!, result.Completeness);
    }

    /// <summary>Asserts the arguments a stability reader must refuse: out-of-range values are rejected, never clamped.</summary>
    public static async Task AssertStabilityRejectsBadArgumentsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        foreach (var (name, value) in new (string, JsonNode)[] { ("windowDays", 0), ("windowDays", 181), ("limit", 0), ("limit", 201), ("windowDays", "thirty") })
        {
            var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { [name] = value }));
            Assert.Equal(ToolOutcome.Failure, result.Outcome);
            Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
            Assert.Contains(name, result.ErrorMessage!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Asserts the <c>system.stability</c> envelope (ADR-0041 §5): every field, all eight categories in order with honest counts
    /// (exact, lower bound, <c>null</c> when unknown, <c>0</c> only for a category whose every source is absent), a dense timeline,
    /// ordered groups, no hint for another tool, the strict <c>complete</c>, and the typed completeness it implies.
    /// </summary>
    public static JsonObject AssertStabilityEnvelope(string output, ToolResultCompleteness completeness)
    {
        ArgumentNullException.ThrowIfNull(output);
        Assert.True(Encoding.UTF8.GetByteCount(output) <= 32_768);
        Assert.DoesNotContain("drillDown", output, StringComparison.Ordinal);
        Assert.DoesNotContain("occurredLocalDate", output, StringComparison.Ordinal);
        Assert.DoesNotContain("displayReset", output, StringComparison.Ordinal);
        var root = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(
            ["schemaVersion", "status", "complete", "truncated", "window", "bucket", "coverage", "sources", "categories", "context", "timeline", "observedGroups", "returnedGroups", "groups", "minidumps"],
            root.Select(pair => pair.Key).ToArray());
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
        var status = root["status"]!.GetValue<string>();
        Assert.Contains(status, InventoryStatuses);
        var coverageState = AssertCoverage(root);
        var truncated = root["truncated"]!.GetValue<bool>();
        var complete = root["complete"]!.GetValue<bool>();
        Assert.Equal(status == "complete" && !truncated && coverageState == "complete", complete);
        Assert.Equal(
            complete ? ToolResultCompleteness.Complete : status == "unavailable" ? ToolResultCompleteness.Unavailable : ToolResultCompleteness.Partial,
            completeness);

        var sources = root["sources"]!.AsArray();
        Assert.InRange(sources.Count, 1, 8);
        var statusBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            Assert.Equal(["name", "status", "detail", "examinedFromUtc"], source!.AsObject().Select(pair => pair.Key).ToArray());
            Assert.Contains(source["status"]!.GetValue<string>(), InventorySourceStatuses);
            Assert.True(source["detail"] is null || source["detail"]!.GetValue<string>().Length <= 256);
            statusBySource[source["name"]!.GetValue<string>()] = source["status"]!.GetValue<string>();
        }

        var categories = root["categories"]!.AsArray();
        Assert.Equal(StabilityCategories, categories.Select(category => category!["category"]!.GetValue<string>()).ToArray());
        var counts = new Dictionary<string, (string? Status, int? Count)>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            Assert.Equal(["category", "applicability", "status", "detail", "count"], category!.AsObject().Select(pair => pair.Key).ToArray());
            var applicability = category["applicability"]!.GetValue<string>();
            Assert.Contains(applicability, Applicabilities);
            var categoryStatus = category["status"]?.GetValue<string>();
            int? count = category["count"]?.GetValue<int>();
            if (applicability != "applicable")
            {
                // N2: a notApplicable or notCollected applicability has no status and an unknown count, and always says why.
                Assert.Null(categoryStatus);
                Assert.Null(count);
                Assert.False(string.IsNullOrWhiteSpace(category["detail"]?.GetValue<string>()));
            }
            else
            {
                Assert.Contains(categoryStatus, CategoryStatuses);
                switch (categoryStatus)
                {
                    case "unavailable":
                        Assert.Null(count); // R2: unknown is never zero
                        break;
                    case "notApplicable":
                        Assert.Equal(0, count); // N2: every source of the category is absent on this machine
                        break;
                    default:
                        Assert.NotNull(count);
                        Assert.True(count >= 0);
                        break;
                }
            }

            counts[category["category"]!.GetValue<string>()] = (categoryStatus, count);
        }

        var bucket = root["bucket"]!.AsObject();
        Assert.Equal(["width", "firstStartUtc", "count"], bucket.Select(pair => pair.Key).ToArray());
        var width = bucket["width"]!.GetValue<string>();
        var bucketCount = bucket["count"]!.GetValue<int>();
        var firstStart = DateTimeOffset.Parse(bucket["firstStartUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var from = DateTimeOffset.Parse(root["window"]!["fromUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(root["window"]!["toUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var days = (int)Math.Round((to - from).TotalDays);
        Assert.Equal(days <= 2 ? "hour" : days <= 31 ? "day" : "week", width);
        Assert.True(firstStart <= from);
        Assert.InRange(bucketCount, 1, width == "hour" ? 49 : width == "day" ? 32 : 27);
        if (width == "week")
        {
            Assert.Equal(DayOfWeek.Monday, firstStart.UtcDateTime.DayOfWeek);
        }

        Assert.Equal(TimeSpan.Zero, firstStart.UtcDateTime.TimeOfDay - (width == "hour" ? TimeSpan.FromHours(firstStart.UtcDateTime.Hour) : TimeSpan.Zero));

        var timeline = root["timeline"]!.AsArray();
        Assert.InRange(timeline.Count, 0, 16);
        var previous = (-1, -1);
        foreach (var row in timeline)
        {
            Assert.Equal(["category", "timestampKind", "counts"], row!.AsObject().Select(pair => pair.Key).ToArray());
            var position = (StabilityCategories.ToList().IndexOf(row["category"]!.GetValue<string>()), Array.IndexOf(TimestampKinds, row["timestampKind"]!.GetValue<string>()));
            Assert.True(position.Item1 >= 0 && position.Item2 >= 0);
            Assert.True(position.CompareTo(previous) > 0, "Timeline rows are ordered by category, then occurred before reported.");
            previous = position;
            var values = row["counts"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
            Assert.Equal(bucketCount, values.Length);
            Assert.True(values.Sum() > 0, "A timeline row exists only for a category and kind with at least one record.");
            Assert.All(values, value => Assert.True(value >= 0));
            var category = counts[row["category"]!.GetValue<string>()];
            Assert.NotEqual("unavailable", category.Status);
        }

        var groups = root["groups"]!.AsArray();
        Assert.Equal(groups.Count, root["returnedGroups"]!.GetValue<int>());
        Assert.True(root["observedGroups"]!.GetValue<int>() >= groups.Count);
        var lastCategory = -1;
        foreach (var group in groups)
        {
            Assert.Equal(
                ["category", "provider", "eventId", "code", "component", "severityClass", "timestampKind", "count", "firstSeenUtc", "lastSeenUtc"],
                group!.AsObject().Select(pair => pair.Key).ToArray());
            var index = StabilityCategories.ToList().IndexOf(group["category"]!.GetValue<string>());
            Assert.True(index >= lastCategory, "Groups are ordered by the fixed category order.");
            lastCategory = index;
            Assert.Contains(group["timestampKind"]!.GetValue<string>(), TimestampKinds);
            Assert.True(group["severityClass"] is null || SeverityClasses.Contains(group["severityClass"]!.GetValue<string>()));
            Assert.True(group["code"] is null || group["code"]!.GetValue<string>().Length <= 32);
            Assert.True(group["component"] is null || group["component"]!.GetValue<string>().Length <= 128);
            Assert.True(group["count"]!.GetValue<int>() >= 1);
            var first = DateTimeOffset.Parse(group["firstSeenUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var last = DateTimeOffset.Parse(group["lastSeenUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            Assert.True(from <= first && first <= last && last <= to);
            var category = counts[group["category"]!.GetValue<string>()];
            Assert.True(category.Count is null || category.Count >= group["count"]!.GetValue<int>());
        }

        if (!truncated)
        {
            foreach (var (name, (categoryStatus, count)) in counts)
            {
                if (count is { } total && categoryStatus is "available" or "partial")
                {
                    Assert.Equal(total, groups.Where(group => group!["category"]!.GetValue<string>() == name).Sum(group => group!["count"]!.GetValue<int>()));
                }
            }
        }

        var context = root["context"]!.AsObject();
        Assert.Equal(["applicability", "status", "detail", "boots", "cleanShutdowns"], context.Select(pair => pair.Key).ToArray());
        if (context["applicability"]!.GetValue<string>() != "applicable" || context["status"]?.GetValue<string>() == "unavailable")
        {
            Assert.Null(context["boots"]);
            Assert.Null(context["cleanShutdowns"]);
        }

        var minidumps = root["minidumps"]!.AsObject();
        Assert.Equal(["applicability", "status", "directory", "observed", "returned", "totalBytes", "files"], minidumps.Select(pair => pair.Key).ToArray());
        var files = minidumps["files"]!.AsArray();
        Assert.InRange(files.Count, 0, 16);
        switch (minidumps["applicability"]!.GetValue<string>(), minidumps["status"]?.GetValue<string>())
        {
            case ("notCollected", _):
                Assert.Null(minidumps["status"]);
                Assert.Null(minidumps["observed"]);
                Assert.Null(minidumps["returned"]);
                Assert.Null(minidumps["totalBytes"]);
                Assert.Empty(files);
                break;
            case ("applicable", "unavailable"):
                Assert.Null(minidumps["observed"]);
                Assert.Null(minidumps["returned"]);
                Assert.Null(minidumps["totalBytes"]);
                Assert.Empty(files);
                Assert.Null(counts["minidump"].Count);
                break;
            case ("applicable", "notApplicable"):
                Assert.Equal(0, minidumps["observed"]!.GetValue<int>());
                Assert.Equal(0, counts["minidump"].Count);
                break;
            default:
                Assert.Equal(files.Count, minidumps["returned"]!.GetValue<int>());
                Assert.True(minidumps["observed"]!.GetValue<int>() >= files.Count);
                Assert.Equal(minidumps["observed"]!.GetValue<int>(), counts["minidump"].Count);
                foreach (var file in files)
                {
                    Assert.Equal(["name", "sizeBytes", "fileTimeUtc", "timestampKind", "fileNameLocalDate"], file!.AsObject().Select(pair => pair.Key).ToArray());
                    Assert.Equal("reported", file["timestampKind"]!.GetValue<string>());
                    Assert.EndsWith(".dmp", file["name"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
                }

                break;
        }

        return root;
    }

    /// <summary>
    /// Asserts the schema-2 <c>system.crashes</c> envelope of either mode: exact root and row/group property names, the strict
    /// <c>complete</c>, coverage, bounded size, and row and group invariants.
    /// </summary>
    public static JsonObject AssertCrashesEnvelope(string output, int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(output);
        Assert.True(Encoding.UTF8.GetByteCount(output) <= 65_536);
        var root = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(2, root["schemaVersion"]!.GetValue<int>());
        var mode = root["mode"]!.GetValue<string>();
        Assert.Equal(
            mode == "raw"
                ? ["schemaVersion", "mode", "status", "complete", "truncated", "window", "coverage", "observedItems", "returnedItems", "catalogAge", "sources", "warnings", "items"]
                : ["schemaVersion", "mode", "status", "complete", "truncated", "window", "coverage", "observedItems", "catalogAge", "sources", "warnings", "observedGroups", "returnedGroups", "groups"],
            root.Select(pair => pair.Key).ToArray());
        var status = root["status"]!.GetValue<string>();
        Assert.Contains(status, MaintenanceStatuses);
        var coverageState = AssertCoverage(root);
        Assert.Equal(status == "complete" && !root["truncated"]!.GetValue<bool>() && coverageState == "complete", root["complete"]!.GetValue<bool>());
        foreach (var source in root["sources"]!.AsArray())
        {
            Assert.Equal(["name", "status", "detail", "examinedFromUtc"], source!.AsObject().Select(pair => pair.Key).ToArray());
            Assert.NotEqual("windows.wer.reportarchive", source["name"]!.GetValue<string>());
            Assert.NotEqual("windows.wer.reportqueue", source["name"]!.GetValue<string>());
        }

        if (mode == "raw")
        {
            var items = root["items"]!.AsArray();
            Assert.InRange(items.Count, 0, maximumEntries);
            Assert.Equal(items.Count, root["returnedItems"]!.GetValue<int>());
            Assert.True(root["observedItems"]!.GetValue<int>() >= items.Count);
            foreach (var item in items)
            {
                Assert.Equal(
                    ["timestampUtc", "process", "pid", "kind", "dumpPath", "eventIdOrCrashId", "summary", "source",
                     "timestampKind", "reportedUtc", "reportId", "eventName", "code", "bugcheckCode", "liveDumpCode", "exceptionCode", "faultModule", "bucket", "evidenceSources"],
                    item!.AsObject().Select(pair => pair.Key).ToArray());
                Assert.Contains(item["timestampKind"]!.GetValue<string>(), TimestampKinds);
            }
        }
        else
        {
            var groups = root["groups"]!.AsArray();
            Assert.InRange(groups.Count, 0, maximumEntries);
            Assert.Equal(groups.Count, root["returnedGroups"]!.GetValue<int>());
            Assert.True(root["observedGroups"]!.GetValue<int>() >= groups.Count);
            foreach (var group in groups)
            {
                Assert.Equal(
                    ["kind", "eventName", "code", "application", "module", "timestampKind", "count", "uncorrelatedCount", "dumpReferenceCount", "firstSeenUtc", "lastSeenUtc", "evidenceSources"],
                    group!.AsObject().Select(pair => pair.Key).ToArray());
                var count = group["count"]!.GetValue<int>();
                Assert.True(count >= 1);
                Assert.InRange(group["uncorrelatedCount"]!.GetValue<int>(), 0, count);
                Assert.InRange(group["dumpReferenceCount"]!.GetValue<int>(), 0, count);
                Assert.True(group["evidenceSources"]!.AsArray().Count <= 8);
                Assert.Contains(group["timestampKind"]!.GetValue<string>(), TimestampKinds);
                if (group["kind"]?.GetValue<string>() != "wer")
                {
                    Assert.Null(group["eventName"]);
                }
            }
        }

        return root;
    }

    private static (double?, double?) Bounds(ToolManifest manifest, string name)
    {
        var parameter = manifest.Parameters.Single(candidate => candidate.Name == name);
        return (parameter.Minimum, parameter.Maximum);
    }
}
