// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// AF-TURN-01B: with the real <see cref="AgentRunner"/> running every model call as a governed MAF turn composed by the bOps context
/// provider, a model that proposes a skills function, a script runner or the knowledge tool gets nothing: the runner has no such
/// tool, so the call is refused as an unknown tool before anything executes, and the offered surface never grows.
/// </summary>
public sealed class KnowledgeRunnerBoundaryTests
{
    private static readonly ModelUsage Usage = new(100, 10, null);

    [Theory]
    [InlineData("run_skill_script")]
    [InlineData("load_skill")]
    [InlineData("read_skill_resource")]
    [InlineData("runtime.knowledge_read")]
    public async Task ASkillsFunctionOrTheKnowledgeTool_ProposedToTheRealRunner_IsRefusedBeforeExecution_AndNeverOffered(string proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var read = new RecordingReadTool("test.read", []);
        var model = new RecordingModel(
            Plan("test.read"), Call("c1", proposed), Call("c2", "test.read"), new ModelResponse("Done.", [], true, Usage), new ModelResponse("Done.", [], true, Usage));
        var audit = new RecordingAuditSink();
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), read);
        var options = new AgentRunnerOptions();
        var inner = new ModelInvocationBroker(
            model, audit, TimeProvider.System, NullLogger.Instance, options, contextProjector: new MafContextProjector(options, NullLogger.Instance));
        var maf = new MafGovernedTurnBroker(inner, audit, null, MafContextAssembly.BopsProvider);
        var runner = new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(),
            TimeProvider.System, NullLogger<AgentRunner>.Instance, options, invocationBroker: maf);

        await runner.RunAsync("check the machine", ActorIdentity.FromOperatingSystemUser("boundary"));

        Assert.True(maf.ComposedTurns > 0);
        var offered = model.Requests.SelectMany(request => request.AvailableTools.Select(tool => tool.Name)).Distinct().ToArray();
        Assert.DoesNotContain(offered, name => name is "load_skill" or "read_skill_resource" or "run_skill_script" or KnowledgeReadBridge.PublicToolName);
        Assert.DoesNotContain(audit.Events.OfType<ModelCallAuditEvent>(), e => e.ContextGateRejection is not null);
        Assert.DoesNotContain(audit.Events, e => e is KnowledgeReadAuditEvent);

        if (proposed.StartsWith("runtime.", StringComparison.Ordinal))
        {
            // The runtime namespace is the runner's: it answers an unsupported control call itself and executes nothing for it.
            Assert.Contains(model.Requests.SelectMany(request => request.History),
                turn => turn.Content?.Contains("Only runtime.evidence_read exists", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(audit.Events.OfType<ToolCallAuditEvent>(), e => e.Tool == proposed);
        }
        else
        {
            var denied = Assert.Single(audit.Events.OfType<ToolCallAuditEvent>(), e => e.Tool == proposed);
            Assert.Equal(AuthorizationKind.UnknownTool, denied.Authorization);
            Assert.Equal(ToolOutcome.Denied, denied.Outcome);
        }

        // Whatever the runner did next, it never ran a tool because the model named one it was not offered.
        Assert.True(read.ExecutionCount <= 1);
        Assert.DoesNotContain(audit.Events.OfType<ToolCallAuditEvent>(), e => e.Outcome == ToolOutcome.Success && e.Tool != "test.read");
    }

    private static ModelResponse Plan(string tool) =>
        new(new JsonObject
        {
            ["rationale"] = "A plan.",
            ["steps"] = new JsonArray(new JsonObject { ["description"] = $"run {tool}", ["expectedTool"] = tool }),
        }.ToJsonString(), [], false, null);

    private static ModelResponse Call(string id, string tool) =>
        new(null, [new ModelToolCall(id, tool, ToolArguments.Empty)], false, Usage);

    private sealed class RecordingModel(params ModelResponse[] script) : IChatModel
    {
        private readonly Queue<ModelResponse> queue = new(script);

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return queue.Count > 0
                ? Task.FromResult(queue.Dequeue())
                : throw new InvalidOperationException("Unscripted model call.");
        }
    }
}
