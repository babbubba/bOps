// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>ADR-0054 G2-2 at the point of assignment: the runner stamps the origin of what it admits, and audits it.</summary>
public sealed class ContextProvenanceRunnerTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    [Fact]
    public async Task Runner_StampsGoalToolOutputAndRuntimeTurns_AndTheModelCallAuditCountsThem()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.read"),
            new ModelResponse(null, [new ModelToolCall("call-1", "test.read", ToolArguments.Empty)], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), new FakeReadTool(output: "42% CPU"));
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(),
            TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());

        var result = await runner.RunAsync("check cpu", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        // Planning call and step call: the operator's goal is Task. A model-authored turn carries no origin.
        Assert.All(model.Requests.Take(3), request =>
            Assert.Equal(ContextOrigin.Task, Assert.Single(request.History, turn => turn.Role == ChatRole.User).Provenance!.Origin));
        // After the tool ran, its observation is attributed as data, never as runtime text.
        var observation = Assert.Single(model.Requests[2].History, turn => turn.Role == ChatRole.Tool);
        Assert.Equal(ContextOrigin.ToolResult, observation.Provenance!.Origin);
        Assert.All(model.Requests.SelectMany(request => request.History).Where(turn => turn.Role == ChatRole.Assistant),
            turn => Assert.Null(turn.Provenance));

        var calls = audit.Events.OfType<ModelCallAuditEvent>().ToList();
        Assert.Equal(model.Requests.Count, calls.Count);
        Assert.All(calls, call => Assert.NotNull(call.ContextProvenance));
        Assert.Contains(calls, call => call.ContextProvenance!.Any(count => count.Provenance?.Origin == ContextOrigin.ToolResult));
    }
}
