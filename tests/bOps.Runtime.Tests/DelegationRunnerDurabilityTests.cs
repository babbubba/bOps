// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0030 section 7 (V1.2-F): a delegated run is durable. It is saved at every transition; the intent of each side-effecting
/// step is committed before the step runs and its outcome after; a run that a crash left running resumes without repeating a
/// completed side effect; a step whose outcome is not known is settled by its own verification or waits for an operator;
/// approvals are never carried across a restart; and a start is idempotent on the caller's key. A "crash" is a store that stops
/// persisting at a chosen write and makes the running process unwind at once, so what a resume finds is exactly what was
/// durable at that instant. Fixtures are shared with <see cref="DelegationRunnerTests"/>.
/// </summary>
public sealed partial class DelegationRunnerTests
{
    private static readonly ActorIdentity Administrator = ActorIdentity.FromOperatingSystemUser("admin");

    /// <summary>Thrown by <see cref="CrashableStore"/> where a process would have died; a cancellation, so it unwinds the run at once.</summary>
    private sealed class SimulatedCrashException : OperationCanceledException
    {
        public SimulatedCrashException()
            : base("The process died here.")
        {
        }

        public SimulatedCrashException(string message)
            : base(message)
        {
        }

        public SimulatedCrashException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>An <see cref="IDelegationStore"/> that can die at a chosen write: nothing after it is persisted, and the process unwinds.</summary>
    private sealed class CrashableStore : IDelegationStore
    {
        private readonly Dictionary<Guid, DelegationRun> _runs = [];

        public bool Dead { get; private set; }

        /// <summary>Dies before this write is persisted.</summary>
        public Func<DelegationRun, bool>? CrashBefore { get; set; }

        /// <summary>Dies after this write is persisted.</summary>
        public Func<DelegationRun, bool>? CrashAfter { get; set; }

        /// <summary>Makes this write fail as a disk would, without dying.</summary>
        public Func<DelegationRun, bool>? Fail { get; set; }

        /// <summary>The process dies now: nothing more is persisted.</summary>
        public void Kill() => Dead = true;

        /// <summary>A new process opens the same store: it lives again, and nothing is set to crash.</summary>
        public void Revive()
        {
            Dead = false;
            CrashBefore = null;
            CrashAfter = null;
            Fail = null;
        }

        public Task<DelegationStartResult> StartAsync(DelegationRun run, CancellationToken ct = default)
        {
            if (run.IdempotencyKey is { } key
                && _runs.Values.FirstOrDefault(r => r.Actor == run.Actor && r.IdempotencyKey == key) is { } earlier)
            {
                return Task.FromResult(new DelegationStartResult(earlier, Created: false));
            }

            if (!Dead)
            {
                _runs[run.Id] = run;
            }

            return Task.FromResult(new DelegationStartResult(run, Created: true));
        }

        public Task SaveAsync(DelegationRun run, CancellationToken ct = default)
        {
            if (Dead)
            {
                return Task.CompletedTask;
            }

            if (Fail?.Invoke(run) == true)
            {
                throw new InvalidOperationException("the disk is full");
            }

            if (CrashBefore?.Invoke(run) == true)
            {
                Dead = true;
                throw new SimulatedCrashException();
            }

            _runs[run.Id] = run;
            if (CrashAfter?.Invoke(run) == true)
            {
                Dead = true;
                throw new SimulatedCrashException();
            }

            return Task.CompletedTask;
        }

        public Task<DelegationRun?> LoadAsync(Guid delegationId, CancellationToken ct = default) =>
            Task.FromResult(_runs.GetValueOrDefault(delegationId));

        public Task<IReadOnlyList<DelegationRun>> ListByStatusAsync(DelegationStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DelegationRun>>([.. _runs.Values.Where(r => r.Status == status)]);

        public Task<IReadOnlyList<DelegationRun>> ListRecentAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DelegationRun>>([.. _runs.Values.OrderByDescending(r => r.UpdatedAtUtc).Take(limit)]);
    }

    private static bool IntentOnly(DelegationRun run) => run.Journal.Count == 1 && run.Journal[0].Outcome is null;

    private static bool OutcomeWritten(DelegationRun run) => run.Journal.Count == 1 && run.Journal[0].Outcome is not null;

    private static bool RoleRunning(DelegationRun run, AgentRoleKind kind) =>
        run.Roles.Any(r => r.Agent.Role == kind && r.Status == DelegationRoleStatus.Running);

    private static DelegationRoleRun[] RolesOf(DelegationRun run, AgentRoleKind kind) => [.. run.Roles.Where(r => r.Agent.Role == kind)];

    /// <summary>The part of the happy script a run needs once Discovery is stored: Diagnostic's plan and answer.</summary>
    private static ModelResponse[] DiagnosticOnly() =>
        [PlanningTestSupport.PlanResponse(), Final(FindingsJson("discovery-0"))];

    // ---- the store is kept up to date ----

    [Fact]
    public async Task Start_StoresTheRunAtEveryStep_AndTheStoredRunIsWhatIsReturned()
    {
        var store = new CrashableStore();
        var h = Create(store: store);

        var run = await h.Runner.StartAsync(Request(), Operator);

        var stored = await store.LoadAsync(run.Id);
        Assert.Equal(DelegationStatus.Completed, stored!.Status);
        Assert.Equal(4, stored.Roles.Count);
        Assert.Equal(run.PlanHash, stored.PlanHash);
        Assert.Equal("Find out why the service stopped and fix it.", stored.Objective);
        Assert.Equal(Remediation.SkillId, stored.Remediation!.SkillId);
        Assert.Equal(Remediation.CapabilityName, stored.Remediation.CapabilityName);
        Assert.Equal(run.Journal.Count, stored.Journal.Count);
    }

    [Fact]
    public async Task Start_JournalsOnlyTheSideEffectingStep_WithItsIntentBeforeItsOutcome()
    {
        var store = new CrashableStore();
        var h = Create(store: store);

        var run = await h.Runner.StartAsync(Request(), Operator);

        // Neither the Read steps of Discovery and Verification nor the step's own verification read are journaled.
        var entry = Assert.Single(run.Journal);
        Assert.Equal("service.restart", entry.ToolName);
        Assert.Equal(DelegationHasher.ComputeArgumentsHash(ToolArguments.Empty), entry.ArgumentsHash);
        Assert.Equal(StepOutcomeKind.Succeeded, entry.Outcome!.Kind);
        Assert.Equal(VerificationStatus.Confirmed, entry.Outcome.Verification);
        Assert.True(entry.IntentAtUtc <= entry.Outcome.AtUtc);

        var journal = h.Audit.Events.OfType<DelegationJournalAuditEvent>().ToList();
        Assert.Equal([JournalPhase.Intent, JournalPhase.Outcome], journal.Select(e => e.Phase));
        Assert.Equal(StepOutcomeKind.Succeeded, journal[1].Outcome);
    }

    [Fact]
    public async Task Start_NeverRunsAStepWhoseIntentCouldNotBeCommitted()
    {
        var store = new CrashableStore { Fail = IntentOnly };
        var h = Create(store: store);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(0, h.Restart.ExecutionCount);
        Assert.Equal(DelegationStatus.Failed, run.Status);
        Assert.Contains("disk is full", run.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_ReturnsTheRun_EvenWhenTheStoreCannotRecordHowItEnded()
    {
        var store = new CrashableStore { Fail = r => r.Status == DelegationStatus.Completed };
        var h = Create(store: store);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(DelegationStatus.Running, (await store.LoadAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task Start_WithoutAStore_StoresNothing_AndStillRunsToTheEnd()
    {
        var h = Create();

        var run = await h.Runner.StartAsync(Request(), Operator, idempotencyKey: "ignored");

        // A run that is not stored keeps no journal: there is nothing to resume from, so nothing to reconcile.
        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Empty(run.Journal);
        Assert.Empty(h.Audit.Events.OfType<DelegationJournalAuditEvent>());
    }

    // ---- idempotent start ----

    [Fact]
    public async Task Start_WithTheSameKey_ReturnsTheEarlierRun_AndStartsNothing()
    {
        var store = new CrashableStore();
        var h = Create(HappyScript(), store: store);

        var first = await h.Runner.StartAsync(Request(), Operator, idempotencyKey: "deploy-42");
        var again = await h.Runner.StartAsync(Request(), Operator, idempotencyKey: "deploy-42");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(DelegationStatus.Completed, again.Status);
        Assert.Equal(1, h.Restart.ExecutionCount);
        Assert.Single(h.Approval.Requests);
        Assert.Equal(5, h.Model.Requests.Count);
    }

    [Fact]
    public async Task Start_WithTheSameKeyForAnotherOperator_StartsAnotherRun()
    {
        var store = new CrashableStore();
        var h = Create([.. HappyScript(), .. HappyScript()], store: store);

        var first = await h.Runner.StartAsync(Request(), Operator, idempotencyKey: "deploy-42");
        var second = await h.Runner.StartAsync(Request(), Approver, idempotencyKey: "deploy-42");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, h.Restart.ExecutionCount);
    }

    // ---- a crash at every durable transition ----

    [Fact]
    public async Task Resume_AfterACrashBetweenTheIntentAndTheStep_DoesNotRepeatTheStep_WhenItsVerificationConfirmsIt()
    {
        var store = new CrashableStore { CrashAfter = IntentOnly };
        var restart = new RestartTool();
        var first = Create(store: store, restart: restart);
        var crashed = await first.Runner.StartAsync(Request(), Operator);
        Assert.Equal(0, restart.ExecutionCount);

        store.Revive();
        var second = Create([], store: store, restart: restart);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        // The step was never run, but the system says it is in the state the plan asked for: settled by verification, not repeated.
        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(0, restart.ExecutionCount);
        Assert.Empty(second.Approval.Requests);
        Assert.Empty(second.Model.Requests);
        var entry = Assert.Single(run.Journal);
        Assert.Equal(ReconciliationAction.VerifiedDone, entry.Reconciliation!.Action);
        Assert.Equal(ActorIdentity.RuntimeSystem, entry.Reconciliation.ResolvedBy);
    }

    [Fact]
    public async Task Resume_AfterACrashBetweenTheStepAndItsOutcome_DoesNotRepeatTheStep()
    {
        var store = new CrashableStore { CrashBefore = OutcomeWritten };
        var restart = new RestartTool();
        var first = Create(store: store, restart: restart);
        var crashed = await first.Runner.StartAsync(Request(), Operator);
        Assert.Equal(1, restart.ExecutionCount);
        Assert.Null((await store.LoadAsync(crashed.Id))!.Journal[0].Outcome);

        store.Revive();
        var second = Create([], store: store, restart: restart);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(1, restart.ExecutionCount);
        Assert.Empty(second.Approval.Requests);
        Assert.Equal(ReconciliationAction.VerifiedDone, Assert.Single(run.Journal).Reconciliation!.Action);
    }

    [Theory]
    [InlineData(VerificationStatus.Refuted)]
    [InlineData(VerificationStatus.Inconclusive)]
    public async Task Resume_WaitsForAnOperator_WhenAnAmbiguousStepCannotBeConfirmed_AndNeverRetriesIt(VerificationStatus verdict)
    {
        var store = new CrashableStore { CrashBefore = OutcomeWritten };
        var restart = new RestartTool();
        var first = Create(store: store, restart: restart);
        var crashed = await first.Runner.StartAsync(Request(), Operator);

        store.Revive();
        restart.Verdict = verdict;
        var second = Create([], store: store, restart: restart);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.RequiresReconciliation, run.Status);
        Assert.Equal(1, restart.ExecutionCount);
        Assert.Empty(second.Approval.Requests);
        var entry = Assert.Single(run.Journal);
        Assert.Equal(ReconciliationAction.EscalatedToOperator, entry.Reconciliation!.Action);
        Assert.Equal(verdict, entry.Reconciliation.Verification);
        Assert.Equal(DelegationStatus.RequiresReconciliation, (await store.LoadAsync(run.Id))!.Status);
        Assert.Contains("operator", run.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // It stays there: resuming it again changes nothing and runs nothing.
        var third = Create([], store: store, restart: restart);
        var again = await third.Runner.ResumeAsync(run.Id, Approver);
        Assert.Equal(DelegationStatus.RequiresReconciliation, again.Status);
        Assert.Equal(1, restart.ExecutionCount);
        Assert.Empty(third.Model.Requests);
    }

    [Fact]
    public async Task Resume_AfterACrashBetweenRoles_ContinuesWithTheNextRole_WithoutRunningTheFinishedOne()
    {
        var store = new CrashableStore
        {
            CrashAfter = r => r.Roles.Count == 1 && r.Roles[0].Status == DelegationRoleStatus.Completed,
        };
        var first = Create(store: store);
        var crashed = await first.Runner.StartAsync(Request(), Operator);
        Assert.Equal(3, first.Model.Requests.Count);

        store.Revive();
        var second = Create(DiagnosticOnly(), store: store);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Single(RolesOf(run, AgentRoleKind.Discovery));
        Assert.Equal(2, second.Model.Requests.Count);
        Assert.Equal(1, second.Restart.ExecutionCount);
    }

    [Fact]
    public async Task Resume_AfterACrashWhileAHumanWasBeingAsked_AsksAgain_ForTheSamePlanHash()
    {
        var store = new CrashableStore();
        var approval = new RecordingPlanApproval(_ =>
        {
            store.Kill();
            throw new SimulatedCrashException();
        });
        var first = Create(store: store, approval: approval);
        var crashed = await first.Runner.StartAsync(Request(), Operator);
        Assert.Equal(0, first.Restart.ExecutionCount);

        store.Revive();
        var second = Create([], store: store, restart: first.Restart);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        var asked = Assert.Single(second.Approval.Requests);
        Assert.Equal(crashed.PlanHash, asked.PlanHash);
        Assert.Equal(1, first.Restart.ExecutionCount);
    }

    [Fact]
    public async Task Resume_NeverTreatsAnEarlierApprovalAsAuthority_EvenWhenItWasStored()
    {
        var store = new CrashableStore { CrashAfter = r => r.Approval is not null && r.Roles.Count == 2 };
        var restart = new RestartTool();
        var first = Create(store: store, restart: restart);
        var crashed = await first.Runner.StartAsync(Request(), Operator);
        Assert.NotNull((await store.LoadAsync(crashed.Id))!.Approval);
        Assert.Equal(0, restart.ExecutionCount);

        store.Revive();
        var refusing = new RecordingPlanApproval(_ => new ApprovalDecision(false, Approver, "no, not again"));
        var second = Create([], store: store, restart: restart, approval: refusing);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        // It was approved once, in a process that is gone; the new one asks, is refused, and runs nothing.
        Assert.Single(refusing.Requests);
        Assert.Equal(DelegationStatus.Rejected, run.Status);
        Assert.Equal(0, restart.ExecutionCount);
    }

    [Fact]
    public async Task Resume_AfterACrashDuringVerification_RestartsVerification_WithoutAskingOrRunningAnythingAgain()
    {
        var store = new CrashableStore { CrashAfter = r => RoleRunning(r, AgentRoleKind.Verification) };
        var restart = new RestartTool();
        var first = Create(store: store, restart: restart);
        var crashed = await first.Runner.StartAsync(Request(), Operator);
        Assert.Equal(1, restart.ExecutionCount);
        Assert.Equal(StepOutcomeKind.Succeeded, (await store.LoadAsync(crashed.Id))!.Journal[0].Outcome!.Kind);

        store.Revive();
        var second = Create([], store: store, restart: restart);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(1, restart.ExecutionCount);
        Assert.Empty(second.Approval.Requests);
        var verifications = RolesOf(run, AgentRoleKind.Verification);
        Assert.Equal([DelegationRoleStatus.Failed, DelegationRoleStatus.Completed], verifications.Select(r => r.Status));
        Assert.Equal(VerificationStatus.Confirmed, verifications[1].Verification!.Status);
    }

    [Fact]
    public async Task Resume_AfterACrashHalfWayThroughAPlan_RunsOnlyTheStepsThatWereNotDone()
    {
        var store = new CrashableStore { CrashAfter = OutcomeWritten };
        var restart = new RestartTool();
        var first = Create(store: store, restart: restart, plan: TwoStepPlan);
        var crashed = await first.Runner.StartAsync(Request(), Operator);
        Assert.Equal(1, restart.ExecutionCount);

        store.Revive();
        var second = Create([], store: store, restart: restart, plan: TwoStepPlan);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        // Step 0 is done and is never run again; step 1 is put to a human first, as a plan with steps left must be.
        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(2, restart.ExecutionCount);
        Assert.Single(second.Approval.Requests);
        Assert.Equal([0, 1], run.Journal.Select(e => e.StepIndex).Order());
        Assert.All(run.Journal, e => Assert.Equal(StepOutcomeKind.Succeeded, e.Outcome!.Kind));
    }

    [Fact]
    public async Task Resume_AfterACrashAsTheRunStarted_StartsTheDiscoveryAgain()
    {
        var store = new CrashableStore { CrashAfter = r => RoleRunning(r, AgentRoleKind.Discovery) };
        var first = Create(store: store);
        var crashed = await first.Runner.StartAsync(Request(), Operator);

        store.Revive();
        var second = Create(store: store);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(1, run.ResumeCount);
        var discoveries = RolesOf(run, AgentRoleKind.Discovery);
        Assert.Equal([DelegationRoleStatus.Failed, DelegationRoleStatus.Completed], discoveries.Select(r => r.Status));
        Assert.Contains("process stopped", discoveries[0].ErrorMessage, StringComparison.Ordinal);
    }

    // ---- budgets, limits and refusals across a restart ----

    [Fact]
    public async Task Resume_NeverGivesBackTheBudgetOfARoleThatWasInterrupted()
    {
        var store = new CrashableStore { CrashAfter = r => RoleRunning(r, AgentRoleKind.Discovery) };
        var authority = new DelegationAuthorityRequest(MaxSteps: 12);
        var first = Create(store: store);
        var crashed = await first.Runner.StartAsync(Request(authority: authority), Operator);

        store.Revive();
        var second = Create(store: store);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        // Ten of the twelve steps were granted to the role that stopped, and are charged whatever it used.
        var discoveries = RolesOf(run, AgentRoleKind.Discovery);
        Assert.Equal(new BudgetConsumption(10, 100_000), discoveries[0].Consumed);
        Assert.Equal(2, discoveries[1].Envelope.Budget.MaxSteps);
        Assert.Equal(DelegationStatus.BudgetExceeded, run.Status);
    }

    [Fact]
    public async Task Resume_EndsAsFailed_OnceARunHasBeenResumedAsOftenAsItMay()
    {
        var store = new CrashableStore { CrashAfter = r => RoleRunning(r, AgentRoleKind.Discovery) };
        var first = Create(store: store, maximumResumes: 1);
        var crashed = await first.Runner.StartAsync(Request(), Operator);

        store.Revive();
        store.CrashAfter = r => r.ResumeCount == 1 && RolesOf(r, AgentRoleKind.Discovery).Length == 2 && RoleRunning(r, AgentRoleKind.Discovery);
        var second = Create(store: store, maximumResumes: 1);
        await second.Runner.ResumeAsync(crashed.Id, Approver);
        Assert.Equal(DelegationStatus.Running, (await store.LoadAsync(crashed.Id))!.Status);

        store.Revive();
        var third = Create([], store: store, maximumResumes: 1);
        var run = await third.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Failed, run.Status);
        Assert.Contains("resumed 1 times", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(third.Model.Requests);
        Assert.Equal(DelegationStatus.Failed, (await store.LoadAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task Resume_RefusesToRunAStoredPlanThatNoLongerHasItsRecordedHash()
    {
        var store = new CrashableStore();
        var approval = new RecordingPlanApproval(_ =>
        {
            store.Kill();
            throw new SimulatedCrashException();
        });
        var first = Create(store: store, approval: approval);
        var crashed = await first.Runner.StartAsync(Request(), Operator);

        store.Revive();
        var stored = (await store.LoadAsync(crashed.Id))!;
        await store.SaveAsync(stored with { PlanHash = "not-the-hash-of-the-stored-plan" });
        var second = Create([], store: store, restart: first.Restart);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Failed, run.Status);
        Assert.Contains("no longer matches", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(second.Approval.Requests);
        Assert.Equal(0, first.Restart.ExecutionCount);
    }

    [Fact]
    public async Task Resume_EndsAsDeadlineExceeded_WhenTheDeadlinePassedWhileTheProcessWasDown()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new CrashableStore { CrashAfter = r => r.Roles.Count == 1 && r.Roles[0].Status == DelegationRoleStatus.Completed };
        var first = Create(store: store, clock: clock);
        var crashed = await first.Runner.StartAsync(Request(), Operator);

        store.Revive();
        clock.Advance(TimeSpan.FromHours(3));
        var second = Create([], store: store, clock: clock);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.DeadlineExceeded, run.Status);
        Assert.Empty(second.Model.Requests);
        Assert.Equal(0, second.Restart.ExecutionCount);
    }

    [Fact]
    public async Task Resume_AuditsWhoResumedTheRun()
    {
        var store = new CrashableStore { CrashAfter = r => RoleRunning(r, AgentRoleKind.Discovery) };
        var first = Create(store: store);
        var crashed = await first.Runner.StartAsync(Request(), Operator);

        store.Revive();
        var second = Create(store: store);
        await second.Runner.ResumeAsync(crashed.Id, Approver);

        var resumed = Assert.Single(second.Audit.Events.OfType<DelegationLifecycleAuditEvent>(), e => e.Stage == DelegationStage.Resumed);
        Assert.Equal(Approver, resumed.Actor);
        Assert.Equal(crashed.Id, resumed.TaskId);
    }

    [Fact]
    public async Task Resume_LeavesARunThatEndedOrWaitsForAnOperatorAsItIs()
    {
        var store = new CrashableStore();
        var h = Create(store: store);
        var done = await h.Runner.StartAsync(Request(), Operator);

        var again = await Create([], store: store).Runner.ResumeAsync(done.Id, Approver);

        Assert.Equal(DelegationStatus.Completed, again.Status);
        Assert.Equal(0, again.ResumeCount);
    }

    [Fact]
    public async Task Resume_NeedsAStore_AndARunThatIsStored()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().Runner.ResumeAsync(Guid.NewGuid(), Approver));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(store: new CrashableStore()).Runner.ResumeAsync(Guid.NewGuid(), Approver));
    }

    [Fact]
    public async Task Start_AfterACancellationDuringAStep_StoresTheStepAsCancelled_AndTheRunAsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var store = new CrashableStore();
        var restart = new RestartTool(whileRunning: async ct =>
        {
            await cts.CancelAsync();
            await Task.Delay(Forever, ct);
        });
        var h = Create(store: store, restart: restart);

        var run = await h.Runner.StartAsync(Request(), Operator, ct: cts.Token);

        var stored = (await store.LoadAsync(run.Id))!;
        Assert.Equal(DelegationStatus.Cancelled, stored.Status);
        var entry = Assert.Single(stored.Journal);
        Assert.Equal(StepOutcomeKind.Cancelled, entry.Outcome!.Kind);

        // Cancelled is an end, not an interruption: resuming it is not a retry.
        var again = await Create([], store: store, restart: restart).Runner.ResumeAsync(run.Id, Approver);
        Assert.Equal(DelegationStatus.Cancelled, again.Status);
        Assert.Equal(1, restart.ExecutionCount);
    }

    // ---- the operator's decision ----

    private static async Task<(CrashableStore Store, RestartTool Restart, Guid Id)> WaitingForAnOperatorAsync()
    {
        var store = new CrashableStore { CrashBefore = OutcomeWritten };
        var restart = new RestartTool();
        var crashed = await Create(store: store, restart: restart).Runner.StartAsync(Request(), Operator);
        store.Revive();
        restart.Verdict = VerificationStatus.Refuted;
        var waiting = await Create([], store: store, restart: restart).Runner.ResumeAsync(crashed.Id, Approver);
        Assert.Equal(DelegationStatus.RequiresReconciliation, waiting.Status);
        return (store, restart, crashed.Id);
    }

    [Fact]
    public async Task Reconcile_AcceptingTheStepAsDone_LetsTheRunResumeWithoutRunningItAgain()
    {
        var (store, restart, id) = await WaitingForAnOperatorAsync();
        var h = Create([], store: store, restart: restart);

        var accepted = await h.Runner.ReconcileAsync(id, ReconciliationAction.OperatorAcceptedDone, Administrator, "checked by hand");

        Assert.Equal(DelegationStatus.Running, accepted.Status);
        var entry = Assert.Single(accepted.Journal);
        Assert.Equal(ReconciliationAction.OperatorAcceptedDone, entry.Reconciliation!.Action);
        Assert.Equal(Administrator, entry.Reconciliation.ResolvedBy);
        var audited = Assert.Single(h.Audit.Events.OfType<DelegationReconciliationAuditEvent>());
        Assert.Equal(ReconciliationAction.OperatorAcceptedDone, audited.Action);
        Assert.Equal(Administrator, audited.ResolvedBy);
        Assert.Equal("checked by hand", audited.Note);
        Assert.Equal(0, entry.StepIndex);

        restart.Verdict = VerificationStatus.Confirmed;
        var next = Create([], store: store, restart: restart);
        var run = await next.Runner.ResumeAsync(id, Administrator);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(1, restart.ExecutionCount);
        Assert.Empty(next.Approval.Requests);
    }

    [Fact]
    public async Task Reconcile_AbandoningTheRun_EndsItForGood_AndAuditsIt()
    {
        var (store, restart, id) = await WaitingForAnOperatorAsync();
        var h = Create([], store: store, restart: restart);

        var abandoned = await h.Runner.ReconcileAsync(id, ReconciliationAction.OperatorAbandoned, Administrator, "not worth pursuing");

        Assert.Equal(DelegationStatus.Abandoned, abandoned.Status);
        Assert.Equal(DelegationStatus.Abandoned, (await store.LoadAsync(id))!.Status);
        var audited = Assert.Single(h.Audit.Events.OfType<DelegationReconciliationAuditEvent>());
        Assert.Equal(ReconciliationAction.OperatorAbandoned, audited.Action);
        Assert.Equal(-1, audited.StepIndex);
        Assert.Equal(DelegationStatus.Abandoned, h.Audit.Events.OfType<DelegationLifecycleAuditEvent>().Last().Status);

        var again = await Create([], store: store, restart: restart).Runner.ResumeAsync(id, Administrator);
        Assert.Equal(DelegationStatus.Abandoned, again.Status);
        Assert.Equal(1, restart.ExecutionCount);
    }

    [Fact]
    public async Task Reconcile_RefusesAnAgentAndTheRuntime_AndARunThatIsNotWaiting_AndAnActionThatIsNotAnOperatorsOwn()
    {
        var (store, restart, id) = await WaitingForAnOperatorAsync();
        var h = Create([], store: store, restart: restart);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ReconcileAsync(id, ReconciliationAction.OperatorAcceptedDone, new ActorIdentity("agent", "diagnostic-1", null)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ReconcileAsync(id, ReconciliationAction.OperatorAbandoned, ActorIdentity.RuntimeSystem));
        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Runner.ReconcileAsync(id, ReconciliationAction.VerifiedDone, Administrator));
        Assert.Equal(DelegationStatus.RequiresReconciliation, (await store.LoadAsync(id))!.Status);

        var finished = await Create(store: store).Runner.StartAsync(Request(), Operator);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ReconcileAsync(finished.Id, ReconciliationAction.OperatorAbandoned, Administrator));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().Runner.ReconcileAsync(id, ReconciliationAction.OperatorAbandoned, Administrator));
    }
}
