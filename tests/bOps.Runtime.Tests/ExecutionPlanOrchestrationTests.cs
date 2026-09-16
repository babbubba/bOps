// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0024: <see cref="AgentRunner.ExecuteExecutionPlanAsync"/> runs an already-built
/// <see cref="ExecutionPlan"/> through the same policy/approval/verification/audit pipeline every
/// model-proposed tool call already goes through — reusing <c>ExecuteStepAsync</c>, not a second
/// implementation of it.
/// </summary>
public sealed class ExecutionPlanOrchestrationTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private static AgentRunner CreateRunner(
        IToolRegistry registry, IAuditSink? audit = null, IPolicyEngine? policyEngine = null, IApprovalProvider? approvalProvider = null) =>
        new(new FakeChatModel(), registry, policyEngine ?? new DefaultTestPolicyEngine(),
            approvalProvider ?? new NeverCalledApprovalProvider(), audit ?? new RecordingAuditSink(),
            new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());

    private static ToolRegistry CreateRegistryWith(params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return registry;
    }

    private static ExecutionPlan SingleStepPlan(string toolName = "test.read") =>
        new("test.capability", "1.0.0", "A single read step.",
            [new ExecutionPlanStep(0, toolName, ToolArguments.Empty, "Read something.")]);

    [Fact]
    public async Task ExecuteExecutionPlanAsync_ExecutesEveryStep_AndRecordsExecutedActionEvidence()
    {
        var registry = CreateRegistryWith(new FakeReadTool(output: "42% CPU"));
        var runner = CreateRunner(registry);

        var report = await runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), Actor, SingleStepPlan(), approval: null);

        var executed = Assert.Single(report.Evidence, e => e.Kind == EvidenceKind.ExecutedAction);
        Assert.Contains("42% CPU", executed.Data, StringComparison.Ordinal);
        Assert.Empty(report.Findings);
        Assert.Equal("test.capability", report.Plan!.CapabilityName);
    }

    [Fact]
    public async Task ExecuteExecutionPlanAsync_RecordsVerificationEvidence_ForANonReadStep()
    {
        var registry = CreateRegistryWith(new FakeHighRiskTool(verificationOutcome: new VerificationOutcome(VerificationStatus.Confirmed, "matches")));
        var runner = CreateRunner(registry, policyEngine: new StubPolicyEngine(PolicyMode.Automatic));
        var plan = SingleStepPlan("test.highrisk");

        var report = await runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), Actor, plan, approval: null);

        var verificationEvidence = Assert.Single(report.Evidence, e => e.Kind == EvidenceKind.Verification);
        Assert.Contains("Confirmed", verificationEvidence.Description, StringComparison.Ordinal);
        Assert.Contains(report.Evidence, e => e.Kind == EvidenceKind.ExecutedAction);
    }

    [Fact]
    public async Task ExecuteExecutionPlanAsync_WritesTheSameAuditEvents_AnOrdinaryToolCallWould()
    {
        var registry = CreateRegistryWith(new FakeReadTool());
        var audit = new RecordingAuditSink();
        var runner = CreateRunner(registry, audit);

        await runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), Actor, SingleStepPlan(), approval: null);

        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Success, Tool: "test.read" });
    }

    [Fact]
    public async Task ExecuteExecutionPlanAsync_StopsAtTheFirstDeniedStep_AndDoesNotRunLaterOnes()
    {
        var registry = CreateRegistryWith(new FakeReadTool("test.read", output: "should not run"), new FakeHighRiskTool());
        var runner = CreateRunner(registry, policyEngine: new StubPolicyEngine(PolicyMode.Forbidden, "denied for test"));
        var plan = new ExecutionPlan("test.capability", "1.0.0", "Two steps, first denied.",
            [
                new ExecutionPlanStep(0, "test.highrisk", ToolArguments.Empty, "First step."),
                new ExecutionPlanStep(1, "test.read", ToolArguments.Empty, "Second step."),
            ]);

        var report = await runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), Actor, plan, approval: null);

        // Only the first (denied) step produced evidence; the second was never attempted.
        Assert.Single(report.Evidence);
        Assert.DoesNotContain(report.Evidence, e => e.SourceTool == "test.read");
    }

    [Fact]
    public async Task ExecuteExecutionPlanAsync_Refuses_WhenTheApprovalHashDoesNotMatchThePlan()
    {
        var registry = CreateRegistryWith(new FakeReadTool());
        var runner = CreateRunner(registry, approvalProvider: new NeverCalledApprovalProvider());
        var plan = SingleStepPlan();
        var staleApproval = new ExecutionPlanApproval("not-the-real-hash", new ApprovalDecision(true, Actor, null));

        var report = await runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), Actor, plan, staleApproval);

        var refusal = Assert.Single(report.Evidence);
        Assert.Contains("does not match", refusal.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteExecutionPlanAsync_Runs_WhenTheApprovalHashMatchesThePlan()
    {
        var registry = CreateRegistryWith(new FakeReadTool(output: "ran"));
        var runner = CreateRunner(registry);
        var plan = SingleStepPlan();
        var validApproval = new ExecutionPlanApproval(ExecutionPlanHasher.ComputeHash(plan), new ApprovalDecision(true, Actor, null));

        var report = await runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), Actor, plan, validApproval);

        Assert.Contains(report.Evidence, e => e.Kind == EvidenceKind.ExecutedAction && e.Data!.Contains("ran", StringComparison.Ordinal));
    }
}
