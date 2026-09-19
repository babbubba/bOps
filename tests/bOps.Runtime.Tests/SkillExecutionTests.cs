// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

public sealed class SkillExecutionTests
{
    private static readonly PackageId SkillPackage = new("sample.package");
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("skill-user");
    private static readonly CapabilityRequest Request = new(
        ToolArguments.Empty, "local", "test", BlastRadius.Single);

    [Fact]
    public async Task PrepareSkillAsync_UsesSamePackageReadTool_AndAuditsContext()
    {
        var capability = new DelegateCapability("sample.inspect", RiskLevel.Read, async (request, invoker, ct) =>
        {
            var result = await invoker.InvokeAsync("sample.read", ToolArguments.Empty, ct);
            var evidence = new Evidence("e-1", EvidenceKind.Fact, "Observed sample.", result.Output, "sample.read", DateTimeOffset.UnixEpoch);
            return new SkillReport([evidence], [new Finding("f-1", "Sample is healthy.", [evidence.Id])], null);
        });
        var contextPolicy = new CapturingPolicyEngine();
        var (runner, audit, taskStore) = CreateRunner(capability, contextPolicy, new FakeReadTool("sample.read", "healthy"));

        var prepared = await runner.PrepareSkillAsync(Guid.NewGuid(), Actor, "sample.skill", "sample.inspect", Request);

        Assert.Equal(SkillPreparationStatus.Prepared, prepared.Status);
        Assert.Equal("healthy", Assert.Single(prepared.Report.Evidence).Data);
        Assert.Contains(contextPolicy.Contexts, context =>
            context.SkillId == "sample.skill"
            && context.CapabilityName == "sample.inspect"
            && context.Target == "local"
            && context.Environment == "test"
            && context.BlastRadius == BlastRadius.Single);
        Assert.Contains(audit.Events, evt => evt is ToolCallAuditEvent
        {
            SkillId: "sample.skill",
            CapabilityName: "sample.inspect",
            Tool: "sample.read",
        });
        Assert.Contains(audit.Events, evt => evt is SkillRunAuditEvent
        {
            Stage: SkillRunStage.Preparation,
            Outcome: SkillRunOutcome.Success,
        });
        Assert.Empty(taskStore.Saves);
    }

    [Theory]
    [InlineData("other.read")]
    [InlineData("sample.highrisk")]
    [InlineData("missing.read")]
    [InlineData("platform.read")]
    public async Task PrepareSkillAsync_RestrictedInvokerRejectsCrossPackageNonReadAndUnavailableTools(string toolName)
    {
        var capability = new DelegateCapability("sample.inspect", RiskLevel.Read, async (request, invoker, ct) =>
        {
            var result = await invoker.InvokeAsync(toolName, ToolArguments.Empty, ct);
            return new SkillReport(
                [new Evidence("e-1", EvidenceKind.Fact, "Invocation result.", result.ErrorMessage, toolName, DateTimeOffset.UnixEpoch)],
                [], null);
        });
        var (runner, audit, _) = CreateRunner(capability, new CapturingPolicyEngine(),
            new FakeReadTool("sample.read"), new FakeHighRiskTool("sample.highrisk"),
            new UnsupportedPlatformReadTool(),
            new PackagedTool(new FakeReadTool("other.read"), new PackageId("other.package")));

        var prepared = await runner.PrepareSkillAsync(Guid.NewGuid(), Actor, "sample.skill", "sample.inspect", Request);

        Assert.Equal(SkillPreparationStatus.Prepared, prepared.Status);
        Assert.Contains("not permitted", Assert.Single(prepared.Report.Evidence).Data, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.Events, evt => evt is ToolCallAuditEvent
        {
            Authorization: AuthorizationKind.PolicyDenied,
            Outcome: ToolOutcome.Denied,
        });
    }

    [Fact]
    public async Task PrepareSkillAsync_TranslatesCapabilityExceptionAndTimeout()
    {
        var throwing = new DelegateCapability("sample.throws", RiskLevel.Read,
            (request, invoker, ct) => throw new InvalidOperationException("boom"));
        var timingOut = new DelegateCapability("sample.timeout", RiskLevel.Read,
            async (request, invoker, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new SkillReport([], [], null);
            }, timeout: TimeSpan.FromMilliseconds(25));
        var (runner, audit, _) = CreateRunner([throwing, timingOut], new CapturingPolicyEngine(), new FakeReadTool("sample.read"));

        var failed = await runner.PrepareSkillAsync(Guid.NewGuid(), Actor, "sample.skill", "sample.throws", Request);
        var timeout = await runner.PrepareSkillAsync(Guid.NewGuid(), Actor, "sample.skill", "sample.timeout", Request);

        Assert.Equal(SkillPreparationStatus.Failed, failed.Status);
        Assert.Contains("boom", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(SkillPreparationStatus.Timeout, timeout.Status);
        Assert.Contains(audit.Events, evt => evt is SkillRunAuditEvent { Outcome: SkillRunOutcome.Failure });
        Assert.Contains(audit.Events, evt => evt is SkillRunAuditEvent { Outcome: SkillRunOutcome.Timeout });
    }

    [Fact]
    public async Task PrepareSkillAsync_PropagatesCallerCancellation()
    {
        var capability = new DelegateCapability("sample.cancel", RiskLevel.Read,
            async (request, invoker, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new SkillReport([], [], null);
            });
        var (runner, _, _) = CreateRunner(capability, new CapturingPolicyEngine(), new FakeReadTool("sample.read"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.PrepareSkillAsync(
                Guid.NewGuid(), Actor, "sample.skill", "sample.cancel", Request, cancellation.Token));
    }

    [Fact]
    public async Task PrepareSkillAsync_FailsWhenFindingsDoNotReferenceRecordedEvidence()
    {
        var capability = new DelegateCapability("sample.invalid", RiskLevel.Read,
            (request, invoker, ct) => Task.FromResult(
                new SkillReport([], [new Finding("f-1", "Invented.", ["missing"])], null)));
        var (runner, _, _) = CreateRunner(capability, new CapturingPolicyEngine(), new FakeReadTool("sample.read"));

        var prepared = await runner.PrepareSkillAsync(Guid.NewGuid(), Actor, "sample.skill", "sample.invalid", Request);

        Assert.Equal(SkillPreparationStatus.Failed, prepared.Status);
        Assert.Contains("unknown Evidence", prepared.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutePreparedSkillAsync_RequiresExactApproval_ThenExecutesAndVerifies()
    {
        var plan = new ExecutionPlan("sample.remediate", "1.0.0", "Apply the sample action.",
            [new ExecutionPlanStep(0, "sample.highrisk", ToolArguments.Empty, "Apply sample action.")]);
        var capability = new DelegateCapability("sample.remediate", RiskLevel.High,
            (request, invoker, ct) => Task.FromResult(new SkillReport([], [], plan)),
            verification: new VerificationSpec("sample.read", [], "Confirms the sample action."));
        var (runner, audit, taskStore) = CreateRunner(
            capability, new CapturingPolicyEngine(), new FakeReadTool("sample.read"), new FakeHighRiskTool("sample.highrisk"));
        var taskId = Guid.NewGuid();
        var prepared = await runner.PrepareSkillAsync(taskId, Actor, "sample.skill", "sample.remediate", Request);

        var refused = await runner.ExecutePreparedSkillAsync(taskId, Actor, prepared, approval: null);
        var approval = new ExecutionPlanApproval(
            prepared.PlanHash!, new ApprovalDecision(true, ActorIdentity.FromOperatingSystemUser("approver"), null));
        var executed = await runner.ExecutePreparedSkillAsync(taskId, Actor, prepared, approval);

        Assert.Empty(refused.Evidence);
        Assert.Contains(audit.Events, evt => evt is SkillRunAuditEvent
        {
            Stage: SkillRunStage.Execution,
            Outcome: SkillRunOutcome.Refused,
        });
        Assert.Contains(executed.Evidence, evidence => evidence.Kind == EvidenceKind.ExecutedAction);
        Assert.Contains(executed.Evidence, evidence => evidence.Kind == EvidenceKind.Verification);
        Assert.Contains(audit.Events, evt => evt is ToolCallAuditEvent { PlanHash: not null });
        Assert.Empty(taskStore.Saves);
    }

    [Fact]
    public async Task ExecutePreparedSkillAsync_RejectsStaleOrRejectedApproval()
    {
        var plan = new ExecutionPlan("sample.remediate", "1.0.0", "Apply.",
            [new ExecutionPlanStep(0, "sample.highrisk", ToolArguments.Empty, null)]);
        var capability = new DelegateCapability("sample.remediate", RiskLevel.High,
            (request, invoker, ct) => Task.FromResult(new SkillReport([], [], plan)),
            verification: new VerificationSpec("sample.read", [], "Confirms."));
        var (runner, _, _) = CreateRunner(
            capability, new CapturingPolicyEngine(), new FakeReadTool("sample.read"), new FakeHighRiskTool("sample.highrisk"));
        var prepared = await runner.PrepareSkillAsync(Guid.NewGuid(), Actor, "sample.skill", "sample.remediate", Request);

        var stale = new ExecutionPlanApproval("stale", new ApprovalDecision(true, Actor, null));
        var rejected = new ExecutionPlanApproval(prepared.PlanHash!, new ApprovalDecision(false, Actor, "no"));

        Assert.Empty((await runner.ExecutePreparedSkillAsync(Guid.NewGuid(), Actor, prepared, stale)).Evidence);
        Assert.Empty((await runner.ExecutePreparedSkillAsync(Guid.NewGuid(), Actor, prepared, rejected)).Evidence);
    }

    private static (AgentRunner Runner, RecordingAuditSink Audit, InMemoryTaskStore Store) CreateRunner(
        ICapability capability, IPolicyEngine policy, params ITool[] tools) =>
        CreateRunner([capability], policy, tools);

    private static (AgentRunner Runner, RecordingAuditSink Audit, InMemoryTaskStore Store) CreateRunner(
        IReadOnlyList<ICapability> capabilities, IPolicyEngine policy, params ITool[] tools)
    {
        var toolRegistry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            if (tool is PackagedTool packaged)
            {
                toolRegistry.Register(packaged.Package, packaged.Inner);
            }
            else
            {
                toolRegistry.Register(SkillPackage, tool);
            }
        }

        var skillRegistry = new SkillRegistry();
        skillRegistry.Register(SkillPackage, new TestSkillProvider(capabilities));
        var audit = new RecordingAuditSink();
        var store = new InMemoryTaskStore();
        var runner = new AgentRunner(
            new FakeChatModel(), toolRegistry, policy, new NeverCalledApprovalProvider(), audit,
            store, TimeProvider.System, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions { MaxObservationCharacters = 1024 }, skillRegistry);
        return (runner, audit, store);
    }

    private sealed class CapturingPolicyEngine : IPolicyEngine
    {
        public List<PolicyContext> Contexts { get; } = [];

        public PolicyDecision Evaluate(PolicyContext context)
        {
            Contexts.Add(context);
            return new PolicyDecision(PolicyMode.Automatic, "test contextual policy");
        }
    }

    private sealed record PackagedTool(ITool Inner, PackageId Package) : ITool
    {
        public ToolManifest Manifest => Inner.Manifest;
        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Inner.ExecuteAsync(arguments, ct);
    }

    private sealed class UnsupportedPlatformReadTool : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "platform.read",
            Description = "A test tool unavailable on the current platform.",
            Risk = RiskLevel.Read,
            Platforms = ["unsupported-test-platform"],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(
            ToolArguments arguments, CancellationToken ct = default) =>
            throw new InvalidOperationException("A platform-incompatible tool must never execute.");
    }
}
