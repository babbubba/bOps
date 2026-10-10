// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// Whether an ordinary task journals its side-effecting tool calls (ADR-0051 §8.1, §11). Set by the runtime when it creates
/// the task and never changed afterwards. The numeric values are persisted and must never be reordered.
/// </summary>
public enum TaskMutationJournalMode
{
    /// <summary>No journal was kept: every task stored before ADR-0051, and every delegated task. Never assumed safe.</summary>
    Absent = 0,

    /// <summary>Every non-<see cref="RiskLevel.Read"/> call of the task is preceded by a durable intent and followed by a durable outcome.</summary>
    Journaled = 1,

    /// <summary>The task was created on a store without <see cref="ITaskMutationJournalStore"/>: it may never run a non-<see cref="RiskLevel.Read"/> tool.</summary>
    MutationsDisabled = 2,
}

/// <summary>How a journaled invocation ended (ADR-0051 §4.1). The numeric values are persisted and must never be reordered.</summary>
public enum MutationOutcomeKind
{
    /// <summary>After the intent the runtime did not call the tool. Known not executed.</summary>
    NotInvoked = 0,

    /// <summary>The invocation ended and the runtime observed its end. Known executed; not a claim that the effect succeeded.</summary>
    Returned = 1,

    /// <summary>The runtime stopped waiting, or the tool reported its own timeout. Unknown.</summary>
    TimedOut = 2,

    /// <summary>The task was cancelled while the tool was invoked. Unknown.</summary>
    Cancelled = 3,

    /// <summary>The attempt-duration budget interrupted the invocation. Unknown.</summary>
    Interrupted = 4,
}

/// <summary>
/// The state of one journal entry (ADR-0051 §4.3), computed by the runtime and persisted by the store, which uses it only in
/// predicates. <see cref="Pending"/>, <see cref="Ambiguous"/> and <see cref="Escalated"/> are unsettled. The numeric values are
/// persisted and must never be reordered.
/// </summary>
public enum TaskMutationState
{
    /// <summary>Intent committed, no outcome: the operation may have happened.</summary>
    Pending = 0,

    /// <summary>Known not executed, or known executed (an observed end, or an unknown end whose verification confirmed the effect).</summary>
    Settled = 1,

    /// <summary>An unknown outcome whose verification did not confirm the effect.</summary>
    Ambiguous = 2,

    /// <summary>A reconciliation verification ran and did not confirm the effect.</summary>
    Escalated = 3,

    /// <summary>Reconciled as already applied: verified, or accepted by an administrator.</summary>
    ReconciledDone = 4,

    /// <summary>An administrator abandoned the task; it is never resumable again.</summary>
    Abandoned = 5,
}

/// <summary>The identity of one journaled mutation (ADR-0051 §5.1): the task, the execution attempt and the step index.</summary>
public sealed record TaskMutationKey
{
    /// <summary>Creates a mutation key.</summary>
    /// <param name="TaskId">The task.</param>
    /// <param name="ExecutionAttempt">The execution attempt that committed the intent.</param>
    /// <param name="StepIndex">The step index of the call within the task.</param>
    public TaskMutationKey(Guid TaskId, int ExecutionAttempt, int StepIndex)
    {
        this.TaskId = TaskId;
        this.ExecutionAttempt = ExecutionAttempt;
        this.StepIndex = StepIndex;
    }

    /// <summary>The task.</summary>
    public Guid TaskId { get; init; }

    /// <summary>The execution attempt that committed the intent.</summary>
    public int ExecutionAttempt { get; init; }

    /// <summary>The step index of the call within the task.</summary>
    public int StepIndex { get; init; }
}

/// <summary>
/// The first durable write of a journaled mutation, committed before the tool is invoked (ADR-0051 §5.2). It names what was
/// about to run and never carries the arguments, a projection of them, credentials or output.
/// </summary>
public sealed record TaskMutationIntent
{
    /// <summary>The mutation's identity.</summary>
    public required TaskMutationKey Key { get; init; }

    /// <summary>The canonical registered tool name.</summary>
    public required string ToolName { get; init; }

    /// <summary>The lowercase hex SHA-256 of the canonical arguments with every sensitive value replaced by the redaction marker.</summary>
    public required string ArgumentsHash { get; init; }

    /// <summary>The tool's declared risk.</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>The verification tool the manifest declared when the call ran.</summary>
    public string? VerificationToolName { get; init; }

    /// <summary>The plan revision in force; context for display only, never identity.</summary>
    public int? PlanRevision { get; init; }

    /// <summary>The planned step the call attempted; context for display only, never identity.</summary>
    public int? PlannedStepIndex { get; init; }

    /// <summary>When the intent was committed.</summary>
    public required DateTimeOffset IntentAtUtc { get; init; }
}

/// <summary>The second durable write of a journaled mutation: how the invocation ended (ADR-0051 §5.3). Carries no message or output.</summary>
public sealed record TaskMutationOutcome
{
    /// <summary>Creates an outcome.</summary>
    /// <param name="Kind">How the invocation ended.</param>
    /// <param name="ToolOutcome">For <see cref="MutationOutcomeKind.Returned"/> and <see cref="MutationOutcomeKind.TimedOut"/>, what the tool reported.</param>
    /// <param name="ToolFailureKind">The tool's typed failure kind, when it reported one.</param>
    /// <param name="Verification">The post-action verification status, when one ran.</param>
    /// <param name="AtUtc">When the outcome was observed.</param>
    /// <exception cref="ArgumentException"><paramref name="Kind"/> is not a defined outcome.</exception>
    public TaskMutationOutcome(
        MutationOutcomeKind Kind, ToolOutcome? ToolOutcome, ToolFailureKind? ToolFailureKind, VerificationStatus? Verification, DateTimeOffset AtUtc)
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentException("Unknown mutation outcome.", nameof(Kind));
        }

        this.Kind = Kind;
        this.ToolOutcome = ToolOutcome;
        this.ToolFailureKind = ToolFailureKind;
        this.Verification = Verification;
        this.AtUtc = AtUtc;
    }

    /// <summary>How the invocation ended.</summary>
    public MutationOutcomeKind Kind { get; init; }

    /// <summary>What the tool reported, when it returned.</summary>
    public ToolOutcome? ToolOutcome { get; init; }

    /// <summary>The tool's typed failure kind, when it reported one.</summary>
    public ToolFailureKind? ToolFailureKind { get; init; }

    /// <summary>The post-action verification status, when one ran.</summary>
    public VerificationStatus? Verification { get; init; }

    /// <summary>When the outcome was observed.</summary>
    public DateTimeOffset AtUtc { get; init; }
}

/// <summary>One persisted journal entry (ADR-0051 §5): the intent, its outcome when one was committed, its state and its reconciliation.</summary>
public sealed record TaskMutationJournalEntry
{
    /// <summary>The intent as committed.</summary>
    public required TaskMutationIntent Intent { get; init; }

    /// <summary>The per-task order of the entry, starting at 1.</summary>
    public required int Sequence { get; init; }

    /// <summary>How the invocation ended, or <c>null</c> when no outcome was ever committed.</summary>
    public TaskMutationOutcome? Outcome { get; init; }

    /// <summary>The entry's state.</summary>
    public required TaskMutationState State { get; init; }

    /// <summary>How an unsettled entry was reconciled, or <c>null</c> while it never was (ADR-0030's record, reused unchanged).</summary>
    public StepReconciliation? Reconciliation { get; init; }

    /// <summary>For a <see cref="TaskMutationState.ReconciledDone"/> entry, the execution attempt whose acquisition recorded it in the task's history.</summary>
    public int? HistoryRecordedInAttempt { get; init; }
}

/// <summary>One per-entry reconciliation write (ADR-0051 §6.3): applied only while the entry is still in <see cref="ExpectedState"/>.</summary>
public sealed record TaskMutationResolution
{
    /// <summary>Creates a resolution.</summary>
    /// <param name="Key">The entry.</param>
    /// <param name="ExpectedState">The unsettled state the entry must still have.</param>
    /// <param name="NewState">The state written.</param>
    /// <param name="Reconciliation">The reconciliation record written.</param>
    public TaskMutationResolution(TaskMutationKey Key, TaskMutationState ExpectedState, TaskMutationState NewState, StepReconciliation Reconciliation)
    {
        ArgumentNullException.ThrowIfNull(Key);
        ArgumentNullException.ThrowIfNull(Reconciliation);
        this.Key = Key;
        this.ExpectedState = ExpectedState;
        this.NewState = NewState;
        this.Reconciliation = Reconciliation;
    }

    /// <summary>The entry.</summary>
    public TaskMutationKey Key { get; init; }

    /// <summary>The unsettled state the entry must still have.</summary>
    public TaskMutationState ExpectedState { get; init; }

    /// <summary>The state written.</summary>
    public TaskMutationState NewState { get; init; }

    /// <summary>The reconciliation record written.</summary>
    public StepReconciliation Reconciliation { get; init; }
}

/// <summary>A task and every journal entry of it, read in one consistent snapshot, in <see cref="TaskMutationJournalEntry.Sequence"/> order.</summary>
public sealed record TaskJournalSnapshot
{
    /// <summary>Creates a snapshot.</summary>
    /// <param name="Task">The task as persisted.</param>
    /// <param name="Entries">Its journal entries, in sequence order.</param>
    public TaskJournalSnapshot(TaskState Task, IReadOnlyList<TaskMutationJournalEntry> Entries)
    {
        ArgumentNullException.ThrowIfNull(Task);
        ArgumentNullException.ThrowIfNull(Entries);
        this.Task = Task;
        this.Entries = Entries;
    }

    /// <summary>The task as persisted.</summary>
    public TaskState Task { get; init; }

    /// <summary>Its journal entries, in sequence order.</summary>
    public IReadOnlyList<TaskMutationJournalEntry> Entries { get; init; }
}

/// <summary>
/// A task store's durable mutation intent journal (ADR-0051 §6, §11) — a capability in addition to
/// <see cref="ITaskTransitionStore"/>, which it extends because every journal write is fenced against the task row. Every method
/// is atomic at the persistence level, across threads and processes sharing the store; a store must never implement one as a
/// load, a check in process and a save. A task created on a store without this capability can never run a
/// non-<see cref="RiskLevel.Read"/> tool.
/// </summary>
public interface ITaskMutationJournalStore : ITaskTransitionStore
{
    /// <summary>Commits <paramref name="intent"/> as a <see cref="TaskMutationState.Pending"/> entry with the task's next sequence, only while the task is stored <c>(Running, intent's attempt)</c> and the key is free.</summary>
    /// <param name="intent">The intent.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    /// <returns><c>true</c> if committed; <c>false</c> if the task is not <c>(Running, attempt)</c> or the key exists, and nothing was written.</returns>
    Task<bool> TryRecordIntentAsync(TaskMutationIntent intent, CancellationToken ct = default);

    /// <summary>
    /// Commits the outcome of <paramref name="key"/> and, in the same transaction, replaces the task row with
    /// <paramref name="owningTask"/> — the attempt's state including the step that records the call — only while the entry has no
    /// outcome and no reconciliation and the task is stored <c>(Running, key's attempt)</c>. Both commit or neither does.
    /// </summary>
    /// <param name="key">The entry.</param>
    /// <param name="outcome">How the invocation ended.</param>
    /// <param name="state">The entry's new state, as the runtime classified it.</param>
    /// <param name="owningTask">The attempt's task state, <c>Running</c> under the key's attempt, including the step.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    /// <returns><c>true</c> if both were committed; <c>false</c> if a precondition failed, and nothing was written.</returns>
    Task<bool> TryRecordOutcomeAsync(TaskMutationKey key, TaskMutationOutcome outcome, TaskMutationState state, TaskState owningTask, CancellationToken ct = default);

    /// <summary>Reads the task and every journal entry of it in one read transaction.</summary>
    /// <param name="taskId">The task.</param>
    /// <param name="ct">Cancelled if the read should be abandoned.</param>
    /// <returns>The snapshot, or <c>null</c> when no task with that id is stored.</returns>
    Task<TaskJournalSnapshot?> LoadWithJournalAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Applies every resolution, only while the task is stored <c>(expectedStatus, expectedExecutionAttempt)</c> with a status
    /// other than <see cref="AgentTaskStatus.Running"/> and every named entry is still in its expected state. One failing
    /// precondition writes nothing.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="expectedStatus">The status the task must have.</param>
    /// <param name="expectedExecutionAttempt">The execution attempt the task must have.</param>
    /// <param name="resolutions">The per-entry writes.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    /// <returns><c>true</c> if every resolution was written; <c>false</c> otherwise, and nothing was written.</returns>
    Task<bool> TryReconcileAsync(Guid taskId, AgentTaskStatus expectedStatus, int expectedExecutionAttempt,
        IReadOnlyList<TaskMutationResolution> resolutions, CancellationToken ct = default);

    /// <summary>
    /// The journal-aware acquisition of a resume: replaces the task row with <paramref name="acquired"/> (Running, one attempt
    /// more) only while it is stored <c>(expectedStatus, expectedExecutionAttempt)</c>, no entry of it is
    /// <see cref="TaskMutationState.Pending"/>, <see cref="TaskMutationState.Ambiguous"/>, <see cref="TaskMutationState.Escalated"/>
    /// or <see cref="TaskMutationState.Abandoned"/>, and every entry of <paramref name="recordedInHistory"/> is
    /// <see cref="TaskMutationState.ReconciledDone"/> and not yet recorded; marks those entries recorded in the acquired attempt.
    /// </summary>
    /// <param name="acquired">The acquired state, carrying the history steps of the recorded entries.</param>
    /// <param name="expectedStatus">The status the task must have.</param>
    /// <param name="expectedExecutionAttempt">The execution attempt the task must have.</param>
    /// <param name="recordedInHistory">The entries whose history steps <paramref name="acquired"/> carries.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    /// <returns><c>true</c> if acquired; <c>false</c> otherwise, and nothing was written.</returns>
    Task<bool> TryAcquireAsync(TaskState acquired, AgentTaskStatus expectedStatus, int expectedExecutionAttempt,
        IReadOnlyList<TaskMutationKey> recordedInHistory, CancellationToken ct = default);
}

/// <summary>Which point of a journaled mutation a <see cref="TaskMutationAuditEvent"/> records (ADR-0051 §14.1). The numeric values never change.</summary>
public enum TaskMutationAuditStage
{
    /// <summary>The intent was durably committed.</summary>
    IntentCommitted = 0,

    /// <summary>The intent was not committed: superseded, store failure, or mutations disabled for the task.</summary>
    IntentNotCommitted = 1,

    /// <summary>The outcome was durably committed with its step.</summary>
    OutcomeCommitted = 2,

    /// <summary>The outcome was refused or could not be written; the event carries what the writer saw.</summary>
    OutcomeNotCommitted = 3,

    /// <summary>An unsettled mutation was found: an ambiguous in-process outcome, or an unsettled entry after a recovery.</summary>
    AmbiguityDiscovered = 4,

    /// <summary>A reconciliation verification was attempted.</summary>
    VerificationAttempted = 5,

    /// <summary>An entry was reconciled.</summary>
    Reconciled = 6,

    /// <summary>A reconciliation request was refused.</summary>
    ReconcileRejected = 7,
}

/// <summary>
/// A journaled mutation's lifecycle (ADR-0051 §14.1). Never carries arguments, output, error text, observations, credentials or
/// provider material. <see cref="AuditEvent.StepIndex"/> is the entry's step index, or -1 when the event concerns the task.
/// </summary>
public sealed record TaskMutationAuditEvent : AuditEvent
{
    /// <summary>What happened.</summary>
    public required TaskMutationAuditStage Stage { get; init; }

    /// <summary>The execution attempt concerned: the entry's, or the task's for a task-level event.</summary>
    public required int ExecutionAttempt { get; init; }

    /// <summary>The entry's sequence, when it is known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Sequence { get; init; }

    /// <summary>The tool, or <c>null</c> for a task-level event.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tool { get; init; }

    /// <summary>The redacted-canonical arguments hash, or <c>null</c> for a task-level event. Never the arguments.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArgumentsHash { get; init; }

    /// <summary>The outcome kind, for an outcome stage.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MutationOutcomeKind? OutcomeKind { get; init; }

    /// <summary>What the tool reported, when it returned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolOutcome? ToolOutcome { get; init; }

    /// <summary>The verification status concerned, when one ran.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VerificationStatus? Verification { get; init; }

    /// <summary>The entry's state after the event.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskMutationState? State { get; init; }

    /// <summary>For <see cref="TaskMutationAuditStage.Reconciled"/>, the reconciliation action.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReconciliationAction? ReconciliationAction { get; init; }

    /// <summary>For <see cref="TaskMutationAuditStage.Reconciled"/>, who reconciled.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActorIdentity? ResolvedBy { get; init; }

    /// <summary>A stable reason code (for example <c>superseded</c>, <c>store_failure</c>, <c>arguments_unavailable</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasonCode { get; init; }

    /// <summary>An administrator's note, bounded; audit only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}
