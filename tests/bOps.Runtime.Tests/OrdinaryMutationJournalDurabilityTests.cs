// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using static bOps.Runtime.Tests.JournalHarness;

namespace bOps.Runtime.Tests;

/// <summary>
/// F-25B crash matrix (task packet §12.2, ADR-0051 §7.2): one ordinary task, policy Automatic unless stated, the model proposes
/// <c>test.mutate</c> once. Each test injects one crash or failure, then "restarts" — a <b>new</b> <see cref="AgentRunner"/> over the
/// revived store, no in-memory state shared — and asserts the persisted journal, the task / recovery state, resumability, the
/// executions, the verifications and the audit trail.
/// </summary>
public sealed class OrdinaryMutationJournalDurabilityTests
{
    private const string Goal = "restart the test service";

    internal sealed class Fixture
    {
        public CrashableJournalTaskStore Store { get; } = new();

        public CountingMutatingTool Mutate { get; } = new();

        public CountingObserveTool Observe { get; } = new();

        public ConcurrentAuditSink Audit { get; } = new();

        public MarkerAwareApprover Approver { get; } = new();

        public Guid TaskId { get; } = Guid.NewGuid();

        public PolicyMode Policy { get; init; } = PolicyMode.Automatic;

        public AgentRunnerOptions Options { get; init; } = new();

        public TaskMutationKey Key => new(TaskId, 1, 0);

        public AgentRunner NewRunner(JournalScriptModel? model = null, IToolRegistry? registry = null) =>
            Runner(model ?? new JournalScriptModel(), Store, Audit, registry ?? Registry(Mutate, Observe), Policy, Approver, Options);

        public IReadOnlyList<TaskMutationJournalEntry> Journal => Store.JournalOf(TaskId);

        public async Task<TaskState> TaskAsync() => (await Store.LoadAsync(TaskId))!;

        /// <summary>Runs attempt 1 until the process dies, then a new process opens the same store.</summary>
        public async Task CrashAsync(AgentRunner runner)
        {
            await Assert.ThrowsAsync<SimulatedCrashException>(() => runner.RunAsync(Goal, Operator, TaskId));
            Store.Revive();
        }

        /// <summary>
        /// After a restart: the task is an orphaned <c>Running</c> attempt 1 that nothing executes — not resumable, recoverable by an
        /// administrator — and the recovery moves it to <c>Failed</c> / <see cref="TaskTerminalKind.ExecutionInterrupted"/> running nothing.
        /// </summary>
        public async Task<AgentRunner> RecoverOrphanAsync()
        {
            var admin = NewRunner();
            var orphan = await TaskAsync();
            Assert.Equal((AgentTaskStatus.Running, 1), (orphan.Status, orphan.ExecutionAttempt));
            Assert.Equal(TaskResumeRefusal.TaskRunning, (await admin.EvaluateResumeWithJournalAsync(orphan)).Refusal!.Code);
            Assert.True(admin.EvaluateRecovery(orphan, executingHere: false).Recoverable);
            var (executions, verifications) = (Mutate.Executions, Observe.Verifications);

            var recovery = await admin.TryRecoverAsync(TaskId, 1, Administrator, executingHere: false);

            Assert.Equal(TaskRecoveryOutcome.Recovered, recovery.Outcome);
            var recovered = await TaskAsync();
            Assert.Equal((AgentTaskStatus.Failed, 1, TaskTerminalKind.ExecutionInterrupted),
                (recovered.Status, recovered.ExecutionAttempt, recovered.TerminalReason!.Kind));
            Assert.Equal(TaskResumePolicy.ExecutionInterruptedStepDescription, recovered.Steps[^1].Description);
            Assert.Equal((executions, verifications), (Mutate.Executions, Observe.Verifications));
            return admin;
        }

        public async Task<TaskResumeDecision> ResumeDecisionAsync(AgentRunner runner) => await runner.EvaluateResumeWithJournalAsync(await TaskAsync());

        /// <summary>C7: dies at the first task write after the atomic outcome-and-step commit (and its audit) — the loop's next save or terminal write.</summary>
        public void CrashAtTheTaskWriteAfterTheOutcome()
        {
            var outcomeWritten = false;
            Store.CrashBefore = write =>
            {
                if (write == "outcome")
                {
                    outcomeWritten = true;
                    return false;
                }

                return outcomeWritten && write is "transition" or "save";
            };
        }
    }

    /// <summary>
    /// T2b–T8: an intent with no outcome after a restart. The task is an orphan; after recovery the resume is refused
    /// <c>mutation_outcome_unknown</c>; <c>verify</c> cannot run (no persisted call: arguments unavailable) and escalates, without invoking the verifier.
    /// </summary>
    private static async Task AssertPendingAfterRestartAsync(Fixture f, int executions, int verifications, bool intentAudited)
    {
        var entry = Assert.Single(f.Journal);
        Assert.Equal((f.Key, TaskMutationState.Pending), (entry.Intent.Key, entry.State));
        Assert.Null(entry.Outcome);

        var admin = await f.RecoverOrphanAsync();
        Assert.Equal(TaskResumeRefusal.MutationOutcomeUnknown, (await f.ResumeDecisionAsync(admin)).Refusal!.Code);

        var verified = await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Verify, Administrator);

        var result = Assert.Single(verified.Results);
        Assert.Equal((TaskMutationState.Escalated, ReconciliationAction.EscalatedToOperator, "arguments_unavailable"),
            (result.State, result.Reconciliation.Action, result.ReasonCode));
        Assert.Equal(VerificationStatus.Inconclusive, result.Reconciliation.Verification);
        Assert.Equal(TaskMutationState.Escalated, Assert.Single(f.Journal).State);
        Assert.Equal(TaskResumeRefusal.MutationOutcomeUnknown, (await f.ResumeDecisionAsync(admin)).Refusal!.Code);
        Assert.Equal((executions, verifications), (f.Mutate.Executions, f.Observe.Verifications));

        Assert.Equal(intentAudited ? 1 : 0, f.Audit.Count(TaskMutationAuditStage.IntentCommitted));
        Assert.Equal(0, f.Audit.Count(TaskMutationAuditStage.OutcomeCommitted));
        Assert.Equal(1, f.Audit.Count(TaskLifecycleStage.RecoveryAccepted));
        Assert.Equal(1, f.Audit.Count(TaskMutationAuditStage.AmbiguityDiscovered));
        Assert.Single(f.Audit.Mutations(), e => e is
        {
            Stage: TaskMutationAuditStage.Reconciled, ReconciliationAction: ReconciliationAction.EscalatedToOperator, ReasonCode: "arguments_unavailable",
        });
    }

    // T1 (C0): nothing is durable for the call; the orphan is recovered and resumable, the tool never ran.
    [Fact]
    public async Task T1_CrashBeforeTheIntentIsPersisted_LeavesNoEntry_AndTheRecoveredTaskIsResumable()
    {
        var f = new Fixture();
        f.Store.CrashBefore = write => write == "intent";

        await f.CrashAsync(f.NewRunner());

        Assert.Empty(f.Journal);
        var admin = await f.RecoverOrphanAsync();
        Assert.True((await f.ResumeDecisionAsync(admin)).Resumable);
        Assert.Equal((0, 0), (f.Mutate.Executions, f.Observe.Verifications));
        Assert.Equal(0, f.Audit.Count(TaskMutationAuditStage.IntentCommitted));
        Assert.Equal(1, f.Audit.Count(TaskLifecycleStage.RecoveryAccepted));
        Assert.Equal(0, f.Audit.Count(TaskMutationAuditStage.AmbiguityDiscovered));
    }

    // T2a: the intent write fails without committing while the process lives: the tool is not run, the containment ends the attempt.
    [Fact]
    public async Task T2a_AnIntentWriteThatFails_NeverRunsTheTool_AndTheContainedAttemptIsResumable()
    {
        var f = new Fixture();
        f.Store.Fail = write => write == "intent";
        var runner = f.NewRunner();

        var escaped = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(Goal, Operator, f.TaskId));
        f.Store.Revive();
        Assert.True(await runner.ContainEscapedFailureAsync(f.TaskId, 1, Operator, null, escaped));

        Assert.Empty(f.Journal);
        var task = await f.TaskAsync();
        Assert.Equal((AgentTaskStatus.Failed, TaskTerminalKind.RuntimeFailure), (task.Status, task.TerminalReason!.Kind));
        Assert.True((await f.ResumeDecisionAsync(f.NewRunner())).Resumable);
        Assert.Equal((0, 0), (f.Mutate.Executions, f.Observe.Verifications));
        Assert.Single(f.Audit.Mutations(), e => e is { Stage: TaskMutationAuditStage.IntentNotCommitted, ReasonCode: "store_failure" });
        Assert.Equal(1, f.Audit.Count(TaskLifecycleStage.ExecutionTerminal));
    }

    // T2b (C1 → C2): the intent committed but the process died before the write returned: the runtime cannot know, so it is Pending.
    [Fact]
    public async Task T2b_AnIntentCommittedJustBeforeTheProcessDied_IsPending_AndBlocksResume()
    {
        var f = new Fixture();
        f.Store.CrashAfter = write => write == "intent";

        await f.CrashAsync(f.NewRunner());

        await AssertPendingAfterRestartAsync(f, executions: 0, verifications: 0, intentAudited: false);
    }

    // T3 (C2): crash after the intent committed, before invocation.
    [Fact]
    public async Task T3_CrashAfterTheIntent_BeforeInvocation_IsPending_NeverNotExecuted()
    {
        var f = new Fixture();
        var runner = f.NewRunner();
        runner.OnIntentCommitted = _ =>
        {
            f.Store.Kill();
            throw new SimulatedCrashException();
        };

        await f.CrashAsync(runner);

        await AssertPendingAfterRestartAsync(f, executions: 0, verifications: 0, intentAudited: true);
    }

    // T4 (C3): the tool applied half its effect, then the process died.
    [Fact]
    public async Task T4_CrashHalfwayThroughTheEffect_IsPending()
    {
        var f = new Fixture();
        f.Mutate.Behaviour = MutationBehaviour.HalfThenCrash;
        f.Mutate.OnCrash = f.Store.Kill;

        await f.CrashAsync(f.NewRunner());

        Assert.Equal((1, 0), (f.Mutate.PartialEffects, f.Mutate.EffectsApplied));
        await AssertPendingAfterRestartAsync(f, executions: 1, verifications: 0, intentAudited: true);
    }

    // T5 (C3): the tool applied its effect, then the process died before the tool returned.
    [Fact]
    public async Task T5_CrashAfterTheEffect_BeforeTheToolReturned_IsPending()
    {
        var f = new Fixture();
        f.Mutate.Behaviour = MutationBehaviour.EffectThenCrash;
        f.Mutate.OnCrash = f.Store.Kill;

        await f.CrashAsync(f.NewRunner());

        Assert.Equal(1, f.Mutate.EffectsApplied);
        await AssertPendingAfterRestartAsync(f, executions: 1, verifications: 0, intentAudited: true);
    }

    // T6 (C4): the invocation returned, the process died before verification and the outcome.
    [Fact]
    public async Task T6_CrashAfterTheInvocationReturned_IsPending()
    {
        var f = new Fixture();
        var runner = f.NewRunner();
        runner.OnInvocationReturned = _ =>
        {
            f.Store.Kill();
            throw new SimulatedCrashException();
        };

        await f.CrashAsync(runner);

        Assert.Equal(1, f.Mutate.EffectsApplied);
        await AssertPendingAfterRestartAsync(f, executions: 1, verifications: 0, intentAudited: true);
    }

    // T7 (C5): the process died inside the post-action verification.
    [Fact]
    public async Task T7_CrashDuringVerification_IsPending_AndVerifyDoesNotRunTheVerifierAgain()
    {
        var f = new Fixture();
        f.Observe.Behaviour = ObserveBehaviour.Crash;
        f.Observe.OnCrash = f.Store.Kill;

        await f.CrashAsync(f.NewRunner());
        f.Observe.Behaviour = ObserveBehaviour.Answer;

        await AssertPendingAfterRestartAsync(f, executions: 1, verifications: 1, intentAudited: true);
    }

    // T8 (C6, R3): verification confirmed the effect in memory, the process died before the outcome was persisted.
    [Fact]
    public async Task T8_CrashBeforeTheOutcomeIsPersisted_LosesTheInMemoryConfirmation_AndIsPending()
    {
        var f = new Fixture();
        f.Observe.Verdict = VerificationStatus.Confirmed;
        f.Store.CrashBefore = write => write == "outcome";

        await f.CrashAsync(f.NewRunner());

        await AssertPendingAfterRestartAsync(f, executions: 1, verifications: 1, intentAudited: true);
    }

    // T9a (C7): the outcome and its step committed together, then the process died: settled, recorded, resumable after recovery.
    [Fact]
    public async Task T9a_CrashAfterTheAtomicOutcomeAndStep_IsSettled_AndTheResumedHistoryHasTheCall()
    {
        var f = new Fixture();
        f.CrashAtTheTaskWriteAfterTheOutcome();

        await f.CrashAsync(f.NewRunner());

        var entry = Assert.Single(f.Journal);
        Assert.Equal(TaskMutationState.Settled, entry.State);
        Assert.Equal((MutationOutcomeKind.Returned, ToolOutcome.Success, VerificationStatus.Confirmed),
            (entry.Outcome!.Kind, entry.Outcome.ToolOutcome, entry.Outcome.Verification));
        var orphan = await f.TaskAsync();
        Assert.Equal("test.mutate", Assert.Single(orphan.Steps).ToolCall!.ToolName);

        var admin = await f.RecoverOrphanAsync();
        Assert.True((await f.ResumeDecisionAsync(admin)).Resumable);
        Assert.Equal((1, 1), (f.Mutate.Executions, f.Observe.Verifications));
        Assert.Equal(1, f.Audit.Count(TaskMutationAuditStage.OutcomeCommitted));
        Assert.Equal(0, f.Audit.Count(TaskMutationAuditStage.AmbiguityDiscovered));

        var model = new JournalScriptModel();
        var resumed = await f.NewRunner(model).ResumeAsync(await f.TaskAsync(), Operator);

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Contains(model.Requests[0].History, turn => turn.ToolCalls?.Any(call => call.ToolName == "test.mutate") == true);
        Assert.Equal(1, f.Mutate.Executions);
    }

    // T9b (C7, ambiguous): the tool timed out and verification refuted the effect: Ambiguous with its step; a later verify confirms it.
    [Fact]
    public async Task T9b_AnAmbiguousOutcomeWithItsStep_IsVerifiedLater_AndTheResumedHistorySaysSo()
    {
        var f = new Fixture();
        f.Mutate.Result = new ToolCallResult(ToolOutcome.Timeout, null, "the service did not answer in time") { FailureKind = ToolFailureKind.Timeout };
        f.Observe.Verdict = VerificationStatus.Refuted;
        f.CrashAtTheTaskWriteAfterTheOutcome();

        await f.CrashAsync(f.NewRunner());

        var entry = Assert.Single(f.Journal);
        Assert.Equal((TaskMutationState.Ambiguous, MutationOutcomeKind.TimedOut, VerificationStatus.Refuted),
            (entry.State, entry.Outcome!.Kind, entry.Outcome.Verification));
        Assert.Single((await f.TaskAsync()).Steps);

        var admin = await f.RecoverOrphanAsync();
        Assert.Equal(TaskResumeRefusal.MutationOutcomeUnknown, (await f.ResumeDecisionAsync(admin)).Refusal!.Code);

        f.Observe.Verdict = VerificationStatus.Confirmed;
        var verified = await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Verify, Administrator);

        Assert.Equal((TaskMutationState.ReconciledDone, ReconciliationAction.VerifiedDone, VerificationStatus.Confirmed),
            (verified.Results[0].State, verified.Results[0].Reconciliation.Action, verified.Results[0].Reconciliation.Verification));
        Assert.True((await f.ResumeDecisionAsync(admin)).Resumable);
        Assert.Equal((1, 2), (f.Mutate.Executions, f.Observe.Verifications));
        Assert.True(f.Audit.Count(TaskMutationAuditStage.AmbiguityDiscovered) >= 1);
        Assert.Equal(1, f.Audit.Count(TaskMutationAuditStage.VerificationAttempted));
        Assert.Single(f.Audit.Mutations(), e => e is { Stage: TaskMutationAuditStage.Reconciled, ReconciliationAction: ReconciliationAction.VerifiedDone });

        var model = new JournalScriptModel();
        await f.NewRunner(model).ResumeAsync(await f.TaskAsync(), Operator);

        var reconciledStep = Assert.Single((await f.TaskAsync()).Steps, step => step.Description == TaskResumePolicy.MutationReconciledStepDescription);
        Assert.Contains("declared verification confirmed", reconciledStep.Observation, StringComparison.Ordinal);
        Assert.Contains(model.Requests[0].History, turn => turn.Content?.Contains("declared verification confirmed", StringComparison.Ordinal) == true);
        Assert.Equal(2, Assert.Single(f.Journal).HistoryRecordedInAttempt);
        Assert.Equal(1, f.Mutate.Executions);
    }

    // T9c: the outcome and step transaction fails on the task-row write: atomicity leaves the entry Pending and the row unchanged.
    [Fact]
    public async Task T9c_AnOutcomeTransactionThatFails_LeavesTheEntryPending_AndTheTaskRowUnchanged()
    {
        var f = new Fixture();
        f.Store.Fail = write => write == "outcome";

        await Assert.ThrowsAsync<IOException>(() => f.NewRunner().RunAsync(Goal, Operator, f.TaskId));
        f.Store.Revive();

        Assert.Empty((await f.TaskAsync()).Steps);
        Assert.Single(f.Audit.Mutations(), e => e is
        {
            Stage: TaskMutationAuditStage.OutcomeNotCommitted, ReasonCode: "store_failure", OutcomeKind: MutationOutcomeKind.Returned,
        });
        await AssertPendingAfterRestartAsync(f, executions: 1, verifications: 1, intentAudited: true);
    }

    // T10: a stale attempt's late outcome, after the task was recovered, reconciled and resumed into attempt 2, changes nothing.
    [Fact]
    public async Task T10_AStaleAttemptsLateOutcome_AfterRecoveryReconciliationAndResume_IsRefused()
    {
        var f = new Fixture();
        f.Mutate.Behaviour = MutationBehaviour.Block;
        var attempt1 = Task.Run(() => f.NewRunner().RunAsync(Goal, Operator, f.TaskId));
        await f.Mutate.Started.WaitAsync(TimeSpan.FromSeconds(30));

        var admin = f.NewRunner();
        Assert.Equal(TaskRecoveryOutcome.Recovered, (await admin.TryRecoverAsync(f.TaskId, 1, Administrator, executingHere: false)).Outcome);
        Assert.Equal(TaskReconcileOutcome.Reconciled, (await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator)).Outcome);
        var acquisition = await admin.TryAcquireResumeAsync(f.TaskId, Operator);
        Assert.Equal(TaskResumeOutcome.Acquired, acquisition.Outcome);
        var attempt2 = await f.TaskAsync();

        f.Mutate.Release();
        var stale = await attempt1;

        var entry = Assert.Single(f.Journal);
        Assert.Equal((TaskMutationState.ReconciledDone, ReconciliationAction.OperatorAcceptedDone, 2),
            (entry.State, entry.Reconciliation!.Action, entry.HistoryRecordedInAttempt));
        Assert.Null(entry.Outcome);
        var current = await f.TaskAsync();
        Assert.Equal((AgentTaskStatus.Running, 2), (current.Status, current.ExecutionAttempt));
        Assert.Equal(attempt2.Steps.Count, current.Steps.Count);
        Assert.Equal((AgentTaskStatus.Running, 2), (stale.Status, stale.ExecutionAttempt));
        Assert.Equal((1, 1), (f.Mutate.Executions, f.Observe.Verifications));
        Assert.Single(f.Audit.Mutations(), e => e is
        {
            Stage: TaskMutationAuditStage.OutcomeNotCommitted, ReasonCode: "superseded", OutcomeKind: MutationOutcomeKind.Returned,
            ToolOutcome: ToolOutcome.Success, ExecutionAttempt: 1,
        });
        Assert.Single(f.Audit.Lifecycle(), e => e is { Stage: TaskLifecycleStage.ExecutionSuperseded, ExecutionAttempt: 1 });
    }

    // T10b: the stale attempt returns after recovery but before reconciliation: its truthful outcome is still refused (§6.4).
    [Fact]
    public async Task T10b_AStaleAttemptsLateOutcome_AfterRecoveryBeforeReconciliation_IsRefused_AndTheEntryStaysPending()
    {
        var f = new Fixture();
        f.Mutate.Behaviour = MutationBehaviour.Block;
        var attempt1 = Task.Run(() => f.NewRunner().RunAsync(Goal, Operator, f.TaskId));
        await f.Mutate.Started.WaitAsync(TimeSpan.FromSeconds(30));

        var admin = f.NewRunner();
        Assert.Equal(TaskRecoveryOutcome.Recovered, (await admin.TryRecoverAsync(f.TaskId, 1, Administrator, executingHere: false)).Outcome);

        f.Mutate.Release();
        await attempt1;

        var entry = Assert.Single(f.Journal);
        Assert.Equal(TaskMutationState.Pending, entry.State);
        Assert.Null(entry.Outcome);
        var task = await f.TaskAsync();
        Assert.Equal((AgentTaskStatus.Failed, 1), (task.Status, task.ExecutionAttempt));
        Assert.Equal(TaskResumeRefusal.MutationOutcomeUnknown, (await f.ResumeDecisionAsync(admin)).Refusal!.Code);
        Assert.Equal((1, 1), (f.Mutate.Executions, f.Observe.Verifications));
        Assert.Single(f.Audit.Mutations(), e => e is
        {
            Stage: TaskMutationAuditStage.OutcomeNotCommitted, ReasonCode: "superseded", OutcomeKind: MutationOutcomeKind.Returned,
        });
    }

    // T10c: the stale attempt was waiting for its approval when it was recovered; the approval then granted is wasted, never used.
    [Fact]
    public async Task T10c_AStaleAttemptsLateIntent_AfterRecovery_IsRefused_AndItsApprovalIsNeverUsed()
    {
        var f = new Fixture { Policy = PolicyMode.Approval };
        f.Approver.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt1 = Task.Run(() => f.NewRunner().RunAsync(Goal, Operator, f.TaskId));
        await f.Approver.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var admin = f.NewRunner();
        Assert.Equal(TaskRecoveryOutcome.Recovered, (await admin.TryRecoverAsync(f.TaskId, 1, Administrator, executingHere: false)).Outcome);
        f.Approver.Gate.SetResult();
        await attempt1;

        Assert.Empty(f.Journal);
        var task = await f.TaskAsync();
        Assert.Equal((AgentTaskStatus.Failed, 1, TaskTerminalKind.ExecutionInterrupted), (task.Status, task.ExecutionAttempt, task.TerminalReason!.Kind));
        Assert.True((await f.ResumeDecisionAsync(admin)).Resumable);
        Assert.Equal((0, 0), (f.Mutate.Executions, f.Observe.Verifications));
        Assert.Single(f.Audit.Mutations(), e => e is { Stage: TaskMutationAuditStage.IntentNotCommitted, ReasonCode: "superseded" });
        Assert.Single(f.Audit.Lifecycle(), e => e is { Stage: TaskLifecycleStage.ExecutionSuperseded, ExecutionAttempt: 1 });

        // The attempt-1 approval authorizes nothing in attempt 2: the same call asks a human again.
        Assert.Equal(1, f.Approver.Requests);
        await f.NewRunner().ResumeAsync(task, Operator);
        Assert.Equal(2, f.Approver.Requests);
    }
}
