// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using Acme.SamplePlugin;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>E2E-11: the sample mutation composes through delegated preparation, approvals, policy, execution and verification.</summary>
public sealed class HardenFourteenMutationCompositionTests
{
    private static readonly PackageId SamplePackage = new("acme.sample");
    private static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("harden-14-operator");
    private static readonly ActorIdentity Approver = ActorIdentity.FromOperatingSystemUser("harden-14-approver");

    [Fact]
    [Trait("Category", "Harden14")]
    public async Task E2E11_MutationWaitsForPlanAndStepApproval_ExecutesOnce_AndIsIndependentlyVerified()
    {
        var markerName = $"harden-14-{Guid.NewGuid():N}";
        var markerPath = Path.Combine(Path.GetTempPath(), "bops-sample-skill", $"{markerName}.marker");
        var status = new SampleMarkerStatusTool();
        var mutation = new CountingMarkerCreateTool(new SampleMarkerCreateTool());

        try
        {
            Assert.Equal("absent", (await status.ExecuteAsync(MarkerArguments(markerName))).Output);

            var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
            registry.Register(SamplePackage, new SampleEchoTool());
            registry.Register(SamplePackage, status);
            registry.Register(SamplePackage, mutation);

            var skills = new SkillRegistry();
            skills.Register(SamplePackage, new SampleToolProvider(TimeProvider.System));

            var audit = new RecordingAuditSink();
            var planApproval = new InspectingPlanApproval(status, mutation, markerName);
            var stepApproval = new RecordingStepApproval();
            var policy = new RecordingPolicy();
            var agent = new AgentRunner(
                new FakeChatModel(
                    PlanningTestSupport.PlanResponse(stepCount: 1),
                    Call("discovery-echo", "sample.echo", EchoArguments("diagnose marker readiness")),
                    Final("Discovery evidence collected."),
                    PlanningTestSupport.PlanResponse(stepCount: 0),
                    Final("{\"findings\":[{\"summary\":\"The bounded marker remediation is ready.\",\"evidenceIds\":[\"discovery-0\"],\"severity\":\"low\"}]}")),
                registry,
                policy,
                stepApproval,
                audit,
                new InMemoryTaskStore(),
                TimeProvider.System,
                NullLogger<AgentRunner>.Instance,
                new AgentRunnerOptions(),
                skills);
            var runner = new DelegationRunner(
                agent,
                new Profiles(),
                planApproval,
                audit,
                TimeProvider.System,
                NullLogger<DelegationRunner>.Instance);
            var request = new DelegationRequest(
                "Collect marker evidence and apply the bounded sample remediation.",
                Remediation: new DelegationRemediation(
                    "sample.echo-marker-skill",
                    "sample.echo-marker",
                    new CapabilityRequest(
                        ToolArguments.FromJson(new JsonObject { ["message"] = "marker evidence", ["markerName"] = markerName }),
                        "local",
                        "test",
                        BlastRadius.Single)));

            var run = await runner.StartAsync(request, Operator);

            Assert.Equal(DelegationStatus.Completed, run.Status);
            Assert.Equal(1, mutation.ExecutionCount);
            Assert.Equal("present", (await status.ExecuteAsync(MarkerArguments(markerName))).Output);

            var approvalRequest = Assert.Single(planApproval.Requests);
            Assert.Equal(ExecutionPlanHasher.ComputeHash(approvalRequest.Plan), approvalRequest.PlanHash);
            Assert.Equal(run.PlanHash, approvalRequest.PlanHash);
            Assert.Equal(approvalRequest.PlanHash, run.Approval!.PlanHash);
            Assert.Equal(Approver, run.Approval.Approver);
            Assert.True(planApproval.MarkerWasAbsent);
            Assert.Equal(0, planApproval.ExecutionCountAtApproval);

            Assert.Equal(["sample.marker.create"], stepApproval.Tools);
            Assert.Contains(policy.Contexts, context => context.Manifest.Name == "sample.marker.create" && context.Manifest.Risk == RiskLevel.Low);
            Assert.Contains(policy.Contexts, context => context.Manifest.Name == "sample.marker.status" && context.Manifest.Risk == RiskLevel.Read);

            var mutationEvent = Assert.Single(audit.Events.OfType<ToolCallAuditEvent>(), evt => evt.Tool == "sample.marker.create");
            Assert.Equal(AuthorizationKind.UserApproved, mutationEvent.Authorization);
            Assert.Equal(VerificationStatus.Confirmed, mutationEvent.Verification);
            Assert.Equal(approvalRequest.PlanHash, mutationEvent.PlanHash);
            Assert.Contains(audit.Events, evt => evt is ApprovalAuditEvent { Tool: "sample.marker.create", Approved: true });
            Assert.Contains(audit.Events, evt => evt is PolicyDecisionAuditEvent { Tool: "sample.marker.create", Mode: PolicyMode.Approval });
            Assert.Contains(audit.Events, evt => evt is DelegationLifecycleAuditEvent { Stage: DelegationStage.PlanDecided });
            Assert.Contains(audit.Events, evt => evt is DelegationLifecycleAuditEvent { Stage: DelegationStage.Terminal, Status: DelegationStatus.Completed });
            var independentRead = Assert.Single(audit.Events.OfType<ToolCallAuditEvent>(), evt => evt.Tool == "sample.marker.status");
            Assert.Equal(AgentRoleKind.Verification, independentRead.Delegation!.Agent!.Role);
            Assert.Equal(VerificationStatus.Confirmed, run.Roles.Single(role => role.Agent.Role == AgentRoleKind.Verification).Verification!.Status);
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    private static ModelResponse Call(string id, string tool, ToolArguments arguments) =>
        new(null, [new ModelToolCall(id, tool, arguments)], false, null);

    private static ModelResponse Final(string text) => new(text, [], true, null);

    private static ToolArguments EchoArguments(string message) =>
        ToolArguments.FromJson(new JsonObject { ["message"] = message });

    private static ToolArguments MarkerArguments(string markerName) =>
        ToolArguments.FromJson(new JsonObject { ["markerName"] = markerName });

    private sealed class CountingMarkerCreateTool(SampleMarkerCreateTool inner) : IVerifiableTool
    {
        public int ExecutionCount { get; private set; }

        public ToolManifest Manifest => inner.Manifest;

        public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            ExecutionCount++;
            return await inner.ExecuteAsync(arguments, ct);
        }

        public Task<VerificationOutcome> EvaluateVerificationAsync(
            ToolArguments originalArguments,
            ToolCallResult verificationToolResult,
            CancellationToken ct = default) =>
            inner.EvaluateVerificationAsync(originalArguments, verificationToolResult, ct);
    }

    private sealed class InspectingPlanApproval(
        SampleMarkerStatusTool status,
        CountingMarkerCreateTool mutation,
        string markerName) : IPlanApprovalProvider
    {
        public List<PlanApprovalRequest> Requests { get; } = [];

        public bool MarkerWasAbsent { get; private set; }

        public int ExecutionCountAtApproval { get; private set; }

        public async Task<ApprovalDecision> RequestPlanApprovalAsync(PlanApprovalRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            MarkerWasAbsent = (await status.ExecuteAsync(MarkerArguments(markerName), ct)).Output == "absent";
            ExecutionCountAtApproval = mutation.ExecutionCount;
            return new ApprovalDecision(true, Approver, "HARDEN-14 simulated human plan approval");
        }
    }

    private sealed class RecordingStepApproval : IApprovalProvider
    {
        public List<string> Tools { get; } = [];

        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest,
            ToolArguments arguments,
            VerificationSpec? verification,
            string reason,
            CancellationToken ct = default)
        {
            Tools.Add(manifest.Name);
            return Task.FromResult(new ApprovalDecision(true, Approver, "HARDEN-14 simulated human step approval"));
        }
    }

    private sealed class RecordingPolicy : IPolicyEngine
    {
        public List<PolicyContext> Contexts { get; } = [];

        public PolicyDecision Evaluate(PolicyContext context)
        {
            Contexts.Add(context);
            return context.Manifest.Risk == RiskLevel.Read
                ? new PolicyDecision(PolicyMode.Automatic, "Read-only evidence is automatic.")
                : new PolicyDecision(PolicyMode.Approval, "The bounded mutation requires independent step approval.");
        }
    }

    private sealed class Profiles : IRoleProfileSource
    {
        public RoleProfile? GetProfile(AgentRoleKind role) => role switch
        {
            AgentRoleKind.Discovery => Profile(role, [], [], ["sample.echo"], RiskLevel.Read, modelTokens: 150_000),
            AgentRoleKind.Diagnostic => Profile(
                role,
                ["sample.echo-marker-skill"],
                ["sample.echo-marker"],
                ["sample.echo"],
                RiskLevel.Read,
                modelTokens: 150_000),
            AgentRoleKind.Remediation => Profile(
                role,
                ["sample.echo-marker-skill"],
                ["sample.echo-marker"],
                ["sample.marker.create", "sample.marker.status"],
                RiskLevel.Low,
                modelTokens: 0),
            AgentRoleKind.Verification => Profile(role, [], [], ["sample.marker.status"], RiskLevel.Read, modelTokens: 0),
            _ => null,
        };

        private static RoleProfile Profile(
            AgentRoleKind role,
            IReadOnlyList<string> skills,
            IReadOnlyList<string> capabilities,
            IReadOnlyList<string> tools,
            RiskLevel risk,
            int modelTokens) =>
            new(
                role,
                skills,
                capabilities,
                tools,
                risk,
                BlastRadius.Single,
                ["local"],
                ["test"],
                MaxSteps: 15,
                MaxTokens: modelTokens,
                MaxDuration: TimeSpan.FromMinutes(5));
    }
}
