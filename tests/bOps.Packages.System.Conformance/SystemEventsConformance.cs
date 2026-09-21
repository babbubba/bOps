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

    /// <summary>The parameters both operating systems must declare, in order, all optional.</summary>
    public static readonly IReadOnlyList<(string Name, ToolParameterType Type)> SystemEventsParameters =
    [
        ("windowMinutes", ToolParameterType.Integer),
        ("minSeverity", ToolParameterType.Enum),
        ("source", ToolParameterType.String),
        ("eventId", ToolParameterType.String),
        ("channel", ToolParameterType.String),
        ("text", ToolParameterType.String),
        ("limit", ToolParameterType.Integer),
        ("maxOutputBytes", ToolParameterType.Integer),
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
        })
        {
            var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { [name] = value }));

            Assert.Equal(ToolOutcome.Failure, result.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
    }

    /// <summary>Asserts the JSON envelope of a <c>system.events</c> result.</summary>
    public static JsonObject AssertSystemEventsEnvelope(string output, int maximumEvents)
    {
        var root = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
        var status = root["status"]!.GetValue<string>();
        Assert.Contains(status, InventoryStatuses);

        var from = DateTimeOffset.Parse(root["window"]!["fromUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(root["window"]!["toUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        Assert.True(from < to);

        var observed = root["observedEvents"]!.GetValue<int>();
        var returned = root["returnedEvents"]!.GetValue<int>();
        var truncated = root["truncated"]!.GetValue<bool>();
        var complete = root["complete"]!.GetValue<bool>();
        Assert.InRange(returned, 0, maximumEvents);
        Assert.True(observed >= returned);
        Assert.Equal(status == "complete" && !truncated, complete);
        if (returned < observed)
        {
            Assert.True(truncated);
        }

        var sources = root["sources"]!.AsArray();
        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            Assert.False(string.IsNullOrWhiteSpace(source!["name"]!.GetValue<string>()));
            Assert.Contains(source["status"]!.GetValue<string>(), InventorySourceStatuses);
            Assert.True(source.AsObject().ContainsKey("detail"));
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
