// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Api.Tests;

/// <summary>
/// HARDEN-2 / ADR-0039 §9: the launcher is the final containment boundary. Whatever escapes the runner turns a task still
/// persisted <see cref="AgentTaskStatus.Running"/> into <see cref="AgentTaskStatus.Failed"/>, audited by a narrow
/// <see cref="TaskExecutionFaultAuditEvent"/>; an already-terminal task is never overwritten; an operator's cancellation
/// still ends <see cref="AgentTaskStatus.Cancelled"/>.
/// </summary>
public sealed class AgentTaskLauncherBackstopTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private sealed class Rig : IDisposable
    {
        public required AgentTaskLauncher Launcher { get; init; }
        public required ScriptedStore Store { get; init; }
        public required ListAuditSink Audit { get; init; }

        public void Dispose() => Launcher.Dispose();

        public async Task<TaskState> SettledAsync(Guid taskId, Func<TaskState, bool> settled)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await Store.LoadAsync(taskId) is { } state && settled(state))
                {
                    // Let the detached run finish its own bookkeeping (audit, capacity release) after the save.
                    await Task.Delay(50);
                    return (await Store.LoadAsync(taskId))!;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Task {taskId} did not settle.");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static Rig Create(IChatModel model, ScriptedStore store)
    {
        var audit = new ListAuditSink();
        var runner = new AgentRunner(
            model, new ToolRegistry(new AllAvailable()), new FixedPolicyEngine(PolicyMode.Automatic), new NoApprovals(), audit, store,
            TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());
        var launcher = new AgentTaskLauncher(
            runner, store, TimeProvider.System, new AgentTaskLauncherOptions(), NullLogger<AgentTaskLauncher>.Instance);
        return new Rig { Launcher = launcher, Store = store, Audit = audit };
    }

    // H2-20, H2-21, H2-26
    [Fact]
    public async Task AnExceptionEscapingTheRunner_FailsTheStillRunningTask_AndAuditsTheContainment()
    {
        // Save 1 is the launcher's initial Running; save 2 is the runner's first Running save after planning, outside every
        // containment the runner has — a runtime defect by construction.
        var store = new ScriptedStore { FailSave = n => n == 2 ? new InvalidOperationException("disk said no: api_key=abc123secret") : null };
        using var rig = Create(new QueueChatModel(QueueChatModel.PlanResponse()), store);

        var taskId = (await rig.Launcher.TryStartAsync("check", Actor, CancellationToken.None))!.Value;
        var task = await rig.SettledAsync(taskId, state => state.Status != AgentTaskStatus.Running);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        var step = Assert.Single(task.Steps);
        Assert.Equal("Unexpected runtime failure", step.Description);
        Assert.StartsWith("unexpected runtime failure: InvalidOperationException: disk said no", step.Observation, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123secret", step.Observation, StringComparison.Ordinal);

        var fault = Assert.Single(rig.Audit.Events.OfType<TaskExecutionFaultAuditEvent>());
        Assert.Equal((taskId, 0, "InvalidOperationException", step.Observation), (fault.TaskId, fault.StepIndex, fault.ExceptionType, fault.Reason));
        Assert.Equal(Actor, fault.Actor);
    }

    [Fact]
    public async Task ACancellationTheLauncherDidNotRequest_IsContainedAsAFailure_NotACancellation()
    {
        var store = new ScriptedStore { FailSave = n => n == 2 ? new OperationCanceledException("not ours") : null };
        using var rig = Create(new QueueChatModel(QueueChatModel.PlanResponse()), store);

        var taskId = (await rig.Launcher.TryStartAsync("check", Actor, CancellationToken.None))!.Value;
        var task = await rig.SettledAsync(taskId, state => state.Status != AgentTaskStatus.Running);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal("OperationCanceledException", Assert.Single(rig.Audit.Events.OfType<TaskExecutionFaultAuditEvent>()).ExceptionType);
    }

    // H2-22: a resume that throws before doing anything leaves a terminal task exactly as it was, and is not audited as a
    // containment transition.
    [Theory]
    [InlineData(AgentTaskStatus.Failed)]
    [InlineData(AgentTaskStatus.Completed)]
    [InlineData(AgentTaskStatus.MaxStepsReached)]
    public async Task AnEscapedFailure_NeverOverwritesATerminalState(AgentTaskStatus terminal)
    {
        var store = new ScriptedStore();
        var existing = new TaskState(Guid.NewGuid(), NodeId.Local, "old goal", terminal, [], [], DateTimeOffset.UtcNow);
        await store.SaveAsync(existing);
        using var rig = Create(new QueueChatModel(), store);

        // No recorded plan: ResumeAsync throws InvalidOperationException inside the detached run.
        Assert.True(await rig.Launcher.TryResumeAsync(existing, Actor, CancellationToken.None));
        // The backstop re-reads the state before deciding; wait for that read, then for its (absent) write.
        await WaitUntilAsync(() => store.LoadCount > 0);
        await Task.Delay(50);

        var after = (await store.LoadAsync(existing.Id))!;
        Assert.Equal(terminal, after.Status);
        Assert.Empty(after.Steps);
        Assert.Equal(1, store.SaveCount);
        Assert.Empty(rig.Audit.Events.OfType<TaskExecutionFaultAuditEvent>());
    }

    // H2-05 end to end through the launcher: a model failure is contained by the runner itself, so the backstop has nothing
    // to do and the task is Failed, never Running.
    [Fact]
    public async Task AModelFailure_EndsFailedThroughTheRunner_WithoutTheBackstop()
    {
        var store = new ScriptedStore();
        using var rig = Create(new ThrowingModel(new ModelProtocolException("no key") { FailureKind = ModelFailureKind.Authentication }), store);

        var taskId = (await rig.Launcher.TryStartAsync("check", Actor, CancellationToken.None))!.Value;
        var task = await rig.SettledAsync(taskId, state => state.Status != AgentTaskStatus.Running);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.StartsWith("Provider authentication failed.", Assert.Single(task.Steps).Observation, StringComparison.Ordinal);
        Assert.Empty(rig.Audit.Events.OfType<TaskExecutionFaultAuditEvent>());
        Assert.Equal(ModelFailureKind.Authentication, Assert.Single(rig.Audit.Events.OfType<ModelCallAuditEvent>()).FailureKind);
    }

    // Packet: an operator's cancellation still ends Cancelled, not Failed.
    [Fact]
    public async Task AnOperatorCancellation_StillEndsCancelled()
    {
        var store = new ScriptedStore();
        using var rig = Create(new StallingModel(), store);

        var taskId = (await rig.Launcher.TryStartAsync("check", Actor, CancellationToken.None))!.Value;
        await Task.Delay(100);
        Assert.True(rig.Launcher.Cancel(taskId));
        var task = await rig.SettledAsync(taskId, state => state.Status != AgentTaskStatus.Running);

        Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
        Assert.Empty(rig.Audit.Events.OfType<TaskExecutionFaultAuditEvent>());
        Assert.DoesNotContain(rig.Audit.Events.OfType<ModelCallAuditEvent>(), e => e.FailureKind == ModelFailureKind.Timeout);
    }

    /// <summary>An in-memory store that can fail a chosen save, to make an exception escape the runner deterministically.</summary>
    private sealed class ScriptedStore : ITaskStore
    {
        private readonly Dictionary<Guid, TaskState> _tasks = [];
        private int _saves;
        private int _loads;

        public Func<int, Exception?>? FailSave { get; init; }

        public int SaveCount => _saves;

        public int LoadCount => _loads;

        public Task SaveAsync(TaskState task, CancellationToken ct = default)
        {
            var number = Interlocked.Increment(ref _saves);
            if (FailSave?.Invoke(number) is { } failure)
            {
                throw failure;
            }

            lock (_tasks)
            {
                _tasks[task.Id] = task;
            }

            return Task.CompletedTask;
        }

        public Task<TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _loads);
            return LoadCoreAsync(taskId);
        }

        private Task<TaskState?> LoadCoreAsync(Guid taskId)
        {
            lock (_tasks)
            {
                return Task.FromResult(_tasks.TryGetValue(taskId, out var task) ? task : null);
            }
        }

        public Task<IReadOnlyList<TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default)
        {
            lock (_tasks)
            {
                return Task.FromResult<IReadOnlyList<TaskState>>(_tasks.Values.Where(t => t.Status == status).ToList());
            }
        }
    }

    private sealed class ListAuditSink : IAuditSink
    {
        private readonly List<AuditEvent> _events = [];

        public IReadOnlyList<AuditEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
        {
            lock (_events)
            {
                _events.Add(evt);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingModel(Exception failure) : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new("test", "test-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) => Task.FromException<ModelResponse>(failure);
    }

    private sealed class StallingModel : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new("test", "test-model");

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class AllAvailable : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class NoApprovals : IApprovalProvider
    {
        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default) =>
            throw new InvalidOperationException("No approval is expected in these tests.");
    }
}
