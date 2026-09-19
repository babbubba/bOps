// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0030 section 6 (V1.2-E): budgets, deadlines and cancellation hold across the roles of a delegated run. A role's
/// budget is reserved from what the run has left and reconciled when the role ends; steps and tokens are counted while it
/// runs; a role stops at its own deadline; one cancellation token tree covers the run, each role and each step; and
/// exhaustion, deadline expiry and cancellation are distinct audited terminal states. Fixtures are shared with
/// <see cref="DelegationRunnerTests"/>.
/// </summary>
public sealed partial class DelegationRunnerTests
{
    private static readonly TimeSpan Forever = Timeout.InfiniteTimeSpan;

    private static RoleProfile WithBudget(AgentRoleKind role, int steps, int? tokens = null) =>
        Profile(role) with { MaxSteps = steps, MaxTokens = tokens ?? Profile(role).MaxTokens };

    private static Dictionary<AgentRoleKind, RoleProfile> ProfilesWith(params RoleProfile[] replaced)
    {
        var profiles = AllProfiles();
        foreach (var profile in replaced)
        {
            profiles[profile.Role] = profile;
        }

        return profiles;
    }

    private static ModelResponse WithUsage(ModelResponse response, int prompt, int completion) =>
        response with { Usage = new ModelUsage(prompt, completion, null) };

    private static DelegationStatus Terminal(Harness h) =>
        h.Audit.Events.OfType<DelegationLifecycleAuditEvent>().Single(e => e.Stage == DelegationStage.Terminal).Status;

    private static ExecutionPlan TwoStepPlan() => new(
        "sample.remediate", "1.0.0", "Restart the service twice.",
        [
            new ExecutionPlanStep(0, "service.restart", ToolArguments.Empty, "Restart the service."),
            new ExecutionPlanStep(1, "service.restart", ToolArguments.Empty, "Restart the service again."),
        ]);

    /// <summary>A model that does something to the run just before it answers the call the test names.</summary>
    private sealed class ActingModel(FakeChatModel inner, int onCall, Action act) : IChatModel
    {
        private int _calls;

        public ChatModelDescriptor Descriptor => inner.Descriptor;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            if (_calls++ == onCall)
            {
                act();
            }

            return inner.CompleteAsync(request, ct);
        }
    }

    /// <summary>A Read tool that answers until its <paramref name="blockOnCall"/>th call, which does something and then never finishes unless it is cancelled.</summary>
    private sealed class BlockingReadTool(string name, Action act, int blockOnCall = 1) : ITool
    {
        private int _calls;

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Blocks until it is cancelled.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            if (++_calls < blockOnCall)
            {
                return ToolCallResult.Success("still running");
            }

            act();
            await Task.Delay(Forever, ct);
            return ToolCallResult.Success("unreachable");
        }
    }

    private sealed class BlockingPlanApproval(Action act) : IPlanApprovalProvider
    {
        public async Task<ApprovalDecision> RequestPlanApprovalAsync(PlanApprovalRequest request, CancellationToken ct = default)
        {
            act();
            await Task.Delay(Forever, ct);
            return new ApprovalDecision(true, Approver, null);
        }
    }

    private sealed class BlockingStepApproval(Action act) : IApprovalProvider
    {
        public async Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
        {
            act();
            await Task.Delay(Forever, ct);
            return new ApprovalDecision(true, Approver, null);
        }
    }

    // ---- the meter ----

    [Fact]
    public void RoleMeter_CountsSteps_AndStopsAtTheBudgetWithoutCountingPastIt()
    {
        var meter = new RoleMeter(new DelegationBudget(2, 100, Start.AddHours(1)));

        Assert.Null(meter.BeginStep(Start));
        Assert.Null(meter.BeginStep(Start));
        Assert.Equal(BudgetStop.Steps, meter.BeginStep(Start));

        Assert.Equal(2, meter.Consumed.Steps);
        Assert.Equal(BudgetStop.Steps, meter.Stopped);
    }

    [Fact]
    public void RoleMeter_TreatsTheDeadlineItselfAsExpired_AndCountsNoStepForIt()
    {
        var meter = new RoleMeter(new DelegationBudget(5, 100, Start.AddMinutes(10)));

        Assert.Null(meter.BeginStep(Start.AddMinutes(10) - TimeSpan.FromTicks(1)));
        Assert.Equal(BudgetStop.Deadline, meter.BeginStep(Start.AddMinutes(10)));

        Assert.Equal(1, meter.Consumed.Steps);
        Assert.Equal(BudgetStop.Deadline, meter.Stopped);
    }

    [Fact]
    public void RoleMeter_AllowsExactlyItsTokens_AndStopsOnceOverThem()
    {
        var meter = new RoleMeter(new DelegationBudget(5, 100, Start.AddHours(1)));

        meter.AddTokens(100);
        Assert.Null(meter.Check(Start));

        meter.AddTokens(1);
        Assert.Equal(BudgetStop.Tokens, meter.Check(Start));
        Assert.Equal(101, meter.Consumed.Tokens);
    }

    [Fact]
    public void RoleMeter_KeepsTheFirstReasonItStopped()
    {
        var meter = new RoleMeter(new DelegationBudget(1, 10, Start.AddMinutes(1)));

        Assert.Null(meter.BeginStep(Start));
        Assert.Equal(BudgetStop.Steps, meter.BeginStep(Start));
        Assert.Equal(BudgetStop.Steps, meter.Check(Start.AddHours(1)));
    }

    [Fact]
    public void RoleMeter_ARestartedRoleCarriesOnFromWhatItSpent_AndNeverGetsItsBudgetBack()
    {
        var budget = new DelegationBudget(3, 1_000, Start.AddHours(1));
        var first = new RoleMeter(budget);
        Assert.Null(first.BeginStep(Start));
        Assert.Null(first.BeginStep(Start));
        first.AddTokens(900);

        var restarted = new RoleMeter(budget, first.Consumed);

        Assert.Equal(new BudgetConsumption(2, 900), restarted.Consumed);
        Assert.Null(restarted.BeginStep(Start));
        Assert.Equal(BudgetStop.Steps, restarted.BeginStep(Start));
        restarted.AddTokens(101);
        Assert.Equal(BudgetStop.Steps, restarted.Check(Start));
        Assert.Equal(1_001, restarted.Consumed.Tokens);
    }

    [Fact]
    public void DelegatedExecutionScope_ARestartedRoleStartsFromItsPriorConsumption()
    {
        var envelope = RoleEnvelope(remediation: false) with { Budget = new DelegationBudget(3, 500, Start.AddHours(1)) };
        var agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Discovery);

        var scope = DelegatedExecutionScope.For(Guid.NewGuid(), agent, envelope, new BudgetConsumption(3, 400));

        Assert.Equal(new BudgetConsumption(3, 400), scope.Meter!.Consumed);
        Assert.Equal(BudgetStop.Steps, scope.Meter.BeginStep(Start));
    }

    // ---- exhausted step and token budgets ----

    [Fact]
    public async Task Start_EndsAsBudgetExceeded_WhenARoleTakesMoreStepsThanItsEnvelopeGrants()
    {
        var h = Create(
            [PlanningTestSupport.PlanResponse(), Call("host.info")],
            profiles: ProfilesWith(WithBudget(AgentRoleKind.Discovery, steps: 1)));

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.BudgetExceeded, run.Status);
        Assert.Equal(DelegationStatus.BudgetExceeded, Terminal(h));
        var discovery = Assert.Single(run.Roles);
        Assert.Equal(DelegationRoleStatus.Failed, discovery.Status);
        Assert.Equal(1, discovery.Consumed.Steps);
        Assert.Contains("every step", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Approval.Requests);
    }

    [Fact]
    public async Task Start_EndsAsBudgetExceeded_WhenARoleSpendsMoreModelTokensThanItsEnvelopeGrants()
    {
        var h = Create(
            [WithUsage(PlanningTestSupport.PlanResponse(), 80, 40)],
            profiles: ProfilesWith(WithBudget(AgentRoleKind.Discovery, steps: 10, tokens: 100)));

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.BudgetExceeded, run.Status);
        var discovery = Assert.Single(run.Roles);
        Assert.Equal(120, discovery.Consumed.Tokens);
        Assert.Contains("tokens", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(h.Model.Requests);
    }

    [Fact]
    public async Task Start_ReservesEachRolesBudgetFromWhatTheRunHasLeft_AndReconcilesWhatItSpent()
    {
        var script = new[]
        {
            WithUsage(PlanningTestSupport.PlanResponse(), 60_000, 30_000), Call("host.info"), Final(),
            PlanningTestSupport.PlanResponse(), Final(FindingsJson("discovery-0")),
        };
        var h = Create(script);

        var run = await h.Runner.StartAsync(
            Request(authority: new DelegationAuthorityRequest(MaxTokens: 120_000)), Operator);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        var discovery = Role(run, AgentRoleKind.Discovery);
        Assert.Equal(new BudgetConsumption(2, 90_000), discovery.Consumed);
        Assert.Equal(100_000, discovery.Envelope.Budget.MaxTokens);

        // What Discovery spent came off the run's tokens before Diagnostic was granted any.
        Assert.Equal(30_000, Role(run, AgentRoleKind.Diagnostic).Envelope.Budget.MaxTokens);
        Assert.Equal(120_000, run.RootEnvelope.Budget.MaxTokens);

        var reconciled = h.Audit.Events.OfType<DelegationLifecycleAuditEvent>().Where(e => e.Stage == DelegationStage.BudgetConsumed).ToList();
        Assert.Equal(4, reconciled.Count);
        Assert.Equal(new BudgetConsumption(2, 90_000), reconciled[0].Consumed);
    }

    [Fact]
    public async Task Start_EndsAsBudgetExceeded_BeforeAskingAHuman_WhenNoStepsAreLeftForRemediation()
    {
        var h = Create();

        // Discovery takes two steps (a tool call and the answer) and Diagnostic one: three of three.
        var run = await h.Runner.StartAsync(
            Request(authority: new DelegationAuthorityRequest(MaxSteps: 3)), Operator);

        Assert.Equal(DelegationStatus.BudgetExceeded, run.Status);
        Assert.Equal(DelegationStatus.BudgetExceeded, Terminal(h));
        Assert.Equal(new BudgetConsumption(2, 0), Role(run, AgentRoleKind.Discovery).Consumed);
        Assert.Equal(new BudgetConsumption(1, 0), Role(run, AgentRoleKind.Diagnostic).Consumed);
        Assert.Contains("Remediation", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Approval.Requests);
        Assert.Equal(0, h.Restart.ExecutionCount);
    }

    [Fact]
    public async Task Start_RefusesAPlanTheRemediationRoleHasTooFewStepsToFinish_BeforeAnythingChanges()
    {
        var h = Create(plan: TwoStepPlan);

        // Three steps used by Discovery and Diagnostic leave one of four; the plan needs two, and half a plan is not applied.
        var run = await h.Runner.StartAsync(
            Request(authority: new DelegationAuthorityRequest(MaxSteps: 4)), Operator);

        Assert.Equal(DelegationStatus.BudgetExceeded, run.Status);
        Assert.Contains("2 steps", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Approval.Requests);
        Assert.Equal(0, h.Restart.ExecutionCount);
    }

    [Fact]
    public async Task Start_EndsAsBudgetExceeded_NotAsVerified_WhenVerificationRunsOutOfSteps()
    {
        var h = Create(plan: TwoStepPlan, profiles: ProfilesWith(WithBudget(AgentRoleKind.Verification, steps: 1)));

        var run = await h.Runner.StartAsync(Request(), Operator);

        // The change was applied; nothing verified it, so it is not success (rule S4).
        Assert.Equal(2, h.Restart.ExecutionCount);
        Assert.Equal(DelegationStatus.BudgetExceeded, run.Status);
        Assert.Equal(DelegationRoleStatus.Failed, Role(run, AgentRoleKind.Verification).Status);
        Assert.Null(Role(run, AgentRoleKind.Verification).Verification);
        Assert.Equal(1, Role(run, AgentRoleKind.Verification).Consumed.Steps);
    }

    [Fact]
    public async Task Start_EndsAsBudgetExceeded_NotDenied_WhenTheTokensAreUsedUpBeforeARoleThatNeedsThem()
    {
        // Discovery spends exactly the run's tokens, which is within its budget; nothing is left for Diagnostic.
        var script = new[] { WithUsage(PlanningTestSupport.PlanResponse(), 60, 40), Call("host.info"), Final() };
        var h = Create(script);

        var run = await h.Runner.StartAsync(
            Request(authority: new DelegationAuthorityRequest(MaxTokens: 100)), Operator);

        Assert.Equal(DelegationStatus.BudgetExceeded, run.Status);
        Assert.Null(run.Denial);
        Assert.Equal(DelegationRoleStatus.Completed, Role(run, AgentRoleKind.Discovery).Status);
        Assert.Contains("model tokens", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(run.Roles);
    }

    [Fact]
    public async Task Start_RunsAPlanThatExactlyFitsTheStepsOfTheRemediationRole()
    {
        var h = Create(profiles: ProfilesWith(WithBudget(AgentRoleKind.Remediation, steps: 1)));

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(1, h.Restart.ExecutionCount);
        Assert.Equal(1, Role(run, AgentRoleKind.Remediation).Consumed.Steps);
    }

    [Fact]
    public async Task Start_ChargesTheEvidenceStepsACapabilityTakes_AndRefusesOneBeyondTheBudget()
    {
        var results = new List<ToolCallResult>();
        var h = Create(
            profiles: ProfilesWith(WithBudget(AgentRoleKind.Diagnostic, steps: 2)),
            evidenceCalls: async (invoker, ct) =>
            {
                results.Add(await invoker.InvokeAsync("host.info", ToolArguments.Empty, ct));
                results.Add(await invoker.InvokeAsync("host.info", ToolArguments.Empty, ct));
            });

        var run = await h.Runner.StartAsync(Request(), Operator);

        // The answer took one of the Diagnostic role's two steps and the first reading the other.
        Assert.Equal(ToolOutcome.Success, results[0].Outcome);
        Assert.Equal(ToolOutcome.Denied, results[1].Outcome);
        Assert.Equal(2, Role(run, AgentRoleKind.Diagnostic).Consumed.Steps);
    }

    [Fact]
    public async Task ExecuteDelegatedPlanAsync_StopsBeforeTheStepThatWouldExceedTheStepBudget_AndSaysWhy()
    {
        var h = Create(plan: TwoStepPlan);
        var (prepared, _) = await PrepareAsync(h);
        var envelope = RoleEnvelope(remediation: true) with { Budget = new DelegationBudget(1, 0, Start.AddHours(1)) };
        var scope = DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), AgentRoleKind.Remediation), envelope);

        var result = await h.Agent.ExecuteDelegatedPlanAsync(
            Guid.NewGuid(), Operator, prepared,
            new ExecutionPlanApproval(prepared.PlanHash!, new ApprovalDecision(true, Approver, null)), scope);

        Assert.Equal(PlanExecutionStatus.OutOfBudget, result.Status);
        Assert.Equal(1, h.Restart.ExecutionCount);
        Assert.Equal(BudgetStop.Steps, scope.Meter!.Stopped);
        Assert.Equal(1, scope.Meter.Consumed.Steps);
    }

    [Fact]
    public async Task VerifyPlanAsync_ConfirmsNothing_WhenTheVerifierHasNoStepLeft()
    {
        var h = Create();
        var (prepared, _) = await PrepareAsync(h);
        var envelope = RoleEnvelope(remediation: false) with { Budget = new DelegationBudget(0, 0, Start.AddHours(1)) };
        var scope = DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), AgentRoleKind.Verification), envelope);

        var report = await h.Agent.VerifyPlanAsync(Guid.NewGuid(), Operator, prepared.Report.Plan!, prepared.PlanHash!, scope);

        Assert.Equal(VerificationStatus.Inconclusive, report.Status);
        Assert.Equal(BudgetStop.Steps, scope.Meter!.Stopped);
        Assert.Equal(0, scope.Meter.Consumed.Steps);
    }

    // ---- deadlines ----

    [Fact]
    public async Task Start_StopsARoleAtItsOwnDeadline_WhileItRuns()
    {
        var clock = new FakeTimeProvider(Start);
        var inner = new FakeChatModel(PlanningTestSupport.PlanResponse(), Call("host.info"), Final());
        var model = new ActingModel(inner, onCall: 1, () => clock.Advance(TimeSpan.FromMinutes(11)));
        var h = Create(model: model, clock: clock);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.DeadlineExceeded, run.Status);
        Assert.Equal(DelegationStatus.DeadlineExceeded, Terminal(h));
        var discovery = Assert.Single(run.Roles);
        Assert.Equal(DelegationRoleStatus.Failed, discovery.Status);
        Assert.Contains("deadline", run.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Approval.Requests);
    }

    [Fact]
    public async Task Start_EndsAsDeadlineExceeded_WhenTheDeadlinePassesWhileAStepIsRunning_AndTheStepsOutcomeIsUnknown()
    {
        var clock = new FakeTimeProvider(Start);
        var restart = new RestartTool(whileRunning: async ct =>
        {
            clock.Advance(TimeSpan.FromMinutes(11));
            await Task.Delay(Forever, ct);
        });
        var h = Create(restart: restart, clock: clock);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.DeadlineExceeded, run.Status);
        Assert.Contains("unknown", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(DelegationRoleStatus.Failed, Role(run, AgentRoleKind.Remediation).Status);
        Assert.DoesNotContain(run.Roles, r => r.Agent.Role == AgentRoleKind.Verification);
        AssertUnknownOutcome(h, "service.restart");
    }

    [Fact]
    public async Task Start_EndsAsDeadlineExceeded_WhenTheDeadlinePassesWhileAHumanIsAskedToApproveThePlan()
    {
        var clock = new FakeTimeProvider(Start);
        var approval = new BlockingPlanApproval(() => clock.Advance(TimeSpan.FromHours(1)));
        var h = Create(clock: clock);
        var runner = new DelegationRunner(
            h.Agent, new FixedProfiles(AllProfiles()), approval, h.Audit, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<DelegationRunner>.Instance);

        var run = await runner.StartAsync(Request(), Operator);

        // An approval nobody gives cannot hold a run past its deadline.
        Assert.Equal(DelegationStatus.DeadlineExceeded, run.Status);
        Assert.Equal(DelegationStatus.DeadlineExceeded, Terminal(h));
        Assert.DoesNotContain(run.Roles, r => r.Agent.Role == AgentRoleKind.Remediation);
        Assert.Equal(0, h.Restart.ExecutionCount);
    }

    // ---- cancellation ----

    private static void AssertUnknownOutcome(Harness h, string tool)
    {
        var journal = Assert.Single(h.Audit.Events.OfType<DelegationJournalAuditEvent>());
        Assert.Equal(JournalPhase.Outcome, journal.Phase);
        Assert.Equal(tool, journal.Tool);
        Assert.Equal(StepOutcomeKind.Cancelled, journal.Outcome);
        Assert.Equal(DelegationHasher.ComputeArgumentsHash(ToolArguments.Empty), journal.ArgumentsHash);

        // Unknown is not failed: the step is not audited as a tool call that failed.
        Assert.DoesNotContain(
            h.Audit.Events.OfType<ToolCallAuditEvent>(), e => e.Tool == tool && e.Outcome == ToolOutcome.Failure);
    }

    [Fact]
    public async Task Start_EndsAsCancelled_WhenCancelledDuringDiscovery()
    {
        using var cts = new CancellationTokenSource();
        var inner = new FakeChatModel(PlanningTestSupport.PlanResponse(), Call("host.info"), Final());
        var h = Create(model: new ActingModel(inner, onCall: 1, cts.Cancel));

        var run = await h.Runner.StartAsync(Request(), Operator, ct: cts.Token);

        Assert.Equal(DelegationStatus.Cancelled, run.Status);
        Assert.Equal(DelegationStatus.Cancelled, Terminal(h));
        var discovery = Assert.Single(run.Roles);
        Assert.Equal(DelegationRoleStatus.Cancelled, discovery.Status);
        Assert.Empty(h.Approval.Requests);
    }

    [Fact]
    public async Task Start_EndsAsCancelled_WhenCancelledDuringDiagnostic()
    {
        using var cts = new CancellationTokenSource();
        var inner = new FakeChatModel(HappyScript());
        var h = Create(model: new ActingModel(inner, onCall: 3, cts.Cancel));

        var run = await h.Runner.StartAsync(Request(), Operator, ct: cts.Token);

        Assert.Equal(DelegationStatus.Cancelled, run.Status);
        Assert.Equal(DelegationRoleStatus.Completed, Role(run, AgentRoleKind.Discovery).Status);
        Assert.Equal(DelegationRoleStatus.Cancelled, Role(run, AgentRoleKind.Diagnostic).Status);
        Assert.Empty(h.Approval.Requests);
        Assert.Equal(0, h.Restart.ExecutionCount);
    }

    [Fact]
    public async Task Start_EndsAsCancelled_WhenCancelledWhileAHumanIsAskedToApproveThePlan()
    {
        using var cts = new CancellationTokenSource();
        var h = Create();
        var runner = new DelegationRunner(
            h.Agent, new FixedProfiles(AllProfiles()), new BlockingPlanApproval(cts.Cancel), h.Audit, h.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DelegationRunner>.Instance);

        var run = await runner.StartAsync(Request(), Operator, ct: cts.Token);

        Assert.Equal(DelegationStatus.Cancelled, run.Status);
        Assert.Equal(DelegationStatus.Cancelled, Terminal(h));
        Assert.Equal([AgentRoleKind.Discovery, AgentRoleKind.Diagnostic], run.Roles.Select(r => r.Agent.Role));
        Assert.All(run.Roles, r => Assert.Equal(DelegationRoleStatus.Completed, r.Status));
        Assert.Equal(0, h.Restart.ExecutionCount);
    }

    [Fact]
    public async Task Start_EndsAsCancelled_WhenCancelledWhileAHumanIsAskedToApproveAStep_AndNothingRan()
    {
        using var cts = new CancellationTokenSource();
        var h = Create(policy: new SplitPolicy(PolicyMode.Approval), stepApproval: new BlockingStepApproval(cts.Cancel));

        var run = await h.Runner.StartAsync(Request(), Operator, ct: cts.Token);

        Assert.Equal(DelegationStatus.Cancelled, run.Status);
        Assert.Equal(DelegationRoleStatus.Cancelled, Role(run, AgentRoleKind.Remediation).Status);
        Assert.Equal(0, h.Restart.ExecutionCount);

        // The step never started, so there is no unknown outcome to record.
        Assert.Empty(h.Audit.Events.OfType<DelegationJournalAuditEvent>());
        Assert.DoesNotContain("unknown", run.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_EndsAsCancelled_AndRecordsTheStepsOutcomeAsUnknown_WhenCancelledDuringASideEffectingStep()
    {
        using var cts = new CancellationTokenSource();
        var restart = new RestartTool(whileRunning: async ct =>
        {
            await cts.CancelAsync();
            await Task.Delay(Forever, ct);
        });
        var h = Create(restart: restart);

        var run = await h.Runner.StartAsync(Request(), Operator, ct: cts.Token);

        Assert.Equal(DelegationStatus.Cancelled, run.Status);
        Assert.Equal(DelegationStatus.Cancelled, Terminal(h));
        Assert.Equal(1, restart.ExecutionCount);
        Assert.Equal(DelegationRoleStatus.Cancelled, Role(run, AgentRoleKind.Remediation).Status);
        Assert.Contains("unknown", run.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Roles, r => r.Agent.Role == AgentRoleKind.Verification);
        AssertUnknownOutcome(h, "service.restart");
    }

    [Fact]
    public async Task Start_EndsAsCancelled_WhenCancelledDuringVerification()
    {
        using var cts = new CancellationTokenSource();

        // The step's own post-action check is the first read of "test.read"; Verification's reading is the second.
        var h = Create(verifyRead: new BlockingReadTool("test.read", cts.Cancel, blockOnCall: 2));

        var run = await h.Runner.StartAsync(Request(), Operator, ct: cts.Token);

        Assert.Equal(DelegationStatus.Cancelled, run.Status);
        Assert.Equal(1, h.Restart.ExecutionCount);
        Assert.Equal(DelegationRoleStatus.Completed, Role(run, AgentRoleKind.Remediation).Status);
        Assert.Equal(DelegationRoleStatus.Cancelled, Role(run, AgentRoleKind.Verification).Status);

        // A read does not change anything, so cancelling one leaves nothing unknown.
        Assert.Empty(h.Audit.Events.OfType<DelegationJournalAuditEvent>());
    }

    [Fact]
    public async Task Start_AuditsACancelledRun_EvenThoughItsTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var inner = new FakeChatModel(PlanningTestSupport.PlanResponse(), Call("host.info"), Final());

        // The sink refuses a cancelled token, as a real one does: the ending of the role and of the run must not depend on it.
        var h = Create(model: new ActingModel(inner, onCall: 1, cts.Cancel), auditHonoursCancellation: true);

        await h.Runner.StartAsync(Request(), Operator, ct: cts.Token);

        var lifecycle = h.Audit.Events.OfType<DelegationLifecycleAuditEvent>().ToList();
        var ended = Assert.Single(lifecycle, e => e.Stage == DelegationStage.RoleCompleted);
        Assert.Equal(DelegationRoleStatus.Cancelled, ended.RoleStatus);
        Assert.Same(lifecycle[^1], Assert.Single(lifecycle, e => e.Stage == DelegationStage.Terminal));
    }

    [Fact]
    public async Task Start_RecordsWhatEachRoleSpent_AtTheEndOfTheRole()
    {
        var h = Create();

        var run = await h.Runner.StartAsync(Request(), Operator);

        // Discovery: a tool call and the answer. Diagnostic: the answer. Remediation: the one plan step. Verification: one read.
        Assert.Equal(new BudgetConsumption(2, 0), Role(run, AgentRoleKind.Discovery).Consumed);
        Assert.Equal(new BudgetConsumption(1, 0), Role(run, AgentRoleKind.Diagnostic).Consumed);
        Assert.Equal(new BudgetConsumption(1, 0), Role(run, AgentRoleKind.Remediation).Consumed);
        Assert.Equal(new BudgetConsumption(1, 0), Role(run, AgentRoleKind.Verification).Consumed);
    }
}
