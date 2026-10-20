// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.AgentFramework.Tests;

/// <summary>KNOW-BENCH-01B: offline wiring only. No valid Event Log call and no provider/LLM connection.</summary>
public sealed class KnowledgeSystemEventsWiringTests
{
    [Fact]
    public void ExplicitDiagnosticCatalog_UsesTheRealReadManifestAndSchema_WithEitherKnowledgeSetting()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var world = new KnowledgeRunnerWorld();
        var process = world.Boot([]);
        var disabled = process.ComposeCanary(new ScriptedRunnerModel([]), withHost: false);
        Assert.DoesNotContain(disabled.Registry.GetAvailableManifests(), tool => tool.Name == "system.events");

        var withoutKnowledge = process.ComposeCanary(
            new ScriptedRunnerModel([]), withHost: false, withWindowsSystemEvents: true);
        using var authorized = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Authorized(KnowledgeRunnerWorld.TenantA));
        var withKnowledge = authorized.Compose(new ScriptedRunnerModel([]), withWindowsSystemEvents: true);
        var expected = SystemToolManifests.Events("windows");
        foreach (var rig in new[] { withoutKnowledge, withKnowledge })
        {
            var manifest = Assert.Single(rig.Registry.GetAvailableManifests());
            var tool = Assert.IsType<WindowsSystemEventsTool>(rig.Registry.Resolve("system.events"));
            Assert.Same(tool.Manifest, manifest);
            Assert.Equal(expected.Name, manifest.Name);
            Assert.Equal(expected.Description, manifest.Description);
            Assert.Equal(expected.Parameters, manifest.Parameters);
            Assert.Equal(RiskLevel.Read, manifest.Risk);
            Assert.Equal(["windows"], manifest.Platforms);
            Assert.Empty(manifest.Requires);
            Assert.Equal(new PackageId("bops.packages.system.windows"), manifest.Package);
            Assert.Equal(PackageTrustLevel.Official, rig.Registry.ResolveForExecution("system.events")!.Trust);
        }
        Assert.Empty(world.Events);
        Assert.Empty(authorized.AuditEvents);
        Assert.Equal(0, authorized.SourceOpens);
    }

    [Fact]
    public async Task OutOfRangeCall_IsRejectedByTheGovernedRunner_BeforeNativeCollection()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var world = new KnowledgeRunnerWorld();
        var process = world.Boot([]);
        var plan = new JsonObject
        {
            ["rationale"] = "Offline validation probe.",
            ["steps"] = new JsonArray(new JsonObject
            {
                ["description"] = "Read bounded Windows diagnostics",
                ["expectedTool"] = "system.events",
                ["expectedArguments"] = new JsonObject { ["mode"] = "raw" },
            }),
        };
        var model = new RejectionProbeModel(
            new ModelResponse(plan.ToJsonString(), [], false, null),
            new ModelResponse(null, [new ModelToolCall("invalid", "system.events",
                ToolArguments.FromJson(new JsonObject { ["mode"] = "raw", ["limit"] = 501 }))], false, Script.Usage));
        var rig = process.ComposeCanary(model, withHost: false, withWindowsSystemEvents: true);

        var state = await rig.Runner.RunAsync("Offline validation probe", KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Failed, state.Status); // The offline model stops after observing the rejection.
        var call = Assert.Single(world.Events.OfType<ToolCallAuditEvent>());
        Assert.Equal("system.events", call.Tool);
        Assert.Equal(ToolOutcome.Failure, call.Outcome);
        Assert.Equal(ToolFailureKind.Validation, call.FailureKind);
        Assert.Equal(0, rig.Read.ExecutionCount);
        Assert.Equal(0, rig.Other.ExecutionCount);
        Assert.Equal(model.Requests.Count, rig.Maf.ComposedTurns);
        Assert.Equal(model.Requests.Count, world.Events.OfType<ModelCallAuditEvent>().Count());
        Assert.DoesNotContain(world.Events.OfType<ModelCallAuditEvent>(), evt => evt.ContextGateRejection is not null);
        Assert.Empty(world.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Empty(world.Events.OfType<KnowledgePackAdmissionAuditEvent>());
        Assert.Contains("system.events [Read]", model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("limit:Integer optional minimum=1 maximum=500", model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal("system.events", Assert.Single(model.Requests[1].AvailableTools).Name);
        Assert.Contains(model.Requests[^1].History, turn => turn.Provenance?.Origin == ContextOrigin.ToolResult);
    }

    // Script only the plan and the invalid proposal. Stop on the next governed turn; never supply successful event evidence.
    private sealed class RejectionProbeModel(params ModelResponse[] responses) : IChatModel
    {
        private readonly Queue<ModelResponse> script = new(responses);
        public ChatModelDescriptor Descriptor { get; } = new("offline", "wiring-probe");
        internal List<ModelRequest> Requests { get; } = [];

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (script.Count > 0) return Task.FromResult(script.Dequeue());
            throw new ModelProtocolException("Offline rejection observed") { FailureKind = ModelFailureKind.Authentication };
        }
    }
}
