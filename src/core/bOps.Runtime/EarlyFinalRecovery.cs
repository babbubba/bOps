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

    /// <summary>A finished malformed replan whose last reply stopped at the provider's output limit.</summary>
    internal static bool IsOutputLimited(PlanStep step) => HasMalformedCheckpoint(step)
        && string.Equals(step.EarlyFinalReplanCalls![^1].FinishReason, "length", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// AF-05H2-R2: an early final that has made no replan call of its own, in a plan revision whose earlier early-final replan
    /// already stopped at the output limit without a plan and was continued. The same replan over a longer history cannot fit
    /// better, so it is not paid for again; the continuation proof below still decides everything.
    /// </summary>
    internal static bool SkipsPaidReplan(AgentPlan plan, IReadOnlyList<PlanStep> steps, int triggerIndex)
    {
        var trigger = steps.FirstOrDefault(step => step.Index == triggerIndex);
        if (trigger is null || !PrematureFinal.IsStep(trigger) || trigger.PlanRevision != plan.Revision
            || trigger.EarlyFinalReplanCalls is { Count: > 0 })
        {
            return false;
        }

        var revision = steps.Where(step => step.PlanRevision == plan.Revision && step.Index < triggerIndex).ToList();
        return revision.Any(earlier => IsOutputLimited(earlier)
            && revision.Any(recovery => IsStep(recovery) && recovery.Index > earlier.Index));
    }

    /// <summary>The stable audit refusal code of <paramref name="reason"/>, for example <c>unresolved_tool_failure</c>.</summary>
    internal static string RefusalCode(EarlyFinalRecoveryReason reason)
    {
        var name = reason.ToString();
        var code = new System.Text.StringBuilder(name.Length + 8);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && code.Length > 0)
            {
                code.Append('_');
            }

            code.Append(char.ToLowerInvariant(c));
        }

        return code.ToString();
    }

    internal static PlannedStepPosition? SafePosition(
        AgentPlan plan, IReadOnlyList<PlanStep> steps, int triggerIndex, Func<string, ToolManifest?> manifestOf,
        IReadOnlyList<ToolManifest> visibleTools) =>
        Evaluate(plan, steps, triggerIndex, manifestOf, visibleTools) is ({ Accepted: true }, { } position) ? position : null;

    /// <summary>
    /// Whether the persisted plan may continue at its unchanged cursor after the early final at <paramref name="triggerIndex"/>
    /// got no plan from its replan. Only an unconditional, available, read-only pending step continues, after a history in
    /// which every tool call is read-only with a known outcome and every failure was a validation failure (the call did not
    /// run as planned) later resolved by a matched, successful call of the same planned step in the same revision.
    /// </summary>
    internal static (EarlyFinalRecoveryDecision Decision, PlannedStepPosition? Position) Evaluate(
        AgentPlan plan, IReadOnlyList<PlanStep> steps, int triggerIndex, Func<string, ToolManifest?> manifestOf,
        IReadOnlyList<ToolManifest> visibleTools)
    {
        (EarlyFinalRecoveryDecision, PlannedStepPosition?) Refuse(
            EarlyFinalRecoveryReason reason, int? cursor = null, int? blocking = null) =>
            (new EarlyFinalRecoveryDecision
            {
                Accepted = false, Reason = reason, PlanRevision = plan.Revision, Cursor = cursor, BlockingStepIndex = blocking,
            }, null);

        var trigger = steps.LastOrDefault(step => step.PlanRevision == plan.Revision);
        if (trigger?.Index != triggerIndex || !PrematureFinal.IsStep(trigger))
        {
            return Refuse(EarlyFinalRecoveryReason.TriggerNotLatest);
        }

        if (plan.SemanticContractVersion != 1)
        {
            return Refuse(EarlyFinalRecoveryReason.LegacyPlanContract);
        }

        var skipped = SkipsPaidReplan(plan, steps, triggerIndex);
        if (!HasMalformedCheckpoint(trigger) && !skipped)
        {
            return Refuse(EarlyFinalRecoveryReason.NoMalformedReplan);
        }

        var prefix = steps.Where(step => step.Index < triggerIndex).ToList();
        var position = PlannedStepPosition.Derive(plan, prefix, manifestOf).Settle(plan, prefix, manifestOf);
        if (position.ReplanRequired)
        {
            return Refuse(EarlyFinalRecoveryReason.IncoherentPlanPosition);
        }

        if (!PrematureFinal.HasUnresolvedStep(plan, position))
        {
            return Refuse(EarlyFinalRecoveryReason.NoUnresolvedPlannedStep);
        }

        var cursor = position.Cursor;
        if (position.SemanticCorrectionSpent || position.ArgumentCorrectionSpent || position.ToolCorrectionSpent)
        {
            return Refuse(EarlyFinalRecoveryReason.PendingCorrection, cursor);
        }

        // Anti-loop: another continuation of the same revision must be strictly further along the plan.
        if (prefix.LastOrDefault(step => step.PlanRevision == plan.Revision && IsStep(step)
                && step.EarlyFinalRecoveryCursor >= cursor) is { } stalled)
        {
            return Refuse(EarlyFinalRecoveryReason.RepeatedWithoutProgress, cursor, stalled.Index);
        }

        var pending = plan.Steps[cursor];
        if (pending.Index != cursor || pending.ExpectedTool is not { } tool)
        {
            return Refuse(EarlyFinalRecoveryReason.IncoherentPlanPosition, cursor);
        }

        if (pending.Activation is not null)
        {
            return Refuse(EarlyFinalRecoveryReason.ConditionalPendingStep, cursor);
        }

        if (manifestOf(tool) is not { Risk: RiskLevel.Read })
        {
            return Refuse(EarlyFinalRecoveryReason.PendingToolNotReadOnly, cursor);
        }

        if (!visibleTools.Any(manifest => manifest.Name == tool && manifest.Risk == RiskLevel.Read))
        {
            return Refuse(EarlyFinalRecoveryReason.PendingToolUnavailable, cursor);
        }

        // Uncertain history fails closed even when it belongs to an older revision; a replan response cannot reconcile a side
        // effect, and an unresolved failure is never reinterpreted as coverage.
        var resolved = new List<int>();
        foreach (var step in prefix.Where(step => step.ToolCall is not null).OrderBy(step => step.Index))
        {
            if (HistoryBlock(plan, prefix, step, manifestOf) is { } block)
            {
                return Refuse(block, cursor, step.Index);
            }

            if (step.Result is not { Outcome: ToolOutcome.Success })
            {
                resolved.Add(step.Index);
            }
        }

        return (new EarlyFinalRecoveryDecision
        {
            Accepted = true,
            Reason = EarlyFinalRecoveryReason.Continued,
            PlanRevision = plan.Revision,
            Cursor = cursor,
            ResolvedValidationStepIndexes = resolved.Count > 0 ? resolved : null,
            PaidReplanSkipped = skipped,
        }, position);
    }

    private static EarlyFinalRecoveryReason? HistoryBlock(
        AgentPlan plan, IReadOnlyList<PlanStep> prefix, PlanStep step, Func<string, ToolManifest?> manifestOf)
    {
        if (step.Result is not { } result)
        {
            return EarlyFinalRecoveryReason.UnknownToolOutcome;
        }

        if (result.Outcome == ToolOutcome.Denied || result.FailureKind == ToolFailureKind.Authorization)
        {
            return EarlyFinalRecoveryReason.AuthorizationDenied;
        }

        if (result.Outcome == ToolOutcome.Timeout || result.FailureKind == ToolFailureKind.Timeout)
        {
            return EarlyFinalRecoveryReason.ToolTimeout;
        }

        if (manifestOf(step.ToolCall!.ToolName) is not { Risk: RiskLevel.Read })
        {
            return EarlyFinalRecoveryReason.NonReadOnlyHistory;
        }

        if (step.VerificationStatus == VerificationStatus.Refuted)
        {
            return EarlyFinalRecoveryReason.VerificationRefuted;
        }

        return result.Outcome == ToolOutcome.Success || IsResolvedValidationFailure(plan, prefix, step)
            ? null
            : EarlyFinalRecoveryReason.UnresolvedToolFailure;
    }

    /// <summary>
    /// A planned-tool call of the current revision that the runtime classified as a semantic mismatch or an argument-validation
    /// failure, followed by a matched, successful call of the same tool at the same planned index of the same revision.
    /// </summary>
    private static bool IsResolvedValidationFailure(AgentPlan plan, IReadOnlyList<PlanStep> prefix, PlanStep step) =>
        step.ToolCall is { ToolNameError: null } call
        && step.Result is { Outcome: ToolOutcome.Failure, FailureKind: ToolFailureKind.Validation }
        && step.ExecutionClassification is PlannedStepExecutionClassification.SemanticMismatch
            or PlannedStepExecutionClassification.ArgumentValidationFailure
        && step.PlanRevision == plan.Revision
        && step.PlannedStepIndex is { } planned && planned >= 0 && planned < plan.Steps.Count
        && string.Equals(plan.Steps[planned].ExpectedTool, call.ToolName, StringComparison.Ordinal)
        && prefix.Any(later => later.Index > step.Index && later.PlanRevision == plan.Revision
            && later.PlannedStepIndex == planned
            && later.ExecutionClassification == PlannedStepExecutionClassification.Matched
            && later.ToolCall is { ToolNameError: null } laterCall
            && string.Equals(laterCall.ToolName, call.ToolName, StringComparison.Ordinal)
            && later.Result is { Outcome: ToolOutcome.Success }
            && later.VerificationStatus != VerificationStatus.Refuted);
}
