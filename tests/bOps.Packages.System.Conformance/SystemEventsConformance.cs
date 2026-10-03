// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Xunit;

namespace bOps.Packages.Sys.Conformance;

/// <summary>The cross-platform conformance suite for <c>system.events</c> (ADR-0032).</summary>
public static partial class SystemToolConformance
{
    private static readonly string[] EventSeverities = ["critical", "error", "warning", "information", "verbose", "unknown"];
    private static readonly string[] CoverageStates = ["complete", "partial", "unknown"];
    private static readonly string[] CoverageBases = ["eventLog", "journal", "directory"];

    /// <summary>The parameters both operating systems must declare, in order, all optional.</summary>
    public static readonly IReadOnlyList<(string Name, ToolParameterType Type)> SystemEventsParameters =
    [
        ("windowMinutes", ToolParameterType.Integer),
        ("minSeverity", ToolParameterType.Enum),
        ("source", ToolParameterType.String),
        ("eventId", ToolParameterType.String),
        ("channel", ToolParameterType.String),
        ("text", ToolParameterType.String),
        ("excludeSources", ToolParameterType.String),
        ("limit", ToolParameterType.Integer),
        ("maxOutputBytes", ToolParameterType.Integer),
        ("mode", ToolParameterType.Enum),
        ("windowDays", ToolParameterType.Integer),
    ];

    /// <summary>
    /// Runs <paramref name="tool"/> against the real operating system and asserts the manifest, the argument checks and the normalized
    /// result shape that Windows and Linux must share.
    /// </summary>
    public static async Task AssertSystemEventsConformAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);
        AssertSystemEventsManifest(tool.Manifest, platform);

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject
        {
            ["windowMinutes"] = 1_440,
            ["limit"] = 5,
            ["maxOutputBytes"] = 8_192,
        }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= 8_192);
        AssertSystemEventsEnvelope(result.Output!, maximumEvents: 5);
    }

    /// <summary>Asserts that a manifest is the shared <c>system.events</c> contract for <paramref name="platform"/>.</summary>
    public static void AssertSystemEventsManifest(ToolManifest manifest, string platform)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        AssertManifestIsWellFormed(manifest, platform, "system.events");

        Assert.Equal(
            SystemEventsParameters,
            manifest.Parameters.Select(parameter => (parameter.Name, parameter.Type)).ToArray());
        Assert.All(manifest.Parameters, parameter => Assert.False(parameter.Required));
        Assert.All(manifest.Parameters, parameter => Assert.False(parameter.Sensitive));
        Assert.Equal(
            ["critical", "error", "warning", "information", "verbose"],
            manifest.Parameters.Single(parameter => parameter.Name == "minSeverity").AllowedValues);
        Assert.Equal(["raw", "aggregate"], manifest.Parameters.Single(parameter => parameter.Name == "mode").AllowedValues);
        var days = manifest.Parameters.Single(parameter => parameter.Name == "windowDays");
        Assert.Equal((1d, 180d), (days.Minimum, days.Maximum));
        var minutes = manifest.Parameters.Single(parameter => parameter.Name == "windowMinutes");
        Assert.Equal((1d, 10080d), (minutes.Minimum, minutes.Maximum));
        var excluded = manifest.Parameters.Single(parameter => parameter.Name == "excludeSources");
        Assert.Equal(ToolParameterType.String, excluded.Type);
        Assert.Equal((1, 1_024), (excluded.MinLength, excluded.MaxLength));
        Assert.Contains("not evidence of absence", excluded.Description, StringComparison.Ordinal);
    }

    /// <summary>Asserts the rejection of arguments a bounded event reader must never accept, on the real tool.</summary>
    public static async Task AssertSystemEventsRejectBadArgumentsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        foreach (var (name, value) in new (string, JsonNode)[]
        {
            ("windowMinutes", 0),
            ("windowMinutes", 99_999),
            ("limit", 0),
            ("limit", 501),
            ("maxOutputBytes", 10),
            ("minSeverity", "fatal"),
            ("source", "x' or '1'='1"),
            ("channel", "System' or '1'='1"),
            ("text", "a\nb"),
            ("mode", "summary"),
            ("excludeSources", ""),
            ("excludeSources", "a,,b"),
            ("excludeSources", "a,A"),
            ("excludeSources", "x' or '1'='1"),
            ("excludeSources", "a,b,c,d,e,f,g,h,i"),
            ("excludeSources", 7),
        })
        {
            var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { [name] = value }));

            Assert.Equal(ToolOutcome.Failure, result.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        // The two cross-field rules, in their fixed order (ADR-0032 HARDEN-7 amendment §2), before any collection.
        var both = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["mode"] = "raw", ["windowMinutes"] = 60, ["windowDays"] = 2 }));
        Assert.Equal("Use either windowMinutes or windowDays, not both.", both.ErrorMessage);
        Assert.Equal(ToolFailureKind.Validation, both.FailureKind);
        foreach (var raw in new[] { new JsonObject { ["windowDays"] = 2 }, new JsonObject { ["mode"] = "raw", ["windowDays"] = 2 } })
        {
            var dayInRaw = await tool.ExecuteAsync(ToolArguments.FromJson(raw));
            Assert.Equal("windowDays applies only to mode aggregate; raw mode is limited to windowMinutes up to 10080 (7 days).", dayInRaw.ErrorMessage);
        }

        var tooLong = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["mode"] = "aggregate", ["windowDays"] = 181 }));
        Assert.Equal("windowDays must be between 1 and 180.", tooLong.ErrorMessage);
    }

    /// <summary>
    /// Runs <paramref name="tool"/> in aggregate mode over a long horizon against the real operating system and asserts the shared
    /// aggregate envelope: groups instead of rows, within the byte budget, with coverage.
    /// </summary>
    public static async Task AssertSystemEventsAggregateConformsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["mode"] = "aggregate", ["windowDays"] = 30, ["limit"] = 20, ["maxOutputBytes"] = 16_384 }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= 16_384);
        var root = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal(2, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal("aggregate", root["mode"]!.GetValue<string>());
        Assert.Null(root["events"]);
        var coverageState = AssertCoverage(root);
        var status = root["status"]!.GetValue<string>();
        Assert.Equal(status == "complete" && !root["truncated"]!.GetValue<bool>() && coverageState == "complete", root["complete"]!.GetValue<bool>());
        var groups = root["groups"]!.AsArray();
        Assert.Equal(groups.Count, root["returnedGroups"]!.GetValue<int>());
        Assert.InRange(groups.Count, 0, 20);
        Assert.True(root["observedGroups"]!.GetValue<int>() >= groups.Count);
        var total = 0;
        foreach (var group in groups)
        {
            Assert.Equal(
                ["channel", "source", "unit", "eventId", "severity", "count", "firstSeenUtc", "lastSeenUtc", "sampleMessage", "sampleMessageTruncated"],
                group!.AsObject().Select(pair => pair.Key).ToArray());
            Assert.True(group["count"]!.GetValue<int>() >= 1);
            Assert.True(group["sampleMessage"]!.GetValue<string>().Length <= 512);
            Assert.True(DateTimeOffset.Parse(group["firstSeenUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture) <= DateTimeOffset.Parse(group["lastSeenUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture));
            total += group["count"]!.GetValue<int>();
        }

        Assert.True(root["observedEvents"]!.GetValue<int>() >= total);
    }

    /// <summary>
    /// Runs <paramref name="tool"/> against the real operating system with <c>excludeSources</c> (ADR-0032 HARDEN-9 amendment): the most
    /// frequent source of a first read is excluded, the echo is exactly the accepted value, no returned row or group carries the excluded
    /// source (ignoring case), an unknown but valid name is accepted and echoed, and a legacy call without the argument still works in
    /// both modes. Returns the excluded source, or <c>null</c> when the host logged nothing in the window (nothing to exclude).
    /// </summary>
    public static async Task<string?> AssertSystemEventsExcludeSourcesConformAsync(ITool tool, int windowDays = 7)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var legacy = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["mode"] = "aggregate", ["windowDays"] = windowDays, ["limit"] = 200, ["maxOutputBytes"] = 65_536 }));
        Assert.True(legacy.Succeeded, legacy.ErrorMessage);
        var baseline = JsonNode.Parse(legacy.Output!)!.AsObject();
        Assert.Empty(baseline["excludeSources"]!.AsArray());
        var groups = baseline["groups"]!.AsArray();
        if (groups.Count == 0)
        {
            return null;
        }

        var noisiest = groups
            .GroupBy(group => group!["source"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(item => item!["count"]!.GetValue<int>()))
            .First();
        var excludedSource = noisiest.Key;
        var excludedCount = noisiest.Sum(item => item!["count"]!.GetValue<int>());

        const string unknown = "Bops Nonexistent Source (conformance)";
        foreach (var mode in new[] { "aggregate", "raw" })
        {
            var arguments = new JsonObject
            {
                ["mode"] = mode,
                ["limit"] = 200,
                ["maxOutputBytes"] = 65_536,
                ["excludeSources"] = unknown + " , " + excludedSource,
            };
            if (mode == "aggregate")
            {
                arguments["windowDays"] = windowDays;
            }
            else
            {
                arguments["windowMinutes"] = windowDays * 1_440;
            }

            var result = await tool.ExecuteAsync(ToolArguments.FromJson(arguments));
            Assert.True(result.Succeeded, result.ErrorMessage);
            var root = JsonNode.Parse(result.Output!)!.AsObject();

            // The echo is the accepted entries, trimmed, in the caller's spelling and sorted ignoring case then ordinally.
            Assert.Equal(
                new[] { unknown, excludedSource }.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal),
                root["excludeSources"]!.AsArray().Select(item => item!.GetValue<string>()));

            var entries = (mode == "aggregate" ? root["groups"] : root["events"])!.AsArray();
            Assert.All(entries, entry => Assert.NotEqual(excludedSource, entry!["source"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase));
            Assert.True(root["status"]!.GetValue<string>() is "complete" or "partial" or "unavailable");
        }

        Assert.True(excludedCount > 0);
        return excludedSource;
    }

    /// <summary>Asserts the shared <c>coverage</c> object (ADR-0032 HARDEN-7 amendment §5) and returns its state.</summary>
    public static string AssertCoverage(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var coverage = root["coverage"]!.AsObject();
        Assert.Equal(["requestedFromUtc", "requestedToUtc", "state", "stores"], coverage.Select(pair => pair.Key).ToArray());
        Assert.Equal(root["window"]!["fromUtc"]!.GetValue<string>(), coverage["requestedFromUtc"]!.GetValue<string>());
        Assert.Equal(root["window"]!["toUtc"]!.GetValue<string>(), coverage["requestedToUtc"]!.GetValue<string>());
        var state = coverage["state"]!.GetValue<string>();
        Assert.Contains(state, CoverageStates);
        var from = DateTimeOffset.Parse(coverage["requestedFromUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var retentionStates = new List<string>();
        foreach (var store in coverage["stores"]!.AsArray())
        {
            Assert.Equal(["name", "basis", "oldestAvailableUtc", "logMaximumBytes", "state"], store!.AsObject().Select(pair => pair.Key).ToArray());
            var basis = store["basis"]!.GetValue<string>();
            Assert.Contains(basis, CoverageBases);
            var storeState = store["state"]!.GetValue<string>();
            var oldest = store["oldestAvailableUtc"]?.GetValue<string>();
            if (basis == "directory")
            {
                // A directory carries no retention guarantee: always unknown, and it never moves the global state.
                Assert.Equal("unknown", storeState);
                Assert.Null(store["logMaximumBytes"]);
                continue;
            }

            if (basis == "journal")
            {
                Assert.Null(store["logMaximumBytes"]);
            }

            var expected = oldest is null ? "unknown" : DateTimeOffset.Parse(oldest, CultureInfo.InvariantCulture) <= from ? "complete" : "partial";
            Assert.Equal(expected, storeState);
            retentionStates.Add(storeState);
        }

        var expectedState = retentionStates.Contains("partial") ? "partial" : retentionStates.Count == 0 || retentionStates.Contains("unknown") ? "unknown" : "complete";
        Assert.Equal(expectedState, state);
        return state;
    }

    /// <summary>Asserts the JSON envelope of a <c>system.events</c> result.</summary>
    public static JsonObject AssertSystemEventsEnvelope(string output, int maximumEvents)
    {
        var root = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(2, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal("raw", root["mode"]!.GetValue<string>());
        var status = root["status"]!.GetValue<string>();
        Assert.Contains(status, InventoryStatuses);
        var coverageState = AssertCoverage(root);

        var from = DateTimeOffset.Parse(root["window"]!["fromUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(root["window"]!["toUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        Assert.True(from < to);

        var observed = root["observedEvents"]!.GetValue<int>();
        var returned = root["returnedEvents"]!.GetValue<int>();
        var truncated = root["truncated"]!.GetValue<bool>();
        var complete = root["complete"]!.GetValue<bool>();
        Assert.InRange(returned, 0, maximumEvents);
        Assert.True(observed >= returned);
        Assert.Equal(status == "complete" && !truncated && coverageState == "complete", complete);
        if (returned < observed)
        {
            Assert.True(truncated);
        }

        // ADR-0032 HARDEN-9 amendment §4: the exclusion echo is always present, right after the window.
        var keys = root.Select(pair => pair.Key).ToList();
        Assert.Equal(keys.IndexOf("window") + 1, keys.IndexOf("excludeSources"));
        Assert.Empty(root["excludeSources"]!.AsArray());

        var sources = root["sources"]!.AsArray();
        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            Assert.False(string.IsNullOrWhiteSpace(source!["name"]!.GetValue<string>()));
            Assert.Contains(source["status"]!.GetValue<string>(), InventorySourceStatuses);
            Assert.True(source.AsObject().ContainsKey("detail"));
            Assert.True(source.AsObject().ContainsKey("examinedFromUtc"));
        }

        var events = root["events"]!.AsArray();
        Assert.Equal(returned, events.Count);
        var previous = DateTimeOffset.MaxValue;
        foreach (var item in events)
        {
            var record = item!.AsObject();
            Assert.Equal(
                ["timestampUtc", "severity", "source", "unit", "eventId", "channel", "message", "messageTruncated", "processId", "processName"],
                record.Select(pair => pair.Key).ToArray());

            var timestamp = DateTimeOffset.Parse(record["timestampUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            Assert.InRange(timestamp, from, to);
            Assert.True(timestamp <= previous, "Events must be newest first.");
            previous = timestamp;

            Assert.Contains(record["severity"]!.GetValue<string>(), EventSeverities);
            Assert.False(string.IsNullOrWhiteSpace(record["source"]!.GetValue<string>()));
            Assert.NotNull(record["message"]);
            Assert.True(record["message"]!.GetValue<string>().Length <= 2_000);
        }

        return root;
    }
}
