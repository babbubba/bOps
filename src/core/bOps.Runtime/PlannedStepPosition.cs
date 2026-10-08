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

    internal static PlannedStepPosition Derive(AgentPlan plan, IEnumerable<PlanStep> steps) =>
        steps.Where(step => step.PlanRevision == plan.Revision)
            .Aggregate(Start, (position, step) => position.After(plan, step));

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
