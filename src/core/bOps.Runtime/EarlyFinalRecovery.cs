// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

internal static class EarlyFinalRecovery
{
    internal const string StepDescription = "Malformed early-final replan; persisted plan continuation";

    internal static bool IsStep(PlanStep step) => step.ToolCall is null && step.Result is null
        && step.Description == StepDescription && step.EarlyFinalRecoveryCursor is not null;

    internal static bool HasMalformedCheckpoint(PlanStep step) => PrematureFinal.IsStep(step)
        && step.EarlyFinalReplanFailed
        && step.EarlyFinalReplanCalls is { Count: > 0 } calls && calls[^1] is
            { Outcome: ModelCallOutcome.Failure, FailureKind: ModelFailureKind.MalformedResponse };

    internal static PlannedStepPosition? SafePosition(
        AgentPlan plan, IReadOnlyList<PlanStep> steps, int triggerIndex, Func<string, ToolManifest?> manifestOf,
        IReadOnlyList<ToolManifest> visibleTools)
    {
        var trigger = steps.LastOrDefault(step => step.PlanRevision == plan.Revision);
        if (plan.SemanticContractVersion != 1 || trigger?.Index != triggerIndex || !HasMalformedCheckpoint(trigger))
        {
            return null;
        }

        var prefix = steps.Where(step => step.Index < triggerIndex).ToList();
        var position = PlannedStepPosition.Derive(plan, prefix, manifestOf).Settle(plan, prefix, manifestOf);
        if (!PrematureFinal.HasUnresolvedStep(plan, position)
            || position.SemanticCorrectionSpent || position.ArgumentCorrectionSpent)
        {
            return null;
        }

        var pending = plan.Steps[position.Cursor];
        // A read-only, unconditional step is the only automatic continuation. Uncertain history fails closed even when
        // it belongs to an older revision; a replan response cannot reconcile a side effect.
        if (pending.Index != position.Cursor || pending.Activation is not null || pending.ExpectedTool is not { } tool
            || manifestOf(tool) is not { Risk: RiskLevel.Read }
            || !visibleTools.Any(manifest => manifest.Name == tool && manifest.Risk == RiskLevel.Read)
            || prefix.Any(step => step.ToolCall is not null
                && (step.Result is not { Outcome: ToolOutcome.Success }
                    || manifestOf(step.ToolCall.ToolName) is not { Risk: RiskLevel.Read })))
        {
            return null;
        }

        return position;
    }
}
