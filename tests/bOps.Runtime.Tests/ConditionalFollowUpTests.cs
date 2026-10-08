// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>PRE-2C2 / ADR-0050: evidence-fact activation, deterministic skip, fact binding and resume.</summary>
public sealed class ConditionalFollowUpTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly ActorIdentity Resumer = new("api-user", "resumer", "Resumer");
    private const string Discover = "diag.discover";
    private const string Follow = "diag.follow";
    private const string Other = "diag.other";
    private const string FactType = "artifact.kind";
    private const string FactKey = "primary";
    private const string SourcePath = "/data/a.dmp";

    private static ToolParameter[] FollowParameters() =>
    [
        new ToolParameter("target", ToolParameterType.Path, "Target path."),
        new ToolParameter("label", ToolParameterType.String, "Label.", Required: false),
    ];

    private static FactTool DiscoverTool(params EvidenceFact[] facts) => new(Discover, [], facts);

    private static FactTool FollowTool() => new(Follow, FollowParameters(), []);

    private static FactTool OtherTool() => new(Other, [], []);

    private static EvidenceFact PathFact(string value = SourcePath) =>
        new(FactType, FactKey, ToolParameterType.Path, JsonValue.Create(value)!);

    private static AgentRunner Runner(IChatModel model, ITaskStore store, params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            new RecordingAuditSink(), store, TimeProvider.System, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions { MaxSteps = 20, MaxLifetimeSteps = 40 });
    }

    private static JsonObject Step(string tool, JsonObject? expected = null, JsonObject? activation = null)
    {
        var step = new JsonObject { ["description"] = $"use {tool}", ["expectedTool"] = tool };
        if (expected is not null)
        {
            step["expectedArguments"] = expected;
        }

        if (activation is not null)
        {
            step["activation"] = activation;
        }

        return step;
    }

    private static JsonObject When(int source, string? bind = null)
    {
        var activation = new JsonObject { ["sourceStep"] = source, ["factType"] = FactType, ["factKey"] = FactKey };
        if (bind is not null)
        {
            activation["bindArgument"] = bind;
        }

        return activation;
    }

    private static ModelResponse Plan(params JsonObject[] steps) =>
        new(new JsonObject { ["rationale"] = "test", ["steps"] = new JsonArray([.. steps]) }.ToJsonString(), [], false, null);

    private static ModelResponse Call(string tool, JsonObject? arguments = null) =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), tool, ToolArguments.FromJson(arguments ?? []))], false, null);

    private static ModelResponse Final() => new("done", [], true, null);

    private static IEnumerable<string> Offered(ModelRequest request) => request.AvailableTools.Select(tool => tool.Name);

    private static async Task<(InMemoryTaskStore Store, TaskState Task)> PersistedAsync(InMemoryTaskStore store, Guid taskId)
    {
        var stored = await store.LoadAsync(taskId);
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(stored))!;
        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        return (fresh, reloaded);
    }

    private static ToolManifest? ManifestOf(string name) => name == Follow ? FollowTool().Manifest : null;

    private static AgentPlan PlanOf(int revision, params PlannedStep[] steps) =>
        new(revision, "test", steps) { SemanticContractVersion = 1 };

    private static PlanStep Executed(int revision, int plannedIndex, params EvidenceFact[] facts) =>
        new(plannedIndex, "step", new ModelToolCall("c", Discover, ToolArguments.FromJson([])),
            ToolCallResult.Success("ok") with { Facts = facts }, "ok", revision)
        {
            PlannedStepIndex = plannedIndex,
            ExecutionClassification = PlannedStepExecutionClassification.Matched,
        };

    private static PlannedStep Normal(int index, string tool = Discover) => new(index, $"step {index}", tool);

    private static PlannedStep Conditional(int index, int source, string? bind = null, ToolArguments? expected = null) =>
        new(index, $"step {index}", Follow)
        {
            Activation = new EvidenceFactExists(source, FactType, FactKey, bind),
            ExpectedArguments = expected,
        };

    [Fact]
    public async Task T1_FactPresent_ActivatesTheConditionalStep()
    {
        var follow = FollowTool();
        var model = new FakeChatModel(
            Plan(Step(Discover), Step(Follow, activation: When(0))),
            Call(Discover), Call(Follow, new JsonObject { ["target"] = SourcePath }), Final());

        var result = await Runner(model, new InMemoryTaskStore(), DiscoverTool(PathFact()), follow)
            .RunAsync("diagnose", Actor);

        Assert.Equal(1, follow.ExecutionCount);
        Assert.Equal([Follow], Offered(model.Requests[2]));
        Assert.Equal([0, 1], result.Steps.Take(2).Select(step => step.PlannedStepIndex));
        Assert.Equal(PlannedStepExecutionClassification.Matched, result.Steps[1].ExecutionClassification);
    }

    [Fact]
    public async Task T2_FactAbsent_SkipsWithoutModelOrToolCall_AndAdvancesCursor()
    {
        var follow = FollowTool();
        var other = OtherTool();
        var model = new FakeChatModel(
            Plan(Step(Discover), Step(Follow, activation: When(0)), Step(Other)),
            Call(Discover), Call(Other), Final());

        var result = await Runner(model, new InMemoryTaskStore(), DiscoverTool(), follow, other)
            .RunAsync("diagnose", Actor);

        Assert.Equal(0, follow.ExecutionCount);
        Assert.Equal(1, other.ExecutionCount);
        Assert.Equal([Other], Offered(model.Requests[2]));
        Assert.Equal(4, model.Requests.Count);
        Assert.Equal([0, 2], result.Steps.Take(2).Select(step => step.PlannedStepIndex));
        Assert.DoesNotContain(result.Steps, step => step.ToolCall?.ToolName == Follow);
    }

    [Fact]
    public void T3_FactOfAnotherRevision_DoesNotActivate()
    {
        var plan = PlanOf(1, Normal(0), Conditional(1, source: 0));
        PlanStep[] steps = [Executed(0, 0, PathFact()), Executed(1, 0)];

        Assert.Null(ConditionalSteps.FindFact(plan, steps, plan.Steps[1].Activation!));
        Assert.Equal(ConditionalStepState.Skipped, ConditionalSteps.StateOf(plan, steps, 2, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task T4_SourceNotEarlier_IsRejectedAtPlanAcceptance(int source)
    {
        var follow = FollowTool();
        var model = new FakeChatModel(
            Plan(Step(Discover), Step(Follow, activation: When(source))),
            Plan(Step(Discover)), Call(Discover), Final());

        var result = await Runner(model, new InMemoryTaskStore(), DiscoverTool(), follow).RunAsync("diagnose", Actor);

        Assert.Equal(2, result.Plans[0].ModelCalls!.Count);
        Assert.Single(result.Plans[0].Steps);
        Assert.Equal(0, follow.ExecutionCount);
    }

    [Fact]
    public async Task T4b_BindingToSensitiveOrUndeclaredParameter_IsRejectedAtPlanAcceptance()
    {
        var model = new FakeChatModel(
            Plan(Step(Discover), Step(Follow, activation: When(0, bind: "missing"))),
            Plan(Step(Discover)), Call(Discover), Final());

        var result = await Runner(model, new InMemoryTaskStore(), DiscoverTool(), FollowTool()).RunAsync("diagnose", Actor);

        Assert.Equal(2, result.Plans[0].ModelCalls!.Count);
        Assert.Single(result.Plans[0].Steps);
    }

    [Fact]
    public async Task T5_BoundFactValue_MustBeUsedByTheFollowUpCall()
    {
        var follow = FollowTool();
        var model = new FakeChatModel(
            Plan(Step(Discover), Step(Follow, activation: When(0, bind: "target"))),
            Call(Discover),
            Call(Follow, new JsonObject { ["target"] = "/data/other.dmp" }),
            Call(Follow, new JsonObject { ["target"] = SourcePath }),
            Final());

        var result = await Runner(model, new InMemoryTaskStore(), DiscoverTool(PathFact()), follow)
            .RunAsync("diagnose", Actor);

        Assert.Equal(1, follow.ExecutionCount);
        Assert.Equal(PlannedStepExecutionClassification.SemanticMismatch, result.Steps[1].ExecutionClassification);
        Assert.Equal(PlannedStepExecutionClassification.Matched, result.Steps[2].ExecutionClassification);
        Assert.Contains(SourcePath, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T6_BindingTypeMismatch_FailsClosedToReplan_WithoutExecutingOrCoercing()
    {
        var follow = FollowTool();
        var stringFact = new EvidenceFact(FactType, FactKey, ToolParameterType.String, JsonValue.Create(SourcePath)!);
        var model = new FakeChatModel(
            Plan(Step(Discover), Step(Follow, activation: When(0, bind: "target"))),
            Call(Discover), Plan(), Final());

        var result = await Runner(model, new InMemoryTaskStore(), DiscoverTool(stringFact), follow)
            .RunAsync("diagnose", Actor);

        Assert.Equal(0, follow.ExecutionCount);
        Assert.Equal(2, result.Plans.Count);
        Assert.Empty(model.Requests[2].AvailableTools);
    }

    [Fact]
    public async Task T7_StaticAndBoundValueConflict_FailsClosedToReplan()
    {
        var follow = FollowTool();
        var model = new FakeChatModel(
            Plan(Step(Discover), Step(Follow, new JsonObject { ["target"] = "/data/static.dmp" }, When(0, bind: "target"))),
            Call(Discover), Plan(), Final());

        var result = await Runner(model, new InMemoryTaskStore(), DiscoverTool(PathFact()), follow)
            .RunAsync("diagnose", Actor);

        Assert.Equal(0, follow.ExecutionCount);
        Assert.Equal(2, result.Plans.Count);
    }

    [Fact]
    public async Task T7b_StaticAndBoundEqualValues_CombineDeterministically()
    {
        var follow = FollowTool();
        var model = new FakeChatModel(
            Plan(Step(Discover), Step(Follow, new JsonObject { ["target"] = SourcePath, ["label"] = "x" }, When(0, bind: "target"))),
            Call(Discover), Call(Follow, new JsonObject { ["target"] = SourcePath, ["label"] = "x" }), Final());

        await Runner(model, new InMemoryTaskStore(), DiscoverTool(PathFact()), follow).RunAsync("diagnose", Actor);

        Assert.Equal(1, follow.ExecutionCount);
    }

    [Fact]
    public async Task T8_ResumeAfterActivation_StaysActivated()
    {
        var store = new InMemoryTaskStore();
        var interrupted = await Runner(
                new FailingAfterModel(Plan(Step(Discover), Step(Follow, activation: When(0))), Call(Discover)),
                store, DiscoverTool(PathFact()), FollowTool())
            .RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);

        var (resumeStore, persisted) = await PersistedAsync(store, interrupted.Id);
        var plan = persisted.Plans[^1];
        Assert.Equal(ConditionalStepState.Activated, ConditionalSteps.StateOf(plan, persisted.Steps, 1, 1));
        Assert.Equal(1, PlannedStepPosition.Derive(plan, persisted.Steps, ManifestOf).Cursor);

        var follow = FollowTool();
        var model = new FakeChatModel(Call(Follow, new JsonObject { ["target"] = SourcePath }), Final());
        await Runner(model, resumeStore, DiscoverTool(PathFact()), follow).ResumeAsync(persisted, Resumer);

        Assert.Equal([Follow], Offered(model.Requests[0]));
        Assert.Equal(1, follow.ExecutionCount);
    }

    [Fact]
    public async Task T9_ResumeAfterSkip_StaysSkipped()
    {
        var store = new InMemoryTaskStore();
        var interrupted = await Runner(
                new FailingAfterModel(Plan(Step(Discover), Step(Follow, activation: When(0)), Step(Other)), Call(Discover)),
                store, DiscoverTool(), FollowTool(), OtherTool())
            .RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);

        var (resumeStore, persisted) = await PersistedAsync(store, interrupted.Id);
        var plan = persisted.Plans[^1];
        Assert.Equal(ConditionalStepState.Skipped, ConditionalSteps.StateOf(plan, persisted.Steps, 2, 1));
        Assert.Equal(2, PlannedStepPosition.Derive(plan, persisted.Steps, ManifestOf).Cursor);

        var follow = FollowTool();
        var model = new FakeChatModel(Call(Other), Final());
        await Runner(model, resumeStore, DiscoverTool(), follow, OtherTool()).ResumeAsync(persisted, Resumer);

        Assert.Equal([Other], Offered(model.Requests[0]));
        Assert.Equal(0, follow.ExecutionCount);
    }

    [Fact]
    public void T9b_CursorNotYetReached_ConditionIsPending()
    {
        var plan = PlanOf(0, Normal(0), Normal(1, Other), Conditional(2, source: 0));

        Assert.Equal(ConditionalStepState.Pending, ConditionalSteps.StateOf(plan, [Executed(0, 0, PathFact())], 1, 2));
        Assert.Equal(ConditionalStepState.Unconditional, ConditionalSteps.StateOf(plan, [], 0, 0));
    }

    [Fact]
    public void T10_FactOfRevisionN_CannotActivateRevisionNPlusOne()
    {
        var revisionOne = PlanOf(1, Normal(0), Conditional(1, source: 0));
        PlanStep[] steps = [Executed(0, 0, PathFact()), Executed(1, 0)];

        var position = PlannedStepPosition.Derive(revisionOne, steps, ManifestOf);

        Assert.Equal(2, position.Cursor);
        Assert.False(position.ReplanRequired);

        PlanStep[] ownFact = [Executed(1, 0, PathFact())];
        Assert.Equal(1, PlannedStepPosition.Derive(revisionOne, ownFact, ManifestOf).Cursor);
    }

    [Fact]
    public void T11_PlanWithoutActivation_KeepsPre2bBehaviourAndSerialization()
    {
        var plan = PlanOf(0, Normal(0), Normal(1, Other));
        PlanStep[] steps = [Executed(0, 0)];

        Assert.Equal(1, PlannedStepPosition.Derive(plan, steps, ManifestOf).Cursor);
        Assert.DoesNotContain("activation", JsonSerializer.Serialize(plan), StringComparison.OrdinalIgnoreCase);

        var roundTripped = JsonSerializer.Deserialize<AgentPlan>(
            JsonSerializer.Serialize(PlanOf(0, Conditional(1, 0, "target"))))!;
        Assert.Equal(new EvidenceFactExists(0, FactType, FactKey, "target"), roundTripped.Steps[0].Activation);
    }

    [Fact]
    public void ConditionPointingAtLaterStep_InPersistedPlan_FailsClosed()
    {
        var plan = PlanOf(0, Normal(0), Conditional(1, source: 1));

        var position = PlannedStepPosition.Derive(plan, [Executed(0, 0, PathFact())], ManifestOf);

        Assert.True(position.ReplanRequired);
    }

    private sealed class FactTool(string name, IReadOnlyList<ToolParameter> parameters, IReadOnlyList<EvidenceFact> facts) : ITool
    {
        public int ExecutionCount { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Read tool that may emit evidence facts.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = parameters,
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult(ToolCallResult.Success("done") with { Facts = facts });
        }
    }

    private sealed class FailingAfterModel(params ModelResponse[] responses) : IChatModel
    {
        private int calls;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            calls < responses.Length
                ? Task.FromResult(responses[calls++])
                : throw new ModelProtocolException("provider unavailable") { FailureKind = ModelFailureKind.Authentication };
    }
}
