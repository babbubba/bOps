// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Logging;

namespace bOps.Api;

/// <summary>
/// Starts or resumes a task as a detached background operation (ADR-0018) — never awaited by the
/// HTTP request that triggers it. Safe without a job queue or hosted-service framework because
/// <see cref="AgentRunner.RunAsync"/>/<see cref="AgentRunner.ResumeAsync"/> already catch every
/// exception except <see cref="OperationCanceledException"/> and always persist their result
/// through <see cref="ITaskStore"/> (rule C1; V0.7) — a client observes progress by reading the
/// store, never by holding this method's own <see cref="Task"/> open.
/// </summary>
internal sealed class AgentTaskLauncher(
    AgentRunner runner,
    ITaskStore taskStore,
    TimeProvider timeProvider,
    AgentTaskLauncherOptions options,
    ILogger<AgentTaskLauncher> logger) : IDisposable
{
    private readonly SemaphoreSlim _capacity = new(Math.Max(1, options.MaxConcurrentTasks));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>Starts a new task in the background and returns its id immediately, before it runs.</summary>
    public async Task<Guid?> TryStartAsync(string goal, ActorIdentity actor, CancellationToken ct)
    {
        if (!await _capacity.WaitAsync(0, ct))
        {
            return null;
        }

        var taskId = Guid.NewGuid();
        var initial = new TaskState(taskId, NodeId.Local, goal, AgentTaskStatus.Running, [], [], timeProvider.GetUtcNow());
        try
        {
            await taskStore.SaveAsync(initial, ct);
        }
        catch
        {
            _capacity.Release();
            throw;
        }
        if (!TryRunDetached(taskId, initial, actor, token => runner.RunAsync(goal, actor, taskId: taskId, ct: token)))
        {
            throw new InvalidOperationException($"The newly allocated task id '{taskId}' is already running.");
        }

        return taskId;
    }

    /// <summary>Resumes a previously stored task in the background.</summary>
    public async Task<bool> TryResumeAsync(TaskState task, ActorIdentity actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (_running.ContainsKey(task.Id) || !await _capacity.WaitAsync(0, ct))
        {
            return false;
        }

        return TryRunDetached(task.Id, task, actor, token => runner.ResumeAsync(task, actor, token));
    }

    public bool Cancel(Guid taskId) =>
        _running.TryGetValue(taskId, out var cancellation) && TryCancel(cancellation);

    /// <summary>
    /// Runs <paramref name="invoke"/> detached from the caller, with <see cref="ApiApprovalProvider.CurrentTaskId"/>
    /// set for the duration so a nested approval request can recover which task raised it.
    /// </summary>
    private bool TryRunDetached(
        Guid taskId,
        TaskState lastKnownState,
        ActorIdentity actor,
        Func<CancellationToken, Task<TaskState>> invoke)
    {
        var cancellation = new CancellationTokenSource();
        if (!_running.TryAdd(taskId, cancellation))
        {
            cancellation.Dispose();
            _capacity.Release();
            return false;
        }

        _ = Task.Run(async () =>
        {
            ApiApprovalProvider.CurrentExecutionContext.Value = new ToolExecutionContext(NodeId.Local, taskId, actor);
            try
            {
                await invoke(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                var current = await taskStore.LoadAsync(taskId) ?? lastKnownState;
                await taskStore.SaveAsync(current with { Status = AgentTaskStatus.Cancelled });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Rule C1 guarantees AgentRunner itself does not let this happen — this remains a
                // defensive backstop against a genuinely unexpected failure in the detached
                // Task.Run wrapper itself, not something the agent loop is expected to trigger.
                logger.LogError(ex, "Task {TaskId}: background execution failed unexpectedly", taskId);
            }
            finally
            {
                ApiApprovalProvider.CurrentExecutionContext.Value = null;
                _running.TryRemove(taskId, out _);
                cancellation.Dispose();
                try
                {
                    _capacity.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Host shutdown disposes the launcher after cancelling all active runs.
                }
            }
        });

        return true;
    }

    private static bool TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var cancellation in _running.Values)
        {
            cancellation.Cancel();
        }

        _capacity.Dispose();
    }
}
