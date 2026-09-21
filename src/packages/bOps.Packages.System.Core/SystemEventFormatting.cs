// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>Creates the bounded, deterministic JSON output of <c>system.events</c> (ADR-0032).</summary>
public static class SystemEventFormatting
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>The wire name of a severity.</summary>
    public static string ToWireValue(SystemEventSeverity severity) => severity switch
    {
        SystemEventSeverity.Critical => "critical",
        SystemEventSeverity.Error => "error",
        SystemEventSeverity.Warning => "warning",
        SystemEventSeverity.Information => "information",
        SystemEventSeverity.Verbose => "verbose",
        SystemEventSeverity.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown event severity."),
    };

    /// <summary>
    /// Selects the events <paramref name="query"/> asks for from <paramref name="snapshot"/>, orders them newest first, applies the
    /// event and UTF-8 byte bounds and states, explicitly, whether the answer is complete.
    /// </summary>
    public static string Format(SystemEventSnapshot snapshot, SystemEventQuery query, int limit, int maxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);

        var observed = snapshot.Events
            .Where(record => IsValid(record) && SystemEventFilter.Matches(record, query))
            .OrderByDescending(record => record.TimestampUtc)
            .ThenBy(record => record.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Source, StringComparer.Ordinal)
            .ThenBy(record => record.Channel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.EventId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Message, StringComparer.Ordinal)
            .ThenBy(record => record.ProcessId)
            .ToArray();
        var selected = observed.Take(limit).Select(CreateEvent).ToList();
        var status = SystemInventoryFormatting.GetStatus(snapshot.Sources);
        var truncated = snapshot.CollectionTruncated || selected.Count < observed.Length;

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = status,
            ["complete"] = status == "complete" && !truncated,
            ["window"] = new JsonObject
            {
                ["fromUtc"] = query.FromUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                ["toUtc"] = query.ToUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            },
            ["observedEvents"] = observed.Length,
            ["returnedEvents"] = selected.Count,
            ["truncated"] = truncated,
            ["sources"] = new JsonArray(snapshot.Sources
                .OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(source => source.Name, StringComparer.Ordinal)
                .Select(source => (JsonNode)new JsonObject
                {
                    ["name"] = SystemInventoryFormatting.Bounded(source.Name, 128),
                    ["status"] = SystemInventoryFormatting.ToWireValue(source.Status),
                    ["detail"] = SystemInventoryFormatting.Bounded(source.Detail, 256),
                })
                .ToArray()),
            ["events"] = new JsonArray(selected.Select(item => (JsonNode)item).ToArray()),
        };

        var output = root.ToJsonString();
        var eventArray = root["events"]!.AsArray();
        while (Encoding.UTF8.GetByteCount(output) > maxOutputBytes && eventArray.Count > 0)
        {
            eventArray.RemoveAt(eventArray.Count - 1);
            root["returnedEvents"] = eventArray.Count;
            root["truncated"] = true;
            root["complete"] = false;
            output = root.ToJsonString();
        }

        if (Encoding.UTF8.GetByteCount(output) > maxOutputBytes)
        {
            throw new InvalidOperationException(
                $"Event metadata exceeds the configured {maxOutputBytes}-byte output limit.");
        }

        return output;
    }

    private static JsonObject CreateEvent(SystemEventRecord record)
    {
        var messageTruncated = record.Message.Length > SystemEventsLimits.MessageCharacters;
        return new JsonObject
        {
            ["timestampUtc"] = record.TimestampUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            ["severity"] = ToWireValue(record.Severity),
            ["source"] = SystemInventoryFormatting.Bounded(record.Source, 256),
            ["unit"] = SystemInventoryFormatting.Bounded(record.Unit, 256),
            ["eventId"] = SystemInventoryFormatting.Bounded(record.EventId, 128),
            ["channel"] = SystemInventoryFormatting.Bounded(record.Channel, 256),
            ["message"] = SystemInventoryFormatting.Bounded(record.Message, SystemEventsLimits.MessageCharacters),
            ["messageTruncated"] = messageTruncated,
            ["processId"] = record.ProcessId,
            ["processName"] = SystemInventoryFormatting.Bounded(record.ProcessName, 256),
        };
    }

    private static bool IsValid(SystemEventRecord record) =>
        !string.IsNullOrWhiteSpace(record.Source) && record.Message is not null;
}
