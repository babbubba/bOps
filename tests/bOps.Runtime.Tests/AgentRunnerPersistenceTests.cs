// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// V0.7 (ADR-0017): every step is persisted through <see cref="ITaskStore"/> as it happens. Resume is
/// the ADR-0040 state machine, covered by <see cref="ResumeStateMachineTests"/>.
/// </summary>
public sealed class AgentRunnerPersistenceTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private static AgentRunner CreateRunner(
        IChatModel model, IToolRegistry registry, ITaskStore taskStore, AgentRunnerOptions? options = null) =>
        new(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            new RecordingAuditSink(), taskStore, TimeProvider.System, NullLogger<AgentRunner>.Instance,
            options ?? new AgentRunnerOptions());

    private static ToolRegistry CreateRegistryWith(params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return registry;
    }

    [Fact]
    public async Task RunAsync_SavesRunningStateAfterEveryStep_AndTerminalStateAtTheEnd()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [new ModelToolCall("call-1", "test.read", ToolArguments.Empty)], false, null),
            new ModelResponse("done", [], true, null));
        var registry = CreateRegistryWith(new FakeReadTool());
        var taskStore = new InMemoryTaskStore();

        var result = await CreateRunner(model, registry, taskStore).RunAsync("check things", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.True(taskStore.Saves.Count >= 3, "expected at least the initial-plan save, the one-step save, and the final save");
        Assert.All(taskStore.Saves[..^1], saved => Assert.Equal(AgentTaskStatus.Running, saved.Status));
        Assert.Equal(AgentTaskStatus.Completed, taskStore.Saves[^1].Status);

        var loaded = await taskStore.LoadAsync(result.Id);
        Assert.NotNull(loaded);
        Assert.Equal(AgentTaskStatus.Completed, loaded!.Status);
    }

    [Fact]
    public async Task RunAsync_LeavesTaskStoredAsRunning_WhenCancelledMidTask()
    {
        using var cts = new CancellationTokenSource();
        var model = new CancelAfterCallChatModel(
            [
                PlanningTestSupport.PlanResponse(),
                new ModelResponse(null, [new ModelToolCall("call-1", "test.read", ToolArguments.Empty)], false, null),
            ],
            cts, cancelAfterCallCount: 2);
        var registry = CreateRegistryWith(new FakeReadTool());
        var taskStore = new InMemoryTaskStore();

        // The token is cancelled right after the one step's model response is returned — so that
        // step still executes and is saved as Running, and the *next* loop iteration is what
        // observes the cancellation, exactly like an operator's Ctrl+C or a crash mid-task: the
        // task is left durable up to its last completed step, never returned as a finished
        // TaskState (rule: "the returned state is never built for a genuinely cancelled run").
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateRunner(model, registry, taskStore).RunAsync("check things", Actor, ct: cts.Token));

        var running = await taskStore.ListByStatusAsync(AgentTaskStatus.Running);
        Assert.Single(running);
        Assert.Single(running[0].Steps);
    }

    /// <summary>Replays responses like <see cref="FakeChatModel"/>, but cancels a shared token after a chosen call — used to make mid-loop cancellation deterministic.</summary>
    private sealed class CancelAfterCallChatModel(ModelResponse[] responses, CancellationTokenSource cts, int cancelAfterCallCount) : IChatModel
    {
        private int _callCount;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var response = responses[_callCount];
            _callCount++;
            if (_callCount == cancelAfterCallCount)
            {
                await cts.CancelAsync();
            }

            return response;
        }
    }
}
