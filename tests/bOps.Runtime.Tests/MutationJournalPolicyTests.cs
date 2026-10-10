// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// F-25 / ADR-0051 §4: <see cref="MutationJournalPolicy"/> exhaustively — every outcome kind × every verification status (and none)
/// → journal state → knowledge / blocking / reconcilable, every persisted state, and the reconciliation verification rule.
/// </summary>
public sealed class MutationJournalPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<MutationOutcomeKind, VerificationStatus?, TaskMutationState, MutationKnowledge> Outcomes()
    {
        var data = new TheoryData<MutationOutcomeKind, VerificationStatus?, TaskMutationState, MutationKnowledge>();
        VerificationStatus?[] statuses =
            [null, VerificationStatus.Confirmed, VerificationStatus.Refuted, VerificationStatus.Inconclusive, VerificationStatus.NotApplicable];
        foreach (var status in statuses)
        {
            data.Add(MutationOutcomeKind.NotInvoked, status, TaskMutationState.Settled, MutationKnowledge.KnownNotExecuted);
            data.Add(MutationOutcomeKind.Returned, status, TaskMutationState.Settled, MutationKnowledge.KnownExecuted);
            foreach (var unknown in new[] { MutationOutcomeKind.TimedOut, MutationOutcomeKind.Cancelled, MutationOutcomeKind.Interrupted })
            {
                data.Add(unknown, status,
                    status == VerificationStatus.Confirmed ? TaskMutationState.Settled : TaskMutationState.Ambiguous,
                    status == VerificationStatus.Confirmed ? MutationKnowledge.KnownExecuted : MutationKnowledge.Unknown);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void EveryOutcomeAndVerification_ClassifiesExactly(
        MutationOutcomeKind kind, VerificationStatus? verification, TaskMutationState state, MutationKnowledge knowledge)
    {
        var classification = MutationJournalPolicy.Classify(kind, verification);

        Assert.Equal(state, MutationJournalPolicy.OutcomeState(kind, verification));
        Assert.Equal(new MutationClassification(state, knowledge, state == TaskMutationState.Ambiguous, state == TaskMutationState.Ambiguous),
            classification);
    }

    [Fact]
    public void TheMatrixCoversEveryOutcomeKind_AndEveryVerificationStatus()
    {
        var rows = Outcomes().Select(row => ((MutationOutcomeKind)row[0]!, (VerificationStatus?)row[1])).ToHashSet();

        foreach (var kind in Enum.GetValues<MutationOutcomeKind>())
        {
            Assert.Contains((kind, (VerificationStatus?)null), rows);
            foreach (var status in Enum.GetValues<VerificationStatus>())
            {
                Assert.Contains((kind, (VerificationStatus?)status), rows);
            }
        }
    }

    [Fact]
    public void AnIntentWithNoOutcome_IsPendingUnknownBlockingAndReconcilable()
    {
        Assert.Equal(new MutationClassification(TaskMutationState.Pending, MutationKnowledge.Unknown, true, true),
            MutationJournalPolicy.Classify(null, null));
        Assert.Equal(new MutationClassification(TaskMutationState.Pending, MutationKnowledge.Unknown, true, true),
            MutationJournalPolicy.Classify(null, VerificationStatus.Confirmed));
    }

    [Fact]
    public void AnUndefinedOutcomeKind_FailsClosedToAmbiguous()
    {
        Assert.Equal(TaskMutationState.Ambiguous, MutationJournalPolicy.OutcomeState((MutationOutcomeKind)99, VerificationStatus.Confirmed));
    }

    public static TheoryData<TaskMutationState, MutationOutcomeKind?, ReconciliationAction?, MutationKnowledge, bool, bool> Entries() => new()
    {
        { TaskMutationState.Pending, null, null, MutationKnowledge.Unknown, true, true },
        { TaskMutationState.Settled, MutationOutcomeKind.NotInvoked, null, MutationKnowledge.KnownNotExecuted, false, false },
        { TaskMutationState.Settled, MutationOutcomeKind.Returned, null, MutationKnowledge.KnownExecuted, false, false },
        { TaskMutationState.Settled, MutationOutcomeKind.TimedOut, null, MutationKnowledge.KnownExecuted, false, false },
        { TaskMutationState.Ambiguous, MutationOutcomeKind.TimedOut, null, MutationKnowledge.Unknown, true, true },
        { TaskMutationState.Ambiguous, MutationOutcomeKind.Cancelled, null, MutationKnowledge.Unknown, true, true },
        { TaskMutationState.Escalated, null, ReconciliationAction.EscalatedToOperator, MutationKnowledge.Unknown, true, true },
        { TaskMutationState.ReconciledDone, null, ReconciliationAction.VerifiedDone, MutationKnowledge.KnownExecuted, false, false },
        { TaskMutationState.ReconciledDone, MutationOutcomeKind.Interrupted, ReconciliationAction.OperatorAcceptedDone, MutationKnowledge.Unknown, false, false },
        { TaskMutationState.Abandoned, null, ReconciliationAction.OperatorAbandoned, MutationKnowledge.Unknown, true, false },
    };

    [Theory]
    [MemberData(nameof(Entries))]
    public void EveryPersistedState_ClassifiesExactly(
        TaskMutationState state, MutationOutcomeKind? outcome, ReconciliationAction? action, MutationKnowledge knowledge, bool blocks, bool reconcilable)
    {
        var entry = new TaskMutationJournalEntry
        {
            Intent = new TaskMutationIntent
            {
                Key = new TaskMutationKey(Guid.NewGuid(), 1, 0), ToolName = "test.mutate", ArgumentsHash = "h", Risk = RiskLevel.Medium,
                IntentAtUtc = Now,
            },
            Sequence = 1,
            State = state,
            Outcome = outcome is { } kind ? new TaskMutationOutcome(kind, null, null, null, Now) : null,
            Reconciliation = action is { } reconciled
                ? new StepReconciliation(reconciled, VerificationStatus.Inconclusive, ActorIdentity.RuntimeSystem, Now)
                : null,
        };

        Assert.Equal(new MutationClassification(state, knowledge, blocks, reconcilable), MutationJournalPolicy.Classify(entry));
        Assert.Equal(blocks, MutationJournalPolicy.BlocksResume(state));
        Assert.Equal(reconcilable, MutationJournalPolicy.IsUnsettled(state));
    }

    [Fact]
    public void EveryState_HasABlockingAndReconcilableAnswer()
    {
        Assert.Equal(
            [TaskMutationState.Pending, TaskMutationState.Ambiguous, TaskMutationState.Escalated, TaskMutationState.Abandoned],
            Enum.GetValues<TaskMutationState>().Where(MutationJournalPolicy.BlocksResume));
        Assert.Equal(
            [TaskMutationState.Pending, TaskMutationState.Ambiguous, TaskMutationState.Escalated],
            Enum.GetValues<TaskMutationState>().Where(MutationJournalPolicy.IsUnsettled));
    }

    [Theory]
    [InlineData(VerificationStatus.Confirmed, TaskMutationState.ReconciledDone, ReconciliationAction.VerifiedDone)]
    [InlineData(VerificationStatus.Refuted, TaskMutationState.Escalated, ReconciliationAction.EscalatedToOperator)]
    [InlineData(VerificationStatus.Inconclusive, TaskMutationState.Escalated, ReconciliationAction.EscalatedToOperator)]
    [InlineData(VerificationStatus.NotApplicable, TaskMutationState.Escalated, ReconciliationAction.EscalatedToOperator)]
    public void AReconciliationVerification_SettlesOnlyOnConfirmed(VerificationStatus status, TaskMutationState state, ReconciliationAction action)
    {
        Assert.Equal((state, action), MutationJournalPolicy.VerificationResult(status));
    }

    [Theory]
    [InlineData(ToolOutcome.Success, MutationOutcomeKind.Returned)]
    [InlineData(ToolOutcome.Failure, MutationOutcomeKind.Returned)]
    [InlineData(ToolOutcome.Timeout, MutationOutcomeKind.TimedOut)]
    public void AToolsOwnTimeout_IsAnUnknownOutcome(ToolOutcome outcome, MutationOutcomeKind kind)
    {
        Assert.Equal(kind, MutationJournalPolicy.ReturnedKind(new ToolCallResult(outcome, null, null)));
    }
}
