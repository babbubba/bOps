// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Why a stored task cannot be resumed (ADR-0040 §3): a stable <see cref="Code"/> for clients and audit, and an operator <see cref="Message"/>.</summary>
/// <param name="Code">The stable refusal code, one of the constants on this type.</param>
/// <param name="Message">What an operator is told.</param>
public sealed record TaskResumeRefusal(string Code, string Message)
{
    /// <summary>The task is the inner task of a delegated role; only its delegation run may continue it.</summary>
    public const string TaskDelegated = "task_delegated";

    /// <summary>The task's origin was not recorded (every task persisted before ADR-0040), so it cannot be proven ordinary.</summary>
    public const string OriginUnknown = "resume_origin_unknown";

    /// <summary>The task is persisted <see cref="AgentTaskStatus.Running"/>: an executor may still be running it, here or in another process.</summary>
    public const string TaskRunning = "task_running";

    /// <summary>The task completed.</summary>
    public const string TaskCompleted = "task_completed";

    /// <summary>The task ended <see cref="AgentTaskStatus.PolicyBlocked"/> (rule C4): resuming would repeat the denied tool.</summary>
    public const string TaskPolicyBlocked = "task_policy_blocked";

    /// <summary>The task's status is not one this runtime knows to be resumable.</summary>
    public const string StatusNotResumable = "task_status_not_resumable";

    /// <summary>The task has used every token the currently configured cap allows.</summary>
    public const string TokenBudgetExhausted = "token_budget_exhausted";

    /// <summary>The task has used its whole lifetime step budget.</summary>
    public const string LifetimeStepsExhausted = "lifetime_steps_exhausted";

    /// <summary>The task ended at its replan limit and has used its whole lifetime replan budget.</summary>
    public const string LifetimeReplansExhausted = "lifetime_replans_exhausted";

    /// <summary>Another resume or writer changed the task between reading it and acquiring it.</summary>
    public const string ResumeConflict = "resume_conflict";

    /// <summary>The task store cannot perform the atomic transition a resume requires, so resume fails closed.</summary>
    public const string TransitionUnsupported = "transition_unsupported";

    /// <summary>The task was stored before ADR-0051 and keeps no mutation journal: it cannot prove no mutation was in flight.</summary>
    public const string MutationJournalAbsent = "mutation_journal_absent";

    /// <summary>The task journals its mutations but the store lacks the journal capability (ADR-0051 §11).</summary>
    public const string MutationJournalUnsupported = "mutation_journal_unsupported";

    /// <summary>The task's mutation journal could not be read.</summary>
    public const string MutationJournalUnavailable = "mutation_journal_unavailable";

    /// <summary>An administrator abandoned the task's unsettled mutations; the task is never resumable again.</summary>
    public const string TaskAbandoned = "task_abandoned";

    /// <summary>A mutation of the task may or may not have been applied; an administrator must reconcile it first.</summary>
    public const string MutationOutcomeUnknown = "mutation_outcome_unknown";
}

/// <summary>Whether a stored task may be resumed now and, if not, why.</summary>
/// <param name="Resumable">Whether an ordinary resume would be accepted.</param>
/// <param name="Refusal">Why not, when <paramref name="Resumable"/> is <c>false</c>.</param>
public sealed record TaskResumeDecision(bool Resumable, TaskResumeRefusal? Refusal)
{
    /// <summary>The decision for a task that may be resumed.</summary>
    public static TaskResumeDecision Allowed { get; } = new(true, null);

    internal static TaskResumeDecision Refused(string code, string message) => new(false, new TaskResumeRefusal(code, message));
}

/// <summary>What <see cref="AgentRunner.TryAcquireResumeAsync(Guid, ActorIdentity, CancellationToken)"/> did.</summary>
public enum TaskResumeOutcome
{
    /// <summary>The task was atomically moved to <see cref="AgentTaskStatus.Running"/> under a new execution attempt.</summary>
    Acquired,

    /// <summary>No task with that id is stored.</summary>
    NotFound,

    /// <summary>The resume was refused; nothing was written.</summary>
    Refused,
}

/// <summary>The result of trying to acquire a stored task for a resume (ADR-0040 §4.3).</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Task">For <see cref="TaskResumeOutcome.Acquired"/>, the persisted snapshot of the new execution attempt; for a refusal, the task as read.</param>
/// <param name="Refusal">Why the resume was refused, for <see cref="TaskResumeOutcome.Refused"/>.</param>
public sealed record TaskResumeAcquisition(TaskResumeOutcome Outcome, TaskState? Task, TaskResumeRefusal? Refusal,
    bool LegacyConfigurationMigrated = false);

/// <summary>Thrown by <see cref="AgentRunner.ResumeAsync"/> when the task cannot be resumed; nothing was executed or written.</summary>
public sealed class TaskResumeRefusedException : Exception
{
    /// <summary>Creates an empty exception for serializer/framework compatibility.</summary>
    public TaskResumeRefusedException()
    {
        Refusal = new TaskResumeRefusal(TaskResumeRefusal.StatusNotResumable, "The task cannot be resumed.");
    }

    /// <summary>Creates an exception for <paramref name="refusal"/>.</summary>
    /// <param name="refusal">Why the resume was refused.</param>
    public TaskResumeRefusedException(TaskResumeRefusal refusal)
        : base((refusal ?? throw new ArgumentNullException(nameof(refusal))).Message)
    {
        Refusal = refusal;
    }

    /// <summary>Creates an exception with a message.</summary>
    public TaskResumeRefusedException(string message)
        : base(message)
    {
        Refusal = new TaskResumeRefusal(TaskResumeRefusal.StatusNotResumable, message);
    }

    /// <summary>Creates an exception wrapping an underlying failure.</summary>
    public TaskResumeRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Refusal = new TaskResumeRefusal(TaskResumeRefusal.StatusNotResumable, message);
    }

    /// <summary>Why the resume was refused.</summary>
    public TaskResumeRefusal Refusal { get; }
}

/// <summary>
/// The one resumability rule (ADR-0040 §3), used by the runner, the API and the CLI alike, and the one place a task's
/// lifetime accounting is read (ADR-0040 §5.4). Pure: it reads the task and the configured budgets, nothing else.
/// </summary>
public static class TaskResumePolicy
{
    /// <summary>The description of a synthetic step the runtime appends when a model call fails terminally (ADR-0039).</summary>
    internal const string ModelFailureStepDescription = "Model protocol failure";

    /// <summary>The description of the synthetic step the host's backstop appends (ADR-0039 §9).</summary>
    internal const string RuntimeFailureStepDescription = "Unexpected runtime failure";

    /// <summary>The description of the synthetic step written when a resumed attempt was never admitted (ADR-0040 §4.3).</summary>
    internal const string NotStartedStepDescription = "Execution not started";

    /// <summary>The synthetic bookkeeping step written when a non-final model response crosses the token cap.</summary>
    internal const string TokenBudgetStepDescription = "Token budget crossing";

    /// <summary>The synthetic bookkeeping step that owns the model calls of a logical call the attempt-duration budget interrupted.</summary>
    internal const string AttemptDurationStepDescription = "Attempt duration interruption";

    /// <summary>The synthetic failure step written when a fifth EvidenceRead is attempted.</summary>
    internal const string EvidenceReadLimitStepDescription = "Evidence read limit exceeded";

    /// <summary>The synthetic step an administrator's recovery of an orphaned Running attempt appends (ADR-0051 §8.3).</summary>
    internal const string ExecutionInterruptedStepDescription = "Execution interrupted";

    /// <summary>The runtime-authored history step a resume appends for a mutation reconciled as already applied (ADR-0051 §9.5).</summary>
    internal const string MutationReconciledStepDescription = "Mutation reconciled";

    /// <summary>
    /// Decides whether an ordinary resume of <paramref name="task"/> would be accepted now, under <paramref name="options"/>, by the
    /// ADR-0040 rules alone. It does not see the task's mutation journal; the runtime, the API and the CLI decide with
    /// <see cref="Evaluate(TaskState, AgentRunnerOptions, bool, IReadOnlyList{TaskMutationJournalEntry})"/>.
    /// </summary>
    /// <param name="task">The task as persisted.</param>
    /// <param name="options">The budgets configured now.</param>
    public static TaskResumeDecision Evaluate(TaskState task, AgentRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(options);

        return EvaluateStatus(task) ?? EvaluateBudgets(task, options);
    }

    /// <summary>
    /// The one journal-aware resumability rule (ADR-0040 §3 as amended by ADR-0051 §9.4): ADR-0040 rows 1–6, then the journal rows
    /// 6a–6e, then ADR-0040 rows 7–9. A task created on a store without the journal capability (<see cref="TaskMutationJournalMode.MutationsDisabled"/>)
    /// could never begin a mutation and passes 6a–6e on any store.
    /// </summary>
    /// <param name="task">The task as persisted.</param>
    /// <param name="options">The budgets configured now.</param>
    /// <param name="storeHasJournal">Whether the store implements <see cref="ITaskMutationJournalStore"/>.</param>
    /// <param name="journal">The task's journal entries, or <c>null</c> when they could not be read.</param>
    public static TaskResumeDecision Evaluate(
        TaskState task, AgentRunnerOptions options, bool storeHasJournal, IReadOnlyList<TaskMutationJournalEntry>? journal)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(options);

        return EvaluateStatus(task) ?? EvaluateJournal(task, storeHasJournal, journal) ?? EvaluateBudgets(task, options);
    }

    /// <summary>ADR-0051 §9.4 rows 6a–6e, or <c>null</c> when they allow the resume.</summary>
    private static TaskResumeDecision? EvaluateJournal(TaskState task, bool storeHasJournal, IReadOnlyList<TaskMutationJournalEntry>? journal)
    {
        switch (task.MutationJournalMode)
        {
            case TaskMutationJournalMode.MutationsDisabled:
                return null;
            case TaskMutationJournalMode.Journaled:
                break;
            default:
                return TaskResumeDecision.Refused(TaskResumeRefusal.MutationJournalAbsent,
                    "This task was stored before bOps kept a durable mutation journal, so nothing proves that its last attempt did not stop " +
                    "inside a change to the system. It is not resumable; start a new task.");
        }

        if (!storeHasJournal)
        {
            return TaskResumeDecision.Refused(TaskResumeRefusal.MutationJournalUnsupported,
                "This task journals its changes, but the configured task store has no mutation journal. It cannot be resumed on this store.");
        }

        if (journal is null)
        {
            return TaskResumeDecision.Refused(TaskResumeRefusal.MutationJournalUnavailable,
                "The task's mutation journal could not be read; it is not resumed while that is unknown. Retry later.");
        }

        if (journal.Any(entry => entry.State == TaskMutationState.Abandoned))
        {
            return TaskResumeDecision.Refused(TaskResumeRefusal.TaskAbandoned,
                "An administrator abandoned this task's unresolved changes; it is never resumable again. Start a new task.");
        }

        var unsettled = journal.Where(entry => MutationJournalPolicy.IsUnsettled(entry.State)).ToList();
        if (unsettled.Count > 0)
        {
            var named = string.Join(", ", unsettled.Select(entry =>
                $"'{entry.Intent.ToolName}' (attempt {entry.Intent.Key.ExecutionAttempt}, step {entry.Intent.Key.StepIndex})"));
            return TaskResumeDecision.Refused(TaskResumeRefusal.MutationOutcomeUnknown,
                $"{unsettled.Count} change(s) may or may not have been applied: {named}. bOps will not repeat them automatically; " +
                "an administrator must reconcile them before the task can be resumed.");
        }

        return null;
    }

    /// <summary>ADR-0040 §3 rows 1–6 (origin, status), or <c>null</c> when they allow the resume.</summary>
    private static TaskResumeDecision? EvaluateStatus(TaskState task)
    {
        // Origin first: no status or budget makes a role task, or a task whose origin cannot be proven, ordinarily resumable.
        switch (task.Origin)
        {
            case TaskOrigin.Delegated:
                return TaskResumeDecision.Refused(TaskResumeRefusal.TaskDelegated,
                    "This task is a role of a delegated run; resume the delegation run instead.");
            case TaskOrigin.Ordinary:
                break;
            default:
                return TaskResumeDecision.Refused(TaskResumeRefusal.OriginUnknown,
                    "This task was stored without a recorded origin, so it cannot be proven to be an ordinary task and is not resumable. Start a new task.");
        }

        switch (task.Status)
        {
            case AgentTaskStatus.Running:
                return TaskResumeDecision.Refused(TaskResumeRefusal.TaskRunning,
                    "This task is stored as Running: an executor may still be running it, in this host or another process. It cannot be resumed.");
            case AgentTaskStatus.Completed:
                return TaskResumeDecision.Refused(TaskResumeRefusal.TaskCompleted, "This task completed; start a new task instead.");
            case AgentTaskStatus.PolicyBlocked:
                return TaskResumeDecision.Refused(TaskResumeRefusal.TaskPolicyBlocked,
                    "This task was blocked by policy; resuming would repeat the denied action. Start a new task or change the policy.");
            case AgentTaskStatus.Failed:
            case AgentTaskStatus.Cancelled:
            case AgentTaskStatus.MaxStepsReached:
            case AgentTaskStatus.ReplanLimitReached:
            case AgentTaskStatus.BudgetExceeded:
                break;
            default:
                return TaskResumeDecision.Refused(TaskResumeRefusal.StatusNotResumable, $"A task in status {task.Status} cannot be resumed.");
        }

        return null;
    }

    /// <summary>ADR-0040 §3 rows 7–9 (lifetime budgets).</summary>
    private static TaskResumeDecision EvaluateBudgets(TaskState task, AgentRunnerOptions options)
    {
        var accounting = EffectiveAccounting(task);
        if (options.MaxTotalTokens is { } tokenCap && accounting.TokensUsed >= tokenCap)
        {
            return TaskResumeDecision.Refused(TaskResumeRefusal.TokenBudgetExhausted,
                $"This task has used {accounting.TokensUsed} tokens of the configured {tokenCap}; only a higher token cap makes it resumable.");
        }

        if (accounting.LifetimeSteps >= options.MaxLifetimeSteps)
        {
            return TaskResumeDecision.Refused(TaskResumeRefusal.LifetimeStepsExhausted,
                $"This task has used {accounting.LifetimeSteps} of its {options.MaxLifetimeSteps} lifetime steps.");
        }

        if (task.Status == AgentTaskStatus.ReplanLimitReached && accounting.LifetimeReplans >= options.MaxLifetimeReplans)
        {
            return TaskResumeDecision.Refused(TaskResumeRefusal.LifetimeReplansExhausted,
                $"This task has used {accounting.LifetimeReplans} of its {options.MaxLifetimeReplans} lifetime replans.");
        }

        return TaskResumeDecision.Allowed;
    }

    /// <summary>
    /// The task's lifetime accounting: the persisted record, or — for a task persisted before accounting was kept — the one
    /// derived deterministically from its persisted history (ADR-0040 §5.4). The derivation is the same on every call, and
    /// the runtime's first authoritative write of such a task persists it, so it is never recomputed differently afterwards.
    /// </summary>
    /// <param name="task">The task as persisted.</param>
    public static TaskAccounting EffectiveAccounting(TaskState task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Accounting ?? DeriveAccounting(task);
    }

    /// <summary>ADR-0040 §5.4: tokens from every recorded model call (plans and steps hold disjoint records), executable steps only, revisions after 0.</summary>
    internal static TaskAccounting DeriveAccounting(TaskState task) => new(
        task.Plans.Sum(plan => RecordedTokens(plan.ModelCalls)) + task.Steps.Sum(step => RecordedTokens(step.ModelCalls)),
        task.Steps.Count(step => !IsSyntheticFailureStep(step)),
        Math.Max(0, task.Plans.Count - 1));

    /// <summary>Whether <paramref name="step"/> is a runtime-authored record rather than an executed or proposed step (ADR-0040 §5.2, ADR-0051 §13): never counted against a step budget.</summary>
    internal static bool IsSyntheticFailureStep(PlanStep step) =>
        step.ToolCall is null
        && step.Description is ModelFailureStepDescription or RuntimeFailureStepDescription or NotStartedStepDescription
            or TokenBudgetStepDescription or EvidenceReadLimitStepDescription or AttemptDurationStepDescription
            or ExecutionInterruptedStepDescription or MutationReconciledStepDescription;

    /// <summary>Whether <paramref name="step"/> is the runtime-authored history of a mutation reconciled as already applied (ADR-0051 §9.5).</summary>
    internal static bool IsMutationReconciledStep(PlanStep step) =>
        step.ToolCall is null && step.Description == MutationReconciledStepDescription;

    /// <summary>Prompt plus completion tokens of every recorded model call that reported usage.</summary>
    internal static long RecordedTokens(IReadOnlyList<ModelCallRecord>? calls) =>
        calls?.Sum(call => (long)(call.Usage?.PromptTokens ?? 0) + (call.Usage?.CompletionTokens ?? 0)) ?? 0;
}
