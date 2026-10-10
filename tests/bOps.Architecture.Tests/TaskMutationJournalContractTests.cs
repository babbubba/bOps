// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Architecture.Tests;

/// <summary>
/// ADR-0051 §6.1, §11, §19: the durable mutation journal contracts of <c>bOps.Abstractions</c> — persisted enum values explicit and
/// append-only, the store capability exactly the five methods of §11, and every new record round-trips.
/// </summary>
public sealed class TaskMutationJournalContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void PersistedEnumValues_AreExplicitAndFrozen()
    {
        Assert.Equal([0, 1, 2], Enum.GetValues<TaskMutationJournalMode>().Select(value => (int)value));
        Assert.Equal((0, 1, 2), ((int)TaskMutationJournalMode.Absent, (int)TaskMutationJournalMode.Journaled, (int)TaskMutationJournalMode.MutationsDisabled));
        Assert.Equal((0, 1, 2, 3, 4), ((int)MutationOutcomeKind.NotInvoked, (int)MutationOutcomeKind.Returned, (int)MutationOutcomeKind.TimedOut,
            (int)MutationOutcomeKind.Cancelled, (int)MutationOutcomeKind.Interrupted));
        Assert.Equal((0, 1, 2, 3, 4, 5), ((int)TaskMutationState.Pending, (int)TaskMutationState.Settled, (int)TaskMutationState.Ambiguous,
            (int)TaskMutationState.Escalated, (int)TaskMutationState.ReconciledDone, (int)TaskMutationState.Abandoned));
        Assert.Equal((0, 1, 2, 3, 4, 5, 6, 7), ((int)TaskMutationAuditStage.IntentCommitted, (int)TaskMutationAuditStage.IntentNotCommitted,
            (int)TaskMutationAuditStage.OutcomeCommitted, (int)TaskMutationAuditStage.OutcomeNotCommitted, (int)TaskMutationAuditStage.AmbiguityDiscovered,
            (int)TaskMutationAuditStage.VerificationAttempted, (int)TaskMutationAuditStage.Reconciled, (int)TaskMutationAuditStage.ReconcileRejected));
        Assert.Equal((14, 15), ((int)TaskTerminalKind.MutationOutcomeUnknown, (int)TaskTerminalKind.ExecutionInterrupted));
        Assert.Equal((5, 6), ((int)TaskLifecycleStage.RecoveryAccepted, (int)TaskLifecycleStage.RecoveryRejected));
        // ADR-0030's reconciliation vocabulary is reused unchanged.
        Assert.Equal((0, 1, 2, 3), ((int)ReconciliationAction.VerifiedDone, (int)ReconciliationAction.OperatorAcceptedDone,
            (int)ReconciliationAction.EscalatedToOperator, (int)ReconciliationAction.OperatorAbandoned));
    }

    [Fact]
    public void TheStoreCapability_ExtendsTransitions_WithExactlyTheFiveMethodsOfTheAdr()
    {
        var capability = typeof(ITaskMutationJournalStore);

        Assert.Contains(typeof(ITaskTransitionStore), capability.GetInterfaces());
        Assert.Equal(
            ["LoadWithJournalAsync", "TryAcquireAsync", "TryReconcileAsync", "TryRecordIntentAsync", "TryRecordOutcomeAsync"],
            capability.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(method => method.Name).Order(StringComparer.Ordinal));
        // ITaskStore itself is unchanged: a third-party store keeps compiling.
        Assert.DoesNotContain(typeof(ITaskStore).GetMethods(), method => method.Name.Contains("Journal", StringComparison.Ordinal));
    }

    [Fact]
    public void TheJournalRecords_CarryNoArgumentsOutputOrCredentials()
    {
        string[] forbidden = ["Arguments", "Output", "Observation", "Message", "Error", "Credential", "Secret", "Approval", "Entitlement"];
        foreach (var type in new[] { typeof(TaskMutationIntent), typeof(TaskMutationOutcome), typeof(TaskMutationJournalEntry), typeof(TaskMutationAuditEvent) })
        {
            var names = type.GetProperties().Select(property => property.Name).Where(name => name != "ArgumentsHash");
            Assert.DoesNotContain(names, name => forbidden.Any(word => name.Contains(word, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void EveryNewRecord_RoundTrips()
    {
        var key = new TaskMutationKey(Guid.NewGuid(), 2, 5);
        var intent = new TaskMutationIntent
        {
            Key = key, ToolName = "test.mutate", ArgumentsHash = new string('c', 64), Risk = RiskLevel.High,
            VerificationToolName = "test.observe", PlanRevision = 1, PlannedStepIndex = 0, IntentAtUtc = Now,
        };
        var outcome = new TaskMutationOutcome(MutationOutcomeKind.Returned, ToolOutcome.Failure, ToolFailureKind.Environment, VerificationStatus.Refuted, Now);
        var reconciliation = new StepReconciliation(ReconciliationAction.VerifiedDone, VerificationStatus.Confirmed, ActorIdentity.RuntimeSystem, Now);
        var entry = new TaskMutationJournalEntry
        {
            Intent = intent, Sequence = 3, Outcome = outcome, State = TaskMutationState.ReconciledDone, Reconciliation = reconciliation,
            HistoryRecordedInAttempt = 3,
        };
        var resolution = new TaskMutationResolution(key, TaskMutationState.Escalated, TaskMutationState.ReconciledDone, reconciliation);
        var task = new TaskState(key.TaskId, NodeId.Local, "goal", AgentTaskStatus.Failed, [], [], Now)
        {
            Origin = TaskOrigin.Ordinary,
            MutationJournalMode = TaskMutationJournalMode.Journaled,
            TerminalReason = new TaskTerminalReason(TaskTerminalKind.MutationOutcomeUnknown),
        };

        Assert.Equal(key, RoundTrip(key));
        Assert.Equal(intent, RoundTrip(intent));
        Assert.Equal(outcome, RoundTrip(outcome));
        var restoredEntry = RoundTrip(entry);
        Assert.Equal((entry.Intent, entry.Sequence, entry.Outcome, entry.State, entry.Reconciliation, entry.HistoryRecordedInAttempt),
            (restoredEntry.Intent, restoredEntry.Sequence, restoredEntry.Outcome, restoredEntry.State, restoredEntry.Reconciliation, restoredEntry.HistoryRecordedInAttempt));
        Assert.Equal(resolution, RoundTrip(resolution));
        var restoredTask = RoundTrip(task);
        Assert.Equal(TaskMutationJournalMode.Journaled, restoredTask.MutationJournalMode);
        Assert.Equal(TaskTerminalKind.MutationOutcomeUnknown, restoredTask.TerminalReason!.Kind);
        var snapshot = RoundTrip(new TaskJournalSnapshot(task, [entry]));
        Assert.Equal(TaskMutationState.ReconciledDone, Assert.Single(snapshot.Entries).State);
    }

    [Fact]
    public void ATaskStoredBeforeTheJournal_LoadsAbsent_AndAnAbsentTaskIsWrittenWithoutTheMember()
    {
        const string legacy = """{"Id":"7f2b1a4c-2a6e-4f0a-9d7f-1d2c3b4a5e6f","Node":"local","Goal":"g","Status":6,"Steps":[],"Plans":[],"CreatedAtUtc":"2026-09-01T00:00:00+00:00","Origin":1}""";

        var loaded = JsonSerializer.Deserialize<TaskState>(legacy, JsonOptions)!;

        Assert.Equal(TaskMutationJournalMode.Absent, loaded.MutationJournalMode);
        Assert.DoesNotContain("MutationJournalMode", JsonSerializer.Serialize(loaded, JsonOptions), StringComparison.Ordinal);
    }

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}
