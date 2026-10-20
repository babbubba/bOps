// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.AgentFramework.Tests.Regression;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.ScriptedAgentic;

/// <summary>
/// AE-1, kind A — CHARACTERIZATION. Each test pins what the runtime does TODAY for one scripted sequence: which requests it makes, what it
/// offers on each, what it persists and what it counts. They stay green while the real behaviour equals this baseline, including where
/// that behaviour is the defect under measurement; a green test here is NOT a diagnostic success (see <see cref="AeDiagnosticTests"/>).
/// When <c>PROMPT-ROUTING-FINALIZATION</c> changes a prompt, the expectations marked "BASELINE" below are rewritten on purpose, in the same
/// change; the safety expectations (<see cref="AeSafetyInvariantTests"/>) are never rewritten.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed class AeCharacterizationTests
{
    private static void Sequence(AeRun run, string[] calls, string[] steps)
    {
        Assert.Equal(calls, run.Calls.Select(AeInvariants.CallLine));
        Assert.Equal(steps, run.Steps.Select(AeInvariants.StepLine));
    }

    // ---- AE1-T02 ----

    [Fact]
    public async Task AE1_T02_ThreePrematureFinals_AreAnsweredByOneAcceptedReplan_OnePaidReplanCutAtTheOutputLimit_AndTwoLive05Continuations()
    {
        var run = await AeScenarios.T02PrematureFinals();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:test.read op=test.read c=0/3",
            "2 Step text op=test.other ctl=runtime.evidence_read c=1/3",
            "3 Replanning plan",
            "4 Step call:test.other op=test.other ctl=runtime.evidence_read c=0/3",
            "5 Step text op=test.logs ctl=runtime.evidence_read c=1/3",
            "6 Replanning plan",
            "7 Step call:test.logs op=test.logs ctl=runtime.evidence_read c=1/3 LIVE-05",
            "8 Step text op=test.extra ctl=runtime.evidence_read c=2/3",
            "9 Step call:test.extra op=test.extra ctl=runtime.evidence_read c=2/3 LIVE-05",
            "10 Step text ctl=runtime.evidence_read c=3/3",
        ],
        [
            "0 tool test.read Success r0 p0 after=1",
            "1 premature-final r0 after=1",
            "2 tool test.other Success r1 p0 after=1",
            "3 premature-final r1 after=1",
            "4 live05-continuation r1 after=1",
            "5 tool test.logs Success r1 p1 after=2",
            "6 premature-final r1 after=2",
            "7 live05-continuation r1 after=2",
            "8 tool test.extra Success r1 p2 after=3",
            "9 final r1 after=3",
        ]);

        var m = run.Metrics;
        Assert.Equal((11, 3, 2, 2, 1, 2), (m.ModelCalls, m.PrematureFinals, m.ReplanCalls, m.ReplansCharged, m.ReplansAccepted, m.Live05Continuations));
        Assert.Equal(1, m.ReplansSkipped); // BASELINE: the third premature final of the revision is not paid for again
        Assert.Equal(["test.read", "test.other", "test.logs", "test.extra"], m.ToolsExecuted); // every operation exactly once, in plan order
        Assert.Equal((AgentTaskStatus.Completed, TaskTerminalKind.Completed), (m.FinalStatus, m.Terminal!.Value));
        Assert.Equal(1210L, m.SyntheticTokens); // 11 calls x (100 + 10): the offline upstream's own declaration, not a Qwen figure
        Assert.Equal(new Dictionary<CallPurpose, int>
        {
            [CallPurpose.Planning] = 1, [CallPurpose.Replanning] = 2, [CallPurpose.Step] = 8, [CallPurpose.EvidenceReadContinuation] = 0,
            [CallPurpose.Summarization] = 0, [CallPurpose.GroundingCheck] = 0, [CallPurpose.Disclosure] = 0,
        }, m.CallsByPurpose);
    }

    [Fact]
    public async Task AE1_T02_TheReplanThatAddsAStep_IsTrackedAndCarriesItsMotivation_AndAddsNothingElse()
    {
        var run = await AeScenarios.T02PrematureFinals();

        var diff = Assert.Single(run.PlanDiffs);
        Assert.Equal((0, 1), (diff.FromRevision, diff.ToRevision));
        Assert.Equal(["test.other", "test.logs"], diff.Owed); // what was still owed when revision 0 ended
        Assert.Equal(["test.extra"], diff.Added); // one step added, none dropped
        Assert.Empty(diff.Dropped);
        Assert.Equal("Add a cross-check of the extra counters after the logs.", diff.Rationale); // the persisted motivation of the change
        Assert.Equal(diff.Rationale, run.State.Plans[1].Rationale);
        Assert.Equal(2, run.State.Plans.Count); // the truncated replan did not create a revision
    }

    [Fact]
    public async Task AE1_T02_TheContinuations_ResumeThePersistedPlanAtItsCursor_AndTheThirdPrematureFinalSpendsNoReplanCall()
    {
        var run = await AeScenarios.T02PrematureFinals();

        var continuations = run.Calls.Where(call => call.Live05Continuation).ToList();
        Assert.Equal([1, 2], continuations.Select(call => call.Cursor!.Value)); // strictly further along the plan each time (anti-loop)
        Assert.Equal(["test.logs"], continuations[0].Operational);
        Assert.Equal(["test.extra"], continuations[1].Operational);
        Assert.Equal(2, run.Steps.Count(step => step.Kind == "live05-continuation"));
        Assert.Equal(2, run.Calls.Count(call => call.Purpose == CallPurpose.Replanning)); // premature #1 and #2 only: #3 is not replanned
        var decisions = run.State.Steps.Select(step => step.EarlyFinalRecoveryDecision).OfType<EarlyFinalRecoveryDecision>().ToList();
        Assert.Equal([false, true], decisions.Select(decision => decision.PaidReplanSkipped));
        Assert.All(decisions, decision => Assert.Equal(EarlyFinalRecoveryReason.Continued, decision.Reason));
        Assert.Equal([1, 2], decisions.Select(decision => decision.Cursor!.Value));
    }

    [Fact]
    public async Task AE1_T02_TheRuntimeRecordsEachContinuationInTheAuditLog()
    {
        var run = await AeScenarios.T02PrematureFinals();

        var lifecycle = run.Audit<TaskLifecycleAuditEvent>().Where(evt => evt.Stage == TaskLifecycleStage.EarlyFinalPlanContinued).ToList();
        Assert.Equal(2, lifecycle.Count); // the one paid-replan continuation and the one skipped
        Assert.DoesNotContain(run.Audit<TaskLifecycleAuditEvent>(), evt => evt.Stage == TaskLifecycleStage.EarlyFinalPlanContinuationRefused);
    }

    [Fact]
    public async Task AE1_T02_Loop_FourPrematureFinalsWithAnIdenticalPlan_EndBoundedAndNeverCompleted()
    {
        var run = await AeScenarios.T02NoProgressLoop();
        var options = new AgentRunnerOptions();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step text op=test.read c=0/3",
            "2 Replanning plan",
            "3 Step text op=test.read c=0/3",
            "4 Replanning plan",
            "5 Step text op=test.read c=0/3",
            "6 Replanning plan",
            "7 Step text op=test.read c=0/3",
        ],
        [
            "0 premature-final r0 after=0",
            "1 premature-final r1 after=0",
            "2 premature-final r2 after=0",
            "3 premature-final r3 after=0",
        ]);

        var m = run.Metrics;
        Assert.Equal((AgentTaskStatus.ReplanLimitReached, TaskTerminalKind.ReplanLimit), (m.FinalStatus, m.Terminal!.Value));
        Assert.Equal((4, 3, 3, 0), (m.PrematureFinals, m.ReplansAccepted, m.ReplansCharged, m.ToolExecutions));
        Assert.Equal(options.MaxReplans, m.ReplansCharged); // the stop is the configured replan budget, not a coincidence of the script
        Assert.Equal(4, run.Calls.Count(call => call.Cursor == 0)); // the cursor never moved
        Assert.Equal(["test.read", "test.other", "test.logs"], run.State.Plans.Select(plan => plan.Steps.Select(step => step.ExpectedTool!)).Last()); // identical plan, no change
        Assert.Equal(880L, m.SyntheticTokens);
    }

    [Fact]
    public async Task AE1_T02_EmptyPlan_AFormalRevisionLetsTheTaskCompleteWithTwoPlannedChecksNeverRun()
    {
        var run = await AeScenarios.T02EmptyPlanWayOut();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:test.read op=test.read c=0/3",
            "2 Step text op=test.other ctl=runtime.evidence_read c=1/3",
            "3 Replanning plan",
            "4 Step text ctl=runtime.evidence_read",
        ],
        [
            "0 tool test.read Success r0 p0 after=1",
            "1 premature-final r0 after=1",
            "2 final r1 after=0",
        ]);

        var diff = Assert.Single(run.PlanDiffs);
        Assert.Equal(["test.other", "test.logs"], diff.Owed);
        Assert.Equal(["test.other", "test.logs"], diff.Dropped); // BASELINE: the revision removed every check that was owed
        Assert.Empty(run.State.Plans[1].Steps);
        Assert.Equal(AgentTaskStatus.Completed, run.Metrics.FinalStatus);
        Assert.Equal(["test.read"], run.Metrics.ToolsExecuted);
        Assert.False(AeRubric.LatestPlanStillOwesSteps(run)); // the invariant holds: Completed only because the plan itself was revised
        Assert.Equal(1, run.Metrics.PrematureFinals);
    }

    [Fact]
    public async Task AE1_T02_Policy_TheLive05ContinuationOffersTheForbiddenToolButThePolicyStillDeniesIt_AndTheReplanRoutesAround()
    {
        var run = await AeScenarios.T02ContinuationUnderPolicy();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:test.read op=test.read c=0/3",
            "2 Step text op=test.logs ctl=runtime.evidence_read c=1/3",
            "3 Replanning plan",
            "4 Step call:test.logs op=test.logs ctl=runtime.evidence_read c=1/3 LIVE-05",
            "5 Replanning plan",
            "6 Step call:test.other op=test.other ctl=runtime.evidence_read c=0/1",
            "7 Step text ctl=runtime.evidence_read c=1/1",
        ],
        [
            "0 tool test.read Success r0 p0 after=1",
            "1 premature-final r0 after=1",
            "2 live05-continuation r0 after=1",
            "3 tool test.logs Failure r0 p1 after=1",
            "4 tool test.other Success r1 p0 after=1",
            "5 final r1 after=1",
        ]);

        // The continuation is a prompt-level resume of a Read step; it grants nothing (the LIVE-05 turn says so) and policy decides.
        Assert.Equal(0, run.Pa.Tools["test.logs"].Invocations);
        var denial = Assert.Single(run.Audit<ToolCallAuditEvent>(), evt => evt.Tool == "test.logs");
        Assert.Equal((AuthorizationKind.PolicyDenied, ToolFailureKind.Authorization), (denial.Authorization, denial.FailureKind!.Value));
        Assert.Null(denial.EffectiveTimeout); // never reached execution
        var decision = Assert.Single(run.Audit<PolicyDecisionAuditEvent>(), evt => evt.Tool == "test.logs");
        Assert.Equal(PolicyMode.Forbidden, decision.Mode);
        Assert.Equal(["test.read", "test.other"], run.Metrics.ToolsExecuted);
        Assert.Equal((1, 1, 2), (run.Metrics.RefusedToolCalls, run.Metrics.PrematureFinals, run.Metrics.ReplansCharged));
    }

    // ---- AE1-P1 ----

    [Fact]
    public async Task AE1_P1_ACallToALaterStepsTool_IsRefusedAuditedAndCorrectedInPlace_WithoutAReplan()
    {
        var run = await AeScenarios.P1CallsALaterStepsTool();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:test.other op=test.read c=0/3",
            "2 Step call:test.read op=test.read ctl=runtime.evidence_read c=0/3",
            "3 Step call:test.other op=test.other ctl=runtime.evidence_read c=1/3",
            "4 Step call:test.logs op=test.logs ctl=runtime.evidence_read c=2/3",
            "5 Step text ctl=runtime.evidence_read c=3/3",
        ],
        [
            "0 refused-call test_other Failure r0 p0 after=0",
            "1 tool test.read Success r0 p0 after=1",
            "2 tool test.other Success r0 p1 after=2",
            "3 tool test.logs Success r0 p2 after=3",
            "4 final r0 after=3",
        ]);

        const string correction = "The previous call named a tool that is not offered for this step, so nothing ran.";
        Assert.DoesNotContain(correction, run.Pa.Seen[1].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(correction, run.Pa.Seen[2].SystemPrompt, StringComparison.Ordinal); // the one in-place correction, on the next request only
        Assert.DoesNotContain(correction, run.Pa.Seen[3].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(PlannedStepExecutionClassification.UnofferedTool, run.State.Steps[0].ExecutionClassification);
        var refusal = Assert.Single(run.Audit<ToolCallAuditEvent>(), evt => evt.Authorization == AuthorizationKind.UnknownTool);
        Assert.Null(refusal.EffectiveTimeout);
        Assert.Equal((6, 1, 3, 0, 0), (run.Metrics.ModelCalls, run.Metrics.RefusedToolCalls, run.Metrics.ToolExecutions, run.Metrics.ReplansAccepted, run.Metrics.PrematureFinals));
        Assert.Equal(["test.read", "test.other", "test.logs"], run.Metrics.ToolsExecuted); // the later tool never ran early (D-043)
    }

    // ---- AE1-T05 ----

    [Fact]
    public async Task AE1_T05_LengthThenEvidenceReadAgainThenGreaterThan_FailsAsEmptyResponse_AndIsNeverCompleted()
    {
        var run = await AeScenarios.T05LengthThenGreaterThan();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:test.read op=test.read c=0/1",
            "2 Step call:runtime.evidence_read ctl=runtime.evidence_read c=1/1",
            "3 EvidenceReadContinuation length-empty ctl=runtime.evidence_read c=1/1",
            "4 Step call:runtime.evidence_read ctl=runtime.evidence_read c=1/1 RC2", // RC2 at plan end now asks for the final answer; the scripted model still reads again (its choice)
            "5 EvidenceReadContinuation text ctl=runtime.evidence_read c=1/1", // PRA-R4: the RC2 turn was answered by the read and is no longer in the request that drew ">"
        ],
        [
            "0 tool test.read Success r0 p0 after=1",
            "1 other", // the failure step: no tool call, no final
        ]);

        var m = run.Metrics;
        Assert.Equal((AgentTaskStatus.Failed, TaskTerminalKind.EmptyResponse), (m.FinalStatus, m.Terminal!.Value));
        Assert.Equal((6, 1, 1), (m.ModelCalls, m.RecoveryTurns, m.ToolExecutions));
        Assert.Equal((2, 1), (m.EvidenceReads, m.RedundantEvidenceReads)); // the repeated read is counted apart from the first
        Assert.Equal(1, new AgentRunnerOptions().EmptyFinalResponseRetries); // the budget the run consumed: one retry, once
        Assert.Equal(new AgentRunnerOptions().EmptyFinalResponseRetries, m.RecoveryTurns);
        Assert.Equal("none (no final-response step persisted)", m.FinalClass);
        Assert.Equal(660L, m.SyntheticTokens);

        // PRA-R4: at plan end the recovery asks for the final answer, never for an offered tool; the model's own choice is not the runtime's.
        var rc2 = run.Pa.Seen[4].History.Single(turn => turn.Provenance?.Origin == ContextOrigin.Runtime).Content!;
        Assert.EndsWith("give your final answer to the operator's goal in plain text now, from the evidence already collected.", rc2, StringComparison.Ordinal);
        Assert.DoesNotContain("call the", rc2, StringComparison.OrdinalIgnoreCase);

        // The same evidence was requested twice, with the same address.
        var reads = run.Audit<EvidenceReadAuditEvent>().ToList();
        Assert.Equal(2, reads.Count);
        Assert.Equal((reads[0].EvidenceId, reads[0].Source, reads[0].Offset, reads[0].RequestedLength),
            (reads[1].EvidenceId, reads[1].Source, reads[1].Offset, reads[1].RequestedLength));
    }

    [Fact]
    public async Task AE1_T05_TheGreaterThanReply_IsNotAcceptedNotCompletedAndNotDressedUpByTheEvidenceDisclosure()
    {
        var run = await AeScenarios.T05LengthThenGreaterThan();

        var failure = run.State.Steps[^1];
        Assert.Null(failure.ToolCall);
        var calls = failure.ModelCalls!;
        Assert.Equal(["tool_calls", "length", "tool_calls", "stop"], calls.Select(call => call.FinishReason)); // the last reply ended normally...
        Assert.Equal(AgentTaskStatus.Failed, run.State.Status); // ...and that proves nothing about whether it answered
        Assert.DoesNotContain(run.State.Steps, FinalResponse.IsFinalStep);
        Assert.DoesNotContain(run.State.Steps, step => step.Observation == ">"); // the text is not persisted as a result either
        Assert.DoesNotContain(run.State.Steps, step => step.Observation?.Contains("Evidence limitations", StringComparison.Ordinal) == true);
        Assert.Contains("no answer content", run.State.Steps[^1].Observation ?? run.FailureText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(1, 6, 1)]
    public async Task AE1_T05_TheRetryBudgetIsTheConfiguredOne(int retries, int expectedCalls, int expectedRecoveryTurns)
    {
        var run = await AeScenarios.T05LengthThenGreaterThan(retries);

        Assert.Equal((AgentTaskStatus.Failed, TaskTerminalKind.EmptyResponse), (run.Metrics.FinalStatus, run.Metrics.Terminal!.Value));
        Assert.Equal((expectedCalls, expectedRecoveryTurns), (run.Metrics.ModelCalls, run.Metrics.RecoveryTurns));
        Assert.Equal(retries == 0, run.Calls.All(call => call.Recovery is null));
    }

    // ---- AE1-T06 ----

    [Fact]
    public async Task AE1_T06_A_ACheckRequestedNativelyAtPlanEnd_IsReplannedAndExecuted_ButTheToolsOwnPathPolicyRefusesIt()
    {
        var run = await AeScenarios.T06CheckRequestedNatively();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:test.other op=test.other c=0/2",
            "2 Step call:test.whoami op=test.whoami ctl=runtime.evidence_read c=1/2",
            "3 Step call:test.size ctl=runtime.evidence_read c=2/2",
            "4 Replanning plan",
            "5 Step call:test.size op=test.size ctl=runtime.evidence_read c=0/1",
            "6 Step text ctl=runtime.evidence_read c=1/1",
        ],
        [
            "0 tool test.other Success r0 p0 after=1",
            "1 tool test.whoami Success r0 p1 after=2",
            "2 refused-call test_size Failure r0 after=2",
            "3 tool test.size Failure r1 p0 after=1",
            "4 final r1 after=1",
        ]);

        var m = run.Metrics;
        Assert.Equal((3, 1, 1), (m.ToolExecutions, m.RefusedToolCalls, m.ReplansAccepted)); // the refused native call is not an execution
        Assert.Equal(["test.other", "test.whoami", "test.size"], m.ToolsExecuted);
        Assert.Equal(1, run.Pa.Tools["test.size"].Invocations); // it ran once, after the replan, and no earlier
        Assert.Equal(AeFixtures.PathRefusal, run.State.Steps[3].Result!.ErrorMessage);
        Assert.Contains("It did not run because a completed plan offers no operational tool", run.Pa.Seen[4].History[^1].Content, StringComparison.Ordinal);
        Assert.Equal(["test.size"], run.PlanDiffs.Single().Added);
    }

    [Fact]
    public async Task AE1_T06_B_ACheckOnlyDescribedInTheFinalProse_IsNeverExecuted_AndTheTaskStillCompletes()
    {
        var run = await AeScenarios.T06CheckOnlyDescribed();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:test.other op=test.other c=0/2",
            "2 Step call:test.whoami op=test.whoami ctl=runtime.evidence_read c=1/2",
            "3 Step text ctl=runtime.evidence_read c=2/2", // BASELINE: plan end, prose accepted as the final answer on first sight
        ],
        [
            "0 tool test.other Success r0 p0 after=1",
            "1 tool test.whoami Success r0 p1 after=2",
            "2 final r0 after=2",
        ]);

        var m = run.Metrics;
        Assert.Equal((AgentTaskStatus.Completed, 2, 0, 0, 1), (m.FinalStatus, m.ToolExecutions, m.RefusedToolCalls, m.ReplansAccepted, run.State.Plans.Count));
        Assert.Equal(0, run.Pa.Tools["test.size"].Invocations); // the prose names the tool; naming is not requesting
        Assert.DoesNotContain(run.State.Steps, step => step.ToolCall?.ToolName is "test.size" or "test_size");
        Assert.DoesNotContain("test.size", m.ToolsExecuted);
        Assert.DoesNotContain(run.Audit<ToolCallAuditEvent>(), evt => evt.Tool.Contains("size", StringComparison.Ordinal));
        Assert.Contains("test.size", run.FinalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AE1_T06_NoPolicyIsChanged_AndNoPathIsAuthorized()
    {
        // Both variants run under the runtime's default test policy (Read automatic, the rest forbidden) and the synthetic size tool has no
        // way to succeed: the scenario never grants a path, and never alters a pattern, to make the objective reachable.
        foreach (var run in new[] { await AeScenarios.T06CheckRequestedNatively(), await AeScenarios.T06CheckOnlyDescribed() })
        {
            Assert.DoesNotContain(run.State.Steps, step => step.ToolCall?.ToolName == "test.size" && step.Result is { Outcome: ToolOutcome.Success });
            Assert.DoesNotContain(run.Audit<PolicyDecisionAuditEvent>(), decision => decision.Mode != PolicyMode.Automatic);
        }
    }

    // ---- AE1-T08 ----

    [Fact]
    public async Task AE1_T08_A_AnAbsentToolProposedInProse_IsNeverRegisteredOrExecuted_AndIsTakenAsTheFinalAnswer()
    {
        var run = await AeScenarios.T08ProposedInProse();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:test.read op=test.read c=0/1",
            "2 Step text ctl=runtime.evidence_read c=1/1", // BASELINE: no replan; the proposal is the final answer
        ],
        [
            "0 tool test.read Success r0 p0 after=1",
            "1 final r0 after=1",
        ]);

        var m = run.Metrics;
        Assert.Equal((AgentTaskStatus.Completed, 1, 0, 3), (m.FinalStatus, m.ToolExecutions, m.RefusedToolCalls, m.ModelCalls));
        Assert.DoesNotContain(AeScenarios.AbsentCapability, run.Pa.Tools.Keys); // not in the registry
        Assert.DoesNotContain(AeScenarios.AbsentCapability, m.ToolsExecuted);
        Assert.DoesNotContain(run.Audit<ToolCallAuditEvent>(), evt => evt.Tool == AeScenarios.AbsentCapability); // not even a refused call: nothing was proposed to the runtime
        Assert.Single(run.State.Plans); // no replan was triggered by the proposal
        Assert.Equal(AeScenarios.ProseProposal, run.FinalText); // the proposal is the persisted final answer, verbatim
    }

    [Fact]
    public async Task AE1_T08_B_ANativeCallToAnAbsentTool_IsRejectedAuditedAndReplanned_AndNeverExecuted()
    {
        var run = await AeScenarios.T08CalledNatively();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:test.read op=test.read c=0/1",
            "2 Step call:system.sensor ctl=runtime.evidence_read c=1/1",
            "3 Replanning plan",
            "4 Step text ctl=runtime.evidence_read",
        ],
        [
            "0 tool test.read Success r0 p0 after=1",
            "1 refused-call system_sensor Failure r0 after=1",
            "2 final r1 after=0",
        ]);

        var rejection = Assert.Single(run.Audit<ToolCallAuditEvent>(), evt => evt.Authorization == AuthorizationKind.UnknownTool);
        Assert.Null(rejection.EffectiveTimeout); // never executed
        Assert.Equal(["test.read"], run.Metrics.ToolsExecuted);
        Assert.Equal((1, 1), (run.Metrics.RefusedToolCalls, run.Metrics.ReplansAccepted));
        Assert.True(run.State.Steps[1].ToolCall!.ToolNameError is not null); // the adapter could not map the name to any offered tool
    }

    // ---- AE1-T10 ----

    [Fact]
    public async Task AE1_T10_AKnowledgeRead_DoesNotExhaustThePlan_AndThePrematureFinalsThatFollowAreMeasured()
    {
        var run = await AeScenarios.T10KnowledgeThenPrematureFinals();

        Sequence(run,
        [
            "0 Planning plan",
            "1 Step call:runtime.knowledge_read op=test.read ctl=runtime.knowledge_read c=0/3",
            "2 Step text op=test.read ctl=runtime.knowledge_read c=0/3", // BASELINE: the premature final comes right after the read
            "3 Replanning plan",
            "4 Step call:test.read op=test.read ctl=runtime.knowledge_read c=0/3 LIVE-05",
            "5 Step text op=test.other ctl=runtime.evidence_read,runtime.knowledge_read c=1/3",
            "6 Step call:test.other op=test.other ctl=runtime.evidence_read,runtime.knowledge_read c=1/3 LIVE-05",
            "7 Step text op=test.logs ctl=runtime.evidence_read,runtime.knowledge_read c=2/3",
            "8 Step call:test.logs op=test.logs ctl=runtime.evidence_read,runtime.knowledge_read c=2/3 LIVE-05",
            "9 Step text ctl=runtime.evidence_read,runtime.knowledge_read c=3/3",
        ],
        [
            "0 premature-final r0 after=0",
            "1 live05-continuation r0 after=0",
            "2 tool test.read Success r0 p0 after=1",
            "3 premature-final r0 after=1",
            "4 live05-continuation r0 after=1",
            "5 tool test.other Success r0 p1 after=2",
            "6 premature-final r0 after=2",
            "7 live05-continuation r0 after=2",
            "8 tool test.logs Success r0 p2 after=3",
            "9 final r0 after=3",
        ]);

        var m = run.Metrics;
        Assert.Equal((10, 3, 1, 2, 1), (m.ModelCalls, m.PrematureFinals, m.ReplanCalls, m.ReplansSkipped, m.KnowledgeReads));
        Assert.Equal(["test.read", "test.other", "test.logs"], m.ToolsExecuted);
        Assert.Single(run.State.Plans); // no accepted revision: the cursor of revision 0 did all the work
        Assert.Equal(AgentTaskStatus.Completed, m.FinalStatus);
        Assert.Equal(1100L, m.SyntheticTokens);
    }

    [Fact]
    public async Task AE1_T10_TheKnowledgeReadIsServedOnce_UnderTheGrantedPack_AndIsNeverRepeatedByTheRuntime()
    {
        var run = await AeScenarios.T10KnowledgeThenPrematureFinals();

        var read = Assert.Single(run.Audit<KnowledgeReadAuditEvent>());
        Assert.Equal((KnowledgeReadResultCode.Success, "read_resource"), (read.ResultCode, read.Operation));
        Assert.NotEqual(true, read.Repeat); // not served a second time
        Assert.DoesNotContain(run.Audit<KnowledgeReadAuditEvent>(), evt => evt.ResultCode != KnowledgeReadResultCode.Success); // no refusal, no NotAuthorized
        Assert.Equal(run.Script.Count(reply => reply.Calls.Any(call => call.Tool == "runtime.knowledge_read")), run.Metrics.KnowledgeReads); // as many as the script asked for

        // The authority the task was admitted with is the authority every read and every index publication used.
        var binding = run.State.KnowledgeBinding!;
        Assert.Equal(binding.PackId, read.PackId);
        Assert.Equal(binding.Version, read.PackVersion);
        Assert.Equal(binding.TenantId, read.TenantId);
        var published = run.Audit<KnowledgeIndexAuditEvent>().ToList();
        Assert.NotEmpty(published);
        Assert.All(published, evt => Assert.Equal((KnowledgeIndexStatus.Published, binding.PackId), (evt.Status, evt.PackId)));
    }

    [Fact]
    public async Task AE1_T10_TheKnowledgeFunctionStaysOfferedAndTheReadResultIsCarriedInEveryLaterRequest()
    {
        var run = await AeScenarios.T10KnowledgeThenPrematureFinals();

        Assert.All(run.Calls.Where(call => call.Purpose == CallPurpose.Step), call => Assert.Contains("runtime.knowledge_read", call.Control));
        Assert.Equal(1, run.Calls[1].KnowledgeTurns); // request 1: the index data turn only
        Assert.All(run.Calls.Skip(2).Where(call => call.Purpose == CallPurpose.Step), call => Assert.Equal(2, call.KnowledgeTurns)); // BASELINE: index turn plus the read result, every time
    }

    // ---- shared ----

    [Fact]
    public async Task AE1_ThePlanEndBehaviourWithAHealthyRun_HasNoPrematureFinalNoReplanAndNoRecovery()
    {
        var run = await AeScenarios.T02CleanRun();

        Assert.Equal((5, 0, 0, 0), (run.Metrics.ModelCalls, run.Metrics.PrematureFinals, run.Metrics.ReplanCalls, run.Metrics.RecoveryTurns));
        Assert.Equal(["test.read", "test.other", "test.logs"], run.Metrics.ToolsExecuted);
    }
}

internal static class AeRunExtensions
{
    /// <summary>The failure text the runtime kept for a failed task, from its persisted synthetic failure step.</summary>
    internal static string FailureText(this AeRun run) => run.State.Steps.LastOrDefault(step => step.ToolCall is null)?.Observation ?? string.Empty;
}
