// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Tests.ScriptedAgentic;

/// <summary>
/// One AE-1 case: a scripted run, its rubric, and what the rubric is expected to say before and after the prompt/finalization
/// work. <see cref="Today"/> is pinned by <see cref="AeDiagnosticTests"/> as OBSERVED behaviour; <see cref="Desired"/> is the direction
/// stated in the AE-1 brief, not a design, and no test asserts it.
/// </summary>
internal sealed record AeCase(
    string Id,
    string Origin,
    string Title,
    Func<Task<AeRun>> Run,
    Func<AeRun, AeVerdict> Evaluate,
    AeOutcome Today,
    string Desired,
    bool IsControl = false);

/// <summary>AE-1: the table of cases (the five brief scenarios, their variants, and the two healthy controls of the rubrics).</summary>
internal static class AeCases
{
    internal static IReadOnlyList<AeCase> All { get; } =
    [
        new("AE1-T02", "T02 / RELEASE-VALIDATION-01", "Three premature finals; a replan adds a step; a paid replan is cut at the output limit; LIVE-05 continuations",
            AeScenarios.T02PrematureFinals, AeRubric.OperationalPlan, AeOutcome.BaselineDefect,
            "No invitation to finalize while steps remain, so fewer premature finals; any early-final replan that remains does not add steps by default (the master's R3: \"an early-final replan must not automatically add useless steps\"), and the anti-loop bounds and LIVE-05 stay."),
        new("AE1-T02-LOOP", "T02 anti-loop bound", "Premature final after premature final with an identical replan: nothing progresses",
            AeScenarios.T02NoProgressLoop, AeRubric.OperationalPlan, AeOutcome.BaselineDefect,
            "The model proposes the pending call, so the loop is never entered; the replan budget stays a guard rail that is not reached."),
        new("AE1-T02-EMPTY", "T02 / PRA-R3 way out", "The early-final replan answers with an empty plan; the remaining checks never run",
            AeScenarios.T02EmptyPlanWayOut, AeRubric.OperationalPlan, AeOutcome.BaselineDefect,
            "A specific early-final lead that allows an empty plan only when nothing is left (the master's R3), so an empty plan is no longer the generic way out of unfinished work; Completed with owed steps stays impossible unless the plan was formally revised."),
        new("AE1-T02-POLICY", "T02 / LIVE-05 governance", "The LIVE-05 continuation offers a tool that policy forbids",
            AeScenarios.T02ContinuationUnderPolicy, AeRubric.OperationalPlan, AeOutcome.BaselineDefect,
            "Unchanged safety behaviour (the denial is enforced and audited); only the premature final in front of it goes away."),
        new("AE1-T02-CLEAN", "T02 control", "The same plan with no premature final (rubric control)",
            AeScenarios.T02CleanRun, AeRubric.OperationalPlan, AeOutcome.Pass, "Unchanged.", IsControl: true),
        new("AE1-P1", "T04 / T05 reasoning", "A later step's tool is called while the earlier step's tool is the only one offered",
            AeScenarios.P1CallsALaterStepsTool, AeRubric.RoutingMisfire, AeOutcome.BaselineDefect,
            "The prompt says why one tool is offered and how names map, so the misroute is not drawn; the in-place correction stays as the safety net."),
        new("AE1-T05", "T05 / LIVE-F1-FIX-01", "Evidence read, reply cut at the output limit, recovery turn, second identical read, final \">\"",
            () => AeScenarios.T05LengthThenGreaterThan(), AeRubric.RecoveredAnswer, AeOutcome.Partial,
            "DONE (PRA-R4): the recovery text depends on the state, at plan end it asks for the final answer, not for an offered tool, and a satisfied recovery turn is dropped. The scripted model still reads again and answers \">\", which is its reply, not the prompt: the verdict is Partial and the \">\" guard (F1) fails the task closed."),
        new("AE1-T05-CLEAN", "T05 control", "An evidence read followed by a substantive final (rubric control)",
            AeScenarios.T05CleanRun, AeRubric.RecoveredAnswer, AeOutcome.Pass, "Unchanged.", IsControl: true),
        new("AE1-T06-A", "T06", "The deferred size check is requested natively at plan end and run; the tool's path policy refuses",
            AeScenarios.T06CheckRequestedNatively, run => AeRubric.DeferredCheck(run, "test.size"), AeOutcome.Partial,
            "A new check is proposed through the channel the runtime recognizes (the native call of the canonical tool, ToolRoutingTests C1-C6) or the answer reports the limit; the path refusal stays and the answer still states it."),
        new("AE1-T06-B", "T06", "The deferred size check is only described in the final prose",
            AeScenarios.T06CheckOnlyDescribed, run => AeRubric.DeferredCheck(run, "test.size"), AeOutcome.BaselineDefect,
            "A needed check is either requested through the recognized channel or reported as not done; it is no longer left as prose in a Completed task, and no natural-language parser is added."),
        new("AE1-T08-A", "T08", "The absent tool system.sensor is proposed in the final prose",
            AeScenarios.T08ProposedInProse, run => AeRubric.AbsentCapability(run, AeScenarios.AbsentCapability), AeOutcome.BaselineDefect,
            "The plan-complete prompt does not invite naming a check that cannot run; the answer states that no tool provides the capability."),
        new("AE1-T08-B", "T08", "The absent tool system.sensor is called natively at plan end",
            AeScenarios.T08CalledNatively, run => AeRubric.AbsentCapability(run, AeScenarios.AbsentCapability), AeOutcome.Partial,
            "Unchanged: the call is rejected, audited and replanned; the objective stays unreachable."),
        new("AE1-T10", "T10", "Knowledge read, then three premature finals while operational steps remain",
            AeScenarios.T10KnowledgeThenPrematureFinals, run => AeRubric.KnowledgeThenSteps(run, 1), AeOutcome.BaselineDefect,
            "The Knowledge control text no longer offers a final answer while steps are pending; one read, no premature final after it, grants unchanged."),
    ];

    internal static AeCase Get(string id) => All.Single(item => item.Id == id);

    internal static TheoryData<string> Ids => [.. All.Select(item => item.Id)];

    internal static TheoryData<string> NonControlIds => [.. All.Where(item => !item.IsControl).Select(item => item.Id)];
}
