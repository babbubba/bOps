// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Policy;
using bOps.Runtime;

namespace bOps.ReplayFixtureCapture;

internal sealed record PreflightQuery(
    QuerySpec Spec, string ManifestSha256, bool ReadOnlyRisk, string? ValidationError, string PolicyMode, string PolicyReason)
{
    public bool Ok => ReadOnlyRisk && ValidationError is null && PolicyMode == nameof(bOps.Abstractions.PolicyMode.Automatic);
}

/// <summary>What is checked about the plan BEFORE any collector exists: manifest risk, the runtime's argument validation, the read-only policy.</summary>
internal static class Preflight
{
    private static readonly PackageId SystemPackage = new("bops.packages.system.windows");

    public static IReadOnlyList<PreflightQuery> Run(IReadOnlyList<QuerySpec> selected, IManifestSource manifests, IPolicyEngine policy)
    {
        var result = new List<PreflightQuery>();
        foreach (var spec in selected)
        {
            var arguments = spec.Arguments;
            CapturePlan.Authorise(selected, spec.Id, spec.Tool, arguments);
            var manifest = manifests.ManifestOf(spec.Tool);
            var toolArguments = ToolArguments.FromJson(arguments);
            var violation = ArgumentSchema.Validate(manifest.Parameters, toolArguments)?.Message;
            var decision = policy.Evaluate(new PolicyContext(NodeId.Local, SystemPackage, PackageTrustLevel.Official, manifest, toolArguments,
                ActorIdentity.FromOperatingSystemUser("replay-fixture-capture")));
            result.Add(new PreflightQuery(spec, ManifestFingerprint.Of(manifest), manifest.Risk == RiskLevel.Read, violation,
                decision.Mode.ToString(), decision.Reason));
        }

        return result;
    }
}

/// <summary>The facts read from one captured result, used for the expectations, the temporal report and the index.</summary>
internal sealed record ResultFacts(
    string? Status, bool? Complete, bool? Truncated, int SchemaVersion, DateTimeOffset? WindowFrom, DateTimeOffset? WindowTo,
    string? Mode, int SourcesAvailable, int SourcesTotal, long? Observed, long? Returned, string? MessageDistributionJson, int OutputBytes);

internal static class ResultChecks
{
    private static readonly (string Tool, string? Mode, string Observed, string Returned)[] CountFields =
    [
        ("system.events", "aggregate", "observedGroups", "returnedGroups"),
        ("system.events", "raw", "observedEvents", "returnedEvents"),
        ("system.crashes", "aggregate", "observedGroups", "returnedGroups"),
        ("system.crashes", "raw", "observedItems", "returnedItems"),
        ("system.stability", null, "observedGroups", "returnedGroups"),
    ];

    public static ResultFacts? Read(QuerySpec spec, ToolCallResult result)
    {
        if (string.IsNullOrEmpty(result.Output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? Text(string name) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            bool? Flag(string name) => root.TryGetProperty(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
            DateTimeOffset? Time(JsonElement parent, string name) =>
                parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                    ? t : null;
            long? Number(string name) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) ? n : null;

            var window = root.TryGetProperty("window", out var w) ? w : default;
            var available = 0;
            var total = 0;
            if (root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sources.EnumerateArray())
                {
                    total++;
                    if (source.TryGetProperty("status", out var s) && s.GetString() == "available")
                    {
                        available++;
                    }
                }
            }

            var mode = spec.EffectiveMode;
            var fields = CountFields.FirstOrDefault(f => f.Tool == spec.Tool && f.Mode == mode);
            return new ResultFacts(
                Text("status"), Flag("complete"), Flag("truncated"),
                root.TryGetProperty("schemaVersion", out var sv) && sv.TryGetInt32(out var v) ? v : -1,
                Time(window, "fromUtc"), Time(window, "toUtc"), Text("mode"), available, total,
                fields.Observed is null ? null : Number(fields.Observed), fields.Returned is null ? null : Number(fields.Returned),
                root.TryGetProperty("messageDistribution", out var md) ? md.GetRawText() : null,
                Encoding.UTF8.GetByteCount(result.Output));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every way in which a result is not what the plan expects. Empty means the result may be kept.</summary>
    public static IReadOnlyList<string> Violations(QuerySpec spec, ToolCallResult result, ResultFacts? facts, DateTimeOffset started, DateTimeOffset ended)
    {
        var problems = new List<string>();
        if (result.Outcome != ToolOutcome.Success)
        {
            problems.Add($"outcome {result.Outcome} ({result.FailureKind}): {result.ErrorMessage}");
        }

        if (facts is null)
        {
            problems.Add("the output is missing or is not a JSON object");
            return problems;
        }

        var expect = spec.Expect;
        if (facts.SchemaVersion != expect.SchemaVersion)
        {
            problems.Add($"schemaVersion {facts.SchemaVersion}, expected {expect.SchemaVersion}");
        }

        if (facts.Status is null || !expect.AllowedStatuses.Contains(facts.Status, StringComparer.Ordinal))
        {
            problems.Add($"status '{facts.Status}' is not one of: {string.Join(", ", expect.AllowedStatuses)}");
        }

        if (!expect.AllowedCompleteness.Contains(result.Completeness))
        {
            problems.Add($"completeness {result.Completeness} is not one of: {string.Join(", ", expect.AllowedCompleteness)}");
        }

        if (facts.Truncated is null)
        {
            problems.Add("the result does not state 'truncated'");
        }
        else if (facts.Truncated == true && expect.Truncation == TruncationPolicy.MustBeFalse)
        {
            problems.Add("the result is truncated, which this query does not expect");
        }

        if (expect.RequireAllSourcesAvailable && (facts.SourcesTotal == 0 || facts.SourcesAvailable != facts.SourcesTotal))
        {
            problems.Add($"{facts.SourcesAvailable} of {facts.SourcesTotal} sources are available; all are required");
        }

        if (spec.EffectiveMode is { } mode && facts.Mode != mode)
        {
            problems.Add($"mode '{facts.Mode}', expected '{mode}'");
        }

        if (facts.WindowFrom is not { } from || facts.WindowTo is not { } to)
        {
            problems.Add("the result carries no window");
        }
        else
        {
            if ((to - from - spec.RequestedWindow).Duration() > TimeSpan.FromMilliseconds(1))
            {
                problems.Add($"window {to - from} differs from the requested {spec.RequestedWindow}");
            }

            if (to < started - TimeSpan.FromSeconds(5) || to > ended + TimeSpan.FromSeconds(5))
            {
                problems.Add($"window end {to:O} lies outside the call interval {started:O}..{ended:O}");
            }
        }

        if (facts.OutputBytes > expect.OutputBudgetBytes)
        {
            problems.Add($"output of {facts.OutputBytes} bytes exceeds the {expect.OutputBudgetBytes}-byte budget");
        }

        if (facts.Observed is null || facts.Returned is null)
        {
            problems.Add("the result lacks its observed/returned counts");
        }

        return problems;
    }

    /// <summary>
    /// What the capture itself proves about limits. It is a description of THIS result, never a rule: a smaller limit returns the same records only if
    /// the tool applies it after aggregation and ordering (verified in the formatter code) AND this result is untruncated AND observed &lt;= that limit.
    /// A truncated result states no smaller limit.
    /// </summary>
    public static JsonObject LimitCoverage(QuerySpec spec, ResultFacts facts) => new()
    {
        ["effectiveLimit"] = spec.EffectiveArguments["limit"]?.DeepClone(),
        ["mode"] = spec.EffectiveMode,
        ["observed"] = facts.Observed,
        ["returned"] = facts.Returned,
        ["truncated"] = facts.Truncated,
        ["smallestLimitReturningTheSameRecords"] = facts.Truncated == false ? facts.Observed : null,
        ["messageDistribution"] = facts.MessageDistributionJson is null ? null : JsonNode.Parse(facts.MessageDistributionJson),
        ["note"] = "Derived from this result only. The matcher must still verify untruncated and observed <= requested limit; no equivalence is assumed from the size of the captured limit.",
    };
}

/// <summary>The measured time skew of the batch. The queries are sequential reads of a live machine, never an atomic snapshot.</summary>
internal sealed record TemporalReport(
    DateTimeOffset FirstStarted, DateTimeOffset LastEnded, TimeSpan TotalElapsed, DateTimeOffset EarliestWindowEnd, DateTimeOffset LatestWindowEnd,
    TimeSpan WindowEndSpread, IReadOnlyList<TemporalRow> Rows)
{
    public static TemporalReport Build(IReadOnlyList<(string Id, DateTimeOffset Started, DateTimeOffset Ended, DateTimeOffset WindowEnd)> rows)
    {
        var first = rows.Min(r => r.Started);
        var windowEnds = rows.Select(r => r.WindowEnd).ToArray();
        return new TemporalReport(
            first, rows.Max(r => r.Ended), rows.Max(r => r.Ended) - first, windowEnds.Min(), windowEnds.Max(), windowEnds.Max() - windowEnds.Min(),
            [.. rows.Select(r => new TemporalRow(r.Id, r.Started, r.Ended, r.WindowEnd, r.WindowEnd - windowEnds.Min(), r.Ended - r.Started))]);
    }

    public JsonObject ToJson() => new()
    {
        ["atomicSnapshot"] = false,
        ["statement"] = "Sequential reads of a live machine. Windows are relative to each call's own clock; this report is the measured skew, not a guarantee of simultaneity.",
        ["firstStartedUtc"] = FirstStarted.ToString("O", CultureInfo.InvariantCulture),
        ["lastEndedUtc"] = LastEnded.ToString("O", CultureInfo.InvariantCulture),
        ["totalElapsedSeconds"] = Math.Round(TotalElapsed.TotalSeconds, 3),
        ["earliestWindowEndUtc"] = EarliestWindowEnd.ToString("O", CultureInfo.InvariantCulture),
        ["latestWindowEndUtc"] = LatestWindowEnd.ToString("O", CultureInfo.InvariantCulture),
        ["windowEndSpreadSeconds"] = Math.Round(WindowEndSpread.TotalSeconds, 3),
        ["queries"] = new JsonArray(Rows.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id,
            ["startedUtc"] = r.Started.ToString("O", CultureInfo.InvariantCulture),
            ["endedUtc"] = r.Ended.ToString("O", CultureInfo.InvariantCulture),
            ["windowToUtc"] = r.WindowEnd.ToString("O", CultureInfo.InvariantCulture),
            ["windowEndOffsetFromEarliestSeconds"] = Math.Round(r.OffsetFromEarliest.TotalSeconds, 3),
            ["durationSeconds"] = Math.Round(r.Duration.TotalSeconds, 3),
        }).ToArray()),
    };
}

internal sealed record TemporalRow(string Id, DateTimeOffset Started, DateTimeOffset Ended, DateTimeOffset WindowEnd, TimeSpan OffsetFromEarliest, TimeSpan Duration);
