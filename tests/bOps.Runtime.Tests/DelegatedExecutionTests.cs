// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0030 section 3 and ADR-0031 section 4 (V1.2-C2): the runner enforces the authority envelope in the one
/// place a tool call is authorized, before policy, on every path a delegated agent can take (the model loop, a
/// Capability's preparation, an approved plan), and correlates everything it writes to the delegated run. The
/// envelope's own answers are in <see cref="EnvelopeEnforcementTests"/>.
/// </summary>
public sealed class DelegatedExecutionTests
{
    private static readonly PackageId SamplePackage = new("sample.package");
    private static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("operator");
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly CapabilityRequest Request = new(ToolArguments.Empty, "local", "test", BlastRadius.Single);

    // ---- fixtures ----

    private static AuthorityEnvelope Envelope(
        IReadOnlyList<string> tools,
        RiskLevel risk = RiskLevel.Read,
        IReadOnlyList<string>? skills = null,
        IReadOnlyList<string>? capabilities = null,
        MaintenanceWindow? window = null,
        ActorIdentity? originator = null) =>
        new(
            originator ?? Operator, Depth: 1,
            skills ?? [], capabilities ?? [], tools,
            risk, BlastRadius.Single, ["local"], ["test"],
            new DelegationBudget(10, 100_000, Start.AddHours(1)), window);

    private static AuthorityEnvelope DiscoveryEnvelope(params string[] tools) => Envelope(tools);

    private static AuthorityEnvelope DiagnosticEnvelope(params string[] tools) =>
        Envelope(tools, skills: ["sample.skill"], capabilities: ["sample.diagnose", "sample.remediate"]);

    private static AuthorityEnvelope RemediationEnvelope(MaintenanceWindow? window = null, params string[] tools) =>
        Envelope(tools, RiskLevel.High, ["sample.skill"], ["sample.remediate"], window);

    private static DelegatedExecutionScope ScopeOf(AuthorityEnvelope envelope, AgentRoleKind role) =>
        DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), role), envelope);

    private sealed class Harness
    {
        public required AgentRunner Runner { get; init; }

        public required RecordingAuditSink Audit { get; init; }

        public required RecordingPolicyEngine Policy { get; init; }

        public required FakeTimeProvider Clock { get; init; }
    }

    private static Harness Create(
        IChatModel? model = null,
        IPolicyEngine? policy = null,
        IApprovalProvider? approval = null,
        IReadOnlyList<ICapability>? capabilities = null,
        AgentRunnerOptions? options = null,
        IReadOnlyList<ITool>? tools = null)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools ?? [])
        {
            registry.Register(SamplePackage, tool);
        }

        var skills = new SkillRegistry();
        // A Skill must declare at least one Capability, so the loop tests get one they never use.
        skills.Register(SamplePackage, new TestSkillProvider(capabilities ??
            [new DelegateCapability("sample.unused", RiskLevel.Read, (_, _, _) => Task.FromResult(new SkillReport([], [], null)))]));
        var audit = new RecordingAuditSink();
        var recording = new RecordingPolicyEngine(policy);
        var clock = new FakeTimeProvider(Start);
        var runner = new AgentRunner(
            model ?? new FakeChatModel(), registry, recording, approval ?? new NeverCalledApprovalProvider(), audit,
            new InMemoryTaskStore(), clock, NullLogger<AgentRunner>.Instance,
            options ?? new AgentRunnerOptions { MaxObservationCharacters = 1024 }, skills);
        return new Harness { Runner = runner, Audit = audit, Policy = recording, Clock = clock };
    }

    private static ModelResponse Call(string tool) =>
        new(null, [new ModelToolCall($"call-{tool}", tool, ToolArguments.Empty)], false, null);

    private static ModelResponse Final() => new("Done.", [], true, null);

    // ---- the model loop ----

    [Fact]
    public async Task RunDelegatedAsync_RunsAnAllowedReadCall_AndCorrelatesEveryEventItWrites()
    {
        var scope = ScopeOf(DiscoveryEnvelope("sample.read"), AgentRoleKind.Discovery);
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Call("sample.read"), Final());
        var harness = Create(model, tools: [new FakeReadTool("sample.read", "42% CPU")]);

        var result = await harness.Runner.RunDelegatedAsync("look at the cpu", Operator, scope);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(harness.Audit.Events, e => e is ModelCallAuditEvent);
        Assert.Contains(harness.Audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Success, Tool: "sample.read" });
        Assert.All(harness.Audit.Events, e => Assert.Equal(scope.Correlation, e.Delegation));
    }

    [Fact]
    public async Task RunDelegatedAsync_CorrelatesAFailedModelCallToo()
    {
        var scope = ScopeOf(DiscoveryEnvelope("sample.read"), AgentRoleKind.Discovery);
        var harness = Create(new ThrowingChatModel(new InvalidOperationException("provider down")), tools: [new FakeReadTool("sample.read")]);

        var result = await harness.Runner.RunDelegatedAsync("look at the cpu", Operator, scope);

        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        var failed = Assert.Single(harness.Audit.Events, e => e is ModelCallAuditEvent { Outcome: ModelCallOutcome.Failure });
        Assert.Equal(scope.Correlation, failed.Delegation);
    }

    [Fact]
    public async Task RunDelegatedAsync_RefusesAToolOutsideTheEnvelope_BeforePolicyRunsAndBeforeTheToolExecutes()
    {
        var scope = ScopeOf(DiscoveryEnvelope("sample.read"), AgentRoleKind.Discovery);
        var other = new RecordingReadTool("sample.other", []);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Call("sample.other"), PlanningTestSupport.PlanResponse(revision: 1), Final());
        var harness = Create(model, new StubPolicyEngine(PolicyMode.Automatic), tools: [new FakeReadTool("sample.read"), other]);

        var result = await harness.Runner.RunDelegatedAsync("look around", Operator, scope);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, other.ExecutionCount);
        Assert.Empty(harness.Policy.Contexts);
        var decision = Assert.Single(harness.Audit.Events.OfType<PolicyDecisionAuditEvent>());
        Assert.Equal(PolicyMode.Forbidden, decision.Mode);
        Assert.Equal("sample.other", decision.Tool);
        Assert.Contains("authority envelope", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(scope.Correlation, decision.Delegation);
        var call = Assert.Single(harness.Audit.Events.OfType<ToolCallAuditEvent>());
        Assert.Equal(AuthorizationKind.PolicyDenied, call.Authorization);
        Assert.Equal(ToolOutcome.Denied, call.Outcome);
        Assert.Equal(scope.Correlation, call.Delegation);
        Assert.Contains(result.Steps, step => step.Observation!.Contains("authority envelope", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunDelegatedAsync_TreatsRepeatedEnvelopeDenialsAsPolicyBlocked_LikeAnyOtherDenial()
    {
        var scope = ScopeOf(DiscoveryEnvelope("sample.read"), AgentRoleKind.Discovery);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Call("sample.other"), PlanningTestSupport.PlanResponse(revision: 1), Call("sample.other"));
        var harness = Create(
            model, options: new AgentRunnerOptions { MaxConsecutivePolicyDenials = 2 },
            tools: [new FakeReadTool("sample.read"), new FakeReadTool("sample.other")]);

        var result = await harness.Runner.RunDelegatedAsync("look around", Operator, scope);

        Assert.Equal(AgentTaskStatus.PolicyBlocked, result.Status);
        Assert.Equal(2, result.Steps.Count);
    }

    [Fact]
    public async Task RunDelegatedAsync_PolicyStillDecides_TheMoreRestrictiveOfTheTwoWins()
    {
        var scope = ScopeOf(DiscoveryEnvelope("sample.read"), AgentRoleKind.Discovery);
        var read = new RecordingReadTool("sample.read", []);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Call("sample.read"), PlanningTestSupport.PlanResponse(revision: 1), Final());
        var harness = Create(model, new StubPolicyEngine(PolicyMode.Forbidden, "policy says no"), tools: [read]);

        var result = await harness.Runner.RunDelegatedAsync("look at the cpu", Operator, scope);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, read.ExecutionCount);
        Assert.Single(harness.Policy.Contexts);
        var decision = Assert.Single(harness.Audit.Events.OfType<PolicyDecisionAuditEvent>());
        Assert.Equal("policy says no", decision.Reason);
        Assert.Equal(scope.Correlation, decision.Delegation);
    }

    [Fact]
    public async Task RunDelegatedAsync_ThePolicyEngineSeesTheDelegationAndTheEnvelope_AndAPlainRunSeesNeither()
    {
        var scope = ScopeOf(DiscoveryEnvelope("sample.read"), AgentRoleKind.Discovery);
        var harness = Create(
            new FakeChatModel(PlanningTestSupport.PlanResponse(), Call("sample.read"), Final()), tools: [new FakeReadTool("sample.read")]);

        await harness.Runner.RunDelegatedAsync("look at the cpu", Operator, scope);

        var context = Assert.Single(harness.Policy.Contexts);
        Assert.Equal(scope.Correlation, context.Delegation);
        Assert.Same(scope.Envelope, context.Envelope);
        Assert.Equal(AgentRoleKind.Discovery, context.Delegation!.Agent!.Role);

        var plain = Create(
            new FakeChatModel(PlanningTestSupport.PlanResponse(), Call("sample.read"), Final()), tools: [new FakeReadTool("sample.read")]);
        await plain.Runner.RunAsync("look at the cpu", Operator);

        var plainContext = Assert.Single(plain.Policy.Contexts);
        Assert.Null(plainContext.Delegation);
        Assert.Null(plainContext.Envelope);
        Assert.All(plain.Audit.Events, e => Assert.Null(e.Delegation));
    }

    [Fact]
    public async Task RunDelegatedAsync_RefusesEveryStepOfAScopeThatCarriesNoEnvelope_RatherThanRunningUnrestricted()
    {
        var agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Discovery);
        var scope = new DelegatedExecutionScope(new DelegationCorrelation(Guid.NewGuid(), "0123abcd", agent), envelope: null);
        var read = new RecordingReadTool("sample.read", []);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Call("sample.read"), PlanningTestSupport.PlanResponse(revision: 1), Final());
        var harness = Create(model, new StubPolicyEngine(PolicyMode.Automatic), tools: [read]);

        var result = await harness.Runner.RunDelegatedAsync("look at the cpu", Operator, scope);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, read.ExecutionCount);
        Assert.Empty(harness.Policy.Contexts);
        Assert.Contains(harness.Audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.PolicyDenied });
        Assert.All(harness.Audit.Events, e => Assert.Equal(scope.Correlation, e.Delegation));
    }

    [Fact]
    public async Task RunDelegatedAsync_RefusesAnEnvelopeGrantedToAnotherOperator()
    {
        var scope = ScopeOf(Envelope(["sample.read"], originator: ActorIdentity.FromOperatingSystemUser("mallory")), AgentRoleKind.Discovery);
        var read = new RecordingReadTool("sample.read", []);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Call("sample.read"), PlanningTestSupport.PlanResponse(revision: 1), Final());
        var harness = Create(model, tools: [read]);

        await harness.Runner.RunDelegatedAsync("look at the cpu", Operator, scope);

        Assert.Equal(0, read.ExecutionCount);
    }

    [Fact]
    public async Task RunDelegatedAsync_RefusesANonReadToolThatIsNotAStepOfAPlan_WhateverPolicyAndTheEnvelopeAllow()
    {
        // The envelope lists the tool and its ceiling is High, and policy is fully permissive: the call is still
        // refused, because only the steps of an approved plan carry the Skill scope that lets a delegated agent act.
        var scope = ScopeOf(RemediationEnvelope(window: null, "sample.action"), AgentRoleKind.Remediation);
        var action = new CountingActionTool("sample.action");
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Call("sample.action"), PlanningTestSupport.PlanResponse(revision: 1), Final());
        var harness = Create(model, new StubPolicyEngine(PolicyMode.Automatic), tools: [new FakeReadTool("sample.read"), action]);

        var result = await harness.Runner.RunDelegatedAsync("fix it", Operator, scope);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, action.ExecutionCount);
        Assert.Empty(harness.Policy.Contexts);
        var decision = Assert.Single(harness.Audit.Events.OfType<PolicyDecisionAuditEvent>());
        Assert.Contains("Skill scope", decision.Reason, StringComparison.Ordinal);

        // The same call outside delegation is what it always was.
        var plainAction = new CountingActionTool("sample.action");
        var plain = Create(
            new FakeChatModel(PlanningTestSupport.PlanResponse(), Call("sample.action"), Final()),
            new StubPolicyEngine(PolicyMode.Automatic), tools: [new FakeReadTool("sample.read"), plainAction]);
        await plain.Runner.RunAsync("fix it", Operator);
        Assert.Equal(1, plainAction.ExecutionCount);
    }

    [Fact]
    public async Task RunDelegatedAsync_CorrelatesThePolicyDecisionAndTheApprovalOfAnApprovedCall()
    {
        var scope = ScopeOf(DiscoveryEnvelope("sample.read"), AgentRoleKind.Discovery);
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Call("sample.read"), Final());
        var harness = Create(
            model, new StubPolicyEngine(PolicyMode.Approval, "ask a human"), new StubApprovalProvider(approved: true),
            tools: [new FakeReadTool("sample.read")]);

        var result = await harness.Runner.RunDelegatedAsync("look at the cpu", Operator, scope);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(harness.Audit.Events, e => e is PolicyDecisionAuditEvent { Mode: PolicyMode.Approval });
        Assert.Contains(harness.Audit.Events, e => e is ApprovalAuditEvent { Approved: true });
        Assert.Contains(harness.Audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.UserApproved });
        Assert.All(harness.Audit.Events, e => Assert.Equal(scope.Correlation, e.Delegation));
    }

    [Fact]
    public async Task RunDelegatedAsync_RequiresADelegation() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => Create().Runner.RunDelegatedAsync("goal", Operator, null!));

    // ---- preparing a Capability ----

    private static (DelegateCapability Capability, Func<bool> Ran) Diagnose(string name = "sample.diagnose", string evidenceTool = "sample.read")
    {
        var ran = false;
        var capability = new DelegateCapability(name, RiskLevel.Read, async (request, invoker, ct) =>
        {
            ran = true;
            var result = await invoker.InvokeAsync(evidenceTool, ToolArguments.Empty, ct);
            var evidence = new Evidence("e-1", EvidenceKind.Fact, "Observed.", result.Output ?? result.ErrorMessage, evidenceTool, DateTimeOffset.UnixEpoch);
            return new SkillReport([evidence], [new Finding("f-1", "Observed a thing.", [evidence.Id])], null);
        });
        return (capability, () => ran);
    }

    [Fact]
    public async Task PrepareDelegatedSkillAsync_RunsInsideTheEnvelope_AndCorrelatesEveryEventItWrites()
    {
        var scope = ScopeOf(DiagnosticEnvelope("sample.read"), AgentRoleKind.Diagnostic);
        var (capability, ran) = Diagnose();
        var harness = Create(capabilities: [capability], tools: [new FakeReadTool("sample.read", "healthy")]);

        var prepared = await harness.Runner.PrepareDelegatedSkillAsync(
            Guid.NewGuid(), Operator, "sample.skill", "sample.diagnose", Request, scope);

        Assert.Equal(SkillPreparationStatus.Prepared, prepared.Status);
        Assert.True(ran());
        Assert.Equal("healthy", Assert.Single(prepared.Report.Evidence).Data);
        Assert.Contains(harness.Audit.Events, e => e is ToolCallAuditEvent { Tool: "sample.read", Outcome: ToolOutcome.Success });
        Assert.Contains(harness.Audit.Events, e => e is SkillRunAuditEvent { Stage: SkillRunStage.Preparation, Outcome: SkillRunOutcome.Success });
        Assert.All(harness.Audit.Events, e => Assert.Equal(scope.Correlation, e.Delegation));
        var context = Assert.Single(harness.Policy.Contexts);
        Assert.Equal(scope.Correlation, context.Delegation);
        Assert.Equal("sample.skill", context.SkillId);
    }

    public static TheoryData<string, string, string> PreparationsTheEnvelopeRefuses() => new()
    {
        { "other.skill", "sample.diagnose", "local" },
        { "sample.skill", "other.capability", "local" },
        { "sample.skill", "sample.diagnose", "elsewhere" },
    };

    [Theory]
    [MemberData(nameof(PreparationsTheEnvelopeRefuses))]
    public async Task PrepareDelegatedSkillAsync_RefusesACapabilityOutsideTheEnvelope_BeforeItsCodeRuns(
        string skillId, string capabilityName, string target)
    {
        // A Capability that makes no evidence call would never meet the per-step check, so it is stopped here.
        var scope = ScopeOf(DiagnosticEnvelope("sample.read"), AgentRoleKind.Diagnostic);
        var (capability, ran) = Diagnose();
        var other = new DelegateCapability("other.capability", RiskLevel.Read, (_, _, _) => Task.FromResult(new SkillReport([], [], null)));
        var harness = Create(capabilities: [capability, other], tools: [new FakeReadTool("sample.read")]);
        var request = new CapabilityRequest(ToolArguments.Empty, target, "test", BlastRadius.Single);

        // "other.skill" is not activated at all; that is refused too, by the ordinary check, and is not the case under test.
        var prepared = await harness.Runner.PrepareDelegatedSkillAsync(
            Guid.NewGuid(), Operator, skillId, capabilityName, request, scope);

        Assert.Equal(SkillPreparationStatus.Failed, prepared.Status);
        Assert.False(ran());
        Assert.Empty(harness.Audit.Events.OfType<ToolCallAuditEvent>());
        if (skillId == "sample.skill")
        {
            var refused = Assert.Single(
                harness.Audit.Events.OfType<SkillRunAuditEvent>(), e => e.Stage == SkillRunStage.Preparation);
            Assert.Equal(SkillRunOutcome.Refused, refused.Outcome);
            Assert.Contains("authority envelope", refused.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(scope.Correlation, refused.Delegation);
        }
    }

    [Fact]
    public async Task PrepareDelegatedSkillAsync_RefusesEveryEvidenceCallOutsideTheEnvelope()
    {
        // The envelope lets this agent prepare the Capability but lists a different tool than the one it calls.
        var scope = ScopeOf(DiagnosticEnvelope("sample.other-read"), AgentRoleKind.Diagnostic);
        var (capability, ran) = Diagnose();
        var read = new RecordingReadTool("sample.read", []);
        var harness = Create(new FakeChatModel(), new StubPolicyEngine(PolicyMode.Automatic), capabilities: [capability], tools: [read, new FakeReadTool("sample.other-read")]);

        var prepared = await harness.Runner.PrepareDelegatedSkillAsync(
            Guid.NewGuid(), Operator, "sample.skill", "sample.diagnose", Request, scope);

        Assert.Equal(SkillPreparationStatus.Prepared, prepared.Status);
        Assert.True(ran());
        Assert.Equal(0, read.ExecutionCount);
        Assert.Contains("authority envelope", Assert.Single(prepared.Report.Evidence).Data, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Policy.Contexts);
        Assert.Contains(harness.Audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.PolicyDenied, Outcome: ToolOutcome.Denied });
    }

    [Fact]
    public async Task ADiscoveryEnvelopeCannotPrepareACapabilityAtAll()
    {
        // Skills and Capabilities are not applicable to Discovery (ADR-0031): what the reduction hands it is empty.
        var scope = ScopeOf(DiscoveryEnvelope("sample.read"), AgentRoleKind.Discovery);
        var (capability, ran) = Diagnose();
        var harness = Create(capabilities: [capability], tools: [new FakeReadTool("sample.read")]);

        var prepared = await harness.Runner.PrepareDelegatedSkillAsync(
            Guid.NewGuid(), Operator, "sample.skill", "sample.diagnose", Request, scope);

        Assert.Equal(SkillPreparationStatus.Failed, prepared.Status);
        Assert.False(ran());
    }

    // ---- executing an approved plan ----

    private static ExecutionPlan Plan(params string[] tools) =>
        new("sample.remediate", "1.0.0", "Apply the sample action.",
            [.. tools.Select((tool, index) => new ExecutionPlanStep(index, tool, ToolArguments.Empty, $"Step {index}."))]);

    private static DelegateCapability Remediate(ExecutionPlan plan) =>
        new("sample.remediate", RiskLevel.High, (_, _, _) => Task.FromResult(new SkillReport([], [], plan)),
            verification: new VerificationSpec("sample.read", [], "Confirms the sample action."));

    private static ExecutionPlanApproval Approval(PreparedSkillRun prepared) =>
        new(prepared.PlanHash!, new ApprovalDecision(true, ActorIdentity.FromOperatingSystemUser("approver"), null));

    [Fact]
    public async Task ExecuteDelegatedPreparedSkillAsync_RunsAnApprovedPlanInsideTheEnvelope_WithEveryEventCorrelated()
    {
        var action = new CountingActionTool("sample.action");
        var diagnostic = ScopeOf(DiagnosticEnvelope("sample.read"), AgentRoleKind.Diagnostic);
        var remediation = ScopeOf(RemediationEnvelope(window: null, "sample.action"), AgentRoleKind.Remediation);
        var harness = Create(
            policy: new StubPolicyEngine(PolicyMode.Automatic), capabilities: [Remediate(Plan("sample.action"))],
            tools: [new FakeReadTool("sample.read"), action]);
        var taskId = Guid.NewGuid();
        var prepared = await harness.Runner.PrepareDelegatedSkillAsync(taskId, Operator, "sample.skill", "sample.remediate", Request, diagnostic);

        var report = await harness.Runner.ExecuteDelegatedPreparedSkillAsync(taskId, Operator, prepared, Approval(prepared), remediation);

        Assert.Equal(1, action.ExecutionCount);
        Assert.Contains(report.Evidence, evidence => evidence.Kind == EvidenceKind.ExecutedAction);
        // The declared verification is runtime infrastructure named by the tool's own manifest, like it is
        // outside delegation: it runs although the Remediation envelope does not list the verification tool.
        Assert.Contains(report.Evidence, evidence => evidence.Kind == EvidenceKind.Verification);
        var executed = Assert.Single(
            harness.Audit.Events.OfType<ToolCallAuditEvent>(), e => e.Tool == "sample.action");
        Assert.Equal(remediation.Correlation, executed.Delegation);
        Assert.Equal(AgentRoleKind.Remediation, executed.Delegation!.Agent!.Role);
        Assert.Equal("sample.skill", executed.SkillId);
        var context = Assert.Single(harness.Policy.Contexts, c => c.Manifest.Name == "sample.action");
        Assert.Same(remediation.Envelope, context.Envelope);
        Assert.Equal("local", context.Target);
        // Every event of the preparation belongs to the Diagnostic agent, every event of the execution to Remediation.
        Assert.All(
            harness.Audit.Events.Where(e => e is SkillRunAuditEvent { Stage: SkillRunStage.Preparation } or SkillRunAuditEvent { Stage: SkillRunStage.ProviderResolved }),
            e => Assert.Equal(diagnostic.Correlation, e.Delegation));
        Assert.All(
            harness.Audit.Events.Where(e => e is SkillRunAuditEvent { Stage: SkillRunStage.Execution }),
            e => Assert.Equal(remediation.Correlation, e.Delegation));
    }

    [Fact]
    public async Task ExecuteDelegatedPreparedSkillAsync_RefusesTheWholePlanBeforeItsFirstStep_WhenALaterStepIsOutsideTheEnvelope()
    {
        var first = new CountingActionTool("sample.action");
        var second = new CountingActionTool("sample.action-2");
        var diagnostic = ScopeOf(DiagnosticEnvelope("sample.read"), AgentRoleKind.Diagnostic);
        var remediation = ScopeOf(RemediationEnvelope(window: null, "sample.action"), AgentRoleKind.Remediation);
        var harness = Create(
            policy: new StubPolicyEngine(PolicyMode.Automatic), capabilities: [Remediate(Plan("sample.action", "sample.action-2"))],
            tools: [new FakeReadTool("sample.read"), first, second]);
        var prepared = await harness.Runner.PrepareDelegatedSkillAsync(Guid.NewGuid(), Operator, "sample.skill", "sample.remediate", Request, diagnostic);

        var report = await harness.Runner.ExecuteDelegatedPreparedSkillAsync(Guid.NewGuid(), Operator, prepared, Approval(prepared), remediation);

        Assert.Equal(0, first.ExecutionCount);
        Assert.Equal(0, second.ExecutionCount);
        Assert.Empty(report.Evidence);
        var refused = Assert.Single(
            harness.Audit.Events.OfType<SkillRunAuditEvent>(), e => e.Stage == SkillRunStage.Execution);
        Assert.Equal(SkillRunOutcome.Refused, refused.Outcome);
        Assert.Contains("Step 1", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("sample.action-2", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(remediation.Correlation, refused.Delegation);
        Assert.DoesNotContain(
            harness.Audit.Events.OfType<ToolCallAuditEvent>(), e => e.Tool.StartsWith("sample.action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteDelegatedPreparedSkillAsync_RefusesAPlanWhenTheScopeCarriesNoEnvelope()
    {
        var action = new CountingActionTool("sample.action");
        var diagnostic = ScopeOf(DiagnosticEnvelope("sample.read"), AgentRoleKind.Diagnostic);
        var noEnvelope = new DelegatedExecutionScope(
            new DelegationCorrelation(Guid.NewGuid(), "0123abcd", new AgentIdentity(AgentId.New(), AgentRoleKind.Remediation)), envelope: null);
        var harness = Create(
            policy: new StubPolicyEngine(PolicyMode.Automatic), capabilities: [Remediate(Plan("sample.action"))],
            tools: [new FakeReadTool("sample.read"), action]);
        var prepared = await harness.Runner.PrepareDelegatedSkillAsync(Guid.NewGuid(), Operator, "sample.skill", "sample.remediate", Request, diagnostic);

        var report = await harness.Runner.ExecuteDelegatedPreparedSkillAsync(Guid.NewGuid(), Operator, prepared, Approval(prepared), noEnvelope);

        Assert.Equal(0, action.ExecutionCount);
        Assert.Empty(report.Evidence);
    }

    [Fact]
    public async Task ExecuteDelegatedPreparedSkillAsync_ChecksTheWindowAgainAtEachStep_NotOnlyBeforeThePlanStarts()
    {
        // The window is open when the plan starts, so the whole-plan check passes. The first step runs for a
        // while; by the time the second is due the window has closed, and the second step is refused at execution.
        var window = new MaintenanceWindow(Start.AddMinutes(-5), Start.AddMinutes(5));
        FakeTimeProvider? clock = null;
        var first = new CountingActionTool("sample.action", onExecute: () => clock!.Advance(TimeSpan.FromMinutes(10)));
        var second = new CountingActionTool("sample.action-2");
        var diagnostic = ScopeOf(DiagnosticEnvelope("sample.read"), AgentRoleKind.Diagnostic);
        var remediation = ScopeOf(RemediationEnvelope(window, "sample.action", "sample.action-2"), AgentRoleKind.Remediation);
        var harness = Create(
            policy: new StubPolicyEngine(PolicyMode.Automatic), capabilities: [Remediate(Plan("sample.action", "sample.action-2"))],
            tools: [new FakeReadTool("sample.read"), first, second]);
        clock = harness.Clock;
        var prepared = await harness.Runner.PrepareDelegatedSkillAsync(Guid.NewGuid(), Operator, "sample.skill", "sample.remediate", Request, diagnostic);

        var report = await harness.Runner.ExecuteDelegatedPreparedSkillAsync(Guid.NewGuid(), Operator, prepared, Approval(prepared), remediation);

        Assert.Equal(1, first.ExecutionCount);
        Assert.Equal(0, second.ExecutionCount);
        var decision = Assert.Single(harness.Audit.Events.OfType<PolicyDecisionAuditEvent>(), e => e.Tool == "sample.action-2");
        Assert.Equal(PolicyMode.Forbidden, decision.Mode);
        Assert.Contains("window", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.Evidence, evidence => evidence.Kind == EvidenceKind.ExecutedAction
            && evidence.Data!.Contains("window", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteDelegatedPreparedSkillAsync_RefusesAPlanWhoseToolIsAboveTheRoleCeiling()
    {
        var action = new CountingActionTool("sample.action");
        var diagnostic = ScopeOf(DiagnosticEnvelope("sample.read"), AgentRoleKind.Diagnostic);
        var readOnly = ScopeOf(Envelope(["sample.action"], RiskLevel.High, ["sample.skill"], ["sample.remediate"]), AgentRoleKind.Verification);
        var harness = Create(
            policy: new StubPolicyEngine(PolicyMode.Automatic), capabilities: [Remediate(Plan("sample.action"))],
            tools: [new FakeReadTool("sample.read"), action]);
        var prepared = await harness.Runner.PrepareDelegatedSkillAsync(Guid.NewGuid(), Operator, "sample.skill", "sample.remediate", Request, diagnostic);

        await harness.Runner.ExecuteDelegatedPreparedSkillAsync(Guid.NewGuid(), Operator, prepared, Approval(prepared), readOnly);

        Assert.Equal(0, action.ExecutionCount);
    }

    [Fact]
    public async Task TheNonDelegatedSkillPathIsUnchanged_AndItsEventsCarryNoCorrelation()
    {
        var action = new CountingActionTool("sample.action");
        var harness = Create(
            policy: new StubPolicyEngine(PolicyMode.Automatic), capabilities: [Remediate(Plan("sample.action"))],
            tools: [new FakeReadTool("sample.read"), action]);
        var prepared = await harness.Runner.PrepareSkillAsync(Guid.NewGuid(), Operator, "sample.skill", "sample.remediate", Request);

        await harness.Runner.ExecutePreparedSkillAsync(Guid.NewGuid(), Operator, prepared, Approval(prepared));

        Assert.Equal(1, action.ExecutionCount);
        Assert.NotEmpty(harness.Audit.Events);
        Assert.All(harness.Audit.Events, e => Assert.Null(e.Delegation));
        Assert.All(harness.Policy.Contexts, context =>
        {
            Assert.Null(context.Delegation);
            Assert.Null(context.Envelope);
        });
    }

    [Fact]
    public async Task TheDelegatedSkillEntryPointsRequireADelegation()
    {
        var harness = Create();
        var prepared = new PreparedSkillRun(
            Guid.NewGuid(), "sample.skill", "sample.remediate", Request, SkillPreparationStatus.Failed,
            new SkillReport([], [], null), null, "unused");

        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Runner.PrepareDelegatedSkillAsync(
            Guid.NewGuid(), Operator, "sample.skill", "sample.diagnose", Request, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Runner.ExecuteDelegatedPreparedSkillAsync(
            Guid.NewGuid(), Operator, prepared, approval: null, null!));
    }

    // ---- doubles ----

    private sealed class RecordingPolicyEngine(IPolicyEngine? inner) : IPolicyEngine
    {
        public List<PolicyContext> Contexts { get; } = [];

        public PolicyDecision Evaluate(PolicyContext context)
        {
            Contexts.Add(context);
            return (inner ?? new DefaultTestPolicyEngine()).Evaluate(context);
        }
    }

    /// <summary>A non-Read tool with a verification, that counts how often it really ran and can act while it runs.</summary>
    private sealed class CountingActionTool(string name, Action? onExecute = null) : IVerifiableTool
    {
        public int ExecutionCount { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "A tool that acts, for envelope tests.",
            Risk = RiskLevel.High,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
            Verification = new VerificationSpec("sample.read", [], "Confirms the action."),
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            ExecutionCount++;
            onExecute?.Invoke();
            return Task.FromResult(ToolCallResult.Success("done"));
        }

        public Task<VerificationOutcome> EvaluateVerificationAsync(
            ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
            Task.FromResult(new VerificationOutcome(VerificationStatus.Confirmed, null));
    }
}
