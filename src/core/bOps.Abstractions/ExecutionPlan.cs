// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.Abstractions;

/// <summary>One concrete, typed tool call within an <see cref="ExecutionPlan"/> (ADR-0023).</summary>
public sealed record ExecutionPlanStep(int Index, string ToolName, ToolArguments Arguments, string? Description);

/// <summary>
/// A concrete, typed, immutable sequence of tool calls a <c>Capability</c> will make — distinct
/// from <see cref="AgentPlan"/>/<see cref="PlannedStep"/>, which stay natural-language and freely
/// revised across replans (rule C8). This is the *authorizable* artifact: approval binds to
/// <see cref="ExecutionPlanHasher.ComputeHash"/> of one of these, and any change to its content
/// produces a different hash, which is what "a material change invalidates the approval" means
/// concretely (ADR-0023). Each step still executes through the same policy/approval/verification/
/// audit path every tool call already goes through — this type carries no shortcut around it.
/// </summary>
public sealed record ExecutionPlan
{
    /// <summary>Creates an execution plan.</summary>
    /// <param name="CapabilityName">The capability this plan realizes.</param>
    /// <param name="CapabilityVersion">The version of that capability, for provenance if the capability's own definition later changes.</param>
    /// <param name="Rationale">Why this plan accomplishes the capability's goal.</param>
    /// <param name="Steps">The concrete steps, in execution order. Indices must be 0..N-1, contiguous, with no gaps or duplicates.</param>
    /// <exception cref="ArgumentException"><paramref name="Steps"/> is empty, or its indices are not exactly 0..N-1 with no gaps or duplicates.</exception>
    public ExecutionPlan(string CapabilityName, string CapabilityVersion, string Rationale, IReadOnlyList<ExecutionPlanStep> Steps)
    {
        ArgumentNullException.ThrowIfNull(Steps);
        if (Steps.Count == 0)
        {
            throw new ArgumentException("An execution plan must have at least one step.", nameof(Steps));
        }

        var indices = Steps.Select(s => s.Index).OrderBy(i => i).ToArray();
        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] != i)
            {
                throw new ArgumentException(
                    "Execution plan step indices must be exactly 0..N-1, with no gaps or duplicates.", nameof(Steps));
            }
        }

        this.CapabilityName = CapabilityName;
        this.CapabilityVersion = CapabilityVersion;
        this.Rationale = Rationale;
        this.Steps = Steps;
    }

    /// <summary>The capability this plan realizes.</summary>
    public string CapabilityName { get; init; }

    /// <summary>The version of that capability, for provenance if the capability's own definition later changes.</summary>
    public string CapabilityVersion { get; init; }

    /// <summary>Why this plan accomplishes the capability's goal.</summary>
    public string Rationale { get; init; }

    /// <summary>The concrete steps, in execution order.</summary>
    public IReadOnlyList<ExecutionPlanStep> Steps { get; init; }
}

/// <summary>
/// Binds a human approval decision to the exact plan content it was granted for (ADR-0023). Before
/// executing, the runtime recomputes <see cref="ExecutionPlanHasher.ComputeHash"/> for the plan
/// about to run and compares it to <see cref="PlanHash"/> — a mismatch means the plan changed
/// since approval and must be re-approved; there is no partial-trust fallback.
/// </summary>
public sealed record ExecutionPlanApproval(string PlanHash, ApprovalDecision Decision);

/// <summary>
/// Computes a deterministic hash over an <see cref="ExecutionPlan"/>'s content (ADR-0023). Never
/// cached on the record itself — a C# record's non-destructive mutation (<c>plan with {...}</c>)
/// would otherwise leave a stale hash sitting on a copy that no longer matches its own content;
/// computing fresh from content every time makes that bug impossible rather than merely avoided.
/// </summary>
public static class ExecutionPlanHasher
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = false };

    /// <summary>Computes this plan's canonical hash, as a lowercase hex string.</summary>
    public static string ComputeHash(ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // CanonicalJson.Sort always returns a non-null JsonObject for a non-null JsonObject input
        // (its JsonObject branch never returns null); the ! reflects that, not an unchecked guess.
        var canonical = CanonicalJson.Sort(ToJsonNode(plan))!;
        var bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString(SerializeOptions));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static JsonObject ToJsonNode(ExecutionPlan plan)
    {
        var steps = new JsonArray();
        foreach (var step in plan.Steps)
        {
            steps.Add(new JsonObject
            {
                ["index"] = step.Index,
                ["toolName"] = step.ToolName,
                ["arguments"] = step.Arguments.ToJson(),
                ["description"] = step.Description,
            });
        }

        return new JsonObject
        {
            ["capabilityName"] = plan.CapabilityName,
            ["capabilityVersion"] = plan.CapabilityVersion,
            ["rationale"] = plan.Rationale,
            ["steps"] = steps,
        };
    }
}

/// <summary>
/// Recursively sorts every JSON object's properties by ordinal key name, so two structurally
/// identical values always produce the same textual form regardless of how they were built —
/// the property-declaration order <see cref="System.Text.Json.JsonSerializer"/> uses by default
/// is an implementation detail, not something a security-relevant hash should depend on
/// (ADR-0023).
/// </summary>
internal static class CanonicalJson
{
    public static JsonNode? Sort(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var key in obj.Select(kvp => kvp.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    sorted[key] = Sort(obj[key]?.DeepClone());
                }

                return sorted;

            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Sort(item?.DeepClone()));
                }

                return items;

            default:
                return node?.DeepClone();
        }
    }
}
