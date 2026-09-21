// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The shared tool shell for <c>system.events</c> (ADR-0032): manifest, argument validation, ordering, bounds and output are
/// written once here, and each OS package implements only collection. The tool also contributes an aggregate audit summary that
/// never carries an event message.
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
    /// Reads the events <paramref name="query"/> asks for. A source that cannot be read is reported in the snapshot, never left out,
    /// and cancellation must stop the native enumeration promptly.
    /// </summary>
    protected abstract Task<SystemEventSnapshot> CollectAsync(SystemEventQuery query, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!SystemEventsArguments.TryRead(arguments, clock.GetUtcNow(), out var query, out var limit, out var maxOutputBytes, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        var platformError = query!.EventId is { } eventId ? ValidateEventId(eventId) : null;
        platformError ??= query.Channel is { } channel ? ValidateChannel(channel) : null;
        if (platformError is not null)
        {
            return ToolCallResult.Failure(platformError);
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(query, ct);
        return ToolCallResult.Success(SystemEventFormatting.Format(snapshot, query, limit, maxOutputBytes));
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

        var bySeverity = new JsonObject();
        foreach (var group in root["events"]!.AsArray().GroupBy(item => item!["severity"]!.GetValue<string>()).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            bySeverity[group.Key] = group.Count();
        }

        return new JsonObject
        {
            ["status"] = root["status"]!.GetValue<string>(),
            ["complete"] = root["complete"]!.GetValue<bool>(),
            ["truncated"] = root["truncated"]!.GetValue<bool>(),
            ["observedEvents"] = root["observedEvents"]!.GetValue<int>(),
            ["returnedEvents"] = root["returnedEvents"]!.GetValue<int>(),
            ["bySeverity"] = bySeverity,
            ["sources"] = new JsonArray(root["sources"]!.AsArray().Select(source => (JsonNode)new JsonObject
            {
                ["name"] = source!["name"]!.GetValue<string>(),
                ["status"] = source["status"]!.GetValue<string>(),
            }).ToArray()),
        };
    }
}
