// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The shared tool shell for <c>system.events</c> (ADR-0032): manifest, argument validation, ordering, aggregation, coverage, bounds
/// and output are written once here, and each OS package implements only collection. The tool also contributes an aggregate audit
/// summary that never carries an event message.
/// </summary>
public abstract class SystemEventsToolBase : IToolAuditSummaryProvider
{
    private readonly TimeProvider clock;

    /// <summary>Creates the shell for <paramref name="platform"/>; <paramref name="clock"/> defaults to the system clock.</summary>
    protected SystemEventsToolBase(string platform, TimeProvider? clock = null)
    {
        Manifest = SystemToolManifests.Events(platform);
        this.clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ToolManifest Manifest { get; }

    /// <summary>The clock the window and the call's time budget are measured on.</summary>
    protected TimeProvider Clock => clock;

    /// <summary>
    /// Checks an <c>eventId</c> against this platform's shape (a number on Windows, a 32-digit hexadecimal id on Linux).
    /// Returns an explanation, or <c>null</c> when the value is acceptable.
    /// </summary>
    protected abstract string? ValidateEventId(string eventId);

    /// <summary>
    /// Checks a <c>channel</c> against this platform's shape. Returns an explanation, or <c>null</c> when the value is acceptable.
    /// </summary>
    protected abstract string? ValidateChannel(string channel);

    /// <summary>
    /// Reads the events <paramref name="query"/> asks for, and the coverage of the stores behind them. A source that cannot be read is
    /// reported in the snapshot, never left out, and cancellation must stop the native enumeration promptly.
    /// </summary>
    protected abstract Task<SystemEventSnapshot> CollectAsync(SystemEventQuery query, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!SystemEventsArguments.TryRead(arguments, clock.GetUtcNow(), out var query, out var mode, out var limit, out var maxOutputBytes, out var error))
        {
            return ToolCallResult.Failure(error!) with { FailureKind = ToolFailureKind.Validation };
        }

        var platformError = query!.EventId is { } eventId ? ValidateEventId(eventId) : null;
        platformError ??= query.Channel is { } channel ? ValidateChannel(channel) : null;
        if (platformError is not null)
        {
            return ToolCallResult.Failure(platformError) with { FailureKind = ToolFailureKind.Validation };
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(query, ct);
        return EvidenceCompleteness.Success(SystemEventFormatting.Format(snapshot, query, mode, limit, maxOutputBytes));
    }

    /// <inheritdoc />
    public JsonObject? CreateAuditSummary(ToolArguments arguments, ToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded || result.Output is null)
        {
            return null;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(result.Output)!.AsObject();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }

        var aggregate = root["groups"] is JsonArray;
        var entries = (aggregate ? root["groups"] : root["events"])!.AsArray();
        var bySeverity = new JsonObject();
        foreach (var group in entries
                     .GroupBy(item => item!["severity"]!.GetValue<string>())
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            bySeverity[group.Key] = aggregate ? group.Sum(item => item!["count"]!.GetValue<int>()) : group.Count();
        }

        var summary = new JsonObject
        {
            ["mode"] = root["mode"]?.GetValue<string>(),
            ["status"] = root["status"]!.GetValue<string>(),
            ["complete"] = root["complete"]!.GetValue<bool>(),
            ["truncated"] = root["truncated"]!.GetValue<bool>(),
            ["observedEvents"] = root["observedEvents"]!.GetValue<int>(),
        };
        if (aggregate)
        {
            summary["observedGroups"] = root["observedGroups"]!.GetValue<int>();
            summary["returnedGroups"] = root["returnedGroups"]!.GetValue<int>();
        }
        else
        {
            summary["returnedEvents"] = root["returnedEvents"]!.GetValue<int>();
        }

        summary["bySeverity"] = bySeverity;
        summary["sources"] = EvidenceAudit.Sources(root);
        EvidenceAudit.AddCoverage(summary, root);
        return summary;
    }
}

/// <summary>The parts of an evidence-tool audit summary every evidence tool shares; never a message, name, path or identifier.</summary>
internal static class EvidenceAudit
{
    internal static JsonArray Sources(JsonObject root) =>
        new(root["sources"]!.AsArray().Select(source => (JsonNode)new JsonObject
        {
            ["name"] = source!["name"]!.GetValue<string>(),
            ["status"] = source["status"]!.GetValue<string>(),
        }).ToArray());

    internal static void AddCoverage(JsonObject summary, JsonObject root)
    {
        if (root["coverage"] is not JsonObject coverage)
        {
            return;
        }

        summary["coverageState"] = coverage["state"]!.GetValue<string>();
        summary["stores"] = new JsonArray(coverage["stores"]!.AsArray().Select(store => (JsonNode)new JsonObject
        {
            ["name"] = store!["name"]!.GetValue<string>(),
            ["state"] = store["state"]!.GetValue<string>(),
            ["oldestAvailableUtc"] = store["oldestAvailableUtc"]?.GetValue<string>(),
        }).ToArray());
    }
}
