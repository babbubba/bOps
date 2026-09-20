// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Logging;

namespace bOps.Api;

/// <summary>What <see cref="DelegationLauncher.TryResumeAsync"/> did.</summary>
internal enum DelegationResumeResult
{
    Started,
    NotFound,
    AlreadyRunning,
    NotResumable,
    AtCapacity,
}

/// <summary>
/// Starts and resumes delegated runs as detached background operations, the way <see cref="AgentTaskLauncher"/> does for tasks:
/// a run waits for a human's decision on its plan, so the HTTP request that starts it cannot be held open, and a client follows
/// it by reading the store. It knows which runs this host is executing, so a run can be cancelled in flight, and a run that
/// nothing is executing is told apart from one that is.
/// </summary>
internal sealed class DelegationLauncher(
    DelegationRunner runner,
    IDelegationStore store,
    IAuditSink audit,
    TimeProvider timeProvider,
    AgentTaskLauncherOptions options,
    ILogger<DelegationLauncher> logger) : IDisposable
{
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CancellationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private sealed class Live
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public Task<DelegationRun?> Completion { get; set; } = Task.FromResult<DelegationRun?>(null);
    }

    private readonly SemaphoreSlim _capacity = new(Math.Max(1, options.MaxConcurrentTasks));
    private readonly ConcurrentDictionary<Guid, Live> _running = new();

    /// <summary>Whether this host is executing the run right now.</summary>
    public bool IsRunning(Guid delegationId) => _running.ContainsKey(delegationId);

    /// <summary>
    /// Starts a run in the background and returns its id once the run is stored. With an idempotency key, a repeated start by the
    /// same operator returns the run the first one created and starts nothing (the store decides, so it holds across restarts).
    /// <c>null</c> when this host is at capacity, or the run could not be stored in time.
    /// </summary>
    public async Task<Guid?> TryStartAsync(DelegationRequest request, ActorIdentity actor, string? idempotencyKey, CancellationToken ct)
    {
        if (!await _capacity.WaitAsync(0, ct))
        {
            return null;
        }

        var id = Guid.NewGuid();
        var live = new Live();
        _running[id] = live;
        live.Completion = RunDetachedAsync(id, actor, live, token => runner.StartAsync(request, actor, id, idempotencyKey, token));

        var deadline = timeProvider.GetUtcNow() + RegistrationTimeout;
        while (true)
        {
            if (live.Completion.IsCompleted)
            {
                // Finished already: an idempotent repeat returns the earlier run, whose id is the one to give out.
                return (await live.Completion)?.Id;
            }

            if (await store.LoadAsync(id, ct) is not null)
            {
                return id;
            }

            if (timeProvider.GetUtcNow() >= deadline)
            {
                Cancel(live);
                return null;
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>Resumes a stored run in the background.</summary>
    public async Task<DelegationResumeResult> TryResumeAsync(Guid delegationId, ActorIdentity actor, CancellationToken ct)
    {
        if (_running.ContainsKey(delegationId))
        {
            return DelegationResumeResult.AlreadyRunning;
        }

        var stored = await store.LoadAsync(delegationId, ct);
        if (stored is null)
        {
            return DelegationResumeResult.NotFound;
        }

        if (stored.Status is not (DelegationStatus.Running or DelegationStatus.AwaitingApproval))
        {
            return DelegationResumeResult.NotResumable;
        }

        if (!await _capacity.WaitAsync(0, ct))
        {
            return DelegationResumeResult.AtCapacity;
        }

        var live = new Live();
        if (!_running.TryAdd(delegationId, live))
        {
            live.Cancellation.Dispose();
            _capacity.Release();
            return DelegationResumeResult.AlreadyRunning;
        }

        live.Completion = RunDetachedAsync(delegationId, actor, live, token => runner.ResumeAsync(delegationId, actor, token));
        return DelegationResumeResult.Started;
    }

    /// <summary>
    /// Cancels a run this host is executing and waits for it to end. <c>false</c> when this host is not executing it: a run a crash
    /// left <see cref="DelegationStatus.Running"/> is cancelled through <see cref="DelegationRunner.CancelAsync"/> instead.
    /// </summary>
    public async Task<bool> TryCancelRunningAsync(Guid delegationId, ActorIdentity cancelledBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cancelledBy);

        if (!_running.TryGetValue(delegationId, out var live))
        {
            return false;
        }

        try
        {
            await live.Cancellation.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        await Task.WhenAny(live.Completion, Task.Delay(CancellationTimeout, ct));

        // The run records that it was cancelled, under the operator who started it. Who asked is a decision of its own.
        await audit.WriteAsync(
            new DelegationLifecycleAuditEvent
            {
                TimestampUtc = timeProvider.GetUtcNow(),
                Node = NodeId.Local,
                TaskId = delegationId,
                StepIndex = -1,
                Actor = cancelledBy,
                Stage = DelegationStage.Terminal,
                Status = DelegationStatus.Cancelled,
                ErrorMessage = "Cancelled on request through the API.",
            },
            CancellationToken.None);
        return true;
    }

    private Task<DelegationRun?> RunDetachedAsync(Guid id, ActorIdentity actor, Live live, Func<CancellationToken, Task<DelegationRun>> invoke) =>
        Task.Run<DelegationRun?>(async () =>
        {
            ApiApprovalProvider.CurrentExecutionContext.Value = new ToolExecutionContext(NodeId.Local, id, actor);
            try
            {
                return await invoke(live.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelled before the run existed, or the host is stopping: there is nothing to report.
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Rule C1: the runner ends a run in a terminal state instead of throwing. This is a backstop for what it refuses to
                // start or resume (no stored plan, a run that is not resumable), which is logged, not lost.
                logger.LogWarning(ex, "Delegation {DelegationId}: the background call ended with an error", id);
                return null;
            }
            finally
            {
                ApiApprovalProvider.CurrentExecutionContext.Value = null;
                _running.TryRemove(id, out _);
                live.Cancellation.Dispose();
                try
                {
                    _capacity.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Host shutdown disposes the launcher after cancelling every run.
                }
            }
        });

    private static void Cancel(Live live)
    {
        try
        {
            live.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var live in _running.Values)
        {
            Cancel(live);
        }

        _capacity.Dispose();
    }
}
