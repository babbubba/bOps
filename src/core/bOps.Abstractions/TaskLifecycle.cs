// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// Who created a task (ADR-0040 §8). Set by the runtime when it creates the task, never by a request. Resume eligibility
/// depends on it, so a value that cannot be proven is <see cref="Unknown"/>, never guessed.
/// </summary>
public enum TaskOrigin
{
    /// <summary>The origin was not recorded — every task persisted before ADR-0040. Never ordinarily resumable.</summary>
    Unknown = 0,

    /// <summary>An operator's own task, started through the API or the CLI.</summary>
    Ordinary = 1,

    /// <summary>The inner task of one role of a delegated run (ADR-0030). Only its delegation run may continue it.</summary>
    Delegated = 2,
}

/// <summary>Why the latest execution attempt of a task ended (ADR-0040 §6).</summary>
public enum TaskTerminalKind
{
    /// <summary>The model reported the goal achieved.</summary>
    Completed = 0,

    /// <summary>The per-attempt step budget (<c>Agent:MaxSteps</c>) ran out.</summary>
    StepLimit = 1,

    /// <summary>The task-lifetime step budget (<c>Agent:MaxLifetimeSteps</c>) ran out.</summary>
    LifetimeStepLimit = 2,

    /// <summary>The cumulative token budget (<c>Agent:MaxTotalTokens</c>) was exceeded.</summary>
    TokenBudget = 3,

    /// <summary>A delegated role ran out of its envelope's steps, tokens or time.</summary>
    DelegationBudget = 4,

    /// <summary>The per-attempt replan budget (<c>Agent:MaxReplans</c>) ran out.</summary>
    ReplanLimit = 5,

    /// <summary>The task-lifetime replan budget (<c>Agent:MaxLifetimeReplans</c>) ran out.</summary>
    LifetimeReplanLimit = 6,

    /// <summary>Repeated policy or entitlement denials of the same tool (rule C4).</summary>
    PolicyBlocked = 7,

    /// <summary>A model call failed terminally (ADR-0039); <see cref="TaskTerminalReason.FailureKind"/> says how.</summary>
    ModelFailure = 8,

    /// <summary>The model kept returning an empty reply.</summary>
    EmptyResponse = 9,

    /// <summary>A failure that was not a model failure, contained by the runner or by the host's backstop.</summary>
    RuntimeFailure = 10,

    /// <summary>The execution attempt was cancelled.</summary>
    Cancelled = 11,

    /// <summary>A resume acquired the task but no executor admitted the new execution attempt, so it never ran.</summary>
    NotAdmitted = 12,
}

/// <summary>Why the latest execution attempt ended. Carries no free text: the operator-facing failure text stays on the synthetic failure step (ADR-0039).</summary>
public sealed record TaskTerminalReason
{
    /// <summary>Creates a terminal reason.</summary>
    /// <param name="Kind">Why the attempt ended.</param>
    public TaskTerminalReason(TaskTerminalKind Kind)
    {
        this.Kind = Kind;
    }

    /// <summary>Why the attempt ended.</summary>
    public TaskTerminalKind Kind { get; init; }

    /// <summary>For <see cref="TaskTerminalKind.ModelFailure"/>, the provider-neutral kind of the last failed model attempt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelFailureKind? FailureKind { get; init; }
}

/// <summary>
/// What a task has consumed over its whole lifetime, across every execution attempt (ADR-0040 §5). A resume never resets
/// any of it.
/// </summary>
public sealed record TaskAccounting
{
    /// <summary>Creates an accounting record.</summary>
    /// <param name="TokensUsed">Prompt plus completion tokens of every model reply that reported usage.</param>
    /// <param name="LifetimeSteps">Executable steps taken; synthetic failure steps are never counted.</param>
    /// <param name="LifetimeReplans">Accepted plan revisions after revision 0.</param>
    public TaskAccounting(long TokensUsed, int LifetimeSteps, int LifetimeReplans)
    {
        this.TokensUsed = TokensUsed;
        this.LifetimeSteps = LifetimeSteps;
        this.LifetimeReplans = LifetimeReplans;
    }

    /// <summary>An accounting record for a task that has consumed nothing yet.</summary>
    public static TaskAccounting None { get; } = new(0, 0, 0);

    /// <summary>Prompt plus completion tokens of every model reply that reported usage.</summary>
    public long TokensUsed { get; init; }

    /// <summary>Executable steps taken; synthetic failure steps are never counted.</summary>
    public int LifetimeSteps { get; init; }

    /// <summary>Accepted plan revisions after revision 0.</summary>
    public int LifetimeReplans { get; init; }
}

/// <summary>
/// A task store's atomic, conditional writes (ADR-0040 §4.1) — a capability a store implements in addition to
/// <see cref="ITaskStore"/>. Both operations are atomic at the persistence level, across threads and across processes
/// sharing the store: a store must never implement them as a load, a check in process and a save. A store that does not
/// implement this interface cannot resume tasks.
/// </summary>
public interface ITaskTransitionStore
{
    /// <summary>Inserts <paramref name="task"/> only if no task with its id is stored.</summary>
    /// <param name="task">The new task.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    /// <returns><c>true</c> if it was inserted; <c>false</c> if a task with that id already exists, which is left unchanged.</returns>
    Task<bool> TryCreateAsync(TaskState task, CancellationToken ct = default);

    /// <summary>
    /// Replaces the stored task with <paramref name="task"/> if and only if its persisted status is
    /// <paramref name="expectedStatus"/> and its persisted execution attempt is <paramref name="expectedExecutionAttempt"/>.
    /// </summary>
    /// <param name="task">The new state; its <see cref="TaskState.ExecutionAttempt"/> is <paramref name="expectedExecutionAttempt"/> (a write by the attempt that owns the task) or one more (an acquisition by a resume).</param>
    /// <param name="expectedStatus">The status the stored task must have.</param>
    /// <param name="expectedExecutionAttempt">The execution attempt the stored task must have.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    /// <returns><c>true</c> if the task was replaced; <c>false</c> if it is not stored or its status or execution attempt differ, and nothing was written.</returns>
    /// <exception cref="ArgumentException"><paramref name="task"/>'s execution attempt is neither <paramref name="expectedExecutionAttempt"/> nor one more.</exception>
    Task<bool> TryTransitionAsync(TaskState task, AgentTaskStatus expectedStatus, int expectedExecutionAttempt, CancellationToken ct = default);
}

/// <summary>
/// Thrown when a write by an execution attempt is refused because the task has been taken over by a newer execution attempt
/// (ADR-0040 §4.4). Nothing was written.
/// </summary>
public sealed class TaskExecutionSupersededException : Exception
{
    /// <summary>Creates an empty exception for serializer/framework compatibility.</summary>
    public TaskExecutionSupersededException()
    {
    }

    /// <summary>Creates an exception for the refused write of <paramref name="executionAttempt"/> of <paramref name="taskId"/>.</summary>
    /// <param name="taskId">The task whose write was refused.</param>
    /// <param name="executionAttempt">The execution attempt that tried to write.</param>
    public TaskExecutionSupersededException(Guid taskId, int executionAttempt)
        : base($"Execution attempt {executionAttempt} of task {taskId} no longer owns the task; the write was refused.")
    {
        TaskId = taskId;
        ExecutionAttempt = executionAttempt;
    }

    /// <summary>Creates an exception with a message.</summary>
    public TaskExecutionSupersededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception wrapping an underlying failure.</summary>
    public TaskExecutionSupersededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The task whose write was refused.</summary>
    public Guid TaskId { get; }

    /// <summary>The execution attempt that tried to write.</summary>
    public int ExecutionAttempt { get; }
}
