// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// KNOW-DISC-INV01 (ADR-0057 T7 subset): what every model request of one benchmark task offered and presented about knowledge, and
/// what its reply proposed, reduced to booleans, counts, hashes, host-generated refs and closed codes. It sees the request after the
/// real MAF/broker projection, exactly as the provider adapter receives it. It reads nothing back into the runner, writes no audit
/// event and never keeps a prompt, a description, a goal or an argument value other than a closed operation and a shape-checked ref.
/// </summary>
internal sealed class BenchmarkKnowledgeDiscovery
{
    // Planning and replan both build their prompt with the runtime's tool-catalog header; a step prompt never carries it.
    private const string PlanningCatalogMarker = "later steps";
    private const string IndexControlMarker = "[knowledge-index-control v1]";

    private readonly object gate = new();
    private readonly List<JsonObject> requests = [];

    internal void Record(ModelRequest request, ModelResponse? response)
    {
        ArgumentNullException.ThrowIfNull(request);
        var indexTurns = request.History.Select((turn, position) => (turn, position))
            .Where(item => item.turn.Provenance is { Operation: "index", Origin: ContextOrigin.Knowledge or ContextOrigin.Skill or ContextOrigin.Experience })
            .ToArray();
        var index = indexTurns.Length == 1 ? indexTurns[0] : default;
        var proposed = response?.ToolCalls.Where(call => call.ToolName == KnowledgeReadBridge.PublicToolName).ToArray() ?? [];
        var entry = new JsonObject
        {
            ["phase"] = request.SystemPrompt.Contains(PlanningCatalogMarker, StringComparison.Ordinal) ? "planning" : "step",
            ["knowledgeFunctionOffered"] = request.AvailableTools.Any(tool => tool.Name == KnowledgeReadBridge.PublicToolName),
            ["indexTurns"] = indexTurns.Length,
            ["indexBeforeGoal"] = index.turn is not null && index.position + 1 < request.History.Count
                && request.History[index.position + 1].Provenance == ContextProvenance.Task,
            ["indexHash"] = index.turn?.Content is { } content ? Sha256(content) : null,
            ["indexControl"] = request.SystemPrompt.Contains(IndexControlMarker, StringComparison.Ordinal),
            ["replied"] = response is not null,
            ["knowledgeProposed"] = proposed.Length,
            ["knowledgeProposedFirst"] = response is { ToolCalls.Count: > 0 } && response.ToolCalls[0].ToolName == KnowledgeReadBridge.PublicToolName,
            ["proposals"] = new JsonArray([.. proposed.Select(call => (JsonNode)Proposal(call))]),
        };
        lock (gate)
        {
            entry["order"] = requests.Count;
            requests.Add(entry);
        }
    }

    /// <summary>The section exported in <c>result.json</c>; <paramref name="events"/> is the task's typed audit, when observed.</summary>
    internal JsonObject Snapshot(IReadOnlyList<AuditEvent>? events, bool bound)
    {
        JsonObject[] recorded;
        lock (gate)
        {
            recorded = [.. requests.Select(entry => (JsonObject)entry.DeepClone())];
        }

        var indexes = events?.OfType<KnowledgeIndexAuditEvent>().ToArray() ?? [];
        var reads = events?.OfType<KnowledgeReadAuditEvent>().ToArray() ?? [];
        var steps = recorded.Where(entry => entry["phase"]!.GetValue<string>() == "step").ToArray();
        var presented = steps.Count(entry => entry["indexTurns"]!.GetValue<int>() == 1);
        var offered = steps.Count(entry => entry["knowledgeFunctionOffered"]!.GetValue<bool>());
        var proposals = recorded.Sum(entry => entry["knowledgeProposed"]!.GetValue<int>());
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["indexStage"] = IndexStage(bound, indexes, steps.Length, presented),
            ["readStage"] = ReadStage(bound, offered, proposals, reads),
            ["planningRequests"] = recorded.Length - steps.Length,
            ["planningRequestsWithKnowledge"] = recorded.Count(entry => entry["phase"]!.GetValue<string>() == "planning"
                && (entry["indexTurns"]!.GetValue<int>() > 0 || entry["knowledgeFunctionOffered"]!.GetValue<bool>())),
            ["stepRequests"] = steps.Length,
            ["stepRequestsWithIndex"] = presented,
            ["stepRequestsOfferingFunction"] = offered,
            ["knowledgeProposals"] = proposals,
            ["indexAudit"] = new JsonArray([.. indexes.Select(evt => (JsonNode)new JsonObject
            {
                ["stepIndex"] = evt.StepIndex,
                ["status"] = evt.Status.ToString(),
                ["suppression"] = evt.Suppression?.ToString(),
                ["authorizedTotal"] = evt.AuthorizedTotal,
                ["omitted"] = evt.Omitted,
                ["selectorVersion"] = evt.SelectorVersion is KnowledgeIndexSelector.Version ? evt.SelectorVersion : null,
                ["indexHash"] = IsHash(evt.IndexHash) ? evt.IndexHash : null,
                ["listed"] = new JsonArray([.. evt.Listed.Select(listing => (JsonNode)new JsonObject
                {
                    ["ref"] = SafeRef(listing.Ref),
                    // ADR-0057 T7: a skill name is exported only when it passes the name rule (U7); it is publisher text otherwise.
                    ["skill"] = KnowledgeTextPolicy.IsValidSkillName(listing.Skill) ? listing.Skill : KnowledgeBenchmarkExport.RedactedFreeText,
                    ["resources"] = listing.Resources,
                    ["score"] = listing.Score,
                })]),
            })]),
            ["reads"] = new JsonArray([.. reads.Select(read => (JsonNode)new JsonObject
            {
                ["stepIndex"] = read.StepIndex,
                ["operation"] = SafeOperation(read.Operation),
                ["ref"] = SafeRef(read.Ref),
                ["byName"] = read.Ref is null && read.Skill is not null,
                ["resultCode"] = read.ResultCode.ToString(),
                ["repeat"] = read.Repeat,
            })]),
            ["requests"] = new JsonArray([.. recorded.Select(entry => (JsonNode)entry)]),
            ["representation"] = "Per-request booleans, counts, SHA-256 hashes, host refs and closed codes after the MAF/broker projection; no prompt, description, goal or argument text",
        };
    }

    /// <summary>Whether the index existed for this task, and whether it reached the step requests that could act on it.</summary>
    internal static string IndexStage(bool bound, IReadOnlyList<KnowledgeIndexAuditEvent> indexes, int stepRequests, int presented) =>
        !bound ? "NotBound"
        : indexes.Count == 0 ? "NotPublished"
        : !indexes.Any(evt => evt.Status == KnowledgeIndexStatus.Published) ? "Suppressed"
        : presented == 0 ? "PublishedNotPresented"
        : presented < stepRequests ? "PresentedPartially"
        : "Presented";

    /// <summary>How far the read path went: offered, selected, served, and with what result.</summary>
    internal static string ReadStage(bool bound, int offered, int proposals, IReadOnlyList<KnowledgeReadAuditEvent> reads) =>
        !bound ? "NotBound"
        : reads.Any(read => read.ResultCode == KnowledgeReadResultCode.Success) ? "ReadSucceeded"
        : reads.Count > 0 ? "ReadRefused"
        // Proposed but never audited by the bridge: e.g. a knowledge call after another runtime control call (first-call-wins).
        : proposals > 0 ? "SelectedNotServed"
        : offered > 0 ? "OfferedNotSelected"
        : "FunctionNotOffered";

    private static JsonObject Proposal(ModelToolCall call)
    {
        if (call.ArgumentsError is not null)
        {
            return new JsonObject { ["operation"] = null, ["ref"] = null, ["byName"] = false, ["malformed"] = true };
        }

        var arguments = call.Arguments.ToJson();
        var operation = arguments["operation"] is JsonValue op && op.TryGetValue<string>(out var text) ? text : null;
        var reference = arguments["ref"] is JsonValue r && r.TryGetValue<string>(out var refText) ? refText : null;
        return new JsonObject
        {
            ["operation"] = SafeOperation(operation),
            ["ref"] = SafeRef(reference),
            ["byName"] = arguments.ContainsKey("skill") || arguments.ContainsKey("resource"),
            ["query"] = arguments.ContainsKey("query"),
            ["malformed"] = false,
        };
    }

    private static string? SafeOperation(string? operation) =>
        operation is null ? null : operation is "discover" or "load" or "read_resource" ? operation : KnowledgeBenchmarkExport.RedactedFreeText;

    /// <summary>Host-generated refs only: <c>K1</c> … <c>K999</c>, optionally <c>.R1</c> … <c>.R999</c>.</summary>
    internal static string? SafeRef(string? reference) =>
        reference is null ? null
        : System.Text.RegularExpressions.Regex.IsMatch(reference, @"\AK[1-9][0-9]{0,2}(\.R[1-9][0-9]{0,2})?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? reference : KnowledgeBenchmarkExport.RedactedFreeText;

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    internal static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
