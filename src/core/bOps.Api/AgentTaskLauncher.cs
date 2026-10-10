// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Logging;

namespace bOps.Api;

/// <summary>
/// Starts a task, or runs an execution attempt a resume acquired, as a detached background operation (ADR-0018) — never
/// awaited by the HTTP request that triggers it. Safe without a job queue or hosted-service framework because
/// <see cref="AgentRunner.RunAsync"/>/<see cref="AgentRunner.ExecuteAcquiredResumeAsync"/> contain every failure except
/// the task's own cancellation and always persist their result through <see cref="ITaskStore"/>
/// (rule C1; V0.7; ADR-0039) — a client observes progress by reading the store, never by holding this
/// method's own <see cref="Task"/> open. Anything that escapes anyway reaches a backstop that never
/// leaves the task <see cref="AgentTaskStatus.Running"/> (ADR-0039 §9). Executions are registered by task id <b>and</b>
/// execution attempt (ADR-0040 §4.4), and every write after an execution is fenced on that attempt.
/// </summary>
internal sealed class AgentTaskLauncher(
    AgentRunner runner,
    ITaskStore taskStore,
    TimeProvider timeProvider,
    AgentTaskLauncherOptions options,
    ILogger<AgentTaskLauncher> logger,
    ProviderConfigurationCoordinator? coordinator = null,
    ExecutionRunnerFactory? executionRunners = null) : IDisposable
{
    private readonly SemaphoreSlim _capacity = new(Math.Max(1, options.MaxConcurrentTasks));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid TaskId, int ExecutionAttempt), CancellationTokenSource> _running = new();

    /// <summary>Starts a new task in the background and returns its id immediately, before it runs; <c>null</c> when no execution slot is free.</summary>
    public async Task<Guid?> TryStartAsync(string goal, ActorIdentity actor, CancellationToken ct)
    {
        if (!await _capacity.WaitAsync(0, ct))
        {
            return null;
        }

        var taskId = Guid.NewGuid();
        PinnedProviderConfiguration? pin;
        AgentRunner executionRunner;
        try
        {
            pin = coordinator is null ? null : coordinator.Current.Pin with { FallbackOrdinal = 0 };
            if (pin is not null) coordinator!.EnsureAdmission(pin);
            executionRunner = pin is null ? runner : executionRunners?.CreateAgent(pin)
                ?? throw new InvalidOperationException("No execution runner factory is registered.");
        }
        catch
        {
            _capacity.Release();
            throw;
        }
        // ADR-0040 §8: the runtime-side host records the origin; no request can set it.
        var initial = new TaskState(taskId, NodeId.Local, goal, AgentTaskStatus.Running, [], [], timeProvider.GetUtcNow())
        {
            Origin = TaskOrigin.Ordinary,
            Accounting = TaskAccounting.None,
            PinnedProviderConfiguration = pin,
            // ADR-0051 §11: decided once, from the store the task is created on, exactly as the runner decides it.
            MutationJournalMode = taskStore is ITaskMutationJournalStore
                ? TaskMutationJournalMode.Journaled
                : TaskMutationJournalMode.MutationsDisabled,
        };
        try
        {
            if (taskStore is ITaskTransitionStore transitions)
            {
                if (!await transitions.TryCreateAsync(initial, ct))
                {
                    throw new InvalidOperationException($"The newly allocated task id '{taskId}' is already stored.");
                }
            }
            else
            {
                await taskStore.SaveAsync(initial, ct);
            }
        }
        catch
        {
            _capacity.Release();
            throw;
        }

        if (!TryRunDetached(initial, actor, executionRunner,
                token => executionRunner.RunAsync(goal, actor, taskId: taskId, ct: token)))
        {
            throw new InvalidOperationException($"The newly allocated task id '{taskId}' is already running.");
        }

        return taskId;
    }

    /// <summary>
    /// Admits the execution attempt a resume acquired (ADR-0040 §4.3 step 6): takes an execution slot and starts the attempt
    /// detached from the persisted snapshot. <c>false</c> when it cannot be admitted — no free slot, or the host is shutting
    /// down — and then nothing was started: the caller must contain the acquired attempt.
    /// </summary>
    public bool TryAdmit(TaskState acquired, ActorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(acquired);
        var executionRunner = acquired.PinnedProviderConfiguration is { } pin
            ? executionRunners?.CreateAgent(pin) ?? throw new InvalidOperationException("No execution runner factory is registered.")
            : runner;
        try
        {
            if (!_capacity.Wait(0))
            {
                return false;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return TryRunDetached(acquired, actor, executionRunner,
            token => executionRunner.ExecuteAcquiredResumeAsync(acquired, actor, token));
    }

    /// <summary>Whether an execution attempt of <paramref name="taskId"/> is registered in this host.</summary>
    public bool IsExecuting(Guid taskId) => _running.Keys.Any(key => key.TaskId == taskId);

    /// <summary>Signals cancellation to every execution attempt of <paramref name="taskId"/> in this host; <c>false</c> when there is none.</summary>
    public bool Cancel(Guid taskId)
    {
        var cancelled = false;
        foreach (var (key, cancellation) in _running)
        {
            if (key.TaskId == taskId)
            {
                cancelled |= TryCancel(cancellation);
            }
        }

        return cancelled;
    }

    /// <summary>
    /// Runs <paramref name="invoke"/> detached from the caller, with <see cref="ApiApprovalProvider.CurrentTaskId"/>
    /// set for the duration so a nested approval request can recover which task raised it. Owns one execution slot, taken
    /// by the caller; releases it when the run ends, or at once when the run cannot be registered.
    /// </summary>
    private bool TryRunDetached(TaskState execution, ActorIdentity actor, AgentRunner executionRunner,
        Func<CancellationToken, Task<TaskState>> invoke)
    {
        var taskId = execution.Id;
        var executionAttempt = execution.ExecutionAttempt;
        var key = (taskId, executionAttempt);
        var cancellation = new CancellationTokenSource();
        if (!_running.TryAdd(key, cancellation))
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
                // Fenced (ADR-0040 §4.5): only this attempt, still Running, becomes Cancelled. Never throws.
                await executionRunner.CompleteCancellationAsync(taskId, executionAttempt, actor, execution, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
            {
                // The final containment boundary (ADR-0039 §9). Rule C1 and ADR-0039 mean the runner should never let this
                // happen — including an OperationCanceledException this launcher did not request, such as a timeout — so
                // whatever does escape is a runtime defect: it is logged, and this attempt, if still persisted Running, is
                // failed and audited so it never stays Running without an executor. A terminal state or a newer execution
                // attempt is never overwritten (ADR-0040 §4.5).
                logger.LogError(ex, "Task {TaskId}: background execution failed unexpectedly", taskId);
                // Never throws for a store or audit failure (it logs them), so nothing escapes this detached task.
                await executionRunner.ContainEscapedFailureAsync(taskId, executionAttempt, actor, execution, ex, CancellationToken.None);
            }
            finally
            {
                ApiApprovalProvider.CurrentExecutionContext.Value = null;
                _running.TryRemove(key, out _);
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
            TryCancel(cancellation);
        }

        _capacity.Dispose();
    }
}
