// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Where execution stands in the current <see cref="AgentPlan"/> (ADR-0047): the planned-step cursor, whether that step has
/// already spent its one argument-validation correction, and whether the plan must be replaced before any operational tool
/// is offered again. It is a pure fold over persisted <see cref="PlanStep"/> data, so a live attempt and a resumed one reach
/// the same position from the same steps, and nothing about it lives only in memory.
/// </summary>
/// <param name="Cursor">The index of the current <see cref="PlannedStep"/>; at or past the plan's end when it is exhausted.</param>
/// <param name="CorrectionSpent">Whether a step of this plan revision already failed argument validation on the current planned tool.</param>
/// <param name="ReplanRequired">Whether this plan revision may not offer another operational tool: its current step failed validation twice, or the persisted steps contradict the one-correction rule.</param>
internal readonly record struct PlannedStepPosition(int Cursor, bool CorrectionSpent, bool ReplanRequired)
{
    /// <summary>The position at the start of a newly accepted plan.</summary>
    internal static PlannedStepPosition Start => default;

    /// <summary>Folds every persisted step taken under <paramref name="plan"/>'s revision, oldest first.</summary>
    internal static PlannedStepPosition Derive(AgentPlan plan, IEnumerable<PlanStep> steps) =>
        steps.Where(step => step.PlanRevision == plan.Revision)
            .Aggregate(Start, (position, step) => position.After(plan, step));

    /// <summary>
    /// The position after <paramref name="step"/>. A first argument-validation failure on the current planned tool keeps the
    /// cursor and spends the correction; a second one requires a replan. Every other step consumes the planned step, as
    /// before ADR-0047.
    /// </summary>
    internal PlannedStepPosition After(AgentPlan plan, PlanStep step)
    {
        if (ReplanRequired)
        {
            return this;
        }

        var expectedTool = Cursor >= 0 && Cursor < plan.Steps.Count ? plan.Steps[Cursor].ExpectedTool : null;
        if (IsArgumentValidationFailure(step, expectedTool))
        {
            return CorrectionSpent ? this with { ReplanRequired = true } : this with { CorrectionSpent = true };
        }

        // Only the same offered tool, or a call the runtime refused before execution, can follow a spent correction. An
        // executed call of any other tool was produced by a runtime that consumed the step on its first failure: fail
        // closed and replan rather than guess which planned step it served.
        if (CorrectionSpent && IsExecutedCallOfAnotherTool(step, expectedTool))
        {
            return this with { ReplanRequired = true };
        }

        return new PlannedStepPosition(Cursor + 1, CorrectionSpent: false, ReplanRequired: false);
    }

    /// <summary>
    /// The exact planned tool was offered, resolved and reached argument validation, which failed: typed result data and an
    /// ordinal name match only, never the error text. An unknown-tool rejection also carries
    /// <see cref="ToolFailureKind.Validation"/>, but under the reserved <see cref="RuntimeStepTokens.Denied"/> description.
    /// </summary>
    private static bool IsArgumentValidationFailure(PlanStep step, string? expectedTool) =>
        !string.IsNullOrWhiteSpace(expectedTool)
        && step.ToolCall is { ToolNameError: null } call
        && string.Equals(call.ToolName, expectedTool, StringComparison.Ordinal)
        && !string.Equals(step.Description, RuntimeStepTokens.Denied, StringComparison.Ordinal)
        && step.Result is { Outcome: ToolOutcome.Failure, FailureKind: ToolFailureKind.Validation };

    private static bool IsExecutedCallOfAnotherTool(PlanStep step, string? expectedTool) =>
        step.ToolCall is { } call
        && !string.Equals(step.Description, RuntimeStepTokens.Denied, StringComparison.Ordinal)
        && !string.Equals(call.ToolName, expectedTool, StringComparison.Ordinal);
}
