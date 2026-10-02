// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The shared tool shell for <c>system.stability</c> (ADR-0041): manifest, argument reading, the category table, aggregation,
/// bucketing, coverage, ordering, bounds, JSON output and the audit summary are written once here; each OS package implements only
/// collection, and every provider name, event id, journal match and message pattern is a constant of that package.
/// </summary>
public abstract class SystemStabilityToolBase : IToolAuditSummaryProvider
{
    private readonly TimeProvider clock;

    /// <summary>Creates the shell for <paramref name="platform"/>; <paramref name="clock"/> defaults to the system clock.</summary>
    protected SystemStabilityToolBase(string platform, TimeProvider? clock = null)
    {
        Manifest = SystemToolManifests.Stability(platform);
        this.clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ToolManifest Manifest { get; }

    /// <summary>The clock the window and the call's time budget are measured on.</summary>
    protected TimeProvider Clock => clock;

    /// <summary>
    /// Reads the stability evidence of the window of <paramref name="query"/> within the shared time bound
    /// (<see cref="StabilityLimits.CallTimeout"/>). A source that cannot be read is reported, never left out.
    /// </summary>
    protected abstract Task<StabilitySnapshot> CollectAsync(StabilityQuery query, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!StabilityArguments.TryRead(arguments, clock.GetUtcNow(), out var query, out var error))
        {
            return ToolCallResult.Failure(error!) with { FailureKind = ToolFailureKind.Validation };
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(query!, ct).ConfigureAwait(false);
        return EvidenceCompleteness.Success(StabilityFormatting.Format(snapshot, query!));
    }

    /// <inheritdoc />
    public JsonObject? CreateAuditSummary(ToolArguments arguments, ToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(arguments);
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

        // Never a group component, a minidump file name or directory, a message or any native field (ADR-0041 §11).
        var summary = new JsonObject
        {
            ["status"] = root["status"]!.GetValue<string>(),
            ["complete"] = root["complete"]!.GetValue<bool>(),
            ["truncated"] = root["truncated"]!.GetValue<bool>(),
            ["windowDays"] = StabilityArguments.TryRead(arguments, clock.GetUtcNow(), out var query, out _) ? query!.WindowDays : null,
            ["categories"] = new JsonArray(root["categories"]!.AsArray().Select(category => (JsonNode)new JsonObject
            {
                ["category"] = category!["category"]!.GetValue<string>(),
                ["applicability"] = category["applicability"]!.GetValue<string>(),
                ["status"] = category["status"]?.GetValue<string>(),
                ["count"] = category["count"]?.GetValue<int>(),
            }).ToArray()),
            ["context"] = new JsonObject
            {
                ["boots"] = root["context"]!["boots"]?.GetValue<int>(),
                ["cleanShutdowns"] = root["context"]!["cleanShutdowns"]?.GetValue<int>(),
            },
            ["sources"] = EvidenceAudit.Sources(root),
        };
        EvidenceAudit.AddCoverage(summary, root);
        return summary;
    }
}
