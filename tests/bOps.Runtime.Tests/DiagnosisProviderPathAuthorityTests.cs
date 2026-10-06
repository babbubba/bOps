// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;
using bOps.Packages.Providers.OpenAiCompatible.Tests;
using Microsoft.Extensions.Logging.Abstractions;

// Each handler/HttpClient pair lives exactly as long as its test method; nothing holds an OS handle worth disposing.
#pragma warning disable CA2000

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0044 section 3 layer 4 and section 20 P1 (review N-5), the provider/model path: a diagnosis-only run over the real
/// OpenAI-compatible adapter and a strict fake upstream. The roles are never offered a tool above Read, so a model that names
/// one anyway is rejected as an unknown tool before resolution — the policy spy is never asked about it, its execution spy never
/// counts a call, and no envelope audit event is needed on this path. The direct path is covered by
/// <c>DelegationRunnerTests.S19_*</c>. The runtime project names no provider; only this test does.
/// </summary>
public sealed class DiagnosisProviderPathAuthorityTests
{
    private static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("operator");

    private sealed class SpyPolicy : IPolicyEngine
    {
        public List<string> Evaluated { get; } = [];

        public PolicyDecision Evaluate(PolicyContext context)
        {
            Evaluated.Add(context.Manifest.Name);
            return new PolicyDecision(PolicyMode.Automatic, "spy: automatic");
        }
    }

    private sealed class CountingMutation : IVerifiableTool
    {
        public int Executions { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = "service.restart",
            Description = "Restarts a service.",
            Risk = RiskLevel.High,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
            Verification = new VerificationSpec("host.info", [], "Reads the host."),
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            Executions++;
            return Task.FromResult(ToolCallResult.Success("restarted"));
        }

        public Task<VerificationOutcome> EvaluateVerificationAsync(ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
            Task.FromResult(new VerificationOutcome(VerificationStatus.Confirmed, "ok"));
    }

    private sealed class Profiles : IRoleProfileSource
    {
        public RoleProfile? GetProfile(AgentRoleKind role) => role switch
        {
            // The mutation's name is even in both read-only profiles: the role risk cap keeps it out of what is offered.
            AgentRoleKind.Discovery or AgentRoleKind.Diagnostic => new RoleProfile(
                role, [], [], ["host.info", "service.restart"], RiskLevel.Read, BlastRadius.Single, ["local"], ["local"], 15, 150_000, TimeSpan.FromMinutes(30)),
            _ => null,
        };
    }

    private static string Text(string content) => new JsonObject
    {
        ["model"] = "vendor/picked",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["finish_reason"] = "stop",
        }),
    }.ToJsonString();

    private static string Plan() => Text(new JsonObject
    {
        ["rationale"] = "r",
        ["steps"] = new JsonArray(new JsonObject { ["description"] = "look", ["expectedTool"] = "host.info" }),
    }.ToJsonString());

    private static string ToolCall(string id, string name) => new JsonObject
    {
        ["model"] = "vendor/picked",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Empty,
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = "{}" },
                }),
            },
            ["finish_reason"] = "tool_calls",
        }),
    }.ToJsonString();

    private static bool IsPlanningRequest(string body) =>
        body.Contains("lay out your plan", StringComparison.Ordinal) || body.Contains("Revise it", StringComparison.Ordinal);

    [Fact]
    public async Task P1_ProviderPath_ANonReadToolNamedByTheModel_IsAnUnknownTool_NeverReachesPolicyOrExecution()
    {
        var provider = new StrictOpenAiProvider(
            Plan(),                                             // Discovery: plan
            ToolCall("call_1", "service_restart"),              // Discovery: a tool it was never offered
            Plan(),                                             // Discovery: replan after the rejection
            ToolCall("call_2", "host_info"),                    // Discovery: the Read tool it was offered
            Text("Read the host.\n\nEvidence limitations\n- one call named an unknown tool."),
            Plan(),                                             // Diagnostic: plan
            Text("{\"findings\":[{\"summary\":\"Host read.\",\"evidenceIds\":[\"discovery-1\"]}]}"))
        {
            ForbidNativeToolsWhen = IsPlanningRequest,
        };
        var model = new OpenAiCompatibleChatModel(
            new ChatModelOptions("OpenRouter", "https://openrouter.ai/api/v1", new SecretReference("environment", "TEST_KEY"), "openrouter/free", true)
            {
                ResolvedApiKey = "sk-test",
            },
            new HttpClient(provider));
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        var mutation = new CountingMutation();
        registry.Register(new PackageId("test.package"), new FakeReadTool("host.info", "cpu 12%"));
        registry.Register(new PackageId("test.package"), mutation);
        var policy = new SpyPolicy();
        var audit = new RecordingAuditSink();
        var agent = new AgentRunner(
            model, registry, policy, new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(), TimeProvider.System,
            NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());
        var runner = new DelegationRunner(agent, new Profiles(), new NeverAskedPlanApproval(), audit, TimeProvider.System, NullLogger<DelegationRunner>.Instance);

        var run = await runner.StartAsync(new DelegationRequest("Why is the host slow?"), Operator);

        Assert.Equal(DelegationStatus.DiagnosisCompleted, run.Status);
        Assert.Equal(0, provider.Rejections);
        Assert.Equal(0, mutation.Executions);
        Assert.DoesNotContain("service.restart", policy.Evaluated);
        Assert.Contains("host.info", policy.Evaluated);
        Assert.DoesNotContain(audit.Events.OfType<ToolCallAuditEvent>(), e => e.Tool == "service.restart");

        // What the roles were offered on the wire: the Read tool only, never the mutation.
        var offered = provider.RequestBodies
            .Where(body => !IsPlanningRequest(body))
            .Select(body => JsonNode.Parse(body)!["tools"]!.AsArray().Select(tool => tool!["function"]!["name"]!.GetValue<string>()).ToList())
            .ToList();
        Assert.Equal(4, offered.Count); // three Discovery step calls and the Diagnostic step call
        Assert.All(offered, names => Assert.Equal(["host_info"], names));

        var limitation = Assert.Single(Assert.Single(run.Roles, r => r.Agent.Role == AgentRoleKind.Discovery).EvidenceLimitations!);
        Assert.True(limitation.UnknownTool);
        Assert.Null(limitation.ToolName);
        Assert.Null(limitation.EvidenceId);
    }

    private sealed class NeverAskedPlanApproval : IPlanApprovalProvider
    {
        public Task<ApprovalDecision> RequestPlanApprovalAsync(PlanApprovalRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("A diagnosis never asks for a plan approval.");
    }
}
