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
internal sealed class AgentTaskLauncher(AgentRunner runner, ILogger<AgentTaskLauncher> logger)
{
    /// <summary>Starts a new task in the background and returns its id immediately, before it runs.</summary>
    public Guid Start(string goal, ActorIdentity actor)
    {
        var taskId = Guid.NewGuid();
        RunDetached(taskId, () => runner.RunAsync(goal, actor, taskId: taskId));
        return taskId;
    }

    /// <summary>Resumes a previously stored task in the background.</summary>
    public void Resume(TaskState task, ActorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(task);
        RunDetached(task.Id, () => runner.ResumeAsync(task, actor));
    }

    /// <summary>
    /// Runs <paramref name="invoke"/> detached from the caller, with <see cref="ApiApprovalProvider.CurrentTaskId"/>
    /// set for the duration so a nested approval request can recover which task raised it.
    /// </summary>
    private void RunDetached(Guid taskId, Func<Task<TaskState>> invoke)
    {
        _ = Task.Run(async () =>
        {
            ApiApprovalProvider.CurrentTaskId.Value = taskId;
            try
            {
                await invoke();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Rule C1 guarantees AgentRunner itself does not let this happen — this remains a
                // defensive backstop against a genuinely unexpected failure in the detached
                // Task.Run wrapper itself, not something the agent loop is expected to trigger.
                logger.LogError(ex, "Task {TaskId}: background execution failed unexpectedly", taskId);
            }
        });
    }
}
