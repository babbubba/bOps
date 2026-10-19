// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Tests.PromptAssembly;

/// <summary>One golden case: a scenario run and the provider requests of it that are frozen.</summary>
internal sealed record PaCase(
    string Id, string File, string Title, Func<Task<PaRun>> Run, IReadOnlyList<(int Index, string Label)> Picks);

/// <summary>PA-1: the table of the golden cases (PA1-01 … PA1-10; PA1-02 and PA1-08 have two variants each).</summary>
internal static class PaCases
{
    internal static IReadOnlyList<PaCase> All { get; } =
    [
        new("PA1-01", "pa1-01-planning", "Ordinary planning request", () => PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan()),
            [(0, "initial planning call")]),
        new("PA1-02A", "pa1-02a-replan-ordinary", "Ordinary replan (a tool is proposed once the plan is complete)",
            () => PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan()),
            [(4, "replan after the proposal of a tool at plan end (trigger PlanDeviation)")]),
        new("PA1-02B", "pa1-02b-replan-early-final", "Replan caused by an early final",
            () => PaCompose.RunAsync(PaScenarios.EarlyFinalReplan()),
            [(2, "step request that drew the premature final"), (3, "early-final replan (trigger EarlyFinalWithUnresolvedPlan)")]),
        new("PA1-03", "pa1-03-step-pending-tool", "Step with a pending operational tool",
            () => PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan()),
            [(1, "step 0 of a two-step plan: the first tool is pending, the second is not offered")]),
        new("PA1-04", "pa1-04-evidence-read", "Step with Evidence Read",
            () => PaCompose.RunAsync(PaScenarios.EvidenceRead()),
            [(2, "step 1 with persisted evidence: control function and evidence index"), (3, "continuation after the evidence read")]),
        new("PA1-05", "pa1-05-knowledge", "Step with Knowledge",
            () => PaScenarios.Knowledge(),
            [(1, "step 0 with the published knowledge index and the knowledge control function"), (2, "continuation after the knowledge read")]),
        new("PA1-06", "pa1-06-plan-exhausted", "Plan exhausted",
            () => PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan()),
            [(3, "both planned steps complete: no operational tool offered")]),
        new("PA1-07", "pa1-07-recovery-rc1", "Recovery RC1 (empty reply)",
            () => PaCompose.RunAsync(PaScenarios.EmptyReply()),
            [(2, "step request that drew the empty reply"), (3, "the same step after the RC1 retry instruction")]),
        new("PA1-08A", "pa1-08a-recovery-rc2-pending", "Recovery RC2 (output limit) with an operational tool pending",
            () => PaCompose.RunAsync(PaScenarios.OutputLimitedWhilePending()),
            [(2, "step request that drew the output-limited reply"), (3, "the same step after the RC2 retry instruction")]),
        new("PA1-08B", "pa1-08b-recovery-rc2-exhausted", "Recovery RC2 (output limit) with the plan exhausted",
            () => PaCompose.RunAsync(PaScenarios.OutputLimitedWhenComplete()),
            [(2, "plan-complete request that drew the output-limited reply"), (3, "the same state after the RC2 retry instruction")]),
        new("PA1-09", "pa1-09-live05-continuation", "LIVE-05 continuation after a failed early-final replan",
            () => PaCompose.RunAsync(PaScenarios.EarlyFinalContinuation()),
            [
                (2, "step request that drew the premature final"),
                (3, "early-final replan cut at the output limit"),
                (4, "continuation step: the persisted plan resumes at its cursor"),
            ]),
        new("PA1-10", "pa1-10-grounding-check", "Evidence grounding check",
            () => PaCompose.RunAsync(PaScenarios.Grounding()),
            [(2, "final synthesis request (the typed-fact ledger is in the system prompt)"), (3, "EvidenceGroundingCheck/v1 request")]),
    ];

    internal static PaCase Get(string id) => All.Single(item => item.Id == id);
}
