// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging;

namespace bOps.Runtime;

/// <summary>
/// ADR-0051: the administrator's recovery of an orphaned execution attempt, the reconciliation of a task's unsettled mutations,
/// and the journal-aware evaluation of a resume. None of them executes a side-effecting tool; nothing here runs at startup.
/// </summary>
public sealed partial class AgentRunner
{
    /// <summary>The bound of an administrator's reconciliation note, which goes to the audit log only (ADR-0051 §9.1).</summary>
    public const int MaxReconcileNoteLength = 500;

    /// <summary>
    /// Decides whether <paramref name="task"/> may be resumed now, with its mutation journal (ADR-0040 §3 as amended by ADR-0051
    /// §9.4). The decision the API and the CLI show; the acquisition re-checks the journal atomically.
    /// </summary>
    /// <param name="task">The task as persisted.</param>
    /// <param name="ct">Cancels the journal read.</param>
    public async Task<TaskResumeDecision> EvaluateResumeWithJournalAsync(TaskState task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var journalStore = taskStore as ITaskMutationJournalStore;
        return TaskResumePolicy.Evaluate(task, options, journalStore is not null, await ReadJournalAsync(task, journalStore, ct));
    }

    /// <summary>PRE-4's projection with the task's mutation journal as an additive input: an unsettled planned step is <see cref="ProjectedStepStatus.OutcomeUnknown"/>.</summary>
    /// <param name="task">The task as persisted.</param>
    /// <param name="executing">Whether this host holds an execution attempt of the task.</param>
    /// <param name="journal">The task's journal entries, or <c>null</c> when none are known.</param>
    public ProjectedPlan? ProjectExecutionPlan(TaskState task, bool executing, IReadOnlyList<TaskMutationJournalEntry>? journal) =>
        ExecutionPlanProjector.Project(task, executing, ManifestOf, journal);

    /// <summary>Decides whether an administrator's recovery of <paramref name="task"/> would be accepted now (ADR-0051 §8.3), without a request.</summary>
    /// <param name="task">The task as persisted.</param>
    /// <param name="executingHere">Whether this host holds an execution attempt of the task.</param>
    public TaskRecoveryDecision EvaluateRecovery(TaskState task, bool executingHere) =>
        TaskRecoveryPolicy.Evaluate(task, executingHere, taskStore is ITaskTransitionStore, taskStore is ITaskMutationJournalStore, null);

    /// <summary>
    /// An administrator's recovery of an orphaned <see cref="AgentTaskStatus.Running"/> execution attempt (ADR-0051 §8.3): the fenced
    /// transition <c>(Running, N) → (Failed, N)</c> with <see cref="TaskTerminalKind.ExecutionInterrupted"/> and a synthetic, uncounted
    /// <c>Execution interrupted</c> step. It executes no tool, runs no verification, asks no approval and changes no journal entry. Its
    /// safety rests on the journal's fencing, never on proof that the old executor is dead: after it, that executor can neither commit
    /// an intent nor settle one. Every outcome for a stored task is audited.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="executionAttempt">The execution attempt the administrator was shown.</param>
    /// <param name="administrator">Who recovers it.</param>
    /// <param name="executingHere">Whether this host holds an execution attempt of the task (then the recovery is refused).</param>
    /// <param name="ct">Cancels the read; once the transition is attempted it is not abandoned half-way.</param>
    public async Task<TaskRecoveryResult> TryRecoverAsync(
        Guid taskId, int executionAttempt, ActorIdentity administrator, bool executingHere, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);

        var task = await taskStore.LoadAsync(taskId, ct);
        if (task is null)
        {
            return new TaskRecoveryResult(TaskRecoveryOutcome.NotFound, null, null);
        }

        var decision = TaskRecoveryPolicy.Evaluate(
            task, executingHere, taskStore is ITaskTransitionStore, taskStore is ITaskMutationJournalStore, executionAttempt);
        if (!decision.Recoverable)
        {
            return await RejectRecoveryAsync(task, administrator, decision.Refusal!, ct);
        }

        var recovered = task with
        {
            Status = AgentTaskStatus.Failed,
            TerminalReason = new TaskTerminalReason(TaskTerminalKind.ExecutionInterrupted),
            Accounting = TaskResumePolicy.EffectiveAccounting(task),
            Steps =
            [
                .. task.Steps,
                new PlanStep(task.Steps.Count, TaskResumePolicy.ExecutionInterruptedStepDescription, null, null,
                    $"Execution attempt {task.ExecutionAttempt} was interrupted and an administrator recovered it. The recovery ran nothing; " +
                    "an operation that attempt had already started may still complete.")
                {
                    ExecutionAttempt = task.ExecutionAttempt,
                },
            ],
        };

        if (!await ((ITaskTransitionStore)taskStore).TryTransitionAsync(recovered, AgentTaskStatus.Running, task.ExecutionAttempt, CancellationToken.None))
        {
            return await RejectRecoveryAsync(task, administrator, new TaskRecoveryRefusal(TaskRecoveryRefusal.RecoveryConflict,
                "The task changed while it was being recovered (another recovery or writer got there first)."), CancellationToken.None);
        }

        await WriteAuditAsync(
            LifecycleEvent(recovered, TaskLifecycleStage.RecoveryAccepted, administrator) with
            {
                PriorStatus = AgentTaskStatus.Running,
                TerminalKind = TaskTerminalKind.ExecutionInterrupted,
            },
            null, CancellationToken.None);

        // Best effort: an unsettled mutation found after the transition is surfaced in the audit log. The recovery itself decided on
        // the task row only, so a journal that cannot be read here changes nothing that was written.
        if (taskStore is ITaskMutationJournalStore journalStore && recovered.MutationJournalMode == TaskMutationJournalMode.Journaled)
        {
            try
            {
                var snapshot = await journalStore.LoadWithJournalAsync(taskId, CancellationToken.None);
                foreach (var entry in snapshot?.Entries.Where(entry => MutationJournalPolicy.IsUnsettled(entry.State)) ?? [])
                {
                    await WriteEntryAuditAsync(recovered, entry, administrator, TaskMutationAuditStage.AmbiguityDiscovered);
                }
            }
            catch (Exception readFailure) when (readFailure is not OperationCanceledException)
            {
                logger.LogWarning(readFailure, "Task {TaskId}: recovered, but its mutation journal could not be read to audit unsettled entries", taskId);
            }
        }

        return new TaskRecoveryResult(TaskRecoveryOutcome.Recovered, recovered, null);
    }

    /// <summary>
    /// An administrator's reconciliation of a task's unsettled mutations (ADR-0051 §9.1–§9.3), by a human identity, only while the
    /// task is not <see cref="AgentTaskStatus.Running"/>. <see cref="TaskReconcileAction.Verify"/> runs each entry's declared
    /// <see cref="RiskLevel.Read"/> verification — only with the call the task itself persisted, matched by tool and redacted-arguments
    /// hash, never reconstructed — and settles it only on <see cref="VerificationStatus.Confirmed"/>;
    /// <see cref="TaskReconcileAction.AcceptDone"/> and <see cref="TaskReconcileAction.Abandon"/> execute nothing. Every write is a
    /// per-entry compare-and-set; every refusal is audited.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="action">What to do.</param>
    /// <param name="administrator">The human deciding.</param>
    /// <param name="note">An optional note, at most <see cref="MaxReconcileNoteLength"/> characters, kept in the audit log only.</param>
    /// <param name="ct">Cancels the reads and the verification; a commit is never abandoned half-way.</param>
    public async Task<TaskReconcileResult> ReconcileMutationsAsync(
        Guid taskId, TaskReconcileAction action, ActorIdentity administrator, string? note = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);

        if (!Enum.IsDefined(action) || note?.Length > MaxReconcileNoteLength)
        {
            return await RejectReconcileAsync(taskId, null, administrator, TaskRecoveryRefusal.InvalidRequest,
                $"The action must be verify, acceptDone or abandon, and a note at most {MaxReconcileNoteLength} characters.", 0, ct);
        }

        var task = await taskStore.LoadAsync(taskId, ct);
        if (task is null)
        {
            return new TaskReconcileResult(TaskReconcileOutcome.NotFound, [], 0, null);
        }

        switch (task.Origin)
        {
            case TaskOrigin.Delegated:
                return await RejectReconcileAsync(taskId, task, administrator, TaskRecoveryRefusal.TaskDelegated,
                    "This task is a role of a delegated run; reconcile the delegation run instead.", 0, ct);
            case TaskOrigin.Ordinary:
                break;
            default:
                return await RejectReconcileAsync(taskId, task, administrator, TaskRecoveryRefusal.TaskOriginUnknown,
                    "This task was stored without a recorded origin.", 0, ct);
        }

        if (task.MutationJournalMode == TaskMutationJournalMode.Absent)
        {
            return await RejectReconcileAsync(taskId, task, administrator, TaskResumeRefusal.MutationJournalAbsent,
                "This task was stored before bOps kept a durable mutation journal; there is nothing it could reconcile.", 0, ct);
        }

        var journalStore = taskStore as ITaskMutationJournalStore;
        if (task.MutationJournalMode == TaskMutationJournalMode.Journaled && journalStore is null)
        {
            return await RejectReconcileAsync(taskId, task, administrator, TaskResumeRefusal.MutationJournalUnsupported,
                "This task journals its changes, but the configured task store has no mutation journal.", 0, ct);
        }

        IReadOnlyList<TaskMutationJournalEntry> entries = [];
        if (task.MutationJournalMode == TaskMutationJournalMode.Journaled)
        {
            try
            {
                var snapshot = await journalStore!.LoadWithJournalAsync(taskId, ct);
                task = snapshot?.Task ?? task;
                entries = snapshot?.Entries ?? [];
            }
            catch (Exception readFailure) when (readFailure is not OperationCanceledException)
            {
                logger.LogWarning(readFailure, "Task {TaskId}: the mutation journal could not be read for a reconciliation", taskId);
                return await RejectReconcileAsync(taskId, task, administrator, TaskResumeRefusal.MutationJournalUnavailable,
                    "The task's mutation journal could not be read. Retry later.", 0, ct);
            }
        }

        if (task.Status == AgentTaskStatus.Running)
        {
            return await RejectReconcileAsync(taskId, task, administrator, TaskRecoveryRefusal.TaskRunning,
                "The task is stored as Running. An administrator must recover it before its changes are reconciled.", entries.Count(IsUnsettled), ct);
        }

        var unsettled = entries.Where(IsUnsettled).OrderBy(entry => entry.Sequence).ToList();
        if (unsettled.Count == 0)
        {
            return await RejectReconcileAsync(taskId, task, administrator, TaskRecoveryRefusal.NothingToReconcile,
                "The task has no change whose outcome is unknown.", 0, ct);
        }

        if (!SeparationOfDuties.IsHumanApprover(administrator, []))
        {
            return await RejectReconcileAsync(taskId, task, administrator, TaskRecoveryRefusal.ReconcileNotHuman,
                "Reconciliation is a human decision; an agent or the runtime cannot make it.", unsettled.Count, ct);
        }

        return action == TaskReconcileAction.Verify
            ? await VerifyMutationsAsync(task, unsettled, administrator, note, ct)
            : await DecideMutationsAsync(task, unsettled, action, administrator, note);
    }

    private static bool IsUnsettled(TaskMutationJournalEntry entry) => MutationJournalPolicy.IsUnsettled(entry.State);

    /// <summary>ADR-0051 §9.1 <c>verify</c>: each unsettled entry, in sequence order, its own verification and its own compare-and-set.</summary>
    private async Task<TaskReconcileResult> VerifyMutationsAsync(
        TaskState task, List<TaskMutationJournalEntry> unsettled, ActorIdentity administrator, string? note, CancellationToken ct)
    {
        var results = new List<TaskReconciledMutation>();
        foreach (var entry in unsettled)
        {
            await WriteEntryAuditAsync(task, entry, administrator, TaskMutationAuditStage.VerificationAttempted, note: note);
            var (status, reasonCode) = await VerifyForReconciliationAsync(task, entry, administrator, ct);
            var (state, reconciliationAction) = MutationJournalPolicy.VerificationResult(status);
            var reconciliation = new StepReconciliation(reconciliationAction, status, ActorIdentity.RuntimeSystem, timeProvider.GetUtcNow());

            if (!await ((ITaskMutationJournalStore)taskStore).TryReconcileAsync(task.Id, task.Status, task.ExecutionAttempt,
                    [new TaskMutationResolution(entry.Intent.Key, entry.State, state, reconciliation)], CancellationToken.None))
            {
                var stillUnsettled = unsettled.Count - results.Count(result => !MutationJournalPolicy.IsUnsettled(result.State));
                var refused = await RejectReconcileAsync(task.Id, task, administrator, TaskRecoveryRefusal.ReconcileConflict,
                    "A change was reconciled by someone else, or the task changed, while it was being verified.", stillUnsettled, CancellationToken.None);
                return refused with { Results = results };
            }

            results.Add(new TaskReconciledMutation(entry.Intent.Key, state, reconciliation, reasonCode));
            await WriteEntryAuditAsync(task, entry with { State = state, Reconciliation = reconciliation }, administrator,
                TaskMutationAuditStage.Reconciled, reasonCode: reasonCode, note: note);
        }

        return new TaskReconcileResult(TaskReconcileOutcome.Reconciled, results,
            results.Count(result => MutationJournalPolicy.IsUnsettled(result.State)), null);
    }

    /// <summary>ADR-0051 §9.1 <c>acceptDone</c> / <c>abandon</c>: every unsettled entry in one compare-and-set; nothing is executed.</summary>
    private async Task<TaskReconcileResult> DecideMutationsAsync(
        TaskState task, List<TaskMutationJournalEntry> unsettled, TaskReconcileAction action, ActorIdentity administrator, string? note)
    {
        var accepted = action == TaskReconcileAction.AcceptDone;
        var now = timeProvider.GetUtcNow();
        var decided = unsettled.Select(entry => (Entry: entry, Reconciliation: new StepReconciliation(
            accepted ? ReconciliationAction.OperatorAcceptedDone : ReconciliationAction.OperatorAbandoned,
            entry.Reconciliation?.Verification ?? entry.Outcome?.Verification, administrator, now))).ToList();
        var state = accepted ? TaskMutationState.ReconciledDone : TaskMutationState.Abandoned;

        if (!await ((ITaskMutationJournalStore)taskStore).TryReconcileAsync(task.Id, task.Status, task.ExecutionAttempt,
                [.. decided.Select(item => new TaskMutationResolution(item.Entry.Intent.Key, item.Entry.State, state, item.Reconciliation))],
                CancellationToken.None))
        {
            return await RejectReconcileAsync(task.Id, task, administrator, TaskRecoveryRefusal.ReconcileConflict,
                "A change was reconciled by someone else, or the task changed, while it was being reconciled.", unsettled.Count, CancellationToken.None);
        }

        foreach (var (entry, reconciliation) in decided)
        {
            await WriteEntryAuditAsync(task, entry with { State = state, Reconciliation = reconciliation }, administrator,
                TaskMutationAuditStage.Reconciled, note: note);
        }

        return new TaskReconcileResult(TaskReconcileOutcome.Reconciled,
            [.. decided.Select(item => new TaskReconciledMutation(item.Entry.Intent.Key, state, item.Reconciliation, null))], 0, null);
    }

    /// <summary>
    /// ADR-0051 §9.3: the reconciliation verification of one entry. It uses only the call the task itself persisted for
    /// <c>(attempt, step)</c>, with the same tool and the same redacted-arguments hash, and only the verifier declared when the call
    /// ran; arguments are never reconstructed. It runs exactly the post-action verification path (fresh entitlement, bounded
    /// timeout, failures contained). Anything else is <see cref="VerificationStatus.Inconclusive"/> — never <see cref="VerificationStatus.Confirmed"/> by default.
    /// </summary>
    private async Task<(VerificationStatus Status, string? ReasonCode)> VerifyForReconciliationAsync(
        TaskState task, TaskMutationJournalEntry entry, ActorIdentity administrator, CancellationToken ct)
    {
        var key = entry.Intent.Key;
        var step = task.Steps.FirstOrDefault(candidate =>
            (candidate.ExecutionAttempt ?? 1) == key.ExecutionAttempt
            && candidate.Index == key.StepIndex
            && candidate.ToolCall is { ToolNameError: null, ArgumentsError: null } call
            && (string.Equals(call.ToolName, entry.Intent.ToolName, StringComparison.Ordinal)
                || string.Equals(ManifestOf(call.ToolName)?.Name, entry.Intent.ToolName, StringComparison.Ordinal)));
        if (step?.ToolCall is not { } persistedCall)
        {
            return (VerificationStatus.Inconclusive, "arguments_unavailable");
        }

        var registration = registry.ResolveForExecution(entry.Intent.ToolName);
        if (registration?.Tool is not IVerifiableTool verifiable
            || registration.Tool.Manifest.Verification is not { } spec
            || !string.Equals(spec.VerifyToolName, entry.Intent.VerificationToolName, StringComparison.Ordinal))
        {
            return (VerificationStatus.Inconclusive, "verification_unavailable");
        }

        if (!string.Equals(MutationArgumentsHash(registration.Tool.Manifest, persistedCall.Arguments), entry.Intent.ArgumentsHash, StringComparison.Ordinal))
        {
            return (VerificationStatus.Inconclusive, "arguments_unavailable");
        }

        try
        {
            var outcome = await EvaluateVerificationAsync(verifiable, spec, persistedCall,
                new ToolExecutionContext(NodeId.Local, task.Id, administrator), key.StepIndex, administrator, null, null, ct);
            return (outcome.Status, outcome.Status == VerificationStatus.Confirmed ? null : "not_confirmed");
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(failure, "Task {TaskId}: the reconciliation verification of step {StepIndex} failed", task.Id, key.StepIndex);
            return (VerificationStatus.Inconclusive, "verification_failed");
        }
    }

    /// <summary>The task's journal entries for a resume decision: empty when it keeps none, <c>null</c> when they cannot be read.</summary>
    private async Task<IReadOnlyList<TaskMutationJournalEntry>?> ReadJournalAsync(
        TaskState task, ITaskMutationJournalStore? journalStore, CancellationToken ct)
    {
        if (journalStore is null || task.MutationJournalMode != TaskMutationJournalMode.Journaled)
        {
            return [];
        }

        try
        {
            return (await journalStore.LoadWithJournalAsync(task.Id, ct))?.Entries ?? [];
        }
        catch (Exception readFailure) when (readFailure is not OperationCanceledException)
        {
            logger.LogWarning(readFailure, "Task {TaskId}: the mutation journal could not be read", task.Id);
            return null;
        }
    }

    /// <summary>
    /// The runtime-authored history of a mutation reconciled as already applied (ADR-0051 §9.5): no tool call, never counted, the
    /// same text whenever it is rebuilt.
    /// </summary>
    internal static PlanStep MutationReconciledStep(TaskMutationJournalEntry entry, int index, int executionAttempt)
    {
        var how = entry.Reconciliation?.Action == ReconciliationAction.VerifiedDone
            ? "its declared verification confirmed the effect"
            : $"an administrator accepted it as already applied (verification: {entry.Reconciliation?.Verification?.ToString() ?? "none"})";
        var fingerprint = entry.Intent.ArgumentsHash.Length > 12 ? entry.Intent.ArgumentsHash[..12] : entry.Intent.ArgumentsHash;
        return new PlanStep(index, TaskResumePolicy.MutationReconciledStepDescription, null, null,
            $"Runtime notice: the change '{entry.Intent.ToolName}' of execution attempt {entry.Intent.Key.ExecutionAttempt}, step " +
            $"{entry.Intent.Key.StepIndex} (arguments fingerprint {fingerprint}) stopped with an unknown outcome and was reconciled: {how}. " +
            "It may already have changed the system. bOps will not repeat it automatically; an identical call needs explicit human approval.")
        {
            ExecutionAttempt = executionAttempt,
        };
    }

    private async Task<TaskRecoveryResult> RejectRecoveryAsync(TaskState task, ActorIdentity administrator, TaskRecoveryRefusal refusal, CancellationToken ct)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Task {TaskId}: recovery refused ({Code})", task.Id, refusal.Code);
        }

        await WriteAuditAsync(
            LifecycleEvent(task, TaskLifecycleStage.RecoveryRejected, administrator) with { PriorStatus = task.Status, RefusalCode = refusal.Code },
            null, ct);
        return new TaskRecoveryResult(TaskRecoveryOutcome.Refused, task, refusal);
    }

    private async Task<TaskReconcileResult> RejectReconcileAsync(
        Guid taskId, TaskState? task, ActorIdentity administrator, string code, string message, int unsettledCount, CancellationToken ct)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Task {TaskId}: reconciliation refused ({Code})", taskId, code);
        }

        await audit.WriteAsync(new TaskMutationAuditEvent
        {
            TimestampUtc = timeProvider.GetUtcNow(),
            Node = NodeId.Local,
            TaskId = taskId,
            StepIndex = -1,
            Actor = administrator,
            Stage = TaskMutationAuditStage.ReconcileRejected,
            ExecutionAttempt = task?.ExecutionAttempt ?? 0,
            ReasonCode = code,
        }, ct);
        return new TaskReconcileResult(TaskReconcileOutcome.Refused, [], unsettledCount, new TaskRecoveryRefusal(code, message));
    }

    private Task WriteEntryAuditAsync(
        TaskState task, TaskMutationJournalEntry entry, ActorIdentity actor, TaskMutationAuditStage stage, string? reasonCode = null, string? note = null) =>
        audit.WriteAsync(new TaskMutationAuditEvent
        {
            TimestampUtc = timeProvider.GetUtcNow(),
            Node = NodeId.Local,
            TaskId = task.Id,
            StepIndex = entry.Intent.Key.StepIndex,
            Actor = actor,
            Stage = stage,
            ExecutionAttempt = entry.Intent.Key.ExecutionAttempt,
            Sequence = entry.Sequence,
            Tool = entry.Intent.ToolName,
            ArgumentsHash = entry.Intent.ArgumentsHash,
            OutcomeKind = entry.Outcome?.Kind,
            ToolOutcome = entry.Outcome?.ToolOutcome,
            Verification = stage == TaskMutationAuditStage.Reconciled ? entry.Reconciliation?.Verification : entry.Outcome?.Verification,
            State = entry.State,
            ReconciliationAction = stage == TaskMutationAuditStage.Reconciled ? entry.Reconciliation?.Action : null,
            ResolvedBy = stage == TaskMutationAuditStage.Reconciled ? entry.Reconciliation?.ResolvedBy : null,
            ReasonCode = reasonCode,
            Note = note,
        }, CancellationToken.None);
}
