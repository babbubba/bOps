// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// What the runner keeps about each call to a model (which model answered, how long, how many tokens, the exact bodies)
/// so a task can be troubleshot afterwards, and rule S3 for a model that answers with nothing.
/// </summary>
public sealed class ModelCallRecordingTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static AgentRunner CreateRunner(
        IChatModel model, IAuditSink audit, AgentRunnerOptions? options = null, TimeProvider? time = null, params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(),
            time ?? TimeProvider.System, NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());
    }

    private static ModelResponse Reply(
        string? text, bool isFinal = true, ModelUsage? usage = null, ModelCallDetails? details = null, ModelToolCall? call = null) =>
        new(text, call is null ? [] : [call], isFinal, usage) { Details = details };

    private static ModelCallDetails Details(string actual = "vendor/actual-model", string finish = "stop") =>
        new(actual, finish, """{"model":"fake-model","messages":[]}""", """{"model":"vendor/actual-model","choices":[]}""");

    private static PlanStep FinalStep(TaskState task) => Assert.Single(task.Steps);

    [Fact]
    public async Task RunAsync_RecordsTheModelTheTimeAndTheTokens_OfTheCallThatProducedAStep()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            Reply("All good.", usage: new ModelUsage(120, 45, null), details: Details()));

        var task = await CreateRunner(model, new RecordingAuditSink()).RunAsync("check", Actor);

        var call = Assert.Single(FinalStep(task).ModelCalls!);
        Assert.Equal("fake", call.Provider);
        Assert.Equal("fake-model", call.RequestedModel);
        Assert.Equal("vendor/actual-model", call.ActualModel);
        Assert.Equal(120, call.Usage!.PromptTokens);
        Assert.Equal(45, call.Usage.CompletionTokens);
        Assert.Equal("stop", call.FinishReason);
        Assert.Equal(ModelCallOutcome.Success, call.Outcome);
        Assert.Null(call.ErrorMessage);
        Assert.False(call.PayloadTruncated);
    }

    [Fact]
    public async Task RunAsync_KeepsTheExactRequestAndReplyBodies_OfEveryCall()
    {
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Reply("Done.", details: Details()));

        var task = await CreateRunner(model, new RecordingAuditSink()).RunAsync("check", Actor);

        var call = Assert.Single(FinalStep(task).ModelCalls!);
        Assert.Equal("""{"model":"fake-model","messages":[]}""", call.RequestJson);
        Assert.Equal("""{"model":"vendor/actual-model","choices":[]}""", call.ResponseJson);
    }

    [Fact]
    public async Task RunAsync_RecordsTheCallThatProducedThePlan_OnThePlan()
    {
        var plan = PlanningTestSupport.PlanResponse() with { Usage = new ModelUsage(30, 70, null), Details = Details("plan-model") };
        var model = new FakeChatModel(plan, Reply("Done."));

        var task = await CreateRunner(model, new RecordingAuditSink()).RunAsync("check", Actor);

        var call = Assert.Single(Assert.Single(task.Plans).ModelCalls!);
        Assert.Equal("plan-model", call.ActualModel);
        Assert.Equal(30, call.Usage!.PromptTokens);
        Assert.Equal(70, call.Usage.CompletionTokens);
    }

    [Fact]
    public async Task RunAsync_KeepsBothCalls_WhenTheFirstPlanReplyWasNotAPlan()
    {
        var model = new FakeChatModel(
            Reply("not json", details: Details("first")),
            PlanningTestSupport.PlanResponse() with { Details = Details("second") },
            Reply("Done."));

        var task = await CreateRunner(model, new RecordingAuditSink()).RunAsync("check", Actor);

        var calls = Assert.Single(task.Plans).ModelCalls!;
        Assert.Equal(["first", "second"], calls.Select(c => c.ActualModel));
    }

    [Fact]
    public async Task RunAsync_MeasuresHowLongTheModelTook_ForTheRecordAndForTheAudit()
    {
        var clock = new FakeTimeProvider(Start);
        var model = new SlowChatModel(clock, TimeSpan.FromMilliseconds(1500), PlanningTestSupport.PlanResponse(), Reply("Done.", details: Details()));
        var audit = new RecordingAuditSink();

        var task = await CreateRunner(model, audit, time: clock).RunAsync("check", Actor);

        var call = Assert.Single(FinalStep(task).ModelCalls!);
        Assert.Equal(1500, call.DurationMs);
        Assert.Equal(Start.AddMilliseconds(1500), call.StartedAtUtc);
        var audited = audit.Events.OfType<ModelCallAuditEvent>().Last();
        Assert.Equal(1500, audited.DurationMs);
        Assert.Equal("vendor/actual-model", audited.ActualModel);
        Assert.Equal("fake-model", audited.Model);
    }

    [Fact]
    public async Task RunAsync_RecordsTheCallOnAToolStep_NotJustOnTheFinalResponse()
    {
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            Reply(null, isFinal: false, usage: new ModelUsage(10, 5, null), details: Details("tool-model"), call: toolCall),
            Reply("Done."));

        var task = await CreateRunner(model, new RecordingAuditSink(), tools: new FakeReadTool()).RunAsync("check", Actor);

        var toolStep = task.Steps[0];
        Assert.Equal("test.read", toolStep.Description);
        var call = Assert.Single(toolStep.ModelCalls!);
        Assert.Equal("tool-model", call.ActualModel);
        Assert.Equal(10, call.Usage!.PromptTokens);
    }

    [Fact]
    public async Task RunAsync_AsksAgain_WhenTheModelEndsWithNoTextAndNoToolCall()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            Reply(null, usage: new ModelUsage(100, 700, null), details: Details("empty-model", "stop")),
            Reply("Here is the answer.", usage: new ModelUsage(110, 20, null)));

        var task = await CreateRunner(model, new RecordingAuditSink()).RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        var step = FinalStep(task);
        Assert.Equal("Here is the answer.", step.Observation);
        Assert.Equal(2, step.ModelCalls!.Count);
        Assert.Equal("empty-model", step.ModelCalls[0].ActualModel);
    }

    [Fact]
    public async Task RunAsync_TellsTheModelItsReplyWasEmpty_WithoutKeepingTheEmptyTurnInTheConversation()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Reply(null), Reply("Here is the answer."));

        await CreateRunner(model, new RecordingAuditSink()).RunAsync("check", Actor);

        var retry = model.Requests[^1];
        Assert.Equal(ChatRole.User, retry.History[^1].Role);
        Assert.Contains("empty", retry.History[^1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(retry.History, turn => turn.Role == ChatRole.Assistant);
        Assert.Single(model.Requests[^2].History);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public async Task RunAsync_FailsTheTask_WhenTheModelStaysEmpty_NeverCompletingItWithNothing(string? text)
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            Reply(text, usage: new ModelUsage(100, 788, null), details: Details("router/picked", "stop")),
            Reply(text, usage: new ModelUsage(100, 0, null), details: Details("router/picked", "stop")));

        var task = await CreateRunner(model, new RecordingAuditSink()).RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        var step = FinalStep(task);
        Assert.Equal("Model protocol failure", step.Description);
        Assert.Contains("empty final response", step.Observation, StringComparison.Ordinal);
        Assert.Contains("2 attempt(s)", step.Observation, StringComparison.Ordinal);
        Assert.Contains("fake-model served by router/picked", step.Observation, StringComparison.Ordinal);
        Assert.Contains("finish reason 'stop'", step.Observation, StringComparison.Ordinal);
        Assert.Equal(2, step.ModelCalls!.Count);
    }

    [Fact]
    public async Task RunAsync_FailsAtTheFirstEmptyReply_WhenNoRetryIsConfigured()
    {
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Reply(null));
        var options = new AgentRunnerOptions { EmptyFinalResponseRetries = 0 };

        var task = await CreateRunner(model, new RecordingAuditSink(), options).RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(2, model.Requests.Count); // the plan and the one step call
        Assert.Single(FinalStep(task).ModelCalls!);
    }

    [Fact]
    public async Task RunAsync_ContinuesAsATool_WhenTheRetryAfterAnEmptyReplyAsksForATool()
    {
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            Reply(null),
            Reply(null, isFinal: false, call: toolCall),
            Reply("Done."));

        var task = await CreateRunner(model, new RecordingAuditSink(), tools: new FakeReadTool()).RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(2, task.Steps.Count);
        Assert.Equal(2, task.Steps[0].ModelCalls!.Count);
        Assert.Single(task.Steps[1].ModelCalls!);
    }

    [Fact]
    public async Task RunAsync_CountsTheTokensOfTheDiscardedEmptyReply_AgainstTheBudget()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            Reply(null, usage: new ModelUsage(400, 600, null)),
            Reply(null, isFinal: false, usage: new ModelUsage(10, 10, null), call: new ModelToolCall("call-1", "test.read", ToolArguments.Empty)));
        var options = new AgentRunnerOptions { MaxTotalTokens = 1000 };

        var task = await CreateRunner(model, new RecordingAuditSink(), options, tools: new FakeReadTool()).RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.BudgetExceeded, task.Status);
    }

    [Fact]
    public async Task RunAsync_CutsALongBody_AndSaysSo()
    {
        var details = new ModelCallDetails("actual", "stop", new string('q', 50), new string('r', 8));
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Reply("Done.", details: details));
        var options = new AgentRunnerOptions { MaxModelPayloadCharacters = 20 };

        var task = await CreateRunner(model, new RecordingAuditSink(), options).RunAsync("check", Actor);

        var call = Assert.Single(FinalStep(task).ModelCalls!);
        Assert.True(call.PayloadTruncated);
        Assert.Equal(new string('q', 20) + "…[truncated 30 characters]", call.RequestJson);
        Assert.Equal(new string('r', 8), call.ResponseJson);
    }

    [Fact]
    public async Task RunAsync_DoesNotSplitASurrogatePair_WhenItCutsABody()
    {
        var details = new ModelCallDetails("actual", "stop", "ab😀cd", null);
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Reply("Done.", details: details));
        var options = new AgentRunnerOptions { MaxModelPayloadCharacters = 3 };

        var task = await CreateRunner(model, new RecordingAuditSink(), options).RunAsync("check", Actor);

        var request = Assert.Single(FinalStep(task).ModelCalls!).RequestJson!;
        Assert.StartsWith("ab…[truncated", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_KeepsNoBodies_WhenTheLimitIsZero_ButStillRecordsTheModelTimeAndTokens()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Reply("Done.", usage: new ModelUsage(7, 3, null), details: Details()));
        var options = new AgentRunnerOptions { MaxModelPayloadCharacters = 0 };

        var task = await CreateRunner(model, new RecordingAuditSink(), options).RunAsync("check", Actor);

        var call = Assert.Single(FinalStep(task).ModelCalls!);
        Assert.Null(call.RequestJson);
        Assert.Null(call.ResponseJson);
        Assert.False(call.PayloadTruncated);
        Assert.Equal("vendor/actual-model", call.ActualModel);
        Assert.Equal(7, call.Usage!.PromptTokens);
    }

    [Fact]
    public async Task RunAsync_RecordsAFailedCall_WithTheBodiesTheAdapterHad()
    {
        var failure = new ModelProtocolException("Provider 'fake' returned HTTP 500 (InternalServerError) for the chat completion request.")
        {
            Details = new ModelCallDetails(null, null, """{"sent":true}""", """{"error":"boom"}"""),
        };
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse());
        var audit = new RecordingAuditSink();
        var runner = CreateRunner(new FailingAfterModel(model, 1, failure), audit);

        var task = await runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        var step = FinalStep(task);
        var call = Assert.Single(step.ModelCalls!);
        Assert.Equal(ModelCallOutcome.Failure, call.Outcome);
        Assert.Contains("HTTP 500", call.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("""{"error":"boom"}""", call.ResponseJson);
        Assert.Equal("""{"sent":true}""", call.RequestJson);
        Assert.Null(call.Usage);
        Assert.Contains(audit.Events.OfType<ModelCallAuditEvent>(), e => e.Outcome == ModelCallOutcome.Failure && e.DurationMs is not null);
    }

    [Fact]
    public async Task RunAsync_RecordsAFailedCall_WhenTheFailureCarriesNoBodies()
    {
        var model = new FailingAfterModel(new FakeChatModel(PlanningTestSupport.PlanResponse()), 1, new InvalidOperationException("transport"));

        var task = await CreateRunner(model, new RecordingAuditSink()).RunAsync("check", Actor);

        var call = Assert.Single(FinalStep(task).ModelCalls!);
        Assert.Equal(ModelCallOutcome.Failure, call.Outcome);
        Assert.Equal("transport", call.ErrorMessage);
        Assert.Null(call.RequestJson);
    }

    /// <summary>A model that takes a fixed time to answer, by moving the fake clock.</summary>
    private sealed class SlowChatModel(FakeTimeProvider clock, TimeSpan delay, params ModelResponse[] responses) : IChatModel
    {
        private readonly FakeChatModel _inner = new(responses);

        public ChatModelDescriptor Descriptor => _inner.Descriptor;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            clock.Advance(delay);
            return _inner.CompleteAsync(request, ct);
        }
    }

    /// <summary>Answers like <paramref name="inner"/> for <paramref name="succeedingCalls"/> calls, then throws.</summary>
    private sealed class FailingAfterModel(FakeChatModel inner, int succeedingCalls, Exception failure) : IChatModel
    {
        private int _calls;

        public ChatModelDescriptor Descriptor => inner.Descriptor;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            _calls++ < succeedingCalls ? inner.CompleteAsync(request, ct) : throw failure;
    }
}
