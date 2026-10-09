// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// Guards the shared planning fixtures: a response they build must be a plan the ADR-0050 production contract accepts,
/// and an ambiguous one must fail loudly in the fixture instead of silently degrading to an empty plan.
/// </summary>
public sealed class PlanningTestSupportTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private static async Task<AgentPlan> AcceptedPlanAsync(ModelResponse plan, params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        var runner = new AgentRunner(
            new FakeChatModel(plan, new ModelResponse("Done.", [], true, null)), registry, new DefaultTestPolicyEngine(),
            new NeverCalledApprovalProvider(), new RecordingAuditSink(), new InMemoryTaskStore(), TimeProvider.System,
            NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());
        var state = await runner.RunAsync("check", Actor);
        return Assert.Single(state.Plans);
    }

    [Fact]
    public async Task TheDefaultPlan_IsAccepted_AsASemanticPlan_WithoutRepeatedTools()
    {
        var plan = await AcceptedPlanAsync(PlanningTestSupport.PlanResponse(), new FakeReadTool());

        Assert.Equal(1, plan.SemanticContractVersion);
        var step = Assert.Single(plan.Steps);
        Assert.Equal("test.read", step.ExpectedTool);
    }

    [Fact]
    public async Task AnIndexedRepeatedTool_IsAccepted_BecauseEveryOccurrenceIsDiscriminable()
    {
        var plan = await AcceptedPlanAsync(
            PlanningTestSupport.PlanResponseIndexed("test.read", 3),
            new FakeReadTool(parameters: [PlanningTestSupport.CallIndexParameter]));

        Assert.Equal(1, plan.SemanticContractVersion);
        Assert.Equal(3, plan.Steps.Count);
    }

    [Fact]
    public async Task DistinctSyntheticSteps_AreAccepted()
    {
        var plan = await AcceptedPlanAsync(PlanningTestSupport.PlanResponseWithDistinctSteps(4), new FakeReadTool());

        Assert.Equal(4, plan.Steps.Count);
        Assert.Equal(4, plan.Steps.Select(s => s.ExpectedTool).Distinct().Count());
    }

    [Fact]
    public void ARepeatedTool_WithoutDiscriminators_IsRefusedByTheFixture_NotHidden()
    {
        Assert.Throws<InvalidOperationException>(() => PlanningTestSupport.PlanResponseFor("test.read", "test.read"));
        Assert.Throws<InvalidOperationException>(() => PlanningTestSupport.PlanResponseFor(
            new PlanTestStep("test.read"), new PlanTestStep("test.read", PlanningTestSupport.IndexArguments(1))));
        Assert.Throws<InvalidOperationException>(() => PlanningTestSupport.PlanResponse(stepCount: 2));
    }

    [Fact]
    public async Task ARepeatedTool_WithDiscriminatingArguments_IsBuiltAndAccepted()
    {
        var plan = await AcceptedPlanAsync(
            PlanningTestSupport.PlanResponseFor(
                new PlanTestStep("test.read", new JsonObject { ["n"] = 1 }),
                new PlanTestStep("test.read", new JsonObject { ["n"] = 2 })),
            new FakeReadTool(parameters: [PlanningTestSupport.CallIndexParameter]));

        Assert.Equal(2, plan.Steps.Count);
    }
}
