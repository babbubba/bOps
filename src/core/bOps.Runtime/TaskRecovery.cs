// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Why an administrator's recovery of a task, or a reconciliation of its mutations, was refused (ADR-0051 §8.3, §9.1): a stable
/// <see cref="Code"/> for clients and audit, and an operator <see cref="Message"/>.
/// </summary>
/// <param name="Code">The stable refusal code, one of the constants on this type or on <see cref="TaskResumeRefusal"/>.</param>
/// <param name="Message">What an administrator is told.</param>
public sealed record TaskRecoveryRefusal(string Code, string Message)
{
    /// <summary>The task is the inner task of a delegated role; its delegation run owns it.</summary>
    public const string TaskDelegated = "task_delegated";

    /// <summary>The task's origin was not recorded, so it cannot be proven to be an ordinary task.</summary>
    public const string TaskOriginUnknown = "task_origin_unknown";

    /// <summary>The task is not persisted <see cref="AgentTaskStatus.Running"/>: there is nothing to recover.</summary>
    public const string TaskNotRunning = "task_not_running";

    /// <summary>This host is executing the task; cancel it instead.</summary>
    public const string TaskExecutingHere = "task_executing_here";

    /// <summary>The persisted execution attempt is not the one the administrator was shown, or another writer got there first.</summary>
    public const string RecoveryConflict = "recovery_conflict";

    /// <summary>The task is persisted <see cref="AgentTaskStatus.Running"/>; it must be recovered before it is reconciled.</summary>
    public const string TaskRunning = "task_running";

    /// <summary>The task has no pending, ambiguous or escalated mutation.</summary>
    public const string NothingToReconcile = "nothing_to_reconcile";

    /// <summary>Reconciliation is a human decision; an agent or the runtime cannot make it.</summary>
    public const string ReconcileNotHuman = "reconcile_not_human";

    /// <summary>A mutation changed between reading the journal and writing its reconciliation.</summary>
    public const string ReconcileConflict = "reconcile_conflict";

    /// <summary>The reconciliation request is malformed (unknown action, or a note longer than the bound).</summary>
    public const string InvalidRequest = "invalid_request";
}

/// <summary>Whether an orphaned task may be recovered now and, if not, why.</summary>
/// <param name="Recoverable">Whether an administrator's recovery would be accepted.</param>
/// <param name="Refusal">Why not, when <paramref name="Recoverable"/> is <c>false</c>.</param>
public sealed record TaskRecoveryDecision(bool Recoverable, TaskRecoveryRefusal? Refusal)
{
    /// <summary>The decision for a task that may be recovered.</summary>
    public static TaskRecoveryDecision Allowed { get; } = new(true, null);

    internal static TaskRecoveryDecision Refused(string code, string message) => new(false, new TaskRecoveryRefusal(code, message));
}

/// <summary>
/// The one recovery rule (ADR-0051 §8.3), used by the runner, the API and the CLI alike. Pure. Recovery is an administrator's
/// fenced transition <c>(Running, N) → (Failed, N)</c> of an orphaned execution attempt: it executes, verifies, approves and
/// reconciles nothing, and never proves the old executor dead — <c>executing == false</c> is never treated as proof; only
/// <c>executing == true</c> in this host refuses.
/// </summary>
public static class TaskRecoveryPolicy
{
    /// <summary>Decides whether an administrator's recovery of <paramref name="task"/> would be accepted now (rows 2–9; row 1 is "not stored").</summary>
    /// <param name="task">The task as persisted.</param>
    /// <param name="executingHere">Whether an execution attempt of the task is registered in this host.</param>
    /// <param name="storeHasTransitions">Whether the store implements <see cref="ITaskTransitionStore"/>.</param>
    /// <param name="storeHasJournal">Whether the store implements <see cref="ITaskMutationJournalStore"/>.</param>
    /// <param name="requestedExecutionAttempt">The attempt the administrator was shown, or <c>null</c> to evaluate without a request (the task view).</param>
    public static TaskRecoveryDecision Evaluate(
        TaskState task, bool executingHere, bool storeHasTransitions, bool storeHasJournal, int? requestedExecutionAttempt)
    {
        ArgumentNullException.ThrowIfNull(task);

        switch (task.Origin)
        {
            case TaskOrigin.Delegated:
                return TaskRecoveryDecision.Refused(TaskRecoveryRefusal.TaskDelegated,
                    "This task is a role of a delegated run; its delegation run owns it.");
            case TaskOrigin.Ordinary:
                break;
            default:
                return TaskRecoveryDecision.Refused(TaskRecoveryRefusal.TaskOriginUnknown,
                    "This task was stored without a recorded origin, so it cannot be proven to be an ordinary task.");
        }

        if (task.Status != AgentTaskStatus.Running)
        {
            return TaskRecoveryDecision.Refused(TaskRecoveryRefusal.TaskNotRunning,
                $"This task is {task.Status}, not Running: there is no interrupted execution attempt to recover.");
        }

        if (executingHere)
        {
            return TaskRecoveryDecision.Refused(TaskRecoveryRefusal.TaskExecutingHere,
                "This host is executing the task. Cancel it instead.");
        }

        if (task.MutationJournalMode == TaskMutationJournalMode.Absent)
        {
            return TaskRecoveryDecision.Refused(TaskResumeRefusal.MutationJournalAbsent,
                "This task was stored before bOps kept a durable mutation journal; its interrupted attempt cannot be recovered safely. Start a new task.");
        }

        if (!storeHasTransitions)
        {
            return TaskRecoveryDecision.Refused(TaskResumeRefusal.TransitionUnsupported,
                "The task store cannot perform the atomic transition a recovery requires.");
        }

        if (task.MutationJournalMode == TaskMutationJournalMode.Journaled && !storeHasJournal)
        {
            return TaskRecoveryDecision.Refused(TaskResumeRefusal.MutationJournalUnsupported,
                "This task journals its changes, but the configured task store has no mutation journal.");
        }

        if (requestedExecutionAttempt is { } requested && requested != task.ExecutionAttempt)
        {
            return TaskRecoveryDecision.Refused(TaskRecoveryRefusal.RecoveryConflict,
                $"The task is now in execution attempt {task.ExecutionAttempt}, not {requested}. Read it again before recovering it.");
        }

        return TaskRecoveryDecision.Allowed;
    }
}

/// <summary>What <see cref="AgentRunner.TryRecoverAsync"/> did.</summary>
public enum TaskRecoveryOutcome
{
    /// <summary>The orphaned attempt was moved to <see cref="AgentTaskStatus.Failed"/> with <see cref="TaskTerminalKind.ExecutionInterrupted"/>.</summary>
    Recovered,

    /// <summary>No task with that id is stored.</summary>
    NotFound,

    /// <summary>The recovery was refused; nothing was written.</summary>
    Refused,
}

/// <summary>The result of an administrator's recovery.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Task">The recovered task, or the task as read for a refusal.</param>
/// <param name="Refusal">Why the recovery was refused.</param>
public sealed record TaskRecoveryResult(TaskRecoveryOutcome Outcome, TaskState? Task, TaskRecoveryRefusal? Refusal);

/// <summary>The only reconciliation actions (ADR-0051 §9.1). There is no retry and no "accept as not executed".</summary>
public enum TaskReconcileAction
{
    /// <summary>Run each unsettled mutation's declared verification again; only <see cref="VerificationStatus.Confirmed"/> settles it.</summary>
    Verify,

    /// <summary>Accept every unsettled mutation as already applied. The runtime treats it as executed; its knowledge stays unknown.</summary>
    AcceptDone,

    /// <summary>Abandon the task: it is never resumable again.</summary>
    Abandon,
}

/// <summary>What <see cref="AgentRunner.ReconcileMutationsAsync"/> did.</summary>
public enum TaskReconcileOutcome
{
    /// <summary>Every unsettled mutation was given its reconciliation.</summary>
    Reconciled,

    /// <summary>No task with that id is stored.</summary>
    NotFound,

    /// <summary>The reconciliation was refused; for <see cref="TaskRecoveryRefusal.ReconcileConflict"/>, entries committed before the conflict stay committed.</summary>
    Refused,
}

/// <summary>One mutation's reconciliation as committed.</summary>
/// <param name="Key">The mutation.</param>
/// <param name="State">Its new state.</param>
/// <param name="Reconciliation">Its reconciliation record.</param>
/// <param name="ReasonCode">Why a verification did not confirm it (for example <c>arguments_unavailable</c>), when it did not.</param>
public sealed record TaskReconciledMutation(TaskMutationKey Key, TaskMutationState State, StepReconciliation Reconciliation, string? ReasonCode);

/// <summary>The result of an administrator's reconciliation.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Results">The mutations reconciled by this call.</param>
/// <param name="UnsettledCount">How many of the task's mutations are still unsettled afterwards.</param>
/// <param name="Refusal">Why the reconciliation was refused.</param>
public sealed record TaskReconcileResult(
    TaskReconcileOutcome Outcome, IReadOnlyList<TaskReconciledMutation> Results, int UnsettledCount, TaskRecoveryRefusal? Refusal);
