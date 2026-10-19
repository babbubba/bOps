// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.ScriptedAgentic;

/// <summary>
/// AE-1: the qualitative states of a scenario. A green test is NOT one of them: a test that passes while pinning a known defect is a
/// characterization test, and its diagnostic state is <see cref="BaselineDefect"/>, never <see cref="Pass"/>.
/// </summary>
internal enum AeOutcome
{
    /// <summary>The objective is reached and no known baseline defect shows in the run.</summary>
    Pass,

    /// <summary>The objective is reached only in part for a reason outside the prompt layer (a missing capability or permission), and the run does not hide it.</summary>
    Partial,

    /// <summary>The objective is not reached, or an invariant is broken, and no known baseline defect explains it: a regression to investigate.</summary>
    Fail,

    /// <summary>A known defect of the prompt/finalization layer (PRA-R2, PRA-R3, PRA-R4, PRA-P1) is observed and costs work or the objective.</summary>
    BaselineDefect,

    /// <summary>The scenario was not executed.</summary>
    NotRun,

    /// <summary>The case cannot be reproduced without changing production code; the report says exactly why.</summary>
    NotCovered,
}

/// <summary>The diagnostic judgement of one scenario, separate from whether its tests pass.</summary>
/// <param name="Outcome">The state per the rubric.</param>
/// <param name="ObjectiveMet">Whether the scenario's own objective was reached (a run can reach it and still carry a defect).</param>
/// <param name="Attributions">The defects of the roadmap this run exhibits, by id (PRA-R2, PRA-R3, PRA-R4, PRA-P1) and the layer they belong to.</param>
/// <param name="Reason">Why, in one sentence, from the observed counts.</param>
internal sealed record AeVerdict(AeOutcome Outcome, bool ObjectiveMet, IReadOnlyList<string> Attributions, string Reason)
{
    internal string Label => Outcome switch
    {
        AeOutcome.Pass => "PASS",
        AeOutcome.Partial => "PARTIAL",
        AeOutcome.Fail => "FAIL",
        AeOutcome.BaselineDefect => "BASELINE_DEFECT",
        AeOutcome.NotRun => "NOT_RUN",
        _ => "NOT_COVERED",
    };
}

/// <summary>
/// AE-1: the rubrics. They read the persisted state, the audit log and the classified requests of a run and never the script, except for
/// the one marker a scenario names (the absent capability or the deferred tool), which is the scenario's own definition of its question.
/// </summary>
internal static class AeRubric
{
    private const string PrematureLayer = "PRA-R3 (finalization/routing prompt)";
    private const string ProposalLayer = "PRA-R2 (plan-complete proposal channel)";
    private const string RecoveryLayer = "PRA-R4 (recovery text)";
    private const string RoutingLayer = "PRA-P1 (one-tool routing is stated, not explained; plausible, not proven)";

    /// <summary>The tools of the first plan that no step of the whole run executed.</summary>
    private static IReadOnlyList<string> NeverExecuted(AeRun run) =>
        [.. run.State.Plans[0].Steps.Select(step => step.ExpectedTool ?? "(none)").Where(tool => !run.Metrics.ToolsExecuted.Contains(tool, StringComparer.Ordinal))];

    /// <summary>Whether the latest plan revision still owes an executable step at the end of the run.</summary>
    internal static bool LatestPlanStillOwesSteps(AeRun run)
    {
        var plan = run.State.Plans[^1];
        return PlannedStepPosition.Derive(plan, run.State.Steps).Cursor < plan.Steps.Count;
    }

    /// <summary>T02 and T10: every planned operational check runs once and a usable final answer is delivered, without finals that the runtime has to reject.</summary>
    internal static AeVerdict OperationalPlan(AeRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var m = run.Metrics;
        var never = NeverExecuted(run);
        var denied = run.Audit<ToolCallAuditEvent>()
            .Where(evt => evt.Authorization is AuthorizationKind.PolicyDenied or AuthorizationKind.EntitlementDenied)
            .Select(evt => evt.Tool).Distinct(StringComparer.Ordinal).ToList();
        var skipped = never.Where(tool => !denied.Contains(tool, StringComparer.Ordinal)).ToList();
        var usable = AeObserver.HasAnswerContent(run.FinalText);

        if (m.FinalStatus == AgentTaskStatus.Completed && (LatestPlanStillOwesSteps(run) || !usable))
        {
            return new AeVerdict(AeOutcome.Fail, false, [], "Completed with a mandatory step still owed or without a usable final: an invariant is broken.");
        }

        if (m.PrematureFinals > 0)
        {
            var objective = m.FinalStatus == AgentTaskStatus.Completed && never.Count == 0;
            var cost = $"{m.PrematureFinals} premature final(s) cost {m.ReplanCalls} paid replan call(s) ({m.ReplansSkipped} skipped)";
            var governance = denied.Count > 0 ? $"; {string.Join(", ", denied)} was denied by policy, which is enforced and is not part of the defect" : string.Empty;
            var reason = m.FinalStatus != AgentTaskStatus.Completed
                ? $"{m.PrematureFinals} premature final(s) and no progress: the task ended {m.FinalStatus}/{m.Terminal}, with {m.ToolExecutions} tool execution(s)."
                : skipped.Count > 0
                    ? $"{cost}; the plan was revised and {skipped.Count} planned check(s) never ran ({string.Join(", ", skipped)}){governance}."
                    : $"{cost}; every planned check that policy allowed ran and a final was delivered{governance}.";
            return new AeVerdict(AeOutcome.BaselineDefect, objective, [PrematureLayer], reason);
        }

        if (m.FinalStatus == AgentTaskStatus.Completed && skipped.Count == 0)
        {
            return denied.Count == 0
                ? new AeVerdict(AeOutcome.Pass, true, [], "Every planned check ran once and a usable final was delivered with no rejected final.")
                : new AeVerdict(AeOutcome.Partial, false, [], $"Policy denied {string.Join(", ", denied)}; every other planned check ran and a usable final was delivered.");
        }

        return new AeVerdict(AeOutcome.Fail, false, [], $"Ended {m.FinalStatus} with {skipped.Count} planned check(s) not run, and no known baseline defect explains it.");
    }

    /// <summary>T05: a usable answer after an output-limited reply and a recovery turn.</summary>
    internal static AeVerdict RecoveredAnswer(AeRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var m = run.Metrics;
        var usable = m.FinalStatus == AgentTaskStatus.Completed && AeObserver.HasAnswerContent(run.FinalText);
        if (usable && m.RecoveryTurns == 0)
        {
            return new AeVerdict(AeOutcome.Pass, true, [], "A usable final was delivered without any recovery turn.");
        }

        var rc2AtPlanEnd = run.Calls.Where(call => call.Recovery == "RC2" && call.Operational.Count == 0).ToList();
        if (rc2AtPlanEnd.Any(call => RecoveryAsksForATool(run, call)))
        {
            return new AeVerdict(
                AeOutcome.BaselineDefect, usable, [RecoveryLayer],
                $"The recovery text asked for an offered tool at plan end, where only a control function exists; {m.EvidenceReads} evidence read(s), {m.RedundantEvidenceReads} of them redundant, and "
                + (usable ? "the answer was delivered." : $"no usable answer was delivered (task {m.FinalStatus}/{m.Terminal})."));
        }

        if (rc2AtPlanEnd.Count > 0 && !usable)
        {
            // PRA-R4 corrected: the text asks for the final answer. What follows is the model's own reply, which the runtime does not
            // decide; the F1 guard fails the task closed instead of completing it with nothing.
            return new AeVerdict(
                AeOutcome.Partial, false, [],
                $"The recovery text asked for the final answer at plan end; the model still replied with {m.EvidenceReads} evidence read(s) ({m.RedundantEvidenceReads} redundant) and no usable answer, and the runtime failed the task closed ({m.FinalStatus}/{m.Terminal}).");
        }

        return usable
            ? new AeVerdict(AeOutcome.Partial, true, [], "A usable final was delivered, after a recovery turn that no known defect explains.")
            : new AeVerdict(AeOutcome.Fail, false, [], $"No usable answer was delivered (task {m.FinalStatus}/{m.Terminal}) and no known baseline defect explains it.");
    }

    /// <summary>Whether the recovery turn of <paramref name="call"/> asks for a tool (the PRA-R4 defect at plan end) rather than for the final answer.</summary>
    private static bool RecoveryAsksForATool(AeRun run, AeCall call) =>
        run.Pa.Seen[call.Index].History
            .Where(turn => turn.Role == ChatRole.User && turn.Provenance?.Origin == ContextOrigin.Runtime)
            .Any(turn => turn.Content?.Contains("call the offered tool", StringComparison.OrdinalIgnoreCase) == true
                || turn.Content?.Contains("Either call a tool", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>T06: a check the final answer defers (<paramref name="deferredTool"/>) is either really requested and run, or only described.</summary>
    internal static AeVerdict DeferredCheck(AeRun run, string deferredTool)
    {
        ArgumentNullException.ThrowIfNull(run);
        var m = run.Metrics;
        var executed = m.ToolsExecuted.Count(tool => string.Equals(tool, deferredTool, StringComparison.Ordinal));
        var succeeded = executed > 0 && run.State.Steps.Any(step => step.ToolCall?.ToolName == deferredTool && step.Result is { Outcome: ToolOutcome.Success });
        var describedOnly = executed == 0 && run.FinalText?.Contains(deferredTool, StringComparison.Ordinal) == true;

        if (succeeded)
        {
            return new AeVerdict(AeOutcome.Pass, true, [], $"The deferred check {deferredTool} was requested through the runtime's channel and succeeded.");
        }

        if (executed > 0)
        {
            return new AeVerdict(
                AeOutcome.Partial, false, [],
                $"The deferred check {deferredTool} was requested through a recognized channel and executed {executed} time(s), but the tool's own path policy refused it: the objective is unreachable without an authorization the scenario must not grant.");
        }

        return describedOnly && m.FinalStatus == AgentTaskStatus.Completed
            ? new AeVerdict(
                AeOutcome.BaselineDefect, false, [ProposalLayer],
                $"The check {deferredTool} appears only in the final prose; no tool call, plan step or replan requested it, so it ran 0 times and the task is nevertheless {m.FinalStatus}.")
            : new AeVerdict(AeOutcome.Fail, false, [], "The deferred check neither ran nor was proposed, and no known baseline defect explains it.");
    }

    /// <summary>T08: the question needs a capability that does not exist (<paramref name="absentCapability"/>); the run must stay honest about it.</summary>
    internal static AeVerdict AbsentCapability(AeRun run, string absentCapability)
    {
        ArgumentNullException.ThrowIfNull(run);
        var m = run.Metrics;
        if (m.ToolsExecuted.Contains(absentCapability, StringComparer.Ordinal) || run.Pa.Tools.ContainsKey(absentCapability))
        {
            return new AeVerdict(AeOutcome.Fail, false, [], $"{absentCapability} is registered or executed: an invented tool was run.");
        }

        var proposedInProse = run.FinalText?.Contains(absentCapability, StringComparison.Ordinal) == true;
        if (proposedInProse && m.FinalStatus == AgentTaskStatus.Completed)
        {
            return new AeVerdict(
                AeOutcome.BaselineDefect, false, [ProposalLayer],
                $"The final proposes the absent capability {absentCapability} as a check: the registry has no such tool, nothing can ever run it, and the task is {m.FinalStatus} with the objective unmet.");
        }

        return m.FinalStatus == AgentTaskStatus.Completed && AeObserver.HasAnswerContent(run.FinalText)
            ? new AeVerdict(
                AeOutcome.Partial, false, [],
                $"The objective needs a capability no tool provides; the run did not present {absentCapability} as available (rejected {m.RefusedToolCalls} call(s), {m.ReplansAccepted} replan(s)) and delivered an answer that states the limit.")
            : new AeVerdict(AeOutcome.Fail, false, [], "The run ended without a usable answer and no known baseline defect explains it.");
    }

    /// <summary>P1: a call to a later step's tool is refused and corrected in place; the cost is the wasted request.</summary>
    internal static AeVerdict RoutingMisfire(AeRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var m = run.Metrics;
        var misrouted = run.Steps.Count(step => step.Classification == nameof(PlannedStepExecutionClassification.UnofferedTool));
        var done = m.FinalStatus == AgentTaskStatus.Completed && NeverExecuted(run).Count == 0 && AeObserver.HasAnswerContent(run.FinalText);
        if (misrouted == 0)
        {
            return done
                ? new AeVerdict(AeOutcome.Pass, true, [], "Every planned check ran in order with no refused call.")
                : new AeVerdict(AeOutcome.Fail, false, [], "The run did not finish its plan and no known baseline defect explains it.");
        }

        return done
            ? new AeVerdict(
                AeOutcome.BaselineDefect, true, [RoutingLayer],
                $"{misrouted} call(s) named a tool that was not offered at the current step; the runtime refused and corrected in place, {m.RefusedToolCalls} refused call(s) and one extra request, and every check then ran in order.")
            : new AeVerdict(AeOutcome.Fail, false, [], "A misrouted call left the plan unfinished.");
    }

    /// <summary>The LIVE-05 plus Knowledge case (T10): knowledge never completes the plan, and the finals that follow it are the measured cost.</summary>
    internal static AeVerdict KnowledgeThenSteps(AeRun run, int scriptedKnowledgeReads)
    {
        ArgumentNullException.ThrowIfNull(run);
        var m = run.Metrics;
        if (m.KnowledgeReads > scriptedKnowledgeReads || m.RepeatedKnowledgeReads > 0)
        {
            return new AeVerdict(AeOutcome.Fail, false, [], "The runtime served more knowledge reads than the script asked for.");
        }

        return OperationalPlan(run);
    }
}
