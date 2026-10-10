// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using static bOps.Runtime.Tests.JournalHarness;
using Fixture = bOps.Runtime.Tests.OrdinaryMutationJournalDurabilityTests.Fixture;

namespace bOps.Runtime.Tests;

/// <summary>
/// F-25B additional required tests (task packet §12.3, ADR-0051 §§7–12): concurrency, legacy rows, third-party stores, read-only
/// interruptions, approvals and policy after a restart, in-process timeout / cancellation / attempt interruption, the reconciliation
/// verification matrix, startup, audit hygiene, the arguments hash and the PRE-4 projection.
/// </summary>
public sealed class OrdinaryMutationJournalBehaviourTests
{
    private const string Goal = "restart the test service";

    /// <summary>A write hook that holds the first <paramref name="parties"/> writes named <paramref name="write"/> until all have arrived.</summary>
    private static Func<string, Task> Barrier(string write, int parties = 2)
    {
        var arrived = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return async name =>
        {
            if (name != write)
            {
                return;
            }

            if (Interlocked.Increment(ref arrived) == parties)
            {
                all.TrySetResult();
            }

            await all.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
    }

    /// <summary>An orphaned attempt-1 task with one Pending entry (T2b): the shape every reconciliation test starts from after recovery.</summary>
    private static async Task<Fixture> PendingAfterRecoveryAsync(Fixture? fixture = null)
    {
        var f = fixture ?? new Fixture();
        f.Mutate.Behaviour = MutationBehaviour.HalfThenCrash;
        f.Mutate.OnCrash = f.Store.Kill;
        await f.CrashAsync(f.NewRunner());
        f.Mutate.Behaviour = MutationBehaviour.Complete;
        Assert.Equal(TaskRecoveryOutcome.Recovered, (await f.NewRunner().TryRecoverAsync(f.TaskId, 1, Administrator, false)).Outcome);
        return f;
    }

    /// <summary>A task whose mutation timed out in process with an unconfirmed verification: Failed / MutationOutcomeUnknown, Ambiguous, step recorded.</summary>
    private static async Task<(Fixture Fixture, TaskState Task, JournalScriptModel Model)> AmbiguousTimeoutAsync(
        VerificationStatus verdict = VerificationStatus.Inconclusive)
    {
        var f = new Fixture { Options = new AgentRunnerOptions { DefaultToolTimeout = TimeSpan.FromMilliseconds(100) } };
        f.Mutate.Behaviour = MutationBehaviour.Hang;
        f.Observe.Verdict = verdict;
        var model = new JournalScriptModel();

        var task = await f.NewRunner(model).RunAsync(Goal, Operator, f.TaskId);
        return (f, task, model);
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task TwoConcurrentRecoveries_ExactlyOneWins_AndTheOtherIsARecoveryConflict()
    {
        var f = new Fixture();
        f.Store.CrashBefore = write => write == "intent";
        await f.CrashAsync(f.NewRunner());
        f.Store.BeforeWrite = Barrier("transition");

        var results = await Task.WhenAll(
            f.NewRunner().TryRecoverAsync(f.TaskId, 1, Administrator, false),
            f.NewRunner().TryRecoverAsync(f.TaskId, 1, Administrator, false));

        Assert.Single(results, r => r.Outcome == TaskRecoveryOutcome.Recovered);
        Assert.Single(results, r => r.Outcome == TaskRecoveryOutcome.Refused && r.Refusal!.Code == TaskRecoveryRefusal.RecoveryConflict);
        Assert.Single((await f.TaskAsync()).Steps, step => step.Description == TaskResumePolicy.ExecutionInterruptedStepDescription);
        Assert.Equal(1, f.Audit.Count(TaskLifecycleStage.RecoveryAccepted));
        Assert.Single(f.Audit.Lifecycle(), e => e is { Stage: TaskLifecycleStage.RecoveryRejected, RefusalCode: TaskRecoveryRefusal.RecoveryConflict });
    }

    [Fact]
    public async Task TwoConcurrentResumes_AfterReconciliation_ExactlyOneAcquires_AndTheHistoryIsAppendedOnce()
    {
        var f = await PendingAfterRecoveryAsync();
        await f.NewRunner().ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator);
        f.Store.BeforeWrite = Barrier("acquire");

        var results = await Task.WhenAll(
            f.NewRunner().TryAcquireResumeAsync(f.TaskId, Operator),
            f.NewRunner().TryAcquireResumeAsync(f.TaskId, Operator));

        Assert.Single(results, r => r.Outcome == TaskResumeOutcome.Acquired);
        Assert.Single(results, r => r.Outcome == TaskResumeOutcome.Refused && r.Refusal!.Code == TaskResumeRefusal.ResumeConflict);
        var task = await f.TaskAsync();
        Assert.Equal((AgentTaskStatus.Running, 2), (task.Status, task.ExecutionAttempt));
        Assert.Single(task.Steps, step => step.Description == TaskResumePolicy.MutationReconciledStepDescription);
        Assert.Equal(2, Assert.Single(f.Journal).HistoryRecordedInAttempt);
    }

    [Theory]
    [InlineData(TaskReconcileAction.AcceptDone, TaskReconcileAction.Abandon)]
    [InlineData(TaskReconcileAction.Verify, TaskReconcileAction.AcceptDone)]
    public async Task ConcurrentReconciliations_PerEntryExactlyOneWins_WithNoMixedState(TaskReconcileAction first, TaskReconcileAction second)
    {
        var f = await PendingAfterRecoveryAsync();
        f.Store.BeforeWrite = Barrier("reconcile");

        var results = await Task.WhenAll(
            f.NewRunner().ReconcileMutationsAsync(f.TaskId, first, Administrator),
            f.NewRunner().ReconcileMutationsAsync(f.TaskId, second, Administrator));

        var won = Assert.Single(results, r => r.Outcome == TaskReconcileOutcome.Reconciled);
        Assert.Single(results, r => r.Outcome == TaskReconcileOutcome.Refused && r.Refusal!.Code == TaskRecoveryRefusal.ReconcileConflict);
        var entry = Assert.Single(f.Journal);
        Assert.Equal(Assert.Single(won.Results).State, entry.State);
        Assert.Equal(Assert.Single(won.Results).Reconciliation, entry.Reconciliation);
        Assert.Single(f.Audit.Mutations(), e => e is { Stage: TaskMutationAuditStage.ReconcileRejected, ReasonCode: TaskRecoveryRefusal.ReconcileConflict });
    }

    // ---------------------------------------------------------------- legacy, third-party stores, read-only work

    [Theory]
    [InlineData(AgentTaskStatus.Failed, false)]
    [InlineData(AgentTaskStatus.Failed, true)]
    [InlineData(AgentTaskStatus.Cancelled, false)]
    [InlineData(AgentTaskStatus.Cancelled, true)]
    public async Task ALegacyRow_IsNeverResumable_AndNothingIsWrittenOrInvented(AgentTaskStatus status, bool withCompletedMutation)
    {
        var f = new Fixture();
        PlanStep[] steps = withCompletedMutation
            ? [new PlanStep(0, "test.mutate", new ModelToolCall("c0", "test.mutate", new ToolArguments(new JsonObject { ["target"] = "svc-1" })),
                ToolCallResult.Success("changed"), "changed", 0)]
            : [];
        var legacy = new TaskState(f.TaskId, NodeId.Local, Goal, status, steps, [new AgentPlan(0, "p", [])], DateTimeOffset.UtcNow)
        {
            Origin = TaskOrigin.Ordinary,
            Accounting = new TaskAccounting(0, steps.Length, 0),
        };
        f.Store.Inner.Seed(legacy);

        var acquisition = await f.NewRunner().TryAcquireResumeAsync(f.TaskId, Operator);

        Assert.Equal((TaskResumeOutcome.Refused, TaskResumeRefusal.MutationJournalAbsent), (acquisition.Outcome, acquisition.Refusal!.Code));
        Assert.Empty(f.Store.Writes);
        Assert.Empty(f.Journal);
        Assert.Equal(legacy, await f.TaskAsync());
        Assert.Single(f.Audit.Lifecycle(), e => e is { Stage: TaskLifecycleStage.ResumeRejected, RefusalCode: TaskResumeRefusal.MutationJournalAbsent });
        Assert.Equal(TaskResumeRefusal.MutationJournalAbsent,
            (await f.NewRunner().ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator)).Refusal!.Code);
    }

    [Fact]
    public async Task ALegacyRunningOrphan_IsNotRecoverable_AndStaysAsItWas()
    {
        var f = new Fixture();
        var legacy = new TaskState(f.TaskId, NodeId.Local, Goal, AgentTaskStatus.Running, [], [], DateTimeOffset.UtcNow) { Origin = TaskOrigin.Ordinary };
        f.Store.Inner.Seed(legacy);

        var recovery = await f.NewRunner().TryRecoverAsync(f.TaskId, 1, Administrator, false);

        Assert.Equal((TaskRecoveryOutcome.Refused, TaskResumeRefusal.MutationJournalAbsent), (recovery.Outcome, recovery.Refusal!.Code));
        Assert.Equal(legacy, await f.TaskAsync());
        Assert.Empty(f.Store.Writes);
        Assert.Single(f.Audit.Lifecycle(), e => e is { Stage: TaskLifecycleStage.RecoveryRejected, RefusalCode: TaskResumeRefusal.MutationJournalAbsent });
    }

    [Fact]
    public async Task AThirdPartyStoreWithoutTheJournal_CreatesMutationsDisabledTasks_ThatRefuseEveryMutationBeforePolicy()
    {
        var store = new TransitionOnlyTaskStore(new InMemoryTaskStore());
        var mutate = new CountingMutatingTool();
        var approver = new MarkerAwareApprover();
        var audit = new ConcurrentAuditSink();
        var taskId = Guid.NewGuid();
        var model = new JournalScriptModel { Plan = ["test.mutate"], Replan = [] };

        var task = await Runner(model, store, audit, Registry(mutate, new CountingObserveTool()), PolicyMode.Approval, approver)
            .RunAsync(Goal, Operator, taskId);

        Assert.Equal(TaskMutationJournalMode.MutationsDisabled, task.MutationJournalMode);
        Assert.Equal((0, 0), (approver.Requests, mutate.Executions));
        var refused = Assert.Single(task.Steps, step => step.ToolCall?.ToolName == "test.mutate");
        Assert.Equal(ToolFailureKind.Authorization, refused.Result!.FailureKind);
        Assert.Single(audit.Events.OfType<PolicyDecisionAuditEvent>(), e => e.Mode == PolicyMode.Forbidden && e.Tool == "test.mutate");
        Assert.Single(audit.Mutations(), e => e is { Stage: TaskMutationAuditStage.IntentNotCommitted, ReasonCode: "mutations_disabled" });
        Assert.Equal(0, audit.Count(TaskMutationAuditStage.IntentCommitted));
    }

    [Fact]
    public async Task AThirdPartyStore_ResumesReadOnlyMutationsDisabledWork_AndRefusesJournaledRows()
    {
        var inner = new InMemoryTaskStore();
        var store = new TransitionOnlyTaskStore(inner);
        var observe = new CountingObserveTool();
        var registry = Registry(new CountingMutatingTool(), observe);
        var readOnly = new TaskState(Guid.NewGuid(), NodeId.Local, Goal, AgentTaskStatus.Cancelled, [],
            [new AgentPlan(0, "p", [new PlannedStep(0, "observe", "test.observe")])], DateTimeOffset.UtcNow)
        {
            Origin = TaskOrigin.Ordinary,
            Accounting = TaskAccounting.None,
            MutationJournalMode = TaskMutationJournalMode.MutationsDisabled,
        };
        inner.Seed(readOnly);

        var resumed = await Runner(new JournalScriptModel(), store, new ConcurrentAuditSink(), registry).ResumeAsync(readOnly, Operator);

        Assert.Equal((AgentTaskStatus.Completed, 2), (resumed.Status, resumed.ExecutionAttempt));
        Assert.Equal(1, observe.Verifications);

        var journaled = readOnly with { Id = Guid.NewGuid(), MutationJournalMode = TaskMutationJournalMode.Journaled };
        inner.Seed(journaled);
        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() =>
            Runner(new JournalScriptModel(), store, new ConcurrentAuditSink(), registry).ResumeAsync(journaled, Operator));
        Assert.Equal(TaskResumeRefusal.MutationJournalUnsupported, refused.Refusal.Code);

        var orphan = journaled with { Id = Guid.NewGuid(), Status = AgentTaskStatus.Running };
        inner.Seed(orphan);
        var recovery = await Runner(new JournalScriptModel(), store, new ConcurrentAuditSink(), registry).TryRecoverAsync(orphan.Id, 1, Administrator, false);
        Assert.Equal(TaskResumeRefusal.MutationJournalUnsupported, recovery.Refusal!.Code);
        Assert.Equal(TaskResumeRefusal.MutationJournalUnsupported,
            (await Runner(new JournalScriptModel(), store, new ConcurrentAuditSink(), registry)
                .ReconcileMutationsAsync(journaled.Id, TaskReconcileAction.AcceptDone, Administrator)).Refusal!.Code);
    }

    [Fact]
    public async Task AStoreWithoutTransitions_CreatesMutationsDisabledTasks_AndRefusesResumeAsBefore()
    {
        var store = new PlainTaskStore();
        var taskId = Guid.NewGuid();
        var model = new JournalScriptModel { Plan = ["test.observe"] };

        var task = await Runner(model, store, new ConcurrentAuditSink(), Registry(new CountingMutatingTool(), new CountingObserveTool()))
            .RunAsync(Goal, Operator, taskId);

        Assert.Equal(TaskMutationJournalMode.MutationsDisabled, task.MutationJournalMode);
        var failed = task with { Status = AgentTaskStatus.Failed };
        await store.SaveAsync(failed);
        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() =>
            Runner(new JournalScriptModel(), store, new ConcurrentAuditSink(), Registry(new CountingObserveTool())).ResumeAsync(failed, Operator));
        Assert.Equal(TaskResumeRefusal.TransitionUnsupported, refused.Refusal.Code);
    }

    [Fact]
    public async Task ACrashInsideARead_LeavesNoEntry_AndAfterRecoveryTheReadIsRepeated()
    {
        var f = new Fixture();
        f.Observe.Behaviour = ObserveBehaviour.Crash;
        f.Observe.OnCrash = f.Store.Kill;

        await f.CrashAsync(f.NewRunner(new JournalScriptModel { Plan = ["test.observe"] }));
        f.Observe.Behaviour = ObserveBehaviour.Answer;

        Assert.Empty(f.Journal);
        var admin = await f.RecoverOrphanAsync();
        Assert.True((await f.ResumeDecisionAsync(admin)).Resumable);
        Assert.Equal(TaskRecoveryRefusal.NothingToReconcile,
            (await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Verify, Administrator)).Refusal!.Code);

        var resumed = await f.NewRunner(new JournalScriptModel { Plan = ["test.observe"] }).ResumeAsync(await f.TaskAsync(), Operator);

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Equal(2, f.Observe.Verifications);
        Assert.Empty(f.Journal);
        Assert.Equal(0, f.Mutate.Executions);
    }

    // ---------------------------------------------------------------- approvals and policy after a restart

    [Fact]
    public async Task AnApprovalGivenBeforeTheRestart_IsNeverReused_ADifferentMutationAsksAgain()
    {
        var f = await PendingAfterRecoveryAsync(new Fixture { Policy = PolicyMode.Approval });
        Assert.Equal(1, f.Approver.Requests);
        await f.NewRunner().ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator);

        var model = new JournalScriptModel { Arguments = (_, _) => new JsonObject { ["target"] = "svc-2" } };
        await f.NewRunner(model).ResumeAsync(await f.TaskAsync(), Operator);

        Assert.Equal(2, f.Approver.Requests);
        Assert.DoesNotContain(MutationJournalPolicy.DuplicateOfReconciledMarker, f.Approver.Reasons[1], StringComparison.Ordinal);
        Assert.Equal(2, f.Mutate.Executions);
        Assert.Equal("svc-2", f.Mutate.Invocations[1].ToJson()["target"]!.GetValue<string>());
    }

    [Fact]
    public async Task AfterARestart_ANonDuplicateMutationRunsAutomaticallyUnderANewIntent()
    {
        var f = await PendingAfterRecoveryAsync();
        await f.NewRunner().ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator);

        var model = new JournalScriptModel { Arguments = (_, _) => new JsonObject { ["target"] = "svc-2" } };
        await f.NewRunner(model).ResumeAsync(await f.TaskAsync(), Operator);

        Assert.Equal(0, f.Approver.Requests);
        Assert.Equal(2, f.Mutate.Executions);
        Assert.Equal(2, f.Journal.Count);
        Assert.Equal((2, TaskMutationState.Settled), (f.Journal[1].Intent.Key.ExecutionAttempt, f.Journal[1].State));
    }

    [Fact]
    public async Task AfterARestart_ACallIdenticalToAReconciledMutation_IsEscalatedToApprovalNamingIt()
    {
        var f = await PendingAfterRecoveryAsync();
        await f.NewRunner().ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator);

        await f.NewRunner(new JournalScriptModel()).ResumeAsync(await f.TaskAsync(), Operator);

        Assert.Equal(1, f.Mutate.Executions);
        var reason = Assert.Single(f.Approver.Reasons);
        Assert.StartsWith(MutationJournalPolicy.DuplicateOfReconciledMarker, reason, StringComparison.Ordinal);
        Assert.Contains("administrator accepted it as already applied", reason, StringComparison.Ordinal);
        Assert.Single(f.Audit.Events.OfType<PolicyDecisionAuditEvent>(), e =>
            e.Mode == PolicyMode.Approval && e.Reason.Contains(MutationJournalPolicy.DuplicateOfReconciledMarker, StringComparison.Ordinal));
        Assert.Single(f.Journal);
    }

    [Fact]
    public async Task AForbiddenMutation_NeverHasAnIntent_BeforeOrAfterAResume()
    {
        var f = new Fixture { Policy = PolicyMode.Forbidden, Options = new AgentRunnerOptions { MaxSteps = 1 } };
        var first = await f.NewRunner(new JournalScriptModel { Replan = ["test.mutate"] }).RunAsync(Goal, Operator, f.TaskId);
        Assert.Equal(AgentTaskStatus.MaxStepsReached, first.Status);

        await f.NewRunner(new JournalScriptModel { Replan = ["test.mutate"] }).ResumeAsync(first, Operator);

        Assert.Empty(f.Journal);
        Assert.Equal(0, f.Mutate.Executions);
        Assert.Equal(2, f.Audit.Events.OfType<PolicyDecisionAuditEvent>().Count(e => e.Mode == PolicyMode.Forbidden && e.Tool == "test.mutate"));
        Assert.Equal(0, f.Audit.Count(TaskMutationAuditStage.IntentCommitted));
    }

    // ---------------------------------------------------------------- in-process timeout, cancellation, interruption

    [Fact]
    public async Task ATimedOutMutationNotConfirmed_EndsTheAttempt_WithNoReplanAndNoFurtherModelCall()
    {
        var (f, task, model) = await AmbiguousTimeoutAsync(VerificationStatus.Inconclusive);

        Assert.Equal((AgentTaskStatus.Failed, TaskTerminalKind.MutationOutcomeUnknown), (task.Status, task.TerminalReason!.Kind));
        var entry = Assert.Single(f.Journal);
        Assert.Equal((TaskMutationState.Ambiguous, MutationOutcomeKind.TimedOut, VerificationStatus.Inconclusive),
            (entry.State, entry.Outcome!.Kind, entry.Outcome.Verification));
        Assert.Equal("test.mutate", Assert.Single(task.Steps).ToolCall!.ToolName);
        Assert.Equal((1, 1), (model.PlanningCalls, model.StepCalls));
        Assert.Single(task.Plans);
        Assert.Equal(TaskResumeRefusal.MutationOutcomeUnknown, (await f.ResumeDecisionAsync(f.NewRunner())).Refusal!.Code);
        Assert.Equal(1, f.Audit.Count(TaskMutationAuditStage.AmbiguityDiscovered));
        Assert.Equal(1, f.Mutate.Executions);
    }

    [Fact]
    public async Task ATimedOutMutationConfirmedByVerification_IsSettled_AndTheLoopReplansAsBefore()
    {
        var (f, task, model) = await AmbiguousTimeoutAsync(VerificationStatus.Confirmed);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        var entry = Assert.Single(f.Journal);
        Assert.Equal((TaskMutationState.Settled, MutationOutcomeKind.TimedOut), (entry.State, entry.Outcome!.Kind));
        Assert.Equal(2, model.PlanningCalls);
        Assert.Equal(2, task.Plans.Count);
    }

    [Fact]
    public async Task ACancellationWhileTheToolRuns_RecordsAnUnknownOutcomeWithItsStep_AndBlocksResume()
    {
        var f = new Fixture();
        f.Mutate.Behaviour = MutationBehaviour.Hang;
        using var cancellation = new CancellationTokenSource();
        var runner = f.NewRunner();
        var running = Task.Run(() => runner.RunAsync(Goal, Operator, f.TaskId, cancellation.Token));
        await f.Mutate.Started.WaitAsync(TimeSpan.FromSeconds(30));

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(await runner.CompleteCancellationAsync(f.TaskId, 1, Operator, null));

        var task = await f.TaskAsync();
        Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
        var entry = Assert.Single(f.Journal);
        Assert.Equal((TaskMutationState.Ambiguous, MutationOutcomeKind.Cancelled), (entry.State, entry.Outcome!.Kind));
        var step = Assert.Single(task.Steps);
        Assert.Contains("outcome is unknown", step.Observation, StringComparison.Ordinal);
        Assert.Equal(0, f.Observe.Verifications);
        var admin = f.NewRunner();
        Assert.Equal(TaskResumeRefusal.MutationOutcomeUnknown, (await f.ResumeDecisionAsync(admin)).Refusal!.Code);

        // The cancelled call is persisted, so its declared verification can run now.
        var verified = await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Verify, Administrator);
        Assert.Equal((TaskMutationState.ReconciledDone, ReconciliationAction.VerifiedDone), (verified.Results[0].State, verified.Results[0].Reconciliation.Action));
        Assert.Equal(1, f.Observe.Verifications);
    }

    [Fact]
    public async Task ACancellationAfterTheIntentButBeforeInvocation_IsKnownNotExecuted_AndResumable()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var runner = f.NewRunner();
        runner.OnIntentCommitted = _ => cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Goal, Operator, f.TaskId, cancellation.Token));
        Assert.True(await runner.CompleteCancellationAsync(f.TaskId, 1, Operator, null));

        var entry = Assert.Single(f.Journal);
        Assert.Equal((TaskMutationState.Settled, MutationOutcomeKind.NotInvoked), (entry.State, entry.Outcome!.Kind));
        Assert.Equal(MutationKnowledge.KnownNotExecuted, MutationJournalPolicy.Classify(entry).Knowledge);
        var task = await f.TaskAsync();
        Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
        Assert.Contains("was not run", Assert.Single(task.Steps).Observation, StringComparison.Ordinal);
        Assert.Equal(0, f.Mutate.Executions);
        Assert.True((await f.ResumeDecisionAsync(f.NewRunner())).Resumable);
    }

    [Fact]
    public async Task AnAttemptDurationInterruptionNotConfirmed_IsInterruptedAmbiguous_AndBlocksResume()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var f = new Fixture { Options = new AgentRunnerOptions { MaxAttemptDuration = TimeSpan.FromMinutes(1), DefaultToolTimeout = TimeSpan.FromMinutes(10) } };
        f.Mutate.Behaviour = MutationBehaviour.Hang;
        f.Mutate.OnExecute = () => clock.Advance(TimeSpan.FromMinutes(2));
        f.Observe.Verdict = VerificationStatus.Refuted;
        var runner = Runner(new JournalScriptModel(), f.Store, f.Audit, Registry(f.Mutate, f.Observe), options: f.Options, time: clock);

        var task = await runner.RunAsync(Goal, Operator, f.TaskId);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.AttemptDurationBudget), (task.Status, task.TerminalReason!.Kind));
        var entry = Assert.Single(f.Journal);
        Assert.Equal((TaskMutationState.Ambiguous, MutationOutcomeKind.Interrupted, VerificationStatus.Refuted),
            (entry.State, entry.Outcome!.Kind, entry.Outcome.Verification));
        Assert.Single(task.Steps);
        Assert.Equal(TaskResumeRefusal.MutationOutcomeUnknown, (await f.ResumeDecisionAsync(f.NewRunner())).Refusal!.Code);
    }

    // ---------------------------------------------------------------- reconciliation

    public static TheoryData<string, TaskMutationState, VerificationStatus, string?> VerifyCases() => new()
    {
        { "confirmed", TaskMutationState.ReconciledDone, VerificationStatus.Confirmed, null },
        { "refuted", TaskMutationState.Escalated, VerificationStatus.Refuted, "not_confirmed" },
        { "inconclusive", TaskMutationState.Escalated, VerificationStatus.Inconclusive, "not_confirmed" },
        { "verifier-throws", TaskMutationState.Escalated, VerificationStatus.Inconclusive, "not_confirmed" },
        { "verifier-times-out", TaskMutationState.Escalated, VerificationStatus.Inconclusive, "not_confirmed" },
        { "verifier-entitlement-denied", TaskMutationState.Escalated, VerificationStatus.Inconclusive, "not_confirmed" },
        { "tool-no-longer-registered", TaskMutationState.Escalated, VerificationStatus.Inconclusive, "verification_unavailable" },
        { "verifier-name-changed", TaskMutationState.Escalated, VerificationStatus.Inconclusive, "verification_unavailable" },
    };

    [Theory]
    [MemberData(nameof(VerifyCases))]
    public async Task Verify_SettlesOnlyOnConfirmed_AndEveryOtherOutcomeEscalates(
        string scenario, TaskMutationState state, VerificationStatus status, string? reasonCode)
    {
        var (f, _, _) = await AmbiguousTimeoutAsync();
        var observe = new CountingObserveTool();
        var options = new AgentRunnerOptions();
        IToolRegistry registry = Registry(f.Mutate, observe);
        switch (scenario)
        {
            case "confirmed":
                observe.Verdict = VerificationStatus.Confirmed;
                break;
            case "refuted":
                observe.Verdict = VerificationStatus.Refuted;
                break;
            case "inconclusive":
                observe.Verdict = VerificationStatus.Inconclusive;
                break;
            case "verifier-throws":
                observe.Behaviour = ObserveBehaviour.Throw;
                break;
            case "verifier-times-out":
                observe.Behaviour = ObserveBehaviour.Hang;
                options = new AgentRunnerOptions { DefaultToolTimeout = TimeSpan.FromMilliseconds(100) };
                break;
            case "verifier-entitlement-denied":
                var governed = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
                governed.Register(new PackageId("test.package"), f.Mutate);
                governed.Register(new PackageId("test.package"), PackageTrustLevel.Official,
                    new EntitlementRequirement(EntitlementApplicability.Governed), observe);
                registry = governed;
                break;
            case "tool-no-longer-registered":
                registry = Registry(observe);
                break;
            case "verifier-name-changed":
                registry = Registry(new CountingMutatingTool(verifier: "test.observe2"), new CountingObserveTool("test.observe2"), observe);
                break;
        }

        var admin = Runner(new JournalScriptModel(), f.Store, f.Audit, registry, approver: f.Approver, options: options);
        var result = await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Verify, Administrator);

        var reconciled = Assert.Single(result.Results);
        Assert.Equal((state, status, reasonCode), (reconciled.State, reconciled.Reconciliation.Verification, reconciled.ReasonCode));
        Assert.Equal(state == TaskMutationState.ReconciledDone ? ReconciliationAction.VerifiedDone : ReconciliationAction.EscalatedToOperator,
            reconciled.Reconciliation.Action);
        Assert.Equal(state, Assert.Single(f.Journal).State);
        Assert.Equal(1, f.Mutate.Executions);
        if (scenario is "verifier-entitlement-denied" or "tool-no-longer-registered" or "verifier-name-changed")
        {
            Assert.Equal(0, observe.Verifications);
        }

        Assert.Equal(state == TaskMutationState.ReconciledDone, (await f.ResumeDecisionAsync(admin)).Resumable);
    }

    [Fact]
    public async Task AcceptDone_ThenResume_TheHistorySaysAnAdministratorAcceptedIt_AndTheKnowledgeStaysUnknown()
    {
        var (f, _, _) = await AmbiguousTimeoutAsync();

        var accepted = await f.NewRunner().ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator, note: "checked by hand");

        var entry = Assert.Single(f.Journal);
        Assert.Equal((TaskMutationState.ReconciledDone, ReconciliationAction.OperatorAcceptedDone, Administrator),
            (entry.State, entry.Reconciliation!.Action, entry.Reconciliation.ResolvedBy));
        Assert.Equal(VerificationStatus.Inconclusive, entry.Reconciliation.Verification);
        Assert.Equal(MutationKnowledge.Unknown, MutationJournalPolicy.Classify(entry).Knowledge);
        Assert.Equal(0, accepted.UnsettledCount);
        Assert.Single(f.Audit.Mutations(), e => e is { Stage: TaskMutationAuditStage.Reconciled, Note: "checked by hand" });

        await f.NewRunner(new JournalScriptModel { Plan = ["test.observe"], Replan = ["test.observe"] }).ResumeAsync(await f.TaskAsync(), Operator);

        var step = Assert.Single((await f.TaskAsync()).Steps, s => s.Description == TaskResumePolicy.MutationReconciledStepDescription);
        Assert.Contains("an administrator accepted it as already applied (verification: Inconclusive)", step.Observation, StringComparison.Ordinal);
        Assert.Null(step.ToolCall);
        Assert.True(TaskResumePolicy.IsSyntheticFailureStep(step));
    }

    [Fact]
    public async Task Abandon_MakesTheTaskPermanentlyNotResumable()
    {
        var (f, _, _) = await AmbiguousTimeoutAsync();
        var admin = f.NewRunner();

        await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Abandon, Administrator);

        Assert.Equal((TaskMutationState.Abandoned, ReconciliationAction.OperatorAbandoned),
            (Assert.Single(f.Journal).State, f.Journal[0].Reconciliation!.Action));
        Assert.Equal(TaskResumeRefusal.TaskAbandoned, (await f.ResumeDecisionAsync(admin)).Refusal!.Code);
        Assert.Equal(TaskRecoveryRefusal.NothingToReconcile,
            (await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator)).Refusal!.Code);
        var acquisition = await admin.TryAcquireResumeAsync(f.TaskId, Operator);
        Assert.Equal(TaskResumeRefusal.TaskAbandoned, acquisition.Refusal!.Code);
    }

    [Fact]
    public async Task Reconcile_IsRefusedOnARunningTask_ByANonHuman_WithNothingUnsettled_OrWithAnOversizedNote()
    {
        var f = new Fixture();
        f.Store.CrashAfter = write => write == "intent";
        await f.CrashAsync(f.NewRunner());
        var admin = f.NewRunner();

        Assert.Equal(TaskRecoveryRefusal.TaskRunning, (await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator)).Refusal!.Code);
        await admin.TryRecoverAsync(f.TaskId, 1, Administrator, false);
        Assert.Equal(TaskRecoveryRefusal.ReconcileNotHuman,
            (await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, new ActorIdentity("agent", "agent-1", null))).Refusal!.Code);
        Assert.Equal(TaskRecoveryRefusal.ReconcileNotHuman,
            (await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, ActorIdentity.RuntimeSystem)).Refusal!.Code);
        Assert.Equal(TaskRecoveryRefusal.InvalidRequest,
            (await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator, new string('x', 501))).Refusal!.Code);
        Assert.Equal(TaskMutationState.Pending, Assert.Single(f.Journal).State);

        await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator);
        Assert.Equal(TaskRecoveryRefusal.NothingToReconcile,
            (await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Verify, Administrator)).Refusal!.Code);
        Assert.Equal(5, f.Audit.Count(TaskMutationAuditStage.ReconcileRejected));
    }

    [Fact]
    public async Task Recover_IsRefusedWhileThisHostExecutesTheTask_OrForAnotherAttempt_AndForANonRunningTask()
    {
        var f = new Fixture();
        f.Store.CrashBefore = write => write == "intent";
        await f.CrashAsync(f.NewRunner());
        var admin = f.NewRunner();

        Assert.Equal(TaskRecoveryRefusal.TaskExecutingHere, (await admin.TryRecoverAsync(f.TaskId, 1, Administrator, executingHere: true)).Refusal!.Code);
        Assert.Equal(TaskRecoveryRefusal.RecoveryConflict, (await admin.TryRecoverAsync(f.TaskId, 2, Administrator, executingHere: false)).Refusal!.Code);
        Assert.Equal(AgentTaskStatus.Running, (await f.TaskAsync()).Status);
        Assert.Equal(TaskRecoveryOutcome.Recovered, (await admin.TryRecoverAsync(f.TaskId, 1, Administrator, executingHere: false)).Outcome);
        Assert.Equal(TaskRecoveryRefusal.TaskNotRunning, (await admin.TryRecoverAsync(f.TaskId, 1, Administrator, executingHere: false)).Refusal!.Code);
        Assert.Equal(TaskRecoveryOutcome.NotFound, (await admin.TryRecoverAsync(Guid.NewGuid(), 1, Administrator, false)).Outcome);
        Assert.Equal(3, f.Audit.Count(TaskLifecycleStage.RecoveryRejected));
    }

    // ---------------------------------------------------------------- startup, hygiene, hash, projection

    [Fact]
    public async Task Startup_ANewRunnerOverAnOrphanWithAPendingEntry_WritesRunsAndReconcilesNothing()
    {
        var f = new Fixture();
        f.Store.CrashAfter = write => write == "intent";
        await f.CrashAsync(f.NewRunner());
        var writes = f.Store.Writes.Count;
        var events = f.Audit.Events.Count;
        var before = await f.TaskAsync();

        var restarted = f.NewRunner();
        _ = restarted.EvaluateRecovery(before, executingHere: false);
        _ = await restarted.EvaluateResumeWithJournalAsync(before);
        _ = restarted.ProjectExecutionPlan(before, false, f.Journal);

        Assert.Equal(writes, f.Store.Writes.Count);
        Assert.Equal(events, f.Audit.Events.Count);
        Assert.Equal(before, await f.TaskAsync());
        Assert.Equal(TaskMutationState.Pending, Assert.Single(f.Journal).State);
        Assert.Equal((0, 0), (f.Mutate.Executions, f.Observe.Verifications));
    }

    [Fact]
    public async Task AuditHygiene_NoArgumentToolOutputOrSecretEverReachesAJournalEventOrEntry()
    {
        const string SecretSentinel = "S3NT1NEL-SECRET-VALUE";
        const string TargetSentinel = "S3NT1NEL-PLAIN-TARGET";
        const string OutputSentinel = "S3NT1NEL-TOOL-OUTPUT";
        var f = new Fixture { Options = new AgentRunnerOptions { DefaultToolTimeout = TimeSpan.FromMilliseconds(100) } };
        f.Mutate.Result = ToolCallResult.Success(OutputSentinel);
        f.Mutate.Behaviour = MutationBehaviour.Hang;
        f.Observe.Verdict = VerificationStatus.Inconclusive;
        var model = new JournalScriptModel { Arguments = (_, _) => new JsonObject { ["target"] = TargetSentinel, ["secret"] = SecretSentinel } };

        await f.NewRunner(model).RunAsync(Goal, Operator, f.TaskId);
        await f.NewRunner().ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Verify, Administrator);
        await f.NewRunner().ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator, note: "accepted");

        var serialized = string.Join('\n', f.Audit.Mutations().Select(e => JsonSerializer.Serialize<AuditEvent>(e))
            .Concat(f.Journal.Select(entry => JsonSerializer.Serialize(entry))));
        Assert.NotEmpty(f.Audit.Mutations());
        foreach (var sentinel in new[] { SecretSentinel, TargetSentinel, OutputSentinel })
        {
            Assert.DoesNotContain(sentinel, serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheArgumentsHash_IsOverRedactedArguments_SoASecretCannotBeRecoveredFromIt()
    {
        var manifest = new CountingMutatingTool().Manifest;
        var first = new ToolArguments(new JsonObject { ["target"] = "svc-1", ["secret"] = "hunter2" });
        var second = new ToolArguments(new JsonObject { ["target"] = "svc-1", ["secret"] = "correct horse" });
        var otherTarget = new ToolArguments(new JsonObject { ["target"] = "svc-2", ["secret"] = "hunter2" });

        Assert.Equal(AgentRunner.MutationArgumentsHash(manifest, first), AgentRunner.MutationArgumentsHash(manifest, second));
        Assert.NotEqual(DelegationHasher.ComputeArgumentsHash(first), AgentRunner.MutationArgumentsHash(manifest, first));
        Assert.NotEqual(AgentRunner.MutationArgumentsHash(manifest, first), AgentRunner.MutationArgumentsHash(manifest, otherTarget));
        Assert.Matches("^[0-9a-f]{64}$", AgentRunner.MutationArgumentsHash(manifest, first));
    }

    [Fact]
    public async Task TheJournalsIntentHash_IsTheRedactedHash_ForTwoCallsDifferingOnlyInASecret()
    {
        var hashes = new List<string>();
        foreach (var secret in new[] { "hunter2", "correct horse" })
        {
            var f = new Fixture();
            var model = new JournalScriptModel { Arguments = (_, _) => new JsonObject { ["target"] = "svc-1", ["secret"] = secret } };
            await f.NewRunner(model).RunAsync(Goal, Operator, f.TaskId);
            hashes.Add(Assert.Single(f.Journal).Intent.ArgumentsHash);
        }

        Assert.Equal(hashes[0], hashes[1]);
    }

    [Fact]
    public async Task ThePlanProjection_ShowsAnUnsettledPlannedStepAsOutcomeUnknown_AndASettledOneUnchanged()
    {
        var pending = new Fixture();
        pending.Store.CrashAfter = write => write == "intent";
        await pending.CrashAsync(pending.NewRunner());
        var runner = pending.NewRunner();
        var orphan = await pending.TaskAsync();

        var projected = runner.ProjectExecutionPlan(orphan, false, pending.Journal)!;
        var plain = runner.ProjectExecutionPlan(orphan, false)!;

        Assert.Equal(ProjectedStepStatus.OutcomeUnknown, projected.Revisions[0].Steps[0].Status);
        Assert.False(projected.Revisions[0].Steps[0].Current);
        Assert.NotEqual(ProjectedStepStatus.OutcomeUnknown, plain.Revisions[0].Steps[0].Status);
        Assert.Equal(plain.Revisions[0].Steps[1], projected.Revisions[0].Steps[1]);

        var settled = new Fixture();
        settled.Store.CrashAfter = write => write == "outcome";
        await settled.CrashAsync(settled.NewRunner());
        var task = await settled.TaskAsync();
        var withJournal = settled.NewRunner().ProjectExecutionPlan(task, false, settled.Journal)!;
        var withoutJournal = settled.NewRunner().ProjectExecutionPlan(task, false)!;
        Assert.Equal(withoutJournal.Revisions.SelectMany(r => r.Steps), withJournal.Revisions.SelectMany(r => r.Steps));
    }
}
