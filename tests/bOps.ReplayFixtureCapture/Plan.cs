// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.QwenLiveBaseline;

namespace bOps.ReplayFixtureCapture;

internal enum TruncationPolicy
{
    /// <summary>A truncated result is an unexpected incomplete result: the capture stops.</summary>
    MustBeFalse,

    /// <summary>Truncation is a known property of the query (a record ceiling, the output budget); it is recorded, never hidden.</summary>
    Allowed,
}

/// <summary>What a result must look like for the capture to keep it. Anything else stops the batch; nothing is retried or substituted.</summary>
internal sealed record Expectation(
    int SchemaVersion,
    string[] AllowedStatuses,
    ToolResultCompleteness[] AllowedCompleteness,
    TruncationPolicy Truncation,
    bool RequireAllSourcesAvailable,
    int OutputBudgetBytes);

/// <summary>One authorised query. The list in <see cref="CapturePlan"/> is closed: a call that is not in it cannot run.</summary>
internal sealed record QuerySpec(
    string Id,
    string Tool,
    string ArgumentsJson,
    bool Optional,
    string Gap,
    Expectation Expect)
{
    public JsonObject Arguments => JsonNode.Parse(ArgumentsJson)!.AsObject();

    /// <summary>The arguments with every verified default of the tool applied (what the tool will really do).</summary>
    public JsonObject EffectiveArguments => ReplayArguments.Effective(Tool, Arguments);

    /// <summary>The window the arguments ask for, from the effective arguments.</summary>
    public TimeSpan RequestedWindow
    {
        get
        {
            var effective = EffectiveArguments;
            foreach (var (name, unit) in new[] { ("windowMinutes", 1d), ("sinceMinutes", 1d), ("windowDays", 1440d), ("sinceDays", 1440d) })
            {
                if (effective.TryGetPropertyValue(name, out var node) && ReplayArguments.TryInteger(node, out var value))
                {
                    return TimeSpan.FromMinutes(value * unit);
                }
            }

            throw new InvalidOperationException($"{Id}: no window argument.");
        }
    }

    public string? EffectiveMode => EffectiveArguments["mode"] is JsonValue v && v.TryGetValue<string>(out var mode) ? mode : null;
}

/// <summary>
/// The closed, reviewed list of queries of snapshot T02-B (REPLAY-DEFAULT-EQUIVALENCE-01, section 3 of the proposal). Only three read-only
/// tools, only these arguments. The plan hash covers everything that decides what the machine is asked, so the `capture` command can only run the
/// plan that was reviewed.
/// </summary>
internal static class CapturePlan
{
    public const int Version = 1;

    public static readonly IReadOnlyList<string> AllowedTools = ["system.events", "system.stability", "system.crashes"];

    private static readonly string[] Complete = ["complete"];
    private static readonly string[] CompleteOrPartial = ["complete", "partial"];

    private static Expectation Events(bool truncationAllowed) => new(
        2, Complete,
        truncationAllowed ? [ToolResultCompleteness.Complete, ToolResultCompleteness.Partial] : [ToolResultCompleteness.Complete],
        truncationAllowed ? TruncationPolicy.Allowed : TruncationPolicy.MustBeFalse,
        RequireAllSourcesAvailable: true, OutputBudgetBytes: 32_768);

    private static Expectation Stability(bool truncationAllowed) => new(
        1, CompleteOrPartial, [ToolResultCompleteness.Complete, ToolResultCompleteness.Partial],
        truncationAllowed ? TruncationPolicy.Allowed : TruncationPolicy.MustBeFalse,
        RequireAllSourcesAvailable: false, OutputBudgetBytes: 32_768);

    private static readonly Expectation Crashes = new(
        2, CompleteOrPartial, [ToolResultCompleteness.Complete, ToolResultCompleteness.Partial], TruncationPolicy.Allowed,
        RequireAllSourcesAvailable: false, OutputBudgetBytes: 65_536);

    /// <summary>The ten mandatory queries (in capture order) and the two optional ones, which are disabled unless named explicitly.</summary>
    public static readonly IReadOnlyList<QuerySpec> All =
    [
        new("Q01", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}""", false,
            "Anchor: the F02 call re-captured at the instant of the snapshot.", Events(truncationAllowed: false)),
        new("Q02", "system.events", """{"windowMinutes":1440,"limit":500,"mode":"aggregate"}""", false,
            "No aggregate without a severity filter existed (T02-r1 original).", Events(truncationAllowed: true)),
        new("Q03", "system.events", """{"windowMinutes":1440,"minSeverity":"error","limit":500,"mode":"aggregate"}""", false,
            "F02 is warning-or-more severe; error is another query (T02-r2 revalidation).", Events(truncationAllowed: false)),
        new("Q04", "system.events", """{"windowMinutes":1440,"minSeverity":"critical","limit":500,"mode":"raw"}""", false,
            "No raw read over 24 hours existed (T02-r3 revalidation).", Events(truncationAllowed: false)),
        new("Q05", "system.crashes", """{"mode":"aggregate","sinceMinutes":1440}""", false,
            "Anchor: the F06 call re-captured at the instant of the snapshot.", Crashes),
        new("Q06", "system.stability", """{"windowDays":1,"limit":200}""", false,
            "Anchor: the F01 window re-captured at the instant of the snapshot.", Stability(truncationAllowed: false)),
        new("Q07", "system.stability", """{"windowDays":2,"limit":200}""", false, "No 2-day stability read existed (T02-r2 original).", Stability(truncationAllowed: false)),
        new("Q08", "system.stability", """{"windowDays":3,"limit":200}""", false, "No 3-day stability read existed (T02-r3 revalidation).", Stability(truncationAllowed: false)),
        new("Q09", "system.stability", """{"windowDays":7,"limit":200}""", false, "Anchor: the F22 window re-captured at the instant of the snapshot.", Stability(truncationAllowed: false)),
        new("Q10", "system.stability", """{"windowDays":30,"limit":200}""", false,
            "No 30-day (default) stability read existed (T02-r2 revalidation). 30 days may reach a per-source record ceiling.", Stability(truncationAllowed: true)),
        new("OPT1", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","mode":"raw"}""", true,
            "Raw warning read over 24 hours at the default limit (50). Larger and more sensitive; truncated by construction.", Events(truncationAllowed: true)),
        new("OPT2", "system.events", """{"windowMinutes":1440,"minSeverity":"critical","limit":500,"mode":"aggregate"}""", true,
            "Aggregate critical read; not requested by the model so far.", Events(truncationAllowed: false)),
    ];

    // T02-B remains byte-for-byte the version-1 plan. T02-C reuses its first ten queries.
    public static readonly IReadOnlyList<QuerySpec> T02C =
    [
        .. All.Take(10),
        new("C1", "system.events", """{"windowMinutes":1440,"mode":"aggregate","limit":50}""", false,
            "Dedicated aggregate without severity at limit 50; limit 500 is not assumed equivalent.", Events(truncationAllowed: true)),
        new("C2", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","mode":"raw","limit":50}""", false,
            "OPT1 promoted to mandatory with explicit limit 50. Raw messages may contain sensitive data.", Events(truncationAllowed: true)),
        new("C3", "system.stability", """{"windowDays":30,"limit":100}""", false,
            "Dedicated 30-day limit 100; the truncated limit-200 fixture is not equivalent.", Stability(truncationAllowed: true)),
    ];

    public static int VersionOf(string plan) => plan switch
    {
        "T02-B" => Version,
        "T02-C" => 2,
        _ => throw new ArgumentException($"Unknown plan '{plan}'. Expected T02-B or T02-C."),
    };

    public static IReadOnlyList<QuerySpec> Queries(string plan) => VersionOf(plan) == 1 ? All : T02C;

    public static IReadOnlyList<string> OptionalIds => [.. All.Where(q => q.Optional).Select(q => q.Id)];

    /// <summary>The mandatory queries plus the optional ones named in <paramref name="includeOptional"/>, in plan order. An unknown id is an error, never ignored.</summary>
    public static IReadOnlyList<QuerySpec> Select(IEnumerable<string> includeOptional, string plan = "T02-B")
    {
        var all = Queries(plan);
        var wanted = includeOptional.ToArray();
        foreach (var id in wanted)
        {
            if (!all.Any(q => q.Optional && q.Id == id))
            {
                throw new ArgumentException($"'{id}' is not an optional query of the plan (optional: {string.Join(", ", OptionalIds)}).");
            }
        }

        return [.. all.Where(q => !q.Optional || wanted.Contains(q.Id, StringComparer.Ordinal))];
    }

    /// <summary>The authorisation check of one call: the id, the tool and the exact arguments must be those of a selected query.</summary>
    public static QuerySpec Authorise(IReadOnlyList<QuerySpec> selected, string id, string tool, JsonObject arguments)
    {
        var spec = selected.FirstOrDefault(q => q.Id == id)
            ?? throw new UnauthorisedQueryException($"query '{id}' is not in the selected plan.");
        if (!AllowedTools.Contains(tool, StringComparer.Ordinal) || spec.Tool != tool
            || ReplayArguments.Literal(arguments) != ReplayArguments.Literal(spec.Arguments))
        {
            throw new UnauthorisedQueryException($"query '{id}' does not match the plan (tool or arguments differ).");
        }

        return spec;
    }

    /// <summary>The SHA-256 of what the plan asks of the machine. Changing any query, argument or expectation changes it.</summary>
    public static string Hash(IReadOnlyList<QuerySpec> selected, string planName = "T02-B")
    {
        var plan = new JsonObject
        {
            ["planVersion"] = VersionOf(planName),
            ["queries"] = new JsonArray(selected.Select(q => (JsonNode)new JsonObject
            {
                ["id"] = q.Id,
                ["tool"] = q.Tool,
                ["arguments"] = JsonNode.Parse(Canonical(q.Arguments)),
                ["expect"] = new JsonObject
                {
                    ["schemaVersion"] = q.Expect.SchemaVersion,
                    ["statuses"] = string.Join(",", q.Expect.AllowedStatuses.Order(StringComparer.Ordinal)),
                    ["completeness"] = string.Join(",", q.Expect.AllowedCompleteness.Select(c => c.ToString()).Order(StringComparer.Ordinal)),
                    ["truncation"] = q.Expect.Truncation.ToString(),
                    ["allSourcesAvailable"] = q.Expect.RequireAllSourcesAvailable,
                    ["budgetBytes"] = q.Expect.OutputBudgetBytes,
                },
            }).ToArray()),
        };
        return Sha256(plan.ToJsonString());
    }

    public static string Canonical(JsonObject value) =>
        "{" + string.Join(",", value.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + (p.Value?.ToJsonString() ?? "null"))) + "}";

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

internal sealed class UnauthorisedQueryException(string message) : Exception(message);

/// <summary>A condition under which the capture refuses to continue. The message says which and why; nothing is retried or worked around.</summary>
internal sealed class CaptureAbortException(string message, int exitCode = 4) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
