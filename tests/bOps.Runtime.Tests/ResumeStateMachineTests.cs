// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// HARDEN-3 / ADR-0040: resume is a persisted state-machine transition. One resumability rule, an atomic transition to a new
/// execution attempt, per-attempt versus lifetime budgets with cumulative tokens, zero-plan re-planning, explicit task origin,
/// execution-attempt fencing of every write, and a lifecycle audit trail. The store here is the in-memory double with the same
/// atomic transition semantics as SQLite (under one lock); the SQLite store itself is proven in <c>bOps.Memory.Tests</c>, and
/// the HTTP sequence against real SQLite in <c>bOps.Api.Tests</c>.
/// </summary>
public sealed class ResumeStateMachineTests
{
    private static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("operator");
    private static readonly ActorIdentity Resumer = new("api-user", "resumer", "Resumer");

    // ---- fixtures ----

    private static AgentRunner Runner(
        IChatModel model, ITaskStore store, IAuditSink? audit = null, AgentRunnerOptions? options = null,
        IPolicyEngine? policy = null, IApprovalProvider? approval = null, params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools.Length == 0 ? [new FakeReadTool()] : tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return new AgentRunner(model, registry, policy ?? new DefaultTestPolicyEngine(), approval ?? new NeverCalledApprovalProvider(),
            audit ?? new RecordingAuditSink(), store, TimeProvider.System, NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());
    }

    private static PlanStep ToolStep(int index, int executionAttempt = 1) =>
        new(index, "test.read", new ModelToolCall($"call-{index}", "test.read", ToolArguments.Empty), ToolCallResult.Success("ok"), "ok", 0)
        {
            ExecutionAttempt = executionAttempt,
        };

    private static PlanStep FailureStep(int index) => new(index, "Model protocol failure", null, null, "The provider failed.");

    /// <summary>A stored task: <paramref name="executableSteps"/> tool steps, then <paramref name="failureSteps"/> synthetic failure steps.</summary>
    private static TaskState Stored(
        AgentTaskStatus status, TaskOrigin origin = TaskOrigin.Ordinary, int executableSteps = 1, int failureSteps = 0, int plans = 1,
        TaskAccounting? accounting = null, int executionAttempt = 1, string goal = "check the disk", string expectedTool = "test.read")
    {
        var steps = Enumerable.Range(0, executableSteps).Select(i => ToolStep(i))
            .Concat(Enumerable.Range(executableSteps, failureSteps).Select(FailureStep))
            .ToList();
        return new TaskState(Guid.NewGuid(), NodeId.Local, goal, status, steps,
            [.. Enumerable.Range(0, plans).Select(revision => new AgentPlan(revision, $"plan {revision}",
                [.. Enumerable.Range(0, 100).Select(index => new PlannedStep(index, $"step {index}", expectedTool))]))], DateTimeOffset.UtcNow)
        {
            ExecutionAttempt = executionAttempt,
            Origin = origin,
            Accounting = accounting ?? new TaskAccounting(100, executableSteps, Math.Max(0, plans - 1)),
            // ADR-0051 §11: an ordinary task the runtime creates now records its journal mode; the legacy (Absent) rows have their
            // own tests (OrdinaryMutationJournalDurabilityTests).
            MutationJournalMode = origin == TaskOrigin.Ordinary ? TaskMutationJournalMode.Journaled : TaskMutationJournalMode.Absent,
        };
    }

    private static (InMemoryTaskStore Store, TaskState Task) Seeded(TaskState task)
    {
        var store = new InMemoryTaskStore();
        store.Seed(task);
        return (store, task);
    }

    private static ModelResponse Read(ModelUsage? usage = null) =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), "test.read", ToolArguments.Empty)], false, usage);

    private static ModelResponse Final(ModelUsage? usage = null) => new("done", [], true, usage);

    private static ModelResponse[] Reads(int count) => [.. Enumerable.Range(0, count).Select(_ => Read())];

    private static List<TaskLifecycleAuditEvent> Lifecycle(RecordingAuditSink audit) => [.. audit.Events.OfType<TaskLifecycleAuditEvent>()];

    // ---- accepted resumes ----

    // H3-01, H3-14
    [Fact]
    public async Task H3_01_H3_14_AFailedOrdinaryTask_IsResumedUnderTheNextExecutionAttempt_AndProgresses()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed, failureSteps: 1));
        var model = new FakeChatModel(Read(), Final());

        var result = await Runner(model, store).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.ExecutionAttempt);
        Assert.Equal(Resumer, result.ResumedBy);
        Assert.NotNull(result.ResumedAtUtc);
        Assert.Equal(TaskTerminalKind.Completed, result.TerminalReason!.Kind);
        Assert.Equal([1, null, 2, 2], result.Steps.Select(step => step.ExecutionAttempt));
        Assert.Equal([0, 1, 2, 3], result.Steps.Select(step => step.Index));
        // Exactly one increment: every write of the resumed attempt carries 2, none carries 3.
        Assert.All(store.Saves, saved => Assert.Equal(2, saved.ExecutionAttempt));
        Assert.Equal(2, (await store.LoadAsync(stored.Id))!.ExecutionAttempt);
    }

    // H3-06
    [Fact]
    public async Task H3_06_ACancelledTask_IsResumed()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Cancelled));

        var result = await Runner(new FakeChatModel(Final()), store).ResumeAsync(stored, Resumer);

        Assert.Equal((AgentTaskStatus.Completed, 2), (result.Status, result.ExecutionAttempt));
    }

    // H3-02: the exact prior K4 failure — a Failed task whose initial plan call failed has no plan; resume used to 202 and
    // then throw InvalidOperationException inside the detached run. It now re-plans as revision 0 of the new attempt.
    [Fact]
    public async Task H3_02_AFailedTaskWithNoPlan_RePlansUnderTheNewAttempt_AndProgresses()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed, executableSteps: 0, failureSteps: 1, plans: 0));
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Read(), Final());

        var result = await Runner(model, store).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.ExecutionAttempt);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(0, plan.Revision);
        Assert.Empty(model.Requests[0].AvailableTools); // the first call of the attempt is the planning call
        Assert.Equal(0, result.Accounting!.LifetimeReplans);
        Assert.Equal(2, result.Accounting.LifetimeSteps);
    }

    [Fact]
    public async Task H3_02_AZeroPlanResumeWhosePlanningFailsAgain_EndsFailedThroughTheNormalPath_NeverThrows()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed, executableSteps: 0, failureSteps: 1, plans: 0));
        var model = new ThrowingChatModel(new ModelProtocolException("provider down") { FailureKind = ModelFailureKind.Authentication });

        var result = await Runner(model, store).ResumeAsync(stored, Resumer);

        Assert.Equal((AgentTaskStatus.Failed, 2), (result.Status, result.ExecutionAttempt));
        Assert.Equal(TaskTerminalKind.ModelFailure, result.TerminalReason!.Kind);
        Assert.Equal(ModelFailureKind.Authentication, result.TerminalReason.FailureKind);
        Assert.Equal(AgentTaskStatus.Failed, (await store.LoadAsync(stored.Id))!.Status);
    }

    // ---- refusals ----

    // H3-03, H3-04, H3-05: refused before anything is written or executed, and audited with the refusal code only.
    [Theory]
    [InlineData(AgentTaskStatus.Completed, TaskResumeRefusal.TaskCompleted)]
    [InlineData(AgentTaskStatus.PolicyBlocked, TaskResumeRefusal.TaskPolicyBlocked)]
    [InlineData(AgentTaskStatus.Running, TaskResumeRefusal.TaskRunning)]
    public async Task H3_03_H3_04_H3_05_ANonResumableStatus_IsRefused_WithoutAnyWriteOrModelCall(AgentTaskStatus status, string code)
    {
        var (store, stored) = Seeded(Stored(status));
        var model = new FakeChatModel();
        var audit = new RecordingAuditSink();

        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() => Runner(model, store, audit).ResumeAsync(stored, Resumer));

        Assert.Equal(code, refused.Refusal.Code);
        Assert.Empty(model.Requests);
        Assert.Empty(store.Saves);
        Assert.Same(stored, await store.LoadAsync(stored.Id));
        var rejected = Assert.Single(Lifecycle(audit));
        Assert.Equal((TaskLifecycleStage.ResumeRejected, 1, code, status), (rejected.Stage, rejected.ExecutionAttempt, rejected.RefusalCode, rejected.PriorStatus));
    }

    // H3-05: a persisted Running task is refused even though nothing in this process executes it — the absence of a local
    // executor proves nothing about other processes.
    [Fact]
    public async Task H3_05_APersistedRunningTaskWithNoExecutorAnywhereHere_IsStillRefused()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Running));

        var acquisition = await Runner(new FakeChatModel(), store).TryAcquireResumeAsync(stored.Id, Resumer);

        Assert.Equal(TaskResumeOutcome.Refused, acquisition.Outcome);
        Assert.Equal(TaskResumeRefusal.TaskRunning, acquisition.Refusal!.Code);
        Assert.Equal((AgentTaskStatus.Running, 1), ((await store.LoadAsync(stored.Id))!.Status, (await store.LoadAsync(stored.Id))!.ExecutionAttempt));
    }

    [Fact]
    public async Task AnUnknownTaskId_IsNotFound_AndNotAudited()
    {
        var audit = new RecordingAuditSink();

        var acquisition = await Runner(new FakeChatModel(), new InMemoryTaskStore(), audit).TryAcquireResumeAsync(Guid.NewGuid(), Resumer);

        Assert.Equal(TaskResumeOutcome.NotFound, acquisition.Outcome);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task AStoreWithoutAtomicTransitions_FailsClosed()
    {
        var store = new PlainTaskStore();
        // ADR-0051 §11: a task created on a store without the journal capability is MutationsDisabled, which passes the journal rows.
        var stored = Stored(AgentTaskStatus.Failed) with { MutationJournalMode = TaskMutationJournalMode.MutationsDisabled };
        await store.SaveAsync(stored);

        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() => Runner(new FakeChatModel(), store).ResumeAsync(stored, Resumer));

        Assert.Equal(TaskResumeRefusal.TransitionUnsupported, refused.Refusal.Code);
        Assert.Equal((AgentTaskStatus.Failed, 1), ((await store.LoadAsync(stored.Id))!.Status, (await store.LoadAsync(stored.Id))!.ExecutionAttempt));
    }

    // H3-15: a lost transition writes nothing and increments nothing.
    [Fact]
    public async Task H3_15_ALostTransition_IsAConflict_AndDoesNotIncrementTheExecutionAttempt()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed));
        var audit = new RecordingAuditSink();
        var winner = stored with { Status = AgentTaskStatus.Running, ExecutionAttempt = 2, Goal = "the other resume" };
        store.BeforeTransition = _ =>
        {
            store.BeforeTransition = null;
            store.Seed(winner); // another resume acquires the task between our read and our transition
            return Task.CompletedTask;
        };

        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() => Runner(new FakeChatModel(), store, audit).ResumeAsync(stored, Resumer));

        Assert.Equal(TaskResumeRefusal.ResumeConflict, refused.Refusal.Code);
        Assert.Same(winner, await store.LoadAsync(stored.Id));
        Assert.Equal(TaskResumeRefusal.ResumeConflict, Assert.Single(Lifecycle(audit)).RefusalCode);
    }

    // ---- budgets ----

    // H3-07: MaxStepsReached with lifetime headroom gets a fresh per-attempt budget and does real work — not the C-06 zero-work loop.
    [Fact]
    public async Task H3_07_MaxStepsReachedWithLifetimeHeadroom_RunsAFreshPerAttemptBudget()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.MaxStepsReached, executableSteps: 15));
        var model = new FakeChatModel(Reads(15));

        var result = await Runner(model, store).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.MaxStepsReached, result.Status);
        Assert.Equal(TaskTerminalKind.StepLimit, result.TerminalReason!.Kind);
        Assert.Equal(30, result.Steps.Count);
        Assert.Equal(15, result.Steps.Count(step => step.ExecutionAttempt == 2));
        Assert.Equal(30, result.Accounting!.LifetimeSteps);
        Assert.Equal(15, model.Requests.Count);
    }

    [Fact]
    public async Task H3_07_TheLifetimeCapAlwaysWins_OverAFreshPerAttemptBudget()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.MaxStepsReached, executableSteps: 58));
        var model = new FakeChatModel(Reads(15));
        var runner = Runner(model, store);

        var result = await runner.ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.MaxStepsReached, result.Status);
        Assert.Equal(TaskTerminalKind.LifetimeStepLimit, result.TerminalReason!.Kind);
        Assert.Equal(2, result.Steps.Count(step => step.ExecutionAttempt == 2));
        Assert.Equal(60, result.Accounting!.LifetimeSteps);
        Assert.Equal(TaskResumeRefusal.LifetimeStepsExhausted, runner.EvaluateResume(result).Refusal!.Code);
    }

    // H3-08
    [Fact]
    public async Task H3_08_MaxStepsReachedWithNoLifetimeCapacity_IsRefused_NeverAZeroWorkResume()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.MaxStepsReached, executableSteps: 60));
        var model = new FakeChatModel();

        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() => Runner(model, store).ResumeAsync(stored, Resumer));

        Assert.Equal(TaskResumeRefusal.LifetimeStepsExhausted, refused.Refusal.Code);
        Assert.Empty(model.Requests);
        Assert.Equal(1, (await store.LoadAsync(stored.Id))!.ExecutionAttempt);
    }

    // C-10: synthetic failure steps are recorded but never consume the step budget.
    [Fact]
    public async Task SyntheticFailureSteps_NeverConsumeTheStepBudget()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed, executableSteps: 7, failureSteps: 3, accounting: new TaskAccounting(0, 7, 0)));
        var model = new FakeChatModel(Reads(15));

        var result = await Runner(model, store).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.MaxStepsReached, result.Status);
        Assert.Equal(15, result.Steps.Count(step => step.ExecutionAttempt == 2)); // the full per-attempt budget, not 15 - 10
        Assert.Equal(22, result.Accounting!.LifetimeSteps);
    }

    // H3-09
    [Fact]
    public async Task H3_09_ReplanLimitReachedWithNoLifetimeReplans_IsRefused()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.ReplanLimitReached, plans: 13));

        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() => Runner(new FakeChatModel(), store).ResumeAsync(stored, Resumer));

        Assert.Equal(TaskResumeRefusal.LifetimeReplansExhausted, refused.Refusal.Code);
    }

    [Fact]
    public async Task H3_09_ReplanLimitReachedWithLifetimeHeadroom_GetsAFreshPerAttemptReplanBudget()
    {
        // Attempt 1 used its three replans (plans 0..3). The resumed attempt may replan again.
        var (store, stored) = Seeded(Stored(AgentTaskStatus.ReplanLimitReached, plans: 4));
        var model = new FakeChatModel(
            new ModelResponse(null, [new ModelToolCall("call-x", "no.such.tool", ToolArguments.Empty)], false, null), // a deviation
            PlanningTestSupport.PlanResponse(stepCount: 0),                                                           // the replan
            Final());

        var result = await Runner(model, store).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(5, result.Plans.Count);
        Assert.Equal(4, result.Plans[^1].Revision);
        Assert.Equal(4, result.Accounting!.LifetimeReplans);
    }

    [Fact]
    public async Task H3_09_TheLifetimeReplanCapWinsInsideAnAttempt()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed, plans: 12)); // 11 replans used; one left over the lifetime
        var deviation = new ModelResponse(null, [new ModelToolCall("call-x", "no.such.tool", ToolArguments.Empty)], false, null);
        var model = new FakeChatModel(deviation, PlanningTestSupport.PlanResponse(stepCount: 0), deviation);

        var result = await Runner(model, store).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.ReplanLimitReached, result.Status);
        Assert.Equal(TaskTerminalKind.LifetimeReplanLimit, result.TerminalReason!.Kind);
        Assert.Equal(12, result.Accounting!.LifetimeReplans);
    }

    // H3-10
    [Theory]
    [InlineData(1000)]
    [InlineData(900)]
    public async Task H3_10_BudgetExceededUnderTheSameOrALowerTokenCap_IsRefused(int cap)
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.BudgetExceeded, accounting: new TaskAccounting(1000, 1, 0)));

        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() =>
            Runner(new FakeChatModel(), store, options: new AgentRunnerOptions { MaxTotalTokens = cap }).ResumeAsync(stored, Resumer));

        Assert.Equal(TaskResumeRefusal.TokenBudgetExhausted, refused.Refusal.Code);
    }

    // H3-11, H3-12
    [Fact]
    public async Task H3_11_H3_12_BudgetExceededAfterTheCapWasRaised_IsResumed_AndTokensAreNeverReset()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.BudgetExceeded, accounting: new TaskAccounting(1000, 1, 0)));
        var audit = new RecordingAuditSink();
        var model = new FakeChatModel(Final(new ModelUsage(50, 25, null)));

        var result = await Runner(model, store, audit, new AgentRunnerOptions { MaxTotalTokens = 2000 }).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1075, result.Accounting!.TokensUsed);
        var accepted = Lifecycle(audit).Single(e => e.Stage == TaskLifecycleStage.ResumeAccepted);
        Assert.Equal((1000L, 2000), (accepted.TokensUsed, accepted.MaxTotalTokens!.Value));
    }

    [Fact]
    public async Task H3_12_TheTokenCapIsCumulativeAcrossAttempts()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.BudgetExceeded, accounting: new TaskAccounting(1000, 1, 0)));
        var model = new FakeChatModel(Read(new ModelUsage(150, 50, null)), Final());

        var result = await Runner(model, store, options: new AgentRunnerOptions { MaxTotalTokens = 1100 }).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.BudgetExceeded, result.Status);
        Assert.Equal(TaskTerminalKind.TokenBudget, result.TerminalReason!.Kind);
        Assert.Equal(1200, result.Accounting!.TokensUsed);
        Assert.Single(model.Requests);
    }

    // A cancelled attempt persists the tokens it already spent before the cancellation propagates, so cancel-and-resume
    // cannot launder token usage.
    [Fact]
    public async Task ACancelledAttempt_KeepsTheTokensItAlreadySpent()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed, accounting: new TaskAccounting(1000, 1, 0)));
        using var cts = new CancellationTokenSource();
        var model = new CallbackChatModel(async _ => await cts.CancelAsync(), Read(new ModelUsage(300, 100, null)));
        var runner = Runner(model, store);
        var acquired = (await runner.TryAcquireResumeAsync(stored.Id, Resumer)).Task!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteAcquiredResumeAsync(acquired, Resumer, cts.Token));
        Assert.True(await runner.CompleteCancellationAsync(stored.Id, 2, Resumer, acquired));

        var persisted = (await store.LoadAsync(stored.Id))!;
        Assert.Equal((AgentTaskStatus.Cancelled, 2), (persisted.Status, persisted.ExecutionAttempt));
        Assert.Equal(1400, persisted.Accounting!.TokensUsed);
    }

    // Tokens are lifetime (ADR-0040 §5.2): a malformed plan reply recorded before the corrective re-ask failed still counts.
    [Fact]
    public async Task TheTokensOfAPlanCallThatFailedAfterAMalformedReply_AreStillCounted()
    {
        var store = new InMemoryTaskStore();
        var model = new SequenceChatModel(
            _ => new ModelResponse("not a plan", [], false, new ModelUsage(30, 10, null)),
            _ => throw new ModelProtocolException("provider down") { FailureKind = ModelFailureKind.Authentication });

        var result = await Runner(model, store).RunAsync("check", Operator);

        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Equal(40, result.Accounting!.TokensUsed);
    }

    /// <summary>Answers call <c>n</c> with the <c>n</c>-th function, which may throw.</summary>
    private sealed class SequenceChatModel(params Func<ModelRequest, ModelResponse>[] replies) : IChatModel
    {
        private int _calls;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            Task.FromResult(replies[_calls++](request));
    }

    // ---- legacy accounting ----

    // H3-17: a row without accounting gets it derived deterministically from its own history — every recorded model call once,
    // executable steps only, revisions after 0 — and materialized by the first authoritative write, the resume transition.
    [Fact]
    public async Task H3_17_MissingAccounting_IsDerivedDeterministically_AndMaterializedByTheTransition()
    {
        static ModelCallRecord Call(int prompt, int completion) =>
            new("test", "m", "m", DateTimeOffset.UtcNow, 1, ModelCallOutcome.Success, new ModelUsage(prompt, completion, null), "stop", null, null, null, false);
        var failedCall = new ModelCallRecord("test", "m", null, DateTimeOffset.UtcNow, 1, ModelCallOutcome.Failure, null, null, "boom", null, null, false);
        var legacy = new TaskState(Guid.NewGuid(), NodeId.Local, "legacy", AgentTaskStatus.Failed,
            [
                ToolStep(0) with { ModelCalls = [Call(20, 5)], ExecutionAttempt = null },
                ToolStep(1) with { ModelCalls = [failedCall, Call(4, 1)], ExecutionAttempt = null },
                FailureStep(2) with { ModelCalls = [Call(7, 3)] },
            ],
            [new AgentPlan(0, "p0", []) { ModelCalls = [Call(30, 10)] }, new AgentPlan(1, "p1", []) { ModelCalls = [Call(6, 4)] }],
            DateTimeOffset.UtcNow)
        {
            Origin = TaskOrigin.Ordinary,
            // ADR-0051 §12.1: a row with no journal mode is refused before any budget is read; the derivation is the subject here.
            MutationJournalMode = TaskMutationJournalMode.Journaled,
        };
        var expected = new TaskAccounting(90, 2, 1);

        Assert.Equal(expected, TaskResumePolicy.EffectiveAccounting(legacy));
        Assert.Equal(expected, TaskResumePolicy.EffectiveAccounting(legacy));

        var (store, _) = Seeded(legacy);
        var acquisition = await Runner(new FakeChatModel(), store).TryAcquireResumeAsync(legacy.Id, Resumer);

        Assert.Equal(TaskResumeOutcome.Acquired, acquisition.Outcome);
        Assert.Equal(expected, (await store.LoadAsync(legacy.Id))!.Accounting);
    }

    // ---- origin ----

    // H3-18: a role task is marked Delegated from its first save, and ordinary resume refuses it whatever its status.
    [Fact]
    public async Task H3_18_ADelegatedRoleTask_IsMarked_AndOrdinaryResumeRefusesIt()
    {
        var store = new InMemoryTaskStore();
        var envelope = new AuthorityEnvelope(Operator, Depth: 1, [], [], ["test.read"], RiskLevel.Read, BlastRadius.Single, ["local"], ["test"],
            new DelegationBudget(10, 100_000, DateTimeOffset.UtcNow.AddHours(1)), null);
        var scope = DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), AgentRoleKind.Discovery), envelope);
        var runner = Runner(new FakeChatModel(PlanningTestSupport.PlanResponse(stepCount: 0), Final()), store);

        var role = await runner.RunDelegatedAsync("discover", Operator, scope);

        Assert.Equal(TaskOrigin.Delegated, role.Origin);
        Assert.Equal(scope.Correlation.DelegationId, role.DelegationId);
        Assert.Equal(AgentRoleKind.Discovery, role.DelegationRole);
        Assert.All(store.Saves, saved => Assert.Equal(TaskOrigin.Delegated, saved.Origin));

        store.Seed(role with { Status = AgentTaskStatus.Failed });
        var persistedRole = (await store.LoadAsync(role.Id))!;
        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() => runner.ResumeAsync(persistedRole, Resumer));
        Assert.Equal(TaskResumeRefusal.TaskDelegated, refused.Refusal.Code);
    }

    // H3-19: a row persisted before the origin was recorded cannot be proven ordinary and is refused.
    [Fact]
    public async Task H3_19_ALegacyTaskWithoutOrigin_IsUnknown_AndRefused()
    {
        var json = JsonSerializer.SerializeToNode(Stored(AgentTaskStatus.Failed))!.AsObject();
        json.Remove(nameof(TaskState.Origin));
        var legacy = json.Deserialize<TaskState>()!;
        var (store, _) = Seeded(legacy);

        Assert.Equal(TaskOrigin.Unknown, legacy.Origin);
        var refused = await Assert.ThrowsAsync<TaskResumeRefusedException>(() => Runner(new FakeChatModel(), store).ResumeAsync(legacy, Resumer));
        Assert.Equal(TaskResumeRefusal.OriginUnknown, refused.Refusal.Code);
    }

    // H3-20: origin alone decides. The runtime's own role-goal text does not make an ordinary task delegated, and an ordinary
    // goal does not make an unknown-origin task ordinary.
    [Fact]
    public async Task H3_20_NoGoalTextHeuristic_DecidesOrigin()
    {
        var options = new AgentRunnerOptions();
        var roleWorded = Stored(AgentTaskStatus.Failed, goal: DelegationRoleData.DiscoveryGoal("check the disk"));
        var ordinaryWorded = Stored(AgentTaskStatus.Failed, TaskOrigin.Unknown);

        Assert.True(TaskResumePolicy.Evaluate(roleWorded, options).Resumable);
        Assert.Equal(TaskResumeRefusal.OriginUnknown, TaskResumePolicy.Evaluate(ordinaryWorded, options).Refusal!.Code);

        var (store, _) = Seeded(roleWorded);
        Assert.Equal(AgentTaskStatus.Completed, (await Runner(new FakeChatModel(Final()), store).ResumeAsync(roleWorded, Resumer)).Status);
    }

    [Fact]
    public async Task AFreshTask_RecordsItsOriginFirstAttemptAndAccounting()
    {
        var store = new InMemoryTaskStore();

        var result = await Runner(new FakeChatModel(PlanningTestSupport.PlanResponse(), Read(new ModelUsage(10, 5, null)), Final()), store)
            .RunAsync("check", Operator);

        Assert.Equal((TaskOrigin.Ordinary, 1), (result.Origin, result.ExecutionAttempt));
        Assert.Equal(new TaskAccounting(15, 2, 0), result.Accounting);
        Assert.Null(result.ResumedBy);
        Assert.All(result.Steps, step => Assert.Equal(1, step.ExecutionAttempt));
    }

    // ---- fencing ----

    // H3-24: while execution attempt 2 has a step in flight, the task is taken over (attempt 3). Attempt 2's next write is
    // refused: it stops, writes nothing more, and is audited as superseded.
    [Fact]
    public async Task H3_24_AStaleAttemptsSave_IsFenced_AndTheAttemptStops()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed));
        var audit = new RecordingAuditSink();
        TaskState? takeover = null;
        var model = new CallbackChatModel(
            _ =>
            {
                takeover = stored with { Status = AgentTaskStatus.Running, ExecutionAttempt = 3, Goal = "taken over" };
                store.Seed(takeover);
                return Task.CompletedTask;
            },
            Read(), Read(), Final());

        var result = await Runner(model, store, audit).ResumeAsync(stored, Resumer);

        Assert.Same(takeover, result);
        Assert.Same(takeover, await store.LoadAsync(stored.Id));
        Assert.Equal(1, model.Calls);
        var superseded = Assert.Single(Lifecycle(audit), e => e.Stage == TaskLifecycleStage.ExecutionSuperseded);
        Assert.Equal(2, superseded.ExecutionAttempt);
        Assert.DoesNotContain(Lifecycle(audit), e => e.Stage == TaskLifecycleStage.ExecutionTerminal);
    }

    // H3-25: the same for a terminal write.
    [Fact]
    public async Task H3_25_AStaleAttemptsTerminalWrite_IsFenced()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed));
        TaskState? takeover = null;
        var model = new CallbackChatModel(
            _ =>
            {
                takeover = stored with { Status = AgentTaskStatus.Running, ExecutionAttempt = 3 };
                store.Seed(takeover);
                return Task.CompletedTask;
            },
            Final());

        var result = await Runner(model, store).ResumeAsync(stored, Resumer);

        Assert.Same(takeover, result);
        Assert.Equal((AgentTaskStatus.Running, 3), ((await store.LoadAsync(stored.Id))!.Status, (await store.LoadAsync(stored.Id))!.ExecutionAttempt));
        Assert.DoesNotContain(store.Saves, saved => saved.Status == AgentTaskStatus.Completed);
    }

    // H3-26
    [Fact]
    public async Task H3_26_TheCancellationWrite_IsFencedByExecutionAttempt()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Running, executionAttempt: 3));
        var audit = new RecordingAuditSink();
        var runner = Runner(new FakeChatModel(), store, audit);

        Assert.False(await runner.CompleteCancellationAsync(stored.Id, 2, Resumer, null));
        Assert.Same(stored, await store.LoadAsync(stored.Id));
        Assert.Empty(audit.Events);

        Assert.True(await runner.CompleteCancellationAsync(stored.Id, 3, Resumer, null));
        var cancelled = (await store.LoadAsync(stored.Id))!;
        Assert.Equal((AgentTaskStatus.Cancelled, 3, TaskTerminalKind.Cancelled), (cancelled.Status, cancelled.ExecutionAttempt, cancelled.TerminalReason!.Kind));
        var terminal = Assert.Single(Lifecycle(audit));
        Assert.Equal((TaskLifecycleStage.ExecutionTerminal, 3, TaskTerminalKind.Cancelled), (terminal.Stage, terminal.ExecutionAttempt, terminal.TerminalKind!.Value));
    }

    // H3-27
    [Fact]
    public async Task H3_27_TheHarden2Backstop_IsFencedByExecutionAttempt()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Running, executionAttempt: 3));
        var audit = new RecordingAuditSink();
        var runner = Runner(new FakeChatModel(), store, audit);

        Assert.False(await runner.ContainEscapedFailureAsync(stored.Id, 2, Resumer, null, new InvalidOperationException("stale")));
        Assert.Same(stored, await store.LoadAsync(stored.Id));
        Assert.Empty(audit.Events);

        Assert.True(await runner.ContainEscapedFailureAsync(stored.Id, 3, Resumer, null, new InvalidOperationException("current")));
        var failed = (await store.LoadAsync(stored.Id))!;
        Assert.Equal((AgentTaskStatus.Failed, 3, TaskTerminalKind.RuntimeFailure), (failed.Status, failed.ExecutionAttempt, failed.TerminalReason!.Kind));
        Assert.Single(audit.Events.OfType<TaskExecutionFaultAuditEvent>());
        Assert.Equal(TaskLifecycleStage.ExecutionTerminal, Assert.Single(Lifecycle(audit)).Stage);
    }

    [Fact]
    public async Task AnAcquiredAttemptThatIsNotAdmitted_IsContainedFailed_WithoutConsumingBudget()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Cancelled));
        var audit = new RecordingAuditSink();
        var runner = Runner(new FakeChatModel(), store, audit);
        var acquired = (await runner.TryAcquireResumeAsync(stored.Id, Resumer)).Task!;

        Assert.True(await runner.ContainUnadmittedResumeAsync(acquired, Resumer));

        var failed = (await store.LoadAsync(stored.Id))!;
        Assert.Equal((AgentTaskStatus.Failed, 2, TaskTerminalKind.NotAdmitted), (failed.Status, failed.ExecutionAttempt, failed.TerminalReason!.Kind));
        Assert.Equal("Execution not started", failed.Steps[^1].Description);
        Assert.Equal(stored.Accounting, failed.Accounting);
        Assert.Equal(TaskLifecycleStage.ExecutionTerminal, Lifecycle(audit)[^1].Stage);
        Assert.True(runner.EvaluateResume(failed).Resumable);
    }

    [Fact]
    public async Task OnlyAnAcquiredSnapshot_CanBeExecuted()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed));

        await Assert.ThrowsAsync<ArgumentException>(() => Runner(new FakeChatModel(), store).ExecuteAcquiredResumeAsync(stored, Resumer));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Runner(new FakeChatModel(), store).ExecuteAcquiredResumeAsync(stored with { Status = AgentTaskStatus.Running }, Resumer));
    }

    // ---- history, containment, policy ----

    // H3-28: the resumed attempt sees exactly the reconstructed history (ADR-0038): no synthetic failure step, no extra note.
    [Fact]
    public async Task H3_28_TheResumedHistory_IsTheTruthfulReconstruction()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed, executableSteps: 2, failureSteps: 1));
        var model = new FakeChatModel(Final());

        await Runner(model, store).ResumeAsync(stored, Resumer);

        var history = model.Requests[0].History;
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool],
            history.Select(turn => turn.Role));
        Assert.Equal("check the disk", history[0].Content);
        Assert.Equal(["call-0", "call-1"], history.Where(turn => turn.Role == ChatRole.Tool).Select(turn => turn.ToolCallId));
    }

    // H3-29: HARDEN-2's per-attempt model timeout still contains a hung provider inside a resumed attempt.
    [Fact]
    public async Task H3_29_ModelTimeoutContainment_StillWorksInsideAResumedAttempt()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed));
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions
        {
            ModelCallMaxAttempts = 1,
            ModelCallAttemptTimeout = TimeSpan.FromMilliseconds(50),
            ModelCallBudget = TimeSpan.FromSeconds(5),
        };

        var result = await Runner(new HangingChatModel(), store, audit, options).ResumeAsync(stored, Resumer);

        Assert.Equal((AgentTaskStatus.Failed, 2), (result.Status, result.ExecutionAttempt));
        Assert.Equal((TaskTerminalKind.ModelFailure, ModelFailureKind.Timeout), (result.TerminalReason!.Kind, result.TerminalReason.FailureKind!.Value));
        Assert.Equal(AgentTaskStatus.Failed, (await store.LoadAsync(stored.Id))!.Status);
        Assert.Contains(audit.Events, e => e is ModelCallAuditEvent { FailureKind: ModelFailureKind.Timeout });
    }

    // H3-30: a resumed step goes through policy and approval again; nothing is carried over from an earlier attempt.
    [Fact]
    public async Task H3_30_PolicyAndApproval_AreEnforcedAgainInAResumedAttempt()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Failed, expectedTool: "test.highrisk"));
        var tool = new FakeHighRiskTool();
        var approval = new CountingApprovalProvider(approved: false);
        var model = new FakeChatModel(
            new ModelResponse(null, [new ModelToolCall("call-h", tool.Manifest.Name, ToolArguments.Empty)], false, null),
            PlanningTestSupport.PlanResponse(stepCount: 0),
            Final());

        var result = await Runner(model, store, policy: new StubPolicyEngine(PolicyMode.Approval), approval: approval, tools: [new FakeReadTool(), tool])
            .ResumeAsync(stored, Resumer);

        Assert.Equal(1, approval.Requests);
        var step = result.Steps.Single(s => s.ToolCall?.Id == "call-h");
        Assert.NotEqual(ToolOutcome.Success, step.Result!.Outcome);
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
    }

    // ---- lifecycle audit ----

    // H3-31
    [Fact]
    public async Task H3_31_EveryLifecycleEvent_CarriesItsExecutionAttempt()
    {
        var store = new InMemoryTaskStore();
        var audit = new RecordingAuditSink();
        var first = await Runner(new FakeChatModel(PlanningTestSupport.PlanResponse(), Read()), store, audit,
            new AgentRunnerOptions { MaxSteps = 1 }).RunAsync("check", Operator);
        Assert.Equal(AgentTaskStatus.MaxStepsReached, first.Status);

        await Runner(new FakeChatModel(Final()), store, audit, new AgentRunnerOptions { MaxSteps = 1 }).ResumeAsync(first, Resumer);

        Assert.Equal(
            [
                (TaskLifecycleStage.ExecutionStarted, 1),
                (TaskLifecycleStage.ExecutionTerminal, 1),
                (TaskLifecycleStage.ResumeAccepted, 2),
                (TaskLifecycleStage.ExecutionStarted, 2),
                (TaskLifecycleStage.ExecutionTerminal, 2),
            ],
            Lifecycle(audit).Select(e => (e.Stage, e.ExecutionAttempt)));
        var accepted = Lifecycle(audit)[2];
        Assert.Equal((AgentTaskStatus.MaxStepsReached, AgentTaskStatus.Running, Resumer), (accepted.PriorStatus!.Value, accepted.Status, accepted.Actor));
        Assert.Equal(TaskTerminalKind.StepLimit, Lifecycle(audit)[1].TerminalKind);
        Assert.All(Lifecycle(audit), e => Assert.Equal(TaskOrigin.Ordinary, e.Origin));
    }

    // H3-32
    [Fact]
    public async Task H3_32_AResumeRejectionAudit_CarriesNoGoalSecretOrPayload()
    {
        var (store, stored) = Seeded(Stored(AgentTaskStatus.Completed, goal: "rotate api_key=sk-SECRET-123 on host db-7"));
        var audit = new RecordingAuditSink();

        await Assert.ThrowsAsync<TaskResumeRefusedException>(() => Runner(new FakeChatModel(), store, audit).ResumeAsync(stored, Resumer));

        var serialized = JsonSerializer.Serialize<AuditEvent>(Assert.Single(audit.Events));
        Assert.Contains("\"RefusalCode\":\"task_completed\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-SECRET-123", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("rotate", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("start a new task", serialized, StringComparison.OrdinalIgnoreCase); // not even the refusal text
        Assert.DoesNotContain("\"ok\"", serialized, StringComparison.Ordinal);                     // no step payload
    }

    // ---- options ----

    [Theory]
    [InlineData(0, 60, 3, 12, "'Agent:MaxSteps'")]
    [InlineData(15, 14, 3, 12, "'Agent:MaxLifetimeSteps'")]
    [InlineData(15, 60, -1, 12, "'Agent:MaxReplans'")]
    [InlineData(15, 60, 3, 2, "'Agent:MaxLifetimeReplans'")]
    public void IncoherentBudgets_AreRejectedNamingTheKey(int maxSteps, int maxLifetimeSteps, int maxReplans, int maxLifetimeReplans, string key)
    {
        var options = new AgentRunnerOptions
        {
            MaxSteps = maxSteps,
            MaxLifetimeSteps = maxLifetimeSteps,
            MaxReplans = maxReplans,
            MaxLifetimeReplans = maxLifetimeReplans,
        };

        Assert.Contains(key, Assert.Throws<InvalidOperationException>(() => options.Validate()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaults_AreFifteenAndSixtyStepsThreeAndTwelveReplans()
    {
        var options = new AgentRunnerOptions();

        Assert.Equal((15, 60, 3, 12), (options.MaxSteps, options.MaxLifetimeSteps, options.MaxReplans, options.MaxLifetimeReplans));
    }

    // ---- doubles ----

    /// <summary>Replays responses, running a callback before the first call only.</summary>
    private sealed class CallbackChatModel(Func<int, Task> beforeFirstReply, params ModelResponse[] responses) : IChatModel
    {
        public int Calls { get; private set; }

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var response = responses[Calls++];
            if (Calls == 1)
            {
                await beforeFirstReply(Calls);
            }

            return response;
        }
    }

    private sealed class HangingChatModel : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class CountingApprovalProvider(bool approved) : IApprovalProvider
    {
        public int Requests { get; private set; }

        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
        {
            Requests++;
            return Task.FromResult(new ApprovalDecision(approved, ActorIdentity.FromOperatingSystemUser("approver"), null));
        }
    }
}
