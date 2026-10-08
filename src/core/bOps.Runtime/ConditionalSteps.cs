// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Where a planned step stands with respect to its ADR-0050 activation condition.</summary>
internal enum ConditionalStepState
{
    /// <summary>The step has no condition.</summary>
    Unconditional,

    /// <summary>The cursor has not reached the conditional step, so its condition is not yet evaluated.</summary>
    Pending,

    /// <summary>The cursor reached the step and the referenced fact exists.</summary>
    Activated,

    /// <summary>The cursor reached the step and the referenced fact does not exist; terminal for the step.</summary>
    Skipped,
}

/// <summary>
/// ADR-0050 deterministic activation, skip and fact-binding. Every decision is a pure function of the persisted plan
/// revision and execution steps (their <see cref="PlanStep.PlanRevision"/>, <see cref="PlanStep.PlannedStepIndex"/> and
/// <see cref="ToolCallResult.Facts"/>), so live execution and resume reach the same answer and no state lives only in memory.
/// </summary>
internal static class ConditionalSteps
{
    /// <summary>
    /// The referenced fact, only if a consumed (<see cref="PlannedStepExecutionClassification.Matched"/>), successful execution of
    /// the same plan revision's source step produced it. Facts of another revision are never considered.
    /// </summary>
    internal static EvidenceFact? FindFact(AgentPlan plan, IEnumerable<PlanStep> steps, EvidenceFactExists condition)
    {
        if (plan.SemanticContractVersion != 1)
        {
            return null;
        }

        foreach (var step in steps)
        {
            if (step.PlanRevision != plan.Revision
                || step.PlannedStepIndex != condition.SourceStepIndex
                || step.ExecutionClassification != PlannedStepExecutionClassification.Matched
                || step.Result is not { Outcome: ToolOutcome.Success } result)
            {
                continue;
            }

            foreach (var fact in result.Facts)
            {
                if (string.Equals(fact.Type, condition.FactType, StringComparison.Ordinal)
                    && string.Equals(fact.Key, condition.FactKey, StringComparison.Ordinal))
                {
                    return fact;
                }
            }
        }

        return null;
    }

    /// <summary>The persisted-state view of one planned step's condition at the given cursor.</summary>
    internal static ConditionalStepState StateOf(AgentPlan plan, IEnumerable<PlanStep> steps, int cursor, int plannedStepIndex)
    {
        if (plan.Steps[plannedStepIndex].Activation is not { } condition)
        {
            return ConditionalStepState.Unconditional;
        }

        if (cursor < plannedStepIndex)
        {
            return ConditionalStepState.Pending;
        }

        return FindFact(plan, steps, condition) is null ? ConditionalStepState.Skipped : ConditionalStepState.Activated;
    }

    /// <summary>
    /// The expected arguments of the step at <paramref name="cursor"/>: its static constraints combined with the optional
    /// fact binding. A binding must match the target parameter's declared type exactly (no coercion) and may not contradict a
    /// static constraint on the same argument.
    /// </summary>
    internal static (ToolArguments? Expected, string? Problem) ResolveExpected(
        AgentPlan plan, IEnumerable<PlanStep> steps, int cursor, ToolManifest manifest)
    {
        var planned = plan.Steps[cursor];
        if (planned.Activation is not { BindToArgument: { } target } condition)
        {
            return (planned.ExpectedArguments, null);
        }

        if (FindFact(plan, steps, condition) is not { } fact)
        {
            return (null, "The bound evidence fact does not exist.");
        }

        var parameter = manifest.Parameters.FirstOrDefault(p => string.Equals(p.Name, target, StringComparison.Ordinal));
        if (parameter is null || parameter.Sensitive)
        {
            return (null, $"Bound argument '{target}' is not a non-sensitive declared parameter.");
        }

        if (fact.ValueType != parameter.Type)
        {
            return (null, $"Evidence fact type {fact.ValueType} is not compatible with parameter '{target}' of type {parameter.Type}.");
        }

        var merged = planned.ExpectedArguments is { } existing ? (JsonObject)existing.ToJson().DeepClone() : [];
        if (merged.TryGetPropertyValue(target, out var staticValue) && staticValue is not null)
        {
            if (!SemanticExpectedArguments.TypedEquals(parameter.Type, staticValue, fact.Value))
            {
                return (null, $"The bound fact value conflicts with the static expected argument '{target}'.");
            }
        }
        else
        {
            merged[target] = fact.Value.DeepClone();
        }

        var expected = ToolArguments.FromJson(merged);
        return SemanticExpectedArguments.Validate(manifest, expected) is { } violation
            ? (null, $"The bound expected arguments are invalid: {violation}")
            : (expected, null);
    }

    /// <summary>The plan with the current step's resolved expected arguments, for the executor prompt only.</summary>
    internal static AgentPlan WithResolvedCurrentStep(
        AgentPlan plan, IEnumerable<PlanStep> steps, int cursor, Func<string, ToolManifest?> manifestOf)
    {
        if (cursor < 0 || cursor >= plan.Steps.Count
            || plan.Steps[cursor] is not { Activation.BindToArgument: not null, ExpectedTool: { } tool } planned
            || manifestOf(tool) is not { } manifest
            || ResolveExpected(plan, steps, cursor, manifest) is not ({ } expected, null))
        {
            return plan;
        }

        var resolved = plan.Steps.ToList();
        resolved[cursor] = planned with { ExpectedArguments = expected };
        return plan with { Steps = resolved };
    }
}
