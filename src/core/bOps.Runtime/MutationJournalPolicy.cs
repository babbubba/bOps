// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>What is known about whether a journaled mutation took place (ADR-0051 §4).</summary>
public enum MutationKnowledge
{
    /// <summary>The runtime proved it did not invoke the tool.</summary>
    KnownNotExecuted,

    /// <summary>The invocation is known to have run to its end, or its effect was confirmed by verification.</summary>
    KnownExecuted,

    /// <summary>It may or may not have happened. An administrator's acceptance does not change this.</summary>
    Unknown,
}

/// <summary>How the runtime classifies one journal outcome or entry.</summary>
/// <param name="State">The entry's journal state.</param>
/// <param name="Knowledge">What is known about it.</param>
/// <param name="BlocksResume">Whether it refuses an ordinary resume of its task.</param>
/// <param name="Reconcilable">Whether an administrator may reconcile it.</param>
public sealed record MutationClassification(TaskMutationState State, MutationKnowledge Knowledge, bool BlocksResume, bool Reconcilable);

/// <summary>
/// The one classification of the ordinary mutation journal (ADR-0051 §4): outcome and verification to journal state, journal state
/// to knowledge, blocking and reconcilability. Pure — no heuristic, no input beyond its arguments — and used alike by the runner,
/// the resume and recovery rules, the projection and the clients' view.
/// </summary>
public static class MutationJournalPolicy
{
    /// <summary>
    /// The fixed marker the duplicate guard (ADR-0051 §9.6) puts in the policy reason of a call identical to a mutation already
    /// reconciled as applied, so the approver is told before deciding.
    /// </summary>
    public const string DuplicateOfReconciledMarker = "[duplicate-of-reconciled-mutation]";

    /// <summary>
    /// The state an outcome commits (ADR-0051 §4.1–§4.3): <see cref="TaskMutationState.Settled"/> when the runtime proved it did not
    /// invoke the tool, observed the invocation end, or — for an unknown end — verification confirmed the effect; otherwise
    /// <see cref="TaskMutationState.Ambiguous"/>. <see cref="VerificationStatus.Refuted"/> and
    /// <see cref="VerificationStatus.Inconclusive"/> never settle an unknown end.
    /// </summary>
    /// <param name="kind">How the invocation ended.</param>
    /// <param name="verification">The post-action verification status, or <c>null</c> when none ran.</param>
    public static TaskMutationState OutcomeState(MutationOutcomeKind kind, VerificationStatus? verification) => kind switch
    {
        MutationOutcomeKind.NotInvoked => TaskMutationState.Settled,
        MutationOutcomeKind.Returned => TaskMutationState.Settled,
        MutationOutcomeKind.TimedOut or MutationOutcomeKind.Cancelled or MutationOutcomeKind.Interrupted =>
            verification == VerificationStatus.Confirmed ? TaskMutationState.Settled : TaskMutationState.Ambiguous,
        _ => TaskMutationState.Ambiguous,
    };

    /// <summary>The classification of a just-observed outcome (or of an intent with no outcome, when <paramref name="kind"/> is <c>null</c>).</summary>
    /// <param name="kind">How the invocation ended, or <c>null</c> when no outcome exists.</param>
    /// <param name="verification">The post-action verification status, or <c>null</c> when none ran.</param>
    public static MutationClassification Classify(MutationOutcomeKind? kind, VerificationStatus? verification)
    {
        if (kind is not { } outcome)
        {
            return Of(TaskMutationState.Pending, MutationKnowledge.Unknown);
        }

        var state = OutcomeState(outcome, verification);
        var knowledge = state != TaskMutationState.Settled
            ? MutationKnowledge.Unknown
            : outcome == MutationOutcomeKind.NotInvoked ? MutationKnowledge.KnownNotExecuted : MutationKnowledge.KnownExecuted;
        return Of(state, knowledge);
    }

    /// <summary>The classification of a persisted entry.</summary>
    /// <param name="entry">The entry.</param>
    public static MutationClassification Classify(TaskMutationJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var knowledge = entry.State switch
        {
            TaskMutationState.Settled when entry.Outcome?.Kind == MutationOutcomeKind.NotInvoked => MutationKnowledge.KnownNotExecuted,
            TaskMutationState.Settled when entry.Outcome is not null => MutationKnowledge.KnownExecuted,
            TaskMutationState.ReconciledDone when entry.Reconciliation?.Action == ReconciliationAction.VerifiedDone => MutationKnowledge.KnownExecuted,
            _ => MutationKnowledge.Unknown,
        };
        return Of(entry.State, knowledge);
    }

    /// <summary>Whether <paramref name="state"/> is unsettled: the mutation may have happened and nobody has decided what it did.</summary>
    /// <param name="state">The state.</param>
    public static bool IsUnsettled(TaskMutationState state) =>
        state is TaskMutationState.Pending or TaskMutationState.Ambiguous or TaskMutationState.Escalated;

    /// <summary>Whether <paramref name="state"/> refuses an ordinary resume: unsettled, or abandoned (permanently).</summary>
    /// <param name="state">The state.</param>
    public static bool BlocksResume(TaskMutationState state) => IsUnsettled(state) || state == TaskMutationState.Abandoned;

    /// <summary>The state and reconciliation action a reconciliation verification yields: only <see cref="VerificationStatus.Confirmed"/> settles.</summary>
    /// <param name="verification">The reconciliation verification's status.</param>
    public static (TaskMutationState State, ReconciliationAction Action) VerificationResult(VerificationStatus verification) =>
        verification == VerificationStatus.Confirmed
            ? (TaskMutationState.ReconciledDone, ReconciliationAction.VerifiedDone)
            : (TaskMutationState.Escalated, ReconciliationAction.EscalatedToOperator);

    /// <summary>The outcome kind of an invocation that returned a result: a tool's own timeout says only that it stopped waiting.</summary>
    /// <param name="result">The result the runtime observed.</param>
    internal static MutationOutcomeKind ReturnedKind(ToolCallResult result) =>
        result.Outcome == ToolOutcome.Timeout ? MutationOutcomeKind.TimedOut : MutationOutcomeKind.Returned;

    private static MutationClassification Of(TaskMutationState state, MutationKnowledge knowledge) =>
        new(state, knowledge, BlocksResume(state), IsUnsettled(state));
}
