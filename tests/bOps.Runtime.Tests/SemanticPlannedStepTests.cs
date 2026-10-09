// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>PRE-2B / ADR-0050 semantic planned-step identity, correction budgets and persistence.</summary>
public sealed class SemanticPlannedStepTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly ActorIdentity Resumer = new("api-user", "resumer", "Resumer");
    private const string Events = "diagnostic.events";
    private const string Other = "diagnostic.other";
    private const string SemanticCorrection = "arguments did not match the current planned step";
    private const string ArgumentCorrection = "failed argument validation and did not run";

    private static RecordingReadTool EventsTool() => new(Events,
    [
        new ToolParameter("topic", ToolParameterType.String, "Diagnostic topic."),
        new ToolParameter("limit", ToolParameterType.Integer, "Maximum rows.", Required: false) { Minimum = 1, Maximum = 10 },
        new ToolParameter("enabled", ToolParameterType.Boolean, "Whether enabled.", Required: false),
    ]);

    private static RecordingReadTool OtherTool() => new(Other, []);

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

    private static ModelResponse Plan(params (string Tool, JsonObject? Expected)[] entries)
    {
        var steps = new JsonArray();
        for (var i = 0; i < entries.Length; i++)
        {
            var step = new JsonObject
            {
                ["description"] = $"step {i}",
                ["expectedTool"] = entries[i].Tool,
            };
            if (entries[i].Expected is { } expected)
            {
                step["expectedArguments"] = expected.DeepClone();
            }

            steps.Add(step);
        }

        return new ModelResponse(new JsonObject { ["rationale"] = "test", ["steps"] = steps }.ToJsonString(), [], false, null);
    }

    private static ModelResponse Call(string tool, JsonObject arguments) =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), tool, ToolArguments.FromJson(arguments))], false, null);

    private static ModelResponse Final() => new("done", [], true, null);

    private static JsonObject Topic(string value) => new() { ["topic"] = value };

    private static IEnumerable<string> Offered(ModelRequest request) => request.AvailableTools.Select(tool => tool.Name);

    private static async Task<(InMemoryTaskStore Store, TaskState Task)> PersistedAsync(InMemoryTaskStore store, Guid taskId)
    {
        var stored = await store.LoadAsync(taskId);
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(stored))!;
        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        return (fresh, reloaded);
    }

    [Fact]
    public async Task T1_CorrectSemanticCall_ExecutesAndConsumesTheStep()
    {
        var tool = EventsTool();
        var model = new FakeChatModel(Plan((Events, Topic("B"))), Call(Events, new JsonObject { ["topic"] = "B", ["limit"] = 3 }), Final());

        var result = await Runner(model, new InMemoryTaskStore(), tool).RunAsync("diagnose", Actor);

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(PlannedStepExecutionClassification.Matched, result.Steps[0].ExecutionClassification);
        Assert.Equal(0, result.Steps[0].PlannedStepIndex);
        Assert.Empty(model.Requests[2].AvailableTools);
    }

    [Fact]
    public async Task T2_WrongSemanticArguments_DoNotExecuteOrAdvance_AndGetOneCorrection()
    {
        var tool = EventsTool();
        var model = new FakeChatModel(
            Plan((Events, Topic("B"))), Call(Events, Topic("C")), Call(Events, Topic("B")), Final());

        var result = await Runner(model, new InMemoryTaskStore(), tool).RunAsync("diagnose", Actor);

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(PlannedStepExecutionClassification.SemanticMismatch, result.Steps[0].ExecutionClassification);
        Assert.Equal(PlannedStepExecutionClassification.Matched, result.Steps[1].ExecutionClassification);
        Assert.Equal([0, 0], result.Steps.Take(2).Select(step => step.PlannedStepIndex));
        Assert.Equal([Events], Offered(model.Requests[2]));
        Assert.Contains(SemanticCorrection, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T3_SecondSemanticMismatch_ReplansWithoutExecuting()
    {
        var tool = EventsTool();
        var model = new FakeChatModel(
            Plan((Events, Topic("B"))), Call(Events, Topic("C")), Call(Events, Topic("A")), Plan(), Final());

        var result = await Runner(model, new InMemoryTaskStore(), tool).RunAsync("diagnose", Actor);

        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal(2, result.Steps.Count(step => step.ExecutionClassification == PlannedStepExecutionClassification.SemanticMismatch));
        Assert.Empty(model.Requests[3].AvailableTools);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
    }

    [Fact]
    public async Task T4_WrongTool_ReplansImmediately_WithoutSpendingSemanticCorrection()
    {
        var tool = EventsTool();
        var model = new FakeChatModel(Plan((Events, Topic("B"))), Call(Other, new JsonObject()), Plan(), Final());

        var result = await Runner(model, new InMemoryTaskStore(), tool, OtherTool()).RunAsync("diagnose", Actor);

        Assert.Equal(0, tool.ExecutionCount);
        Assert.Null(result.Steps[0].ExecutionClassification);
        Assert.Empty(model.Requests[2].AvailableTools);
        Assert.DoesNotContain(SemanticCorrection, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
    }

    [Fact]
    public async Task T5_SemanticMatchThenManifestFailure_UsesArgumentCorrectionOnly()
    {
        var tool = EventsTool();
        var invalid = new JsonObject { ["topic"] = "B", ["unknown"] = true };
        var model = new FakeChatModel(Plan((Events, Topic("B"))), Call(Events, invalid), Call(Events, Topic("B")), Final());

        var result = await Runner(model, new InMemoryTaskStore(), tool).RunAsync("diagnose", Actor);

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(PlannedStepExecutionClassification.ArgumentValidationFailure, result.Steps[0].ExecutionClassification);
        Assert.Contains(ArgumentCorrection, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SemanticCorrection, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T6_SemanticAndArgumentCorrections_HaveIndependentBudgets()
    {
        var tool = EventsTool();
        var manifestInvalid = new JsonObject { ["topic"] = "B", ["unknown"] = true };
        var model = new FakeChatModel(
            Plan((Events, Topic("B"))), Call(Events, Topic("C")), Call(Events, manifestInvalid),
            Call(Events, Topic("B")), Final());

        var result = await Runner(model, new InMemoryTaskStore(), tool).RunAsync("diagnose", Actor);

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(
            [PlannedStepExecutionClassification.SemanticMismatch, PlannedStepExecutionClassification.ArgumentValidationFailure, PlannedStepExecutionClassification.Matched],
            result.Steps.Take(3).Select(step => step.ExecutionClassification));
        Assert.Contains(SemanticCorrection, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(ArgumentCorrection, model.Requests[3].SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SemanticCorrection, model.Requests[3].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(0, result.Accounting!.LifetimeReplans);
    }

    [Fact]
    public async Task T7_RepeatedSameToolSteps_CannotConsumeEachOther()
    {
        var tool = EventsTool();
        var model = new FakeChatModel(
            Plan((Events, Topic("A")), (Events, Topic("B")), (Events, Topic("C"))),
            Call(Events, Topic("A")), Call(Events, Topic("C")), Call(Events, Topic("B")),
            Call(Events, Topic("C")), Final());

        var result = await Runner(model, new InMemoryTaskStore(), tool).RunAsync("diagnose", Actor);

        Assert.Equal(3, tool.ExecutionCount);
        Assert.Equal([0, 1, 1, 2], result.Steps.Take(4).Select(step => step.PlannedStepIndex));
        Assert.Equal(PlannedStepExecutionClassification.SemanticMismatch, result.Steps[1].ExecutionClassification);
        Assert.Equal(PlannedStepExecutionClassification.Matched, result.Steps[2].ExecutionClassification);
    }

    [Fact]
    public async Task T8_ResumeAfterSemanticCorrection_RemembersSpentBudget()
    {
        var store = new InMemoryTaskStore();
        var interrupted = await Runner(
                new FailingAfterModel(Plan((Events, Topic("B"))), Call(Events, Topic("C"))), store, EventsTool())
            .RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);

        var (resumeStore, persisted) = await PersistedAsync(store, interrupted.Id);
        var tool = EventsTool();
        var model = new FakeChatModel(Call(Events, Topic("A")), Plan(), Final());

        var result = await Runner(model, resumeStore, tool).ResumeAsync(persisted, Resumer);

        Assert.Contains(SemanticCorrection, model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Empty(model.Requests[1].AvailableTools);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
    }

    [Fact]
    public void T9_LegacyPlan_UsesExpectedToolOnly()
    {
        var plan = new AgentPlan(0, "legacy", [new PlannedStep(0, "read B", Events)]);
        var legacyCall = new PlanStep(0, Events, new ModelToolCall("call", Events, ToolArguments.FromJson(Topic("C"))),
            ToolCallResult.Success("ok"), "ok", 0);

        var position = PlannedStepPosition.Derive(plan, [legacyCall]);

        Assert.Null(plan.SemanticContractVersion);
        Assert.Equal(1, position.Cursor);
        Assert.False(position.SemanticCorrectionSpent);
        Assert.False(position.ReplanRequired);
    }

    public static TheoryData<JsonObject> InvalidConstraints => new()
    {
        new JsonObject { ["missing"] = "B" },
        new JsonObject { ["limit"] = "1" },
        new JsonObject { ["limit"] = 11 },
    };

    [Theory]
    [MemberData(nameof(InvalidConstraints))]
    public async Task T10_InvalidPlannedConstraint_IsRejectedDuringPlanAcceptance(JsonObject invalid)
    {
        var tool = EventsTool();
        var model = new FakeChatModel(Plan((Events, invalid)), Plan((Events, Topic("B"))), Call(Events, Topic("B")), Final());

        var result = await Runner(model, new InMemoryTaskStore(), tool).RunAsync("diagnose", Actor);

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(2, result.Plans[0].ModelCalls!.Count);
        Assert.Equal(1, result.Plans[0].SemanticContractVersion);
        Assert.Equal("B", result.Plans[0].Steps[0].ExpectedArguments!.ToJson()["topic"]!.GetValue<string>());
        Assert.Contains("topic:String required", model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("limit:Integer optional", model.Requests[0].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedSameToolPlan_WithoutPairwiseDiscriminators_IsRejected()
    {
        var tool = EventsTool();
        var unusable = Plan((Events, Topic("B")), (Events, Topic("B")));
        var model = new FakeChatModel(unusable, Plan((Events, Topic("B"))), Call(Events, Topic("B")), Final());

        var result = await Runner(model, new InMemoryTaskStore(), tool).RunAsync("diagnose", Actor);

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(2, result.Plans[0].ModelCalls!.Count);
        Assert.Single(result.Plans[0].Steps);
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
