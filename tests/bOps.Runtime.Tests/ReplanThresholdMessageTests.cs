// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0049: the first runtime-authored system message. <c>agent.replan.threshold</c> (Warning, <c>runtime/agent</c>) is written when an
/// accepted replan brings the task's lifetime count to the threshold — once per task, never for an uncommitted attempt, never fatal.
/// </summary>
public sealed class ReplanThresholdMessageTests
{
    private const string SecretGoal = "GOAL-SENTINEL rotate the production credentials";
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly ActorIdentity Resumer = new("api-user", "resumer", "Resumer");

    private static AgentRunnerOptions Options(int? threshold = null) => new()
    {
        DefaultToolTimeout = TimeSpan.FromMilliseconds(40),
        MaxReplans = 10,
        MaxLifetimeReplans = 12,
        ReplanWarningThreshold = threshold,
    };

    private static AgentRunner Runner(IChatModel model, ISystemMessageStore? messages, AgentRunnerOptions options, ITaskStore? store = null)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), new HangingTool());
        return new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), new RecordingAuditSink(),
            store ?? new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance, options, systemMessages: messages);
    }

    private static ModelResponse Hang() =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), "test.hangs", ToolArguments.Empty)], false, null);

    /// <summary>A model that is replanned exactly <paramref name="replans"/> times (each tool call times out) and then completes.</summary>
    private static FakeChatModel ReplanningModel(int replans) => new([.. ReplanningModelScript(replans)]);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BelowTheThreshold_NoMessageIsWritten(int replans)
    {
        var messages = new RecordingSystemMessages();

        var result = await Runner(ReplanningModel(replans), messages, Options()).RunAsync(SecretGoal, Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Empty(messages.Items);
    }

    [Fact]
    public async Task ExactlyAtTheDefaultThreshold_OneWarningIsWritten_WithSafeStructuredFacts()
    {
        var messages = new RecordingSystemMessages();
        var taskId = Guid.NewGuid();

        var result = await Runner(ReplanningModel(3), messages, Options()).RunAsync(SecretGoal, Actor, taskId);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        var message = Assert.Single(messages.Items);
        Assert.Equal(SystemMessageSeverity.Warning, message.Severity);
        Assert.Equal("agent.replan.threshold", message.Code);
        Assert.Equal("runtime/agent", message.Source);
        Assert.Equal(taskId, message.TaskId);
        Assert.Equal(NodeId.Local, message.Node);
        Assert.Equal(SystemComponentType.Runtime, message.ComponentType);
        var json = message.Metadata.ToJson();
        Assert.Equal(taskId.ToString(), json["taskId"]!.GetValue<string>());
        Assert.Equal("fake", json["provider"]!.GetValue<string>());
        Assert.Equal("fake-model", json["model"]!.GetValue<string>());
        Assert.Equal(3, json["lifetimeReplans"]!.GetValue<int>());
        Assert.Equal(3, json["replanWarningThreshold"]!.GetValue<int>());
        Assert.Equal(1, json["executionAttempt"]!.GetValue<int>());
        message.Validate();
    }

    [Fact]
    public async Task LaterReplans_DoNotRepeatTheWarning()
    {
        var messages = new RecordingSystemMessages();

        var result = await Runner(ReplanningModel(6), messages, Options()).RunAsync(SecretGoal, Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(messages.Items);
    }

    [Fact]
    public async Task AConfiguredThreshold_IsHonoured()
    {
        var messages = new RecordingSystemMessages();

        await Runner(ReplanningModel(2), messages, Options(threshold: 2)).RunAsync(SecretGoal, Actor);

        var message = Assert.Single(messages.Items);
        Assert.Equal(2, message.Metadata.ToJson()["lifetimeReplans"]!.GetValue<int>());
    }

    [Fact]
    public async Task AMalformedReplan_ThatIsNotCommitted_DoesNotCount()
    {
        var messages = new RecordingSystemMessages();
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.hangs"),
            Hang(),
            new ModelResponse("this is not a plan", [], true, null),
            new ModelResponse("still not a plan", [], true, null));

        var result = await Runner(model, messages, Options(threshold: 1)).RunAsync(SecretGoal, Actor);

        Assert.NotEqual(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.Empty(messages.Items);
    }

    [Fact]
    public async Task AResumeThatIsAlreadyPastTheThreshold_WritesNoDuplicate()
    {
        var messages = new RecordingSystemMessages();
        var store = new InMemoryTaskStore();
        var stored = Stored(accountedReplans: 5);
        store.Seed(stored);

        var result = await Runner(
            new FakeChatModel(Hang(), PlanningTestSupport.PlanResponse(revision: 1, expectedTool: "test.hangs"), new ModelResponse("done", [], true, null)),
            messages, Options(), store).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(6, result.Accounting!.LifetimeReplans);
        Assert.Empty(messages.Items);
    }

    [Fact]
    public async Task AResumeThatReachesTheThreshold_WritesTheWarningOnce()
    {
        var messages = new RecordingSystemMessages();
        var store = new InMemoryTaskStore();
        var stored = Stored(accountedReplans: 2);
        store.Seed(stored);

        var result = await Runner(
            new FakeChatModel(
                Hang(), PlanningTestSupport.PlanResponse(revision: 1, expectedTool: "test.hangs"),
                Hang(), PlanningTestSupport.PlanResponse(revision: 2, expectedTool: "test.hangs"),
                new ModelResponse("done", [], true, null)),
            messages, Options(), store).ResumeAsync(stored, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        var message = Assert.Single(messages.Items);
        Assert.Equal(2, message.Metadata.ToJson()["executionAttempt"]!.GetValue<int>());
        Assert.Equal(3, message.Metadata.ToJson()["lifetimeReplans"]!.GetValue<int>());
    }

    [Fact]
    public async Task APersistenceFailure_IsLogged_AndNeverFailsOrChangesTheTask()
    {
        var withStore = new FakeChatModel([.. ReplanningModelScript(3)]);
        var withoutStore = new FakeChatModel([.. ReplanningModelScript(3)]);
        var messages = new ThrowingSystemMessages();

        var failing = await Runner(withStore, messages, Options()).RunAsync(SecretGoal, Actor);
        var baseline = await Runner(withoutStore, null, Options()).RunAsync(SecretGoal, Actor);

        Assert.Equal(1, messages.Attempts);
        Assert.Equal(AgentTaskStatus.Completed, failing.Status);
        Assert.Equal(baseline.Status, failing.Status);
        Assert.Equal(baseline.Plans.Count, failing.Plans.Count);
        Assert.Equal(baseline.Steps.Count, failing.Steps.Count);
        Assert.Equal(withoutStore.Requests.Count, withStore.Requests.Count); // no extra model call, no retry
    }

    [Fact]
    public async Task TheMessage_NeverCarriesThePromptTheModelReplyOrToolEvidence()
    {
        var messages = new RecordingSystemMessages();
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.hangs"),
            new ModelResponse("MODEL-REPLY-SENTINEL", [new ModelToolCall("c1", "test.hangs", ToolArguments.Empty)], false, null),
            PlanningTestSupport.PlanResponse(revision: 1, rationale: "RATIONALE-SENTINEL", expectedTool: "test.hangs"),
            Hang(), PlanningTestSupport.PlanResponse(revision: 2, expectedTool: "test.hangs"),
            Hang(), PlanningTestSupport.PlanResponse(revision: 3, expectedTool: "test.hangs"),
            new ModelResponse("done", [], true, null));

        await Runner(model, messages, Options()).RunAsync(SecretGoal, Actor);

        var text = System.Text.Json.JsonSerializer.Serialize(Assert.Single(messages.Items));
        foreach (var sentinel in new[] { "GOAL-SENTINEL", "credentials", "MODEL-REPLY-SENTINEL", "RATIONALE-SENTINEL" })
        {
            Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheThresholdOption_IsValidated_AndTheDefaultNeverBreaksAnExistingConfiguration()
    {
        Assert.Equal(3, new AgentRunnerOptions().EffectiveReplanWarningThreshold);
        Assert.Equal(2, new AgentRunnerOptions { MaxReplans = 1, MaxLifetimeReplans = 2 }.EffectiveReplanWarningThreshold);
        new AgentRunnerOptions { MaxReplans = 1, MaxLifetimeReplans = 2 }.Validate();
        new AgentRunnerOptions { ReplanWarningThreshold = 12 }.Validate();

        Assert.Throws<InvalidOperationException>(() => new AgentRunnerOptions { ReplanWarningThreshold = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AgentRunnerOptions { ReplanWarningThreshold = 13 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AgentRunnerOptions { ReplanWarningThreshold = -1 }.Validate());
    }

    private static List<ModelResponse> ReplanningModelScript(int replans)
    {
        var script = new List<ModelResponse> { PlanningTestSupport.PlanResponse(expectedTool: "test.hangs") };
        for (var i = 0; i < replans; i++)
        {
            script.Add(Hang());
            script.Add(PlanningTestSupport.PlanResponse(revision: 1 + i, expectedTool: "test.hangs"));
        }

        script.Add(new ModelResponse("done", [], true, null));
        return script;
    }

    private static TaskState Stored(int accountedReplans) => new(
        Guid.NewGuid(), NodeId.Local, SecretGoal, AgentTaskStatus.Failed,
        [new PlanStep(0, "Model protocol failure", null, null, "The provider failed.")],
        [new AgentPlan(0, "plan 0", [.. Enumerable.Range(0, 100).Select(index => new PlannedStep(index, $"step {index}", "test.hangs"))])],
        DateTimeOffset.UtcNow)
    {
        ExecutionAttempt = 1,
        Origin = TaskOrigin.Ordinary,
        Accounting = new TaskAccounting(100, 0, accountedReplans),
        MutationJournalMode = TaskMutationJournalMode.Journaled,
    };

    private sealed class RecordingSystemMessages : ISystemMessageStore
    {
        private readonly List<SystemMessage> _items = [];

        public IReadOnlyList<SystemMessage> Items
        {
            get
            {
                lock (_items)
                {
                    return [.. _items];
                }
            }
        }

        public Task AppendAsync(SystemMessage message, CancellationToken ct = default)
        {
            message.Validate();
            lock (_items)
            {
                _items.Add(message);
            }

            return Task.CompletedTask;
        }

        public Task<SystemMessagePage> QueryAsync(SystemMessageQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingSystemMessages : ISystemMessageStore
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task AppendAsync(SystemMessage message, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            throw new IOException("database is locked");
        }

        public Task<SystemMessagePage> QueryAsync(SystemMessageQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
