// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0030 section 5 (V1.2-G): no role approves or verifies its own work. Approvals are human only, at the plan and at every
/// step; a run whose roles do not all have their own agent identity is invalid; and Verification reads the system itself,
/// consumes nothing Remediation produced, makes no model call and never turns anything but a confirmation into success.
/// Fixtures are shared with <see cref="DelegationRunnerTests"/>.
/// </summary>
public sealed partial class DelegationRunnerTests
{
    /// <summary>A step-level approval provider whose decision the test chooses, and that remembers what it was asked about.</summary>
    private sealed class DecidingStepApproval(Func<ActorIdentity> who, bool approved = true) : IApprovalProvider
    {
        public List<string> Asked { get; } = [];

        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
        {
            Asked.Add(manifest.Name);
            return Task.FromResult(new ApprovalDecision(approved, who(), null));
        }
    }

    /// <summary>A Read tool that reports each of its readings once, in order, and the last one from then on.</summary>
    private sealed class SequencedReadTool(string name, params string[] readings) : ITool
    {
        private int _reads;

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Reports what the system looks like at the time.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(readings[Math.Min(_reads++, readings.Length - 1)]));
    }

    private static ActorIdentity AgentOfTheRun(RecordingAuditSink sink, AgentRoleKind role) =>
        new("os-user", sink.Events.OfType<DelegationLifecycleAuditEvent>()
            .First(e => e.Stage == DelegationStage.RoleStarted && e.Delegation!.Agent!.Role == role)
            .Delegation!.Agent!.Id.ToString(), null);

    private static ExecutionPlan RestartThenStop() => new(
        "sample.remediate", "1.0.0", "Restart the service, then stop the other.",
        [
            new ExecutionPlanStep(0, "service.restart", ToolArguments.Empty, "Restart."),
            new ExecutionPlanStep(1, "service.stop", ToolArguments.Empty, "Stop."),
        ]);

    private static Dictionary<AgentRoleKind, RoleProfile> ProfilesForBothTools() =>
        ProfilesWith(Profile(AgentRoleKind.Remediation, tools: ["service.restart", "service.stop"]));

    // ---- approvals are human only, at every step ----

    [Theory]
    [InlineData("agent", "diagnostic-1")]
    [InlineData("system", "bops-runtime")]
    public async Task Start_RefusesAStepApprovalDecidedByAnAgentOrTheRuntime_AndTheStepDoesNotRun(string kind, string id)
    {
        var approval = new DecidingStepApproval(() => new ActorIdentity(kind, id, null));
        var h = Create(policy: new SplitPolicy(PolicyMode.Approval), stepApproval: approval);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Rejected, run.Status);
        Assert.Equal(0, h.Restart.ExecutionCount);
        var audited = Assert.Single(h.Audit.Events.OfType<ApprovalAuditEvent>());
        Assert.False(audited.Approved);
        Assert.Equal(new ActorIdentity(kind, id, null), audited.Approver);
    }

    [Theory]
    [InlineData(AgentRoleKind.Remediation)]
    [InlineData(AgentRoleKind.Discovery)]
    public async Task Start_RefusesAStepApprovalGrantedInTheNameOfAnAgentOfTheRun(AgentRoleKind claimed)
    {
        RecordingAuditSink? sink = null;
        var approval = new DecidingStepApproval(() => AgentOfTheRun(sink!, claimed));
        var h = Create(policy: new SplitPolicy(PolicyMode.Approval), stepApproval: approval);
        sink = h.Audit;

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Rejected, run.Status);
        Assert.Equal(0, h.Restart.ExecutionCount);
        Assert.False(Assert.Single(h.Audit.Events.OfType<ApprovalAuditEvent>()).Approved);
    }

    [Fact]
    public async Task Start_AgentsOnlyRequest_AHumanDecidesEachStepThatNeedsIt()
    {
        var approval = new DecidingStepApproval(() => Approver);
        var h = Create(policy: new SplitPolicy(PolicyMode.Approval), stepApproval: approval);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(["service.restart"], approval.Asked);
        Assert.Equal(1, h.Restart.ExecutionCount);
        var audited = Assert.Single(h.Audit.Events.OfType<ApprovalAuditEvent>());
        Assert.True(audited.Approved);
        Assert.Equal(Approver, audited.Approver);
    }

    [Fact]
    public async Task Start_TheOperatorWhoStartedTheRun_IsAHumanAndMayApproveIt()
    {
        var approval = new DecidingStepApproval(() => Operator);
        var h = Create(policy: new SplitPolicy(PolicyMode.Approval), stepApproval: approval);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Start_NeverAsksAStepApprovalProvider_ForAReadStepOfAnyRole()
    {
        // The default provider throws if it is ever called: Discovery's and Verification's reads, and the runtime's own
        // verification reads, are never put to anyone as an approval.
        var h = Create();

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Empty(h.Audit.Events.OfType<ApprovalAuditEvent>());
    }

    [Fact]
    public async Task Start_AnAgentThatRejects_IsNotAnEscapeHatch_ButItIsStillARejection()
    {
        var approval = new DecidingStepApproval(() => new ActorIdentity("agent", "diagnostic-1", null), approved: false);
        var h = Create(policy: new SplitPolicy(PolicyMode.Approval), stepApproval: approval);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Rejected, run.Status);
        Assert.Equal(0, h.Restart.ExecutionCount);
    }

    // ---- distinct identities ----

    [Fact]
    public void SharedIdentity_IsFound_WhenAnyTwoRolesHaveTheSameAgent_AndOnlyThen()
    {
        var shared = AgentId.New();
        AgentIdentity[] distinct =
        [
            new(AgentId.New(), AgentRoleKind.Discovery), new(AgentId.New(), AgentRoleKind.Diagnostic),
            new(AgentId.New(), AgentRoleKind.Remediation), new(AgentId.New(), AgentRoleKind.Verification),
        ];
        AgentIdentity[] verifierIsTheRemediator = [distinct[0], distinct[1], new(shared, AgentRoleKind.Remediation), new(shared, AgentRoleKind.Verification)];

        Assert.Null(SeparationOfDuties.SharedIdentity(distinct));
        Assert.Null(SeparationOfDuties.SharedIdentity([]));
        var found = SeparationOfDuties.SharedIdentity(verifierIsTheRemediator);
        Assert.Contains("Remediation and Verification roles share one agent identity", found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_EndsAsFailed_WhenARoleWouldShareTheAgentIdentityOfAnEarlierOne()
    {
        var only = AgentId.New();
        var h = Create(agentIds: () => only);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Failed, run.Status);
        Assert.Contains("share one agent identity", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal([AgentRoleKind.Discovery], run.Roles.Select(r => r.Agent.Role));
        Assert.Equal(0, h.Restart.ExecutionCount);
        Assert.Empty(h.Approval.Requests);
    }

    [Fact]
    public async Task Resume_RefusesAStoredRunWhoseVerificationIsTheAgentThatRemediated()
    {
        var store = new CrashableStore { CrashAfter = r => RoleRunning(r, AgentRoleKind.Verification) };
        var restart = new RestartTool();
        var crashed = await Create(store: store, restart: restart).Runner.StartAsync(Request(), Operator);
        store.Revive();

        // Whatever wrote this run to the store, it now says one identity both changed the system and confirmed the change.
        var stored = (await store.LoadAsync(crashed.Id))!;
        var remediator = stored.Roles.Single(r => r.Agent.Role == AgentRoleKind.Remediation).Agent.Id;
        await store.SaveAsync(stored with
        {
            Roles = [.. stored.Roles.Select(r => r.Agent.Role == AgentRoleKind.Verification ? r with { Agent = new AgentIdentity(remediator, AgentRoleKind.Verification) } : r)],
        });
        var second = Create([], store: store, restart: restart);
        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Failed, run.Status);
        Assert.Contains("share one agent identity", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, restart.ExecutionCount);
        Assert.Empty(second.Approval.Requests);
        Assert.Empty(second.Model.Requests);
        Assert.Equal(DelegationStatus.Failed, (await store.LoadAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task Resume_RefusesAStoredRunThatSharesAnIdentity_EvenWhenNoRoleIsLeftToStart()
    {
        // A dry run ends as a diagnosis: resuming it starts no role, so only the check on the stored run can refuse it.
        var dryRun = new DelegationRemediation(
            "sample.skill", "sample.remediate", new CapabilityRequest(ToolArguments.Empty, "local", "test", BlastRadius.Single, DryRun: true));
        var store = new CrashableStore { CrashAfter = r => r.Roles.Count == 2 && r.Roles[1].Status == DelegationRoleStatus.Completed };
        var crashed = await Create(store: store).Runner.StartAsync(Request(dryRun), Operator);
        store.Revive();

        var stored = (await store.LoadAsync(crashed.Id))!;
        var discovery = stored.Roles[0].Agent.Id;
        await store.SaveAsync(stored with
        {
            Roles = [stored.Roles[0], stored.Roles[1] with { Agent = new AgentIdentity(discovery, AgentRoleKind.Diagnostic) }],
        });
        var run = await Create([], store: store).Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Failed, run.Status);
        Assert.Contains("The stored run is not resumed", run.ErrorMessage, StringComparison.Ordinal);
    }

    // ---- verification is independent of remediation ----

    [Fact]
    public void VerifyPlanAsync_TakesNothingRemediationProduced()
    {
        var method = typeof(AgentRunner).GetMethod("VerifyPlanAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

        // The plan and its hash say what was approved; the scope is the Verification agent's own. There is no parameter through
        // which a step's result, a report or a plan execution could reach it, so it cannot take any of them as proof.
        Assert.Equal(
            [typeof(Guid), typeof(ActorIdentity), typeof(ExecutionPlan), typeof(string), typeof(DelegatedExecutionScope), typeof(CancellationToken)],
            method.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public async Task Start_EndsAsVerificationFailed_WhenTheSystemChangedBackAfterRemediationReportedSuccess()
    {
        // Remediation's own check reads "running" and its step reports success. By the time Verification reads the system it
        // is stopped again. Nothing Remediation said can outvote what Verification reads.
        var restart = new RestartTool(judge: reading => reading!.Contains("running", StringComparison.Ordinal) ? VerificationStatus.Confirmed : VerificationStatus.Refuted);
        var h = Create(restart: restart, verifyRead: new SequencedReadTool("test.read", "service is running", "service is stopped"));

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.VerificationFailed, run.Status);
        Assert.Equal(["service is running", "service is stopped"], restart.VerificationReads);
        var verification = Role(run, AgentRoleKind.Verification).Verification!;
        Assert.Equal(VerificationStatus.Refuted, verification.Status);
        Assert.Contains("service is stopped", Assert.Single(verification.Evidence).Data, StringComparison.Ordinal);
        Assert.Equal(1, restart.ExecutionCount);
    }

    [Fact]
    public async Task Start_VerificationConfirms_OnlyFromItsOwnReading()
    {
        var restart = new RestartTool(judge: reading => reading!.Contains("running", StringComparison.Ordinal) ? VerificationStatus.Confirmed : VerificationStatus.Refuted);
        var h = Create(restart: restart, verifyRead: new SequencedReadTool("test.read", "service is stopped", "service is running"));

        var run = await h.Runner.StartAsync(Request(), Operator);

        // Remediation's own check saw a stopped service and did not stop the plan; Verification's later reading is what counts.
        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(["service is stopped", "service is running"], restart.VerificationReads);
    }

    [Fact]
    public async Task VerifyPlanAsync_ConfirmsNothing_ForAStepWhoseToolIsNoLongerRegistered()
    {
        var h = Create();
        var plan = new ExecutionPlan(
            "sample.remediate", "1.0.0", "Restart, then a tool that is gone.",
            [
                new ExecutionPlanStep(0, "service.restart", ToolArguments.Empty, "Restart."),
                new ExecutionPlanStep(1, "service.gone", ToolArguments.Empty, "Something that cannot be checked."),
            ]);
        var envelope = RoleEnvelope(remediation: false) with { AllowedTools = ["test.read"] };
        var scope = DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), AgentRoleKind.Verification), envelope);

        var report = await h.Agent.VerifyPlanAsync(Guid.NewGuid(), Operator, plan, ExecutionPlanHasher.ComputeHash(plan), scope);

        Assert.Equal(VerificationStatus.Inconclusive, report.Status);
        Assert.Contains("service.gone", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPlanAsync_LeavesAReadStepOfThePlanOutOfTheVerdict()
    {
        var h = Create();
        var plan = new ExecutionPlan(
            "sample.remediate", "1.0.0", "Look, then restart.",
            [
                new ExecutionPlanStep(0, "host.info", ToolArguments.Empty, "Look."),
                new ExecutionPlanStep(1, "service.restart", ToolArguments.Empty, "Restart."),
            ]);
        var envelope = RoleEnvelope(remediation: false) with { AllowedTools = ["test.read"] };
        var scope = DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), AgentRoleKind.Verification), envelope);

        var report = await h.Agent.VerifyPlanAsync(Guid.NewGuid(), Operator, plan, ExecutionPlanHasher.ComputeHash(plan), scope);

        Assert.Equal(VerificationStatus.Confirmed, report.Status);
        Assert.Equal("verification-1", Assert.Single(report.Evidence).Id);
    }

    // ---- what a run may end as ----

    [Theory]
    [InlineData(VerificationStatus.Confirmed, VerificationStatus.Confirmed, VerificationStatus.Confirmed)]
    [InlineData(VerificationStatus.Confirmed, VerificationStatus.Refuted, VerificationStatus.Refuted)]
    [InlineData(VerificationStatus.Refuted, VerificationStatus.Confirmed, VerificationStatus.Refuted)]
    [InlineData(VerificationStatus.Inconclusive, VerificationStatus.Confirmed, VerificationStatus.Inconclusive)]
    [InlineData(VerificationStatus.Confirmed, VerificationStatus.Inconclusive, VerificationStatus.Inconclusive)]
    [InlineData(VerificationStatus.Refuted, VerificationStatus.Inconclusive, VerificationStatus.Refuted)]
    [InlineData(VerificationStatus.NotApplicable, VerificationStatus.Confirmed, VerificationStatus.NotApplicable)]
    [InlineData(VerificationStatus.Confirmed, VerificationStatus.NotApplicable, VerificationStatus.NotApplicable)]
    [InlineData(VerificationStatus.Inconclusive, VerificationStatus.NotApplicable, VerificationStatus.Inconclusive)]
    [InlineData(VerificationStatus.NotApplicable, VerificationStatus.NotApplicable, VerificationStatus.NotApplicable)]
    public async Task Start_TheVerdictOfAPlanWithSeveralSteps_IsTheWorstOfItsSteps_AndOnlyConfirmedIsSuccess(
        VerificationStatus first, VerificationStatus second, VerificationStatus expected)
    {
        // Each tool's own check right after its step confirms, so the plan runs to the end; the verdicts that matter are the
        // Verification role's, one per step.
        var restart = new RestartTool();
        restart.Verdicts.Enqueue(VerificationStatus.Confirmed);
        restart.Verdicts.Enqueue(first);
        var stop = new RestartTool("service.stop");
        stop.Verdicts.Enqueue(VerificationStatus.Confirmed);
        stop.Verdicts.Enqueue(second);
        var h = Create(restart: restart, stop: stop, plan: RestartThenStop, profiles: ProfilesForBothTools());

        var run = await h.Runner.StartAsync(Request(), Operator);

        var verification = Role(run, AgentRoleKind.Verification);
        Assert.Equal(expected, verification.Verification!.Status);
        Assert.Equal(
            expected == VerificationStatus.Confirmed ? DelegationStatus.Completed : DelegationStatus.VerificationFailed, run.Status);
        Assert.Equal(["verification-0", "verification-1"], verification.Verification.Evidence.Select(e => e.Id));

        // Deterministic and free of the model: nothing was asked of it after Diagnostic, and Verification spent no tokens.
        Assert.Equal(5, h.Model.Requests.Count);
        Assert.Equal(0, verification.Consumed.Tokens);
    }

    [Fact]
    public async Task Start_APlanWhoseVerificationCannotBeRead_IsNeverASuccess()
    {
        var profiles = ProfilesWith(Profile(AgentRoleKind.Verification, tools: ["host.info"]));
        var h = Create(profiles: profiles);

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.VerificationFailed, run.Status);
        Assert.NotEqual(VerificationStatus.Confirmed, Role(run, AgentRoleKind.Verification).Verification!.Status);
    }
}
