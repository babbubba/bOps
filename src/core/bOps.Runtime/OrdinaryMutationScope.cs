// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The ordinary-task mutation journal of one execution attempt (ADR-0051 §3.2): created only by the ordinary model-driven loop
/// (<see cref="AgentRunner"/>'s initial run and acquired resume), never for a delegated run, a skill run or a verification. For an
/// ordinary task it is never <c>null</c>; a task that cannot journal refuses every non-<see cref="RiskLevel.Read"/> call at the
/// journal-mode gate.
/// </summary>
internal sealed class OrdinaryMutationScope
{
    internal OrdinaryMutationScope(
        Guid taskId, int executionAttempt, TaskMutationJournalMode mode, ITaskMutationJournalStore? store,
        IReadOnlyList<TaskMutationJournalEntry> reconciledDone)
    {
        TaskId = taskId;
        ExecutionAttempt = executionAttempt;
        Mode = mode;
        Store = store;
        ReconciledDone = reconciledDone;
    }

    internal Guid TaskId { get; }

    internal int ExecutionAttempt { get; }

    internal TaskMutationJournalMode Mode { get; }

    /// <summary>The store's journal capability; <c>null</c> when the store lacks it.</summary>
    internal ITaskMutationJournalStore? Store { get; }

    /// <summary>
    /// The task's <see cref="TaskMutationState.ReconciledDone"/> entries, read when the attempt started: no entry can become
    /// reconciled while the task is <see cref="AgentTaskStatus.Running"/>, so the set cannot change during the attempt.
    /// </summary>
    internal IReadOnlyList<TaskMutationJournalEntry> ReconciledDone { get; }

    /// <summary>Whether a non-<see cref="RiskLevel.Read"/> call may run: the task is journaled and the store can journal.</summary>
    internal bool CanJournal => Mode == TaskMutationJournalMode.Journaled && Store is not null;

    /// <summary>The planned step the next call attempts (context for the intent only, never identity).</summary>
    internal int? PlannedStepIndex { get; set; }

    /// <summary>The outcome the step executor classified; the loop commits it atomically with the step that records the call.</summary>
    internal PendingMutationOutcome? Pending { get; set; }

    /// <summary>
    /// The step recorded for a call stopped by the task's cancellation after its intent (not invoked, or invoked with an unknown
    /// outcome): the loop adds it to the run and commits its outcome before the cancellation propagates.
    /// </summary>
    internal PlanStep? CancelledStep { get; set; }

    /// <summary>The <see cref="ReconciledDone"/> entry the call is identical to (same tool, same redacted-arguments hash), if any.</summary>
    internal TaskMutationJournalEntry? DuplicateOf(string toolName, string argumentsHash) =>
        ReconciledDone.FirstOrDefault(entry =>
            string.Equals(entry.Intent.ToolName, toolName, StringComparison.Ordinal)
            && string.Equals(entry.Intent.ArgumentsHash, argumentsHash, StringComparison.Ordinal));

    /// <summary>Clears the per-call state before the next call.</summary>
    internal void BeginCall(int? plannedStepIndex)
    {
        PlannedStepIndex = plannedStepIndex;
        Pending = null;
        CancelledStep = null;
    }
}

/// <summary>An outcome classified by the step executor, waiting to be committed with its step.</summary>
/// <param name="Intent">The committed intent.</param>
/// <param name="Outcome">How the invocation ended.</param>
/// <param name="State">The journal state the outcome commits.</param>
internal sealed record PendingMutationOutcome(TaskMutationIntent Intent, TaskMutationOutcome Outcome, TaskMutationState State);
