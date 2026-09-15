namespace bOps.Abstractions;

/// <summary>
/// Persists <see cref="TaskState"/> so a task can survive a crash, a restart, or an operator's
/// own interruption, and be resumed from its last completed step instead of started over
/// (V0.7, ADR-0017). Node-local, like <see cref="IAuditSink"/> — a task never spans nodes
/// (agentic/01-architecture-rules.md, rule A3), so there is nothing to aggregate here across a
/// fleet.
/// </summary>
public interface ITaskStore
{
    /// <summary>
    /// Writes the current state of a task, replacing whatever was previously stored for the same
    /// <see cref="TaskState.Id"/>. Called after every step while a task is
    /// <see cref="AgentTaskStatus.Running"/>, and once more when it reaches a terminal status —
    /// so a reader always sees either a task's last fully completed step or its final outcome,
    /// never a half-written one.
    /// </summary>
    /// <param name="task">The task state to persist.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    Task SaveAsync(TaskState task, CancellationToken ct = default);

    /// <summary>Loads a previously saved task by id, or <c>null</c> if none is stored.</summary>
    /// <param name="taskId">The task's id.</param>
    /// <param name="ct">Cancelled if the read should be abandoned.</param>
    Task<TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Lists every stored task currently at <paramref name="status"/> — in practice, the way an
    /// operator finds what is resumable after a restart: every task still
    /// <see cref="AgentTaskStatus.Running"/>.
    /// </summary>
    /// <param name="status">The status to filter by.</param>
    /// <param name="ct">Cancelled if the read should be abandoned.</param>
    Task<IReadOnlyList<TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default);
}
