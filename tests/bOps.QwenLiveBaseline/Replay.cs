// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.QwenLiveBaseline;

/// <summary>One captured tool result with the exact arguments it answered and where it came from.</summary>
internal sealed record Fixture(string Id, string Tool, JsonObject Arguments, ToolCallResult Result, string? OriginScenario, string SourceTaskId, string Sha256);

/// <summary>The audit-quality record of one tool call the model actually made during a replay run.</summary>
internal sealed record ReplayCall(
    int Sequence, string Tool, JsonObject Arguments, string Verdict, string? FixtureId, string? Rule, string[] OtherCandidates, DateTimeOffset At);

/// <summary>
/// Chooses a captured result for the call the model really made. It never imposes a sequence of calls and never invents output: a call
/// that no fixture answers by the stated rules is UNSUPPORTED_REPLAY (a failed tool result that says so), not a guess and never
/// live data of the current machine. There is no fuzzy matching: every rule below is an equality or a proof from the capture itself.
/// Rules (ReplayMatching.cs holds the per-tool facts, each verified against the production tool):
/// (1) "exact": the arguments are literally equal (types preserved, property order ignored, null is not an absent property).
/// (2) "default-equivalent": equal once the tool's VERIFIED defaults are applied to what both calls omitted (a null is read as omitted only
///     for tools whose reader is verified to do so). An explicit value different from the default never matches; neither does an omitted
///     argument that has no verified default (system.events minSeverity: omitted means every severity, not "warning").
/// (3) "limit-covered": everything else effective-equal, the request asks for a limit at least as large as the capture's, and the capture
///     itself says truncated:false (a larger window could not have returned anything else).
/// (4) "limit-not-binding": everything else effective-equal, the capture says truncated:false and its own observed count (the field the
///     spec names for the mode) is not above the requested limit (a smaller limit that cuts nothing returns the same records).
/// Different windows, different limits that could change the records, different modes, filters or enum spellings never match.
/// Several literal "exact" fixtures (the same call captured twice) keep the legacy order: the scenario's own capture, then the lowest id.
/// Several candidates found by rules 2-4 are never chosen between: the call is UNSUPPORTED_REPLAY with Rule "ambiguous" and the ids.
/// </summary>
internal sealed class ReplayBroker(IReadOnlyList<Fixture> fixtures, string scenario)
{
    private readonly List<ReplayCall> ledger = [];

    public IReadOnlyList<ReplayCall> Ledger => ledger;

    public ToolCallResult Answer(string tool, JsonObject arguments)
    {
        var matches = Candidates(tool, arguments);
        var sequence = ledger.Count + 1;
        var exact = matches.Where(m => m.Rule == "exact")
            .OrderBy(m => string.Equals(m.Fixture.OriginScenario, scenario, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(m => m.Fixture.Id, StringComparer.Ordinal)
            .ToList();
        if (exact.Count > 0)
        {
            return Serve(sequence, tool, arguments, exact[0], [.. matches.Where(m => m != exact[0]).Select(m => m.Fixture.Id).Order(StringComparer.Ordinal)]);
        }

        var derived = matches.OrderBy(m => m.Fixture.Id, StringComparer.Ordinal).ToList();
        if (derived.Count == 1)
        {
            return Serve(sequence, tool, arguments, derived[0], []);
        }

        var ambiguous = derived.Count > 1;
        ledger.Add(new ReplayCall(sequence, tool, (JsonObject)arguments.DeepClone(), "UNSUPPORTED_REPLAY", null, ambiguous ? "ambiguous" : null,
            [.. derived.Select(m => m.Fixture.Id)], DateTimeOffset.UtcNow));
        return new ToolCallResult(ToolOutcome.Failure, null,
            ambiguous
                ? "UNSUPPORTED_REPLAY: several captured results could answer this call and the benchmark does not choose between them; nothing was executed on the machine."
                : "UNSUPPORTED_REPLAY: the benchmark has no captured evidence for this call; nothing was executed on the machine.")
        {
            FailureKind = ToolFailureKind.Environment,
            Completeness = ToolResultCompleteness.Unavailable,
        };
    }

    /// <summary>Every fixture that answers the call by one of the rules, with the strongest rule that does. Pure: nothing is recorded.</summary>
    internal List<(Fixture Fixture, string Rule)> Candidates(string tool, JsonObject arguments)
    {
        var matches = new List<(Fixture Fixture, string Rule)>();
        foreach (var fixture in fixtures.Where(f => string.Equals(f.Tool, tool, StringComparison.Ordinal)))
        {
            if (ReplayArguments.Literal(arguments) == ReplayArguments.Literal(fixture.Arguments))
            {
                matches.Add((fixture, "exact"));
            }
            else if (ReplayArguments.Canonical(tool, arguments) == ReplayArguments.Canonical(tool, fixture.Arguments))
            {
                matches.Add((fixture, "default-equivalent"));
            }
            else if (LimitRule(tool, arguments, fixture) is { } rule)
            {
                matches.Add((fixture, rule));
            }
        }

        return matches;
    }

    private ToolCallResult Serve(int sequence, string tool, JsonObject arguments, (Fixture Fixture, string Rule) chosen, string[] others)
    {
        ledger.Add(new ReplayCall(sequence, tool, (JsonObject)arguments.DeepClone(), "SERVED", chosen.Fixture.Id, chosen.Rule, others, DateTimeOffset.UtcNow));
        return chosen.Fixture.Result;
    }

    private static string? LimitRule(string tool, JsonObject arguments, Fixture fixture)
    {
        var requested = ReplayArguments.Effective(tool, arguments);
        var captured = ReplayArguments.Effective(tool, fixture.Arguments);
        if (!requested.TryGetPropertyValue("limit", out var wantedNode) || !captured.TryGetPropertyValue("limit", out var hadNode)
            || !ReplayArguments.TryInteger(wantedNode, out var wanted) || !ReplayArguments.TryInteger(hadNode, out var had)
            || wanted == had
            || ReplayArguments.Literal(ReplayArguments.Without(requested, "limit")) != ReplayArguments.Literal(ReplayArguments.Without(captured, "limit"))
            || fixture.Result.Output is not { Length: > 0 } output)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("truncated", out var truncated) || truncated.ValueKind != JsonValueKind.False)
            {
                return null;
            }

            if (wanted > had)
            {
                return "limit-covered";
            }

            // wanted < had: only when the capture proves the smaller limit cuts nothing.
            if (ToolArgumentSpecs.For(tool) is { } spec
                && spec.CountFieldByMode.TryGetValue(captured["mode"] is JsonValue modeValue && modeValue.TryGetValue<string>(out var mode) ? mode : string.Empty, out var field)
                && root.TryGetProperty(field, out var observed) && observed.ValueKind == JsonValueKind.Number
                && observed.TryGetInt64(out var count) && count <= wanted)
            {
                return "limit-not-binding";
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class FixtureStore
{
    public static List<Fixture> Load(string directory)
    {
        var result = new List<Fixture>();
        foreach (var path in Directory.EnumerateFiles(directory, "F*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
            var r = root["result"]!.AsObject();
            var facts = r["Facts"] is JsonArray array
                ? array.Select(f => new EvidenceFact(
                    f!["Type"]!.GetValue<string>(), f["Key"]!.GetValue<string>(),
                    (ToolParameterType)f["ValueType"]!.GetValue<int>(), f["Value"]!.DeepClone())).ToList()
                : [];
            var call = new ToolCallResult((ToolOutcome)r["Outcome"]!.GetValue<int>(), r["Output"]?.GetValue<string>(), r["ErrorMessage"]?.GetValue<string>())
            {
                FailureKind = (ToolFailureKind)r["FailureKind"]!.GetValue<int>(),
                Completeness = (ToolResultCompleteness)r["Completeness"]!.GetValue<int>(),
                Facts = facts,
            };
            var provenance = root["provenance"]!.AsObject();
            result.Add(new Fixture(
                root["id"]!.GetValue<string>(), root["tool"]!.GetValue<string>(), root["arguments"]!.AsObject(), call,
                provenance["originScenario"]?.GetValue<string>(), provenance["sourceTaskId"]!.GetValue<string>(), root["sha256"]!.GetValue<string>()));
        }

        return result;
    }

    public static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

/// <summary>
/// The real tool's manifest (so the planner and the model see the production tool surface), with execution answered by the
/// <see cref="ReplayBroker"/>. The real tool is never executed: no machine data is read, nothing is mutated.
/// </summary>
internal class ReplayTool(ITool real, ReplayBroker broker) : IToolAuditSummaryProvider
{
    public ToolManifest Manifest => real.Manifest;

    // Pure summarisation of the (replayed) result for the audit event; the real tool performs no I/O here.
    public JsonObject? CreateAuditSummary(ToolArguments arguments, ToolCallResult result) =>
        (real as IToolAuditSummaryProvider)?.CreateAuditSummary(arguments, result);

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(broker.Answer(Manifest.Name, arguments.ToJson()));
}

/// <summary>A non-Read tool must be IVerifiableTool to register. Policy forbids every non-Read call in this campaign; if one ever ran it would be refused here.</summary>
internal sealed class ReplayVerifiableTool(ITool real, ReplayBroker broker) : ReplayTool(real, broker), IVerifiableTool
{
    public Task<VerificationOutcome> EvaluateVerificationAsync(ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        throw new InvalidOperationException("QWEN-LIVE-BASELINE-01 replay never executes or verifies a mutating tool.");
}
