// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Pure persisted fold of planned-step progress. ADR-0050 keeps semantic and complete argument-validation corrections as
/// independent one-use budgets; legacy plans retain ADR-0047's expected-tool-only fold.
/// </summary>
internal readonly record struct PlannedStepPosition(
    int Cursor,
    bool SemanticCorrectionSpent,
    bool ArgumentCorrectionSpent,
    bool ReplanRequired)
{
    internal static PlannedStepPosition Start => default;

    internal static PlannedStepPosition Derive(
        AgentPlan plan, IEnumerable<PlanStep> steps, Func<string, ToolManifest?>? manifestOf = null)
    {
        var all = steps as IReadOnlyList<PlanStep> ?? steps.ToList();
        return all.Where(step => step.PlanRevision == plan.Revision)
            .Aggregate(Start, (position, step) => position.After(plan, step).Settle(plan, all, manifestOf));
    }

    /// <summary>
    /// ADR-0050: moves the cursor over conditional steps whose referenced fact does not exist (a skipped step is terminal and
    /// costs no model or tool call) and stops on the first unconditional or activated step. A condition that cannot be
    /// evaluated — a source that is not an earlier step, or a binding that cannot be resolved — fails closed to replan.
    /// Legacy plans have no conditions and are returned unchanged.
    /// </summary>
    internal PlannedStepPosition Settle(
        AgentPlan plan, IReadOnlyList<PlanStep> steps, Func<string, ToolManifest?>? manifestOf)
    {
        var position = this;
        while (plan.SemanticContractVersion == 1 && !position.ReplanRequired
               && position.Cursor >= 0 && position.Cursor < plan.Steps.Count
               && plan.Steps[position.Cursor].Activation is { } condition)
        {
            if (condition.SourceStepIndex < 0 || condition.SourceStepIndex >= position.Cursor)
            {
                return position with { ReplanRequired = true };
            }

            if (ConditionalSteps.FindFact(plan, steps, condition) is null)
            {
                position = new PlannedStepPosition(position.Cursor + 1, false, false, false);
                continue;
            }

            if (condition.BindToArgument is not null
                && (plan.Steps[position.Cursor].ExpectedTool is not { } tool
                    || manifestOf?.Invoke(tool) is not { } manifest
                    || ConditionalSteps.ResolveExpected(plan, steps, position.Cursor, manifest).Problem is not null))
            {
                return position with { ReplanRequired = true };
            }

            return position;
        }

        return position;
    }

    internal PlannedStepPosition After(AgentPlan plan, PlanStep step) =>
        plan.SemanticContractVersion == 1 ? AfterSemantic(step) : AfterLegacy(plan, step);

    private PlannedStepPosition AfterSemantic(PlanStep step)
    {
        if (ReplanRequired)
        {
            return this;
        }

        if (step.PlannedStepIndex != Cursor)
        {
            return this with { ReplanRequired = true };
        }

        switch (step.ExecutionClassification)
        {
            case PlannedStepExecutionClassification.SemanticMismatch:
                return SemanticCorrectionSpent
                    ? this with { ReplanRequired = true }
                    : this with { SemanticCorrectionSpent = true };
            case PlannedStepExecutionClassification.ArgumentValidationFailure:
                return ArgumentCorrectionSpent
                    ? this with { ReplanRequired = true }
                    : this with { ArgumentCorrectionSpent = true };
            case PlannedStepExecutionClassification.Matched:
                return IsDeviation(step)
                    ? this with { ReplanRequired = true }
                    : new PlannedStepPosition(Cursor + 1, false, false, false);
            default:
                // A semantic-contract execution without a semantic classification is a wrong/not-offered tool or
                // contradictory persisted history. Both require ADR-0046 replanning before another operational call.
                return step.ToolCall is null ? this : this with { ReplanRequired = true };
        }
    }

    private PlannedStepPosition AfterLegacy(AgentPlan plan, PlanStep step)
    {
        if (ReplanRequired)
        {
            return this;
        }

        var expectedTool = Cursor >= 0 && Cursor < plan.Steps.Count ? plan.Steps[Cursor].ExpectedTool : null;
        if (IsLegacyArgumentValidationFailure(step, expectedTool))
        {
            return ArgumentCorrectionSpent
                ? this with { ReplanRequired = true }
                : this with { ArgumentCorrectionSpent = true };
        }

        if (ArgumentCorrectionSpent && (IsCallOfAnotherTool(step, expectedTool) || IsDeviation(step)))
        {
            return this with { ReplanRequired = true };
        }

        return new PlannedStepPosition(Cursor + 1, false, false, false);
    }

    private static bool IsLegacyArgumentValidationFailure(PlanStep step, string? expectedTool) =>
        !string.IsNullOrWhiteSpace(expectedTool)
        && step.ToolCall is { ToolNameError: null } call
        && string.Equals(call.ToolName, expectedTool, StringComparison.Ordinal)
        && step.Result is { Outcome: ToolOutcome.Failure, FailureKind: ToolFailureKind.Validation };

    private static bool IsCallOfAnotherTool(PlanStep step, string? expectedTool) =>
        step.ToolCall is { } call
        && (call.ToolNameError is not null || !string.Equals(call.ToolName, expectedTool, StringComparison.Ordinal));

    private static bool IsDeviation(PlanStep step) =>
        step.ToolCall is not null
        && (step.Result is { FailureKind: ToolFailureKind.Authorization } or { Outcome: ToolOutcome.Timeout }
            || step.VerificationStatus == VerificationStatus.Refuted);
}
