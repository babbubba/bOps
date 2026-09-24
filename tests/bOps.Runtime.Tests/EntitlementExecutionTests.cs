// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;

namespace bOps.Runtime.Tests;

public sealed class EntitlementExecutionTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("entitlement-user");
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GovernedDeniedAfterApproval_DoesNotExecute_AndIsDistinctAuthorization()
    {
        var mutation = new ApprovalBoundHighRiskTool();
        var service = new RecordingEntitlementService(request => Denied(request.Binding));
        var (runner, audit) = Create(mutation, service, PolicyMode.Approval);

        await runner.RunAsync("perform governed work", Actor);

        Assert.Equal(0, mutation.ExecutionCount);
        Assert.Single(service.Requests);
        Assert.Contains(audit.Events, e => e is ApprovalAuditEvent { Approved: true });
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent
        {
            Authorization: AuthorizationKind.EntitlementDenied,
            Outcome: ToolOutcome.Denied,
        });
        Assert.Contains(audit.Events, e => e is EntitlementDecisionAuditEvent
        {
            Applicability: EntitlementApplicability.Governed,
            Result: EntitlementDecisionKind.Denied,
            Reason: EntitlementReasonCode.NotEntitled,
        });
    }

    [Fact]
    public async Task GovernedMissingService_DoesNotExecute()
    {
        var mutation = new ApprovalBoundHighRiskTool();
        var (runner, audit) = Create(mutation, null, PolicyMode.Automatic);

        await runner.RunAsync("perform governed work", Actor);

        Assert.Equal(0, mutation.ExecutionCount);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.EntitlementDenied });
        Assert.Contains(audit.Events, e => e is EntitlementDecisionAuditEvent
        {
            Result: EntitlementDecisionKind.Denied,
            Reason: EntitlementReasonCode.Unavailable,
            Source: EntitlementSourceCategory.Unknown,
        });
    }

    [Fact]
    public async Task GovernedAllowWithWrongBinding_DoesNotExecute()
    {
        var mutation = new ApprovalBoundHighRiskTool();
        var service = new RecordingEntitlementService(_ => Allowed(new RequestBinding("wrong-binding")));
        var (runner, _) = Create(mutation, service, PolicyMode.Automatic);

        await runner.RunAsync("perform governed work", Actor);

        Assert.Equal(0, mutation.ExecutionCount);
    }

    [Fact]
    public async Task GovernedProviderFailure_DoesNotExecute()
    {
        var mutation = new ApprovalBoundHighRiskTool();
        var service = new ThrowingEntitlementService();
        var (runner, audit) = Create(mutation, service, PolicyMode.Automatic);

        await runner.RunAsync("perform governed work", Actor);

        Assert.Equal(0, mutation.ExecutionCount);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.EntitlementDenied });
    }

    [Fact]
    public async Task UngovernedWithoutService_ExecutesNormally()
    {
        var tool = new RecordingReadTool("test.ungoverned", []);
        var registry = Registry(tool, EntitlementApplicability.NotGoverned);
        var model = ModelFor(tool.Manifest.Name);
        var runner = new AgentRunner(model, registry, new StubPolicyEngine(PolicyMode.Automatic), new NeverCalledApprovalProvider(),
            new RecordingAuditSink(), new InMemoryTaskStore(), new FakeTimeProvider(Now), NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());

        await runner.RunAsync("read", Actor);

        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task GovernedVerificationDenied_SkipsVerifierAndPackageEvaluator()
    {
        var mutation = new CountingVerifiableTool("test.mutation", "test.verify");
        var verifier = new RecordingReadTool("test.verify", []);
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), PackageTrustLevel.Official,
            new EntitlementRequirement(EntitlementApplicability.NotGoverned), mutation);
        registry.Register(new PackageId("test.package"), PackageTrustLevel.Official,
            new EntitlementRequirement(EntitlementApplicability.Governed), verifier);
        var service = new RecordingEntitlementService(request => Denied(request.Binding));
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(ModelFor(mutation.Manifest.Name), registry, new StubPolicyEngine(PolicyMode.Automatic),
            new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(), new FakeTimeProvider(Now),
            NullLogger<AgentRunner>.Instance, new AgentRunnerOptions(), entitlementService: service);

        var result = await runner.RunAsync("mutate", Actor);

        Assert.Equal(1, mutation.ExecutionCount);
        Assert.Equal(0, verifier.ExecutionCount);
        Assert.Equal(0, mutation.EvaluationCount);
        Assert.Contains(result.Steps, step => step.Observation?.Contains("Inconclusive", StringComparison.Ordinal) == true);
        Assert.Contains(audit.Events, e => e is EntitlementDecisionAuditEvent
        {
            Tool: "test.verify",
            Result: EntitlementDecisionKind.Denied,
        });
    }

    [Fact]
    public async Task GovernedDecisionThatExpiresWhileProviderIsAwaited_DoesNotExecute()
    {
        var clock = new FakeTimeProvider(Now);
        var mutation = new ApprovalBoundHighRiskTool();
        var service = new AdvancingEntitlementService(clock, request => Allowed(request.Binding) with { ValidUntil = Now.AddMinutes(1) });
        var registry = Registry(mutation, EntitlementApplicability.Governed);
        registry.Register(new PackageId("test.package"), new FakeReadTool());
        var runner = new AgentRunner(ModelFor(mutation.Manifest.Name), registry, new StubPolicyEngine(PolicyMode.Automatic),
            new StubApprovalProvider(true), new RecordingAuditSink(), new InMemoryTaskStore(), clock,
            NullLogger<AgentRunner>.Instance, new AgentRunnerOptions(), entitlementService: service);

        await runner.RunAsync("perform governed work", Actor);

        Assert.Equal(0, mutation.ExecutionCount);
        Assert.True(service.Called);
    }

    [Fact]
    public async Task GovernedDeniedCall_RedactsSensitiveArgumentsBeforeAudit()
    {
        const string secret = "entitlement-denial-secret-sentinel";
        var tool = new SensitiveReadTool();
        var registry = Registry(tool, EntitlementApplicability.Governed);
        var call = new ModelToolCall("call-1", tool.Manifest.Name, ToolArguments.FromJson(new JsonObject { ["secret"] = secret }));
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(new FakeChatModel(PlanningTestSupport.PlanResponse(), new ModelResponse(null, [call], false, null),
            PlanningTestSupport.PlanResponse(revision: 1), new ModelResponse("done", [], true, null)), registry,
            new StubPolicyEngine(PolicyMode.Automatic), new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(),
            new FakeTimeProvider(Now), NullLogger<AgentRunner>.Instance, new AgentRunnerOptions(),
            entitlementService: new RecordingEntitlementService(request => Denied(request.Binding)));

        await runner.RunAsync("read governed secret", Actor);

        var denied = Assert.Single(audit.Events.OfType<ToolCallAuditEvent>());
        Assert.Equal(AuthorizationKind.EntitlementDenied, denied.Authorization);
        Assert.DoesNotContain(secret, denied.Arguments.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("***redacted***", denied.Arguments["secret"]!.GetValue<string>());
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(audit.Events.OfType<EntitlementDecisionAuditEvent>()), StringComparison.Ordinal);
    }

    private static (AgentRunner Runner, RecordingAuditSink Audit) Create(
        ApprovalBoundHighRiskTool mutation, IEntitlementService? service, PolicyMode policy)
    {
        var registry = Registry(mutation, EntitlementApplicability.Governed);
        registry.Register(new PackageId("test.package"), new FakeReadTool());
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(ModelFor(mutation.Manifest.Name), registry, new StubPolicyEngine(policy), new StubApprovalProvider(true),
            audit, new InMemoryTaskStore(), new FakeTimeProvider(Now), NullLogger<AgentRunner>.Instance, new AgentRunnerOptions(), entitlementService: service);
        return (runner, audit);
    }

    private static ToolRegistry Registry(ITool tool, EntitlementApplicability applicability)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), PackageTrustLevel.Official, new EntitlementRequirement(applicability), tool);
        return registry;
    }

    private static FakeChatModel ModelFor(string tool) => new(
        PlanningTestSupport.PlanResponse(),
        new ModelResponse(null, [new ModelToolCall("call-1", tool, ToolArguments.Empty)], false, null),
        PlanningTestSupport.PlanResponse(revision: 1),
        new ModelResponse("done", [], true, null));

    private static EntitlementDecision Allowed(RequestBinding binding) =>
        new(binding, EntitlementDecisionKind.Allowed, EntitlementReasonCode.Permitted, EntitlementSourceCategory.Local,
            "test-authority", Now.AddMinutes(-1), Now.AddMinutes(1), null);

    private static EntitlementDecision Denied(RequestBinding binding) =>
        new(binding, EntitlementDecisionKind.Denied, EntitlementReasonCode.NotEntitled, EntitlementSourceCategory.Local,
            "test-authority", Now.AddMinutes(-1), Now.AddMinutes(1), null);

    private sealed class RecordingEntitlementService(Func<EntitlementRequest, EntitlementDecision> decide) : IEntitlementService
    {
        public List<EntitlementRequest> Requests { get; } = [];

        public Task<EntitlementDecision> EvaluateAsync(EntitlementRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(decide(request));
        }
    }

    private sealed class ThrowingEntitlementService : IEntitlementService
    {
        public Task<EntitlementDecision> EvaluateAsync(EntitlementRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("provider unavailable");
    }

    private sealed class AdvancingEntitlementService(FakeTimeProvider clock, Func<EntitlementRequest, EntitlementDecision> decide) : IEntitlementService
    {
        public bool Called { get; private set; }

        public Task<EntitlementDecision> EvaluateAsync(EntitlementRequest request, CancellationToken ct = default)
        {
            Called = true;
            clock.Advance(TimeSpan.FromMinutes(2));
            return Task.FromResult(decide(request));
        }
    }

    private sealed class SensitiveReadTool : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "test.sensitive-read",
            Description = "A governed read with one sensitive parameter.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [new ToolParameter("secret", ToolParameterType.String, "Test secret.", Sensitive: true)],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            throw new InvalidOperationException("An entitlement-denied tool must not execute.");
    }

    private sealed class CountingVerifiableTool(string name, string verifier) : IVerifiableTool
    {
        public int ExecutionCount { get; private set; }
        public int EvaluationCount { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Counts execution and evaluation.",
            Risk = RiskLevel.High,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
            Verification = new VerificationSpec(verifier, [], "Reads the mutation result."),
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult(ToolCallResult.Success("mutated"));
        }

        public Task<VerificationOutcome> EvaluateVerificationAsync(ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default)
        {
            EvaluationCount++;
            return Task.FromResult(new VerificationOutcome(VerificationStatus.Confirmed, null));
        }
    }
}
