// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Memory.Tests;

/// <summary>
/// F-25 / ADR-0051 §6: the durable mutation journal of <see cref="SqliteTaskStore"/>, against a real SQLite file. Every write is
/// fenced on the task row in the same statement or <c>BEGIN IMMEDIATE</c> transaction; concurrency is proven with separate store
/// instances on one file (separate connections, the way two processes share one <c>tasks.db</c>).
/// </summary>
public sealed class SqliteTaskMutationJournalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bops-journal-tests-{Guid.NewGuid():N}.db");

    private static TaskState Task(AgentTaskStatus status, int executionAttempt = 1, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), NodeId.Local, "restart the service", status, [], [new AgentPlan(0, "plan", [])], Now)
        {
            ExecutionAttempt = executionAttempt,
            Origin = TaskOrigin.Ordinary,
            Accounting = new TaskAccounting(0, 0, 0),
            MutationJournalMode = TaskMutationJournalMode.Journaled,
        };

    private static TaskMutationIntent Intent(Guid taskId, int attempt, int stepIndex, string tool = "test.mutate") => new()
    {
        Key = new TaskMutationKey(taskId, attempt, stepIndex),
        ToolName = tool,
        ArgumentsHash = new string('a', 64),
        Risk = RiskLevel.Medium,
        VerificationToolName = "test.observe",
        PlanRevision = 0,
        PlannedStepIndex = stepIndex,
        IntentAtUtc = Now,
    };

    private static TaskMutationOutcome Returned(VerificationStatus verification = VerificationStatus.Confirmed) =>
        new(MutationOutcomeKind.Returned, ToolOutcome.Success, null, verification, Now);

    private static TaskState WithStep(TaskState task, int stepIndex) => task with
    {
        Steps =
        [
            .. task.Steps,
            new PlanStep(stepIndex, "test.mutate", new ModelToolCall($"call-{stepIndex}", "test.mutate", ToolArguments.Empty),
                ToolCallResult.Success("done"), "done", 0) { ExecutionAttempt = task.ExecutionAttempt },
        ],
    };

    private static StepReconciliation Reconciliation(ReconciliationAction action) =>
        new(action, VerificationStatus.Inconclusive, new ActorIdentity("api-user", "admin", null), Now);

    private async Task<(SqliteTaskStore Store, TaskState Task)> RunningWithIntentAsync(int attempt = 1, int stepIndex = 0)
    {
        var store = new SqliteTaskStore(_filePath);
        var task = Task(AgentTaskStatus.Running, attempt);
        await store.SaveAsync(task);
        Assert.True(await store.TryRecordIntentAsync(Intent(task.Id, attempt, stepIndex)));
        return (store, task);
    }

    private async Task<(SqliteTaskStore Store, TaskState Task)> TerminalWithEntryAsync(TaskMutationState state)
    {
        var (store, task) = await RunningWithIntentAsync();
        if (state is TaskMutationState.Settled or TaskMutationState.Ambiguous)
        {
            var outcome = state == TaskMutationState.Settled
                ? Returned()
                : new TaskMutationOutcome(MutationOutcomeKind.TimedOut, ToolOutcome.Timeout, ToolFailureKind.Timeout, VerificationStatus.Refuted, Now);
            var outcomeState = state == TaskMutationState.Settled ? TaskMutationState.Settled : TaskMutationState.Ambiguous;
            Assert.True(await store.TryRecordOutcomeAsync(new TaskMutationKey(task.Id, 1, 0), outcome, outcomeState, WithStep(task, 0)));
        }

        var failed = (await store.LoadAsync(task.Id))! with { Status = AgentTaskStatus.Failed };
        Assert.True(await store.TryTransitionAsync(failed, AgentTaskStatus.Running, 1));

        if (state is TaskMutationState.Escalated or TaskMutationState.ReconciledDone or TaskMutationState.Abandoned)
        {
            var action = state switch
            {
                TaskMutationState.Escalated => ReconciliationAction.EscalatedToOperator,
                TaskMutationState.ReconciledDone => ReconciliationAction.OperatorAcceptedDone,
                _ => ReconciliationAction.OperatorAbandoned,
            };
            Assert.True(await store.TryReconcileAsync(task.Id, AgentTaskStatus.Failed, 1,
                [new TaskMutationResolution(new TaskMutationKey(task.Id, 1, 0), TaskMutationState.Pending, state, Reconciliation(action))]));
        }

        return (store, (await store.LoadAsync(task.Id))!);
    }

    private static TaskState Acquired(TaskState task) =>
        task with { Status = AgentTaskStatus.Running, ExecutionAttempt = task.ExecutionAttempt + 1 };

    // ------------------------------------------------------------------ intent

    [Fact]
    public async Task Intent_ForTheRunningOwnerAttempt_IsCommittedPending_WithSequenceOne()
    {
        var (store, task) = await RunningWithIntentAsync();

        var snapshot = (await store.LoadWithJournalAsync(task.Id))!;
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(new TaskMutationKey(task.Id, 1, 0), entry.Intent.Key);
        Assert.Equal((1, TaskMutationState.Pending), (entry.Sequence, entry.State));
        Assert.Null(entry.Outcome);
        Assert.Null(entry.Reconciliation);
        Assert.Null(entry.HistoryRecordedInAttempt);
        Assert.Equal(("test.mutate", RiskLevel.Medium, "test.observe", 0, 0), (entry.Intent.ToolName, entry.Intent.Risk,
            entry.Intent.VerificationToolName, entry.Intent.PlanRevision, entry.Intent.PlannedStepIndex));
        Assert.Equal(Now, entry.Intent.IntentAtUtc);
    }

    [Fact]
    public async Task Intent_OfAStaleAttempt_IsRefused_AndWritesNothing()
    {
        var store = new SqliteTaskStore(_filePath);
        var task = Task(AgentTaskStatus.Running, executionAttempt: 2);
        await store.SaveAsync(task);

        Assert.False(await store.TryRecordIntentAsync(Intent(task.Id, attempt: 1, stepIndex: 0)));
        Assert.Empty((await store.LoadWithJournalAsync(task.Id))!.Entries);
    }

    [Theory]
    [InlineData(AgentTaskStatus.Failed)]
    [InlineData(AgentTaskStatus.Cancelled)]
    [InlineData(AgentTaskStatus.Completed)]
    public async Task Intent_ForATaskThatIsNotRunning_IsRefused(AgentTaskStatus status)
    {
        var store = new SqliteTaskStore(_filePath);
        var task = Task(status);
        await store.SaveAsync(task);

        Assert.False(await store.TryRecordIntentAsync(Intent(task.Id, 1, 0)));
        Assert.Empty((await store.LoadWithJournalAsync(task.Id))!.Entries);
    }

    [Fact]
    public async Task Intent_ForATaskThatIsNotStored_IsRefused()
    {
        var store = new SqliteTaskStore(_filePath);
        Assert.False(await store.TryRecordIntentAsync(Intent(Guid.NewGuid(), 1, 0)));
    }

    [Fact]
    public async Task Intent_ForAKeyAlreadyJournaled_IsRefused_AndTheEntryIsUnchanged()
    {
        var (store, task) = await RunningWithIntentAsync();

        Assert.False(await store.TryRecordIntentAsync(Intent(task.Id, 1, 0, tool: "other.tool")));

        var entry = Assert.Single((await store.LoadWithJournalAsync(task.Id))!.Entries);
        Assert.Equal("test.mutate", entry.Intent.ToolName);
    }

    [Fact]
    public async Task Sequence_IsPerTaskAndMonotonic_AcrossStepsAndExecutionAttempts()
    {
        var (store, task) = await RunningWithIntentAsync();
        Assert.True(await store.TryRecordIntentAsync(Intent(task.Id, 1, 1)));
        var attempt2 = task with { ExecutionAttempt = 2 };
        await store.SaveAsync(attempt2);
        Assert.True(await store.TryRecordIntentAsync(Intent(task.Id, 2, 1)));

        var other = Task(AgentTaskStatus.Running);
        await store.SaveAsync(other);
        Assert.True(await store.TryRecordIntentAsync(Intent(other.Id, 1, 0)));

        Assert.Equal([1, 2, 3], (await store.LoadWithJournalAsync(task.Id))!.Entries.Select(entry => entry.Sequence));
        Assert.Equal(1, Assert.Single((await store.LoadWithJournalAsync(other.Id))!.Entries).Sequence);
    }

    // ------------------------------------------------------------------ outcome

    [Fact]
    public async Task Outcome_ForTheRunningOwnerAttempt_CommitsTheOutcomeAndTheStepTogether()
    {
        var (store, task) = await RunningWithIntentAsync();
        var key = new TaskMutationKey(task.Id, 1, 0);

        Assert.True(await store.TryRecordOutcomeAsync(key, Returned(), TaskMutationState.Settled, WithStep(task, 0)));

        var snapshot = (await store.LoadWithJournalAsync(task.Id))!;
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(TaskMutationState.Settled, entry.State);
        Assert.Equal(new TaskMutationOutcome(MutationOutcomeKind.Returned, ToolOutcome.Success, null, VerificationStatus.Confirmed, Now), entry.Outcome);
        Assert.Equal(0, Assert.Single(snapshot.Task.Steps).Index);
        Assert.Equal((AgentTaskStatus.Running, 1), (snapshot.Task.Status, snapshot.Task.ExecutionAttempt));
    }

    [Fact]
    public async Task Outcome_OfAStaleAttempt_IsRefused_AndNeitherTheEntryNorTheTaskChange()
    {
        var (store, task) = await RunningWithIntentAsync();
        var recovered = task with { Status = AgentTaskStatus.Failed };
        Assert.True(await store.TryTransitionAsync(recovered, AgentTaskStatus.Running, 1));

        Assert.False(await store.TryRecordOutcomeAsync(new TaskMutationKey(task.Id, 1, 0), Returned(), TaskMutationState.Settled, WithStep(task, 0)));

        var snapshot = (await store.LoadWithJournalAsync(task.Id))!;
        Assert.Equal(TaskMutationState.Pending, Assert.Single(snapshot.Entries).State);
        Assert.Null(snapshot.Entries[0].Outcome);
        Assert.Equal(AgentTaskStatus.Failed, snapshot.Task.Status);
        Assert.Empty(snapshot.Task.Steps);
    }

    [Fact]
    public async Task Outcome_OfAnAttemptAfterANewerOneOwnsTheTask_IsRefused()
    {
        var (store, task) = await RunningWithIntentAsync();
        await store.SaveAsync(task with { ExecutionAttempt = 2 });

        Assert.False(await store.TryRecordOutcomeAsync(new TaskMutationKey(task.Id, 1, 0), Returned(), TaskMutationState.Settled, WithStep(task, 0)));
        Assert.Equal(TaskMutationState.Pending, Assert.Single((await store.LoadWithJournalAsync(task.Id))!.Entries).State);
    }

    [Fact]
    public async Task Outcome_IsWrittenOnce_ASecondOutcomeIsRefused()
    {
        var (store, task) = await RunningWithIntentAsync();
        var key = new TaskMutationKey(task.Id, 1, 0);
        Assert.True(await store.TryRecordOutcomeAsync(key, Returned(), TaskMutationState.Settled, WithStep(task, 0)));

        var timedOut = new TaskMutationOutcome(MutationOutcomeKind.TimedOut, ToolOutcome.Timeout, ToolFailureKind.Timeout, VerificationStatus.Refuted, Now);
        Assert.False(await store.TryRecordOutcomeAsync(key, timedOut, TaskMutationState.Ambiguous, WithStep(task, 0)));
        Assert.Equal(MutationOutcomeKind.Returned, Assert.Single((await store.LoadWithJournalAsync(task.Id))!.Entries).Outcome!.Kind);
    }

    [Fact]
    public async Task Outcome_ForAReconciledEntry_IsRefused()
    {
        var (store, task) = await TerminalWithEntryAsync(TaskMutationState.ReconciledDone);
        // Even if the task were somehow Running under the entry's attempt again, a reconciled entry takes no late outcome.
        await store.SaveAsync(task with { Status = AgentTaskStatus.Running });

        Assert.False(await store.TryRecordOutcomeAsync(new TaskMutationKey(task.Id, 1, 0), Returned(), TaskMutationState.Settled,
            WithStep(task with { Status = AgentTaskStatus.Running }, 0)));
        Assert.Equal(TaskMutationState.ReconciledDone, Assert.Single((await store.LoadWithJournalAsync(task.Id))!.Entries).State);
    }

    [Fact]
    public async Task Outcome_RollsBackTheEntry_WhenTheTaskRowWriteFailsInTheSameTransaction()
    {
        var (store, task) = await RunningWithIntentAsync();
        SetFailingTaskWrites(enabled: true);

        await Assert.ThrowsAsync<SqliteException>(() => store.TryRecordOutcomeAsync(
            new TaskMutationKey(task.Id, 1, 0), Returned(), TaskMutationState.Settled, WithStep(task, 0)));

        SetFailingTaskWrites(enabled: false);
        var snapshot = (await store.LoadWithJournalAsync(task.Id))!;
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(TaskMutationState.Pending, entry.State);
        Assert.Null(entry.Outcome);
        Assert.Empty(snapshot.Task.Steps);
    }

    [Fact]
    public async Task Outcome_RequiresTheOwningTaskToBeTheRunningStateOfTheSameAttempt()
    {
        var (store, task) = await RunningWithIntentAsync();
        var key = new TaskMutationKey(task.Id, 1, 0);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.TryRecordOutcomeAsync(key, Returned(), TaskMutationState.Settled, WithStep(task, 0) with { Status = AgentTaskStatus.Failed }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.TryRecordOutcomeAsync(key, Returned(), TaskMutationState.Settled, WithStep(task, 0) with { ExecutionAttempt = 2 }));
    }

    // ------------------------------------------------------------------ reconcile

    [Fact]
    public async Task Reconcile_WritesEveryResolution_WhenTheTaskAndEveryEntryMatch()
    {
        var (store, task) = await TerminalWithEntryAsync(TaskMutationState.Pending);
        var reconciliation = Reconciliation(ReconciliationAction.OperatorAcceptedDone);

        Assert.True(await store.TryReconcileAsync(task.Id, AgentTaskStatus.Failed, 1,
            [new TaskMutationResolution(new TaskMutationKey(task.Id, 1, 0), TaskMutationState.Pending, TaskMutationState.ReconciledDone, reconciliation)]));

        var entry = Assert.Single((await store.LoadWithJournalAsync(task.Id))!.Entries);
        Assert.Equal(TaskMutationState.ReconciledDone, entry.State);
        Assert.Equal(reconciliation, entry.Reconciliation);
    }

    [Fact]
    public async Task Reconcile_IsAPerEntryCompareAndSet_OneStaleExpectationRollsBackTheWholeCall()
    {
        var store = new SqliteTaskStore(_filePath);
        var task = Task(AgentTaskStatus.Running);
        await store.SaveAsync(task);
        Assert.True(await store.TryRecordIntentAsync(Intent(task.Id, 1, 0)));
        Assert.True(await store.TryRecordIntentAsync(Intent(task.Id, 1, 1)));
        Assert.True(await store.TryTransitionAsync(task with { Status = AgentTaskStatus.Failed }, AgentTaskStatus.Running, 1));

        var accept = Reconciliation(ReconciliationAction.OperatorAcceptedDone);
        Assert.False(await store.TryReconcileAsync(task.Id, AgentTaskStatus.Failed, 1,
        [
            new TaskMutationResolution(new TaskMutationKey(task.Id, 1, 0), TaskMutationState.Pending, TaskMutationState.ReconciledDone, accept),
            new TaskMutationResolution(new TaskMutationKey(task.Id, 1, 1), TaskMutationState.Ambiguous, TaskMutationState.ReconciledDone, accept),
        ]));

        Assert.All((await store.LoadWithJournalAsync(task.Id))!.Entries, entry =>
        {
            Assert.Equal(TaskMutationState.Pending, entry.State);
            Assert.Null(entry.Reconciliation);
        });
    }

    [Fact]
    public async Task Reconcile_IsRefused_WhileTheTaskIsRunning_OrUnderAnotherStatusOrAttempt()
    {
        var (store, task) = await RunningWithIntentAsync();
        var resolution = new TaskMutationResolution(new TaskMutationKey(task.Id, 1, 0), TaskMutationState.Pending,
            TaskMutationState.ReconciledDone, Reconciliation(ReconciliationAction.OperatorAcceptedDone));

        Assert.False(await store.TryReconcileAsync(task.Id, AgentTaskStatus.Running, 1, [resolution]));
        Assert.True(await store.TryTransitionAsync(task with { Status = AgentTaskStatus.Failed }, AgentTaskStatus.Running, 1));
        Assert.False(await store.TryReconcileAsync(task.Id, AgentTaskStatus.Cancelled, 1, [resolution]));
        Assert.False(await store.TryReconcileAsync(task.Id, AgentTaskStatus.Failed, 2, [resolution]));
        Assert.Equal(TaskMutationState.Pending, Assert.Single((await store.LoadWithJournalAsync(task.Id))!.Entries).State);
    }

    // ------------------------------------------------------------------ acquire

    [Theory]
    [InlineData(TaskMutationState.Pending)]
    [InlineData(TaskMutationState.Ambiguous)]
    [InlineData(TaskMutationState.Escalated)]
    [InlineData(TaskMutationState.Abandoned)]
    public async Task Acquire_IsRefused_WhileAnyEntryBlocks(TaskMutationState blocking)
    {
        var (store, task) = await TerminalWithEntryAsync(blocking);

        Assert.False(await store.TryAcquireAsync(Acquired(task), AgentTaskStatus.Failed, 1, []));

        var loaded = (await store.LoadAsync(task.Id))!;
        Assert.Equal((AgentTaskStatus.Failed, 1), (loaded.Status, loaded.ExecutionAttempt));
    }

    [Theory]
    [InlineData(TaskMutationState.Pending)]
    [InlineData(TaskMutationState.Ambiguous)]
    [InlineData(TaskMutationState.Escalated)]
    [InlineData(TaskMutationState.Abandoned)]
    public async Task TryTransition_RefusesAnAcquisition_WhileAnyEntryBlocks(TaskMutationState blocking)
    {
        var (store, task) = await TerminalWithEntryAsync(blocking);

        Assert.False(await store.TryTransitionAsync(Acquired(task), AgentTaskStatus.Failed, 1));
        Assert.Equal(1, (await store.LoadAsync(task.Id))!.ExecutionAttempt);
    }

    [Fact]
    public async Task TryTransition_StillLetsTheOwnerAttemptWrite_WithAPendingEntry()
    {
        var (store, task) = await RunningWithIntentAsync();

        Assert.True(await store.TryTransitionAsync(task with { Status = AgentTaskStatus.Failed }, AgentTaskStatus.Running, 1));
    }

    [Fact]
    public async Task Acquire_OfASettledTask_TransitionsTheTask()
    {
        var (store, task) = await TerminalWithEntryAsync(TaskMutationState.Settled);

        Assert.True(await store.TryAcquireAsync(Acquired(task), AgentTaskStatus.Failed, 1, []));
        Assert.Equal((AgentTaskStatus.Running, 2), ((await store.LoadAsync(task.Id))!.Status, (await store.LoadAsync(task.Id))!.ExecutionAttempt));
    }

    [Fact]
    public async Task Acquire_MarksTheRecordedEntries_InTheSameTransaction_AndOnlyOnce()
    {
        var (store, task) = await TerminalWithEntryAsync(TaskMutationState.ReconciledDone);
        var key = new TaskMutationKey(task.Id, 1, 0);

        Assert.True(await store.TryAcquireAsync(Acquired(task), AgentTaskStatus.Failed, 1, [key]));
        Assert.Equal(2, Assert.Single((await store.LoadWithJournalAsync(task.Id))!.Entries).HistoryRecordedInAttempt);

        // A later acquisition can no longer claim to record it: nothing is written, not even the task row.
        var failed2 = (await store.LoadAsync(task.Id))! with { Status = AgentTaskStatus.Failed };
        Assert.True(await store.TryTransitionAsync(failed2, AgentTaskStatus.Running, 2));
        Assert.False(await store.TryAcquireAsync(Acquired(failed2), AgentTaskStatus.Failed, 2, [key]));
        Assert.Equal(2, (await store.LoadAsync(task.Id))!.ExecutionAttempt);
        Assert.Equal(2, Assert.Single((await store.LoadWithJournalAsync(task.Id))!.Entries).HistoryRecordedInAttempt);
    }

    [Fact]
    public async Task Acquire_RollsBackTheHistoryMarks_WhenTheTaskRowChanged()
    {
        var (store, task) = await TerminalWithEntryAsync(TaskMutationState.ReconciledDone);

        Assert.False(await store.TryAcquireAsync(Acquired(task), AgentTaskStatus.Cancelled, 1, [new TaskMutationKey(task.Id, 1, 0)]));
        Assert.Null(Assert.Single((await store.LoadWithJournalAsync(task.Id))!.Entries).HistoryRecordedInAttempt);
    }

    // ------------------------------------------------------------------ concurrency (two stores, one file)

    [Fact]
    public async Task ConcurrentReconciliations_FromTwoStores_ExactlyOneWinsPerEntry_WithNoMixedState()
    {
        for (var round = 0; round < 10; round++)
        {
            var (first, task) = await TerminalWithEntryAsync(TaskMutationState.Pending);
            var second = new SqliteTaskStore(_filePath);
            var key = new TaskMutationKey(task.Id, 1, 0);

            var results = await System.Threading.Tasks.Task.WhenAll(
                first.TryReconcileAsync(task.Id, AgentTaskStatus.Failed, 1,
                    [new TaskMutationResolution(key, TaskMutationState.Pending, TaskMutationState.ReconciledDone, Reconciliation(ReconciliationAction.OperatorAcceptedDone))]),
                second.TryReconcileAsync(task.Id, AgentTaskStatus.Failed, 1,
                    [new TaskMutationResolution(key, TaskMutationState.Pending, TaskMutationState.Abandoned, Reconciliation(ReconciliationAction.OperatorAbandoned))]));

            Assert.Equal(1, results.Count(result => result));
            var entry = Assert.Single((await first.LoadWithJournalAsync(task.Id))!.Entries);
            Assert.Equal(results[0] ? TaskMutationState.ReconciledDone : TaskMutationState.Abandoned, entry.State);
            Assert.Equal(results[0] ? ReconciliationAction.OperatorAcceptedDone : ReconciliationAction.OperatorAbandoned, entry.Reconciliation!.Action);
        }
    }

    [Fact]
    public async Task ConcurrentAcquisitions_FromTwoStores_ExactlyOneWins_AndTheHistoryIsMarkedOnce()
    {
        for (var round = 0; round < 10; round++)
        {
            var (first, task) = await TerminalWithEntryAsync(TaskMutationState.ReconciledDone);
            var second = new SqliteTaskStore(_filePath);
            var key = new TaskMutationKey(task.Id, 1, 0);

            var results = await System.Threading.Tasks.Task.WhenAll(
                first.TryAcquireAsync(Acquired(task) with { Goal = "first" }, AgentTaskStatus.Failed, 1, [key]),
                second.TryAcquireAsync(Acquired(task) with { Goal = "second" }, AgentTaskStatus.Failed, 1, [key]));

            Assert.Equal(1, results.Count(result => result));
            var snapshot = (await first.LoadWithJournalAsync(task.Id))!;
            Assert.Equal(results[0] ? "first" : "second", snapshot.Task.Goal);
            Assert.Equal(2, Assert.Single(snapshot.Entries).HistoryRecordedInAttempt);
        }
    }

    [Fact]
    public async Task ConcurrentIntentAndRecovery_FromTwoStores_NeverLeaveAnIntentAfterTheRecovery()
    {
        for (var round = 0; round < 20; round++)
        {
            var store = new SqliteTaskStore(_filePath);
            var task = Task(AgentTaskStatus.Running);
            await store.SaveAsync(task);
            var recoverer = new SqliteTaskStore(_filePath);

            var results = await System.Threading.Tasks.Task.WhenAll(
                store.TryRecordIntentAsync(Intent(task.Id, 1, 0)),
                recoverer.TryTransitionAsync(task with { Status = AgentTaskStatus.Failed }, AgentTaskStatus.Running, 1));

            Assert.True(results[1]);
            var snapshot = (await store.LoadWithJournalAsync(task.Id))!;
            Assert.Equal(AgentTaskStatus.Failed, snapshot.Task.Status);
            // Either the intent committed before the recovery (and is Pending, blocking) or it was refused; never after.
            Assert.Equal(results[0] ? 1 : 0, snapshot.Entries.Count);
            Assert.False(await store.TryRecordIntentAsync(Intent(task.Id, 1, 1)));
        }
    }

    [Fact]
    public async Task ConcurrentIntents_ForOneKey_FromTwoStores_ExactlyOneCommits()
    {
        var first = new SqliteTaskStore(_filePath);
        var task = Task(AgentTaskStatus.Running);
        await first.SaveAsync(task);
        var second = new SqliteTaskStore(_filePath);

        var results = await System.Threading.Tasks.Task.WhenAll(
            first.TryRecordIntentAsync(Intent(task.Id, 1, 0)),
            second.TryRecordIntentAsync(Intent(task.Id, 1, 0, tool: "other.tool")));

        Assert.Equal(1, results.Count(result => result));
        Assert.Single((await first.LoadWithJournalAsync(task.Id))!.Entries);
    }

    // ------------------------------------------------------------------ schema, durability, legacy

    [Fact]
    public async Task Migration_OnAPreF25Database_IsAdditiveAndIdempotent_AndLeavesTaskRowsByteIdentical()
    {
        const string legacyJson = """{"Id":"7f2b1a4c-2a6e-4f0a-9d7f-1d2c3b4a5e6f","Node":"local","Goal":"legacy","Status":6,"Steps":[],"Plans":[],"CreatedAtUtc":"2026-09-01T00:00:00+00:00","ExecutionAttempt":1,"Origin":1}""";
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE tasks (id TEXT PRIMARY KEY, status TEXT NOT NULL, updated_at_utc TEXT NOT NULL, state_json TEXT NOT NULL,
                                    execution_attempt INTEGER NOT NULL DEFAULT 1);
                CREATE INDEX ix_tasks_status ON tasks(status);
                INSERT INTO tasks (id, status, updated_at_utc, state_json, execution_attempt)
                VALUES ('7f2b1a4c-2a6e-4f0a-9d7f-1d2c3b4a5e6f', 'Failed', '2026-09-01T00:00:00.0000000+00:00', $json, 1);
                """;
            command.Parameters.AddWithValue("$json", legacyJson);
            await command.ExecuteNonQueryAsync();
        }

        var before = ReadRawRow();
        _ = new SqliteTaskStore(_filePath);
        _ = new SqliteTaskStore(_filePath);

        Assert.Equal(before, ReadRawRow());
        Assert.Equal(JournalColumns, JournalTableColumns());
        var legacy = (await new SqliteTaskStore(_filePath).LoadWithJournalAsync(Guid.Parse("7f2b1a4c-2a6e-4f0a-9d7f-1d2c3b4a5e6f")))!;
        Assert.Equal(TaskMutationJournalMode.Absent, legacy.Task.MutationJournalMode);
        Assert.Empty(legacy.Entries);
    }

    [Fact]
    public async Task JournalConnections_RunWithSynchronousFull()
    {
        var store = new SqliteTaskStore(_filePath);
        using var connection = await store.OpenJournalConnectionAsync(CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous;";

        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public void TheJournalTable_HasNoColumnForArgumentsOutputOrCredentials()
    {
        _ = new SqliteTaskStore(_filePath);

        Assert.Equal(JournalColumns, JournalTableColumns());
    }

    [Fact]
    public async Task LoadWithJournal_ReturnsNull_ForATaskThatIsNotStored()
    {
        Assert.Null(await new SqliteTaskStore(_filePath).LoadWithJournalAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task MutationJournalMode_RoundTripsThroughTheTaskRow()
    {
        var store = new SqliteTaskStore(_filePath);
        var task = Task(AgentTaskStatus.Running) with { MutationJournalMode = TaskMutationJournalMode.MutationsDisabled };
        await store.SaveAsync(task);

        Assert.Equal(TaskMutationJournalMode.MutationsDisabled, (await store.LoadAsync(task.Id))!.MutationJournalMode);
    }

    private static readonly string[] JournalColumns =
    [
        "task_id", "execution_attempt", "step_index", "sequence", "tool_name", "arguments_hash", "risk", "verification_tool_name",
        "plan_revision", "planned_step_index", "intent_at_utc", "outcome_kind", "outcome_tool_outcome", "outcome_failure_kind",
        "outcome_verification", "outcome_at_utc", "state", "reconciliation_json", "history_recorded_in_attempt",
    ];

    private string[] JournalTableColumns()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(task_mutation_journal);";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return [.. columns];
    }

    private (string, string, string, long) ReadRawRow()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, status, updated_at_utc || '|' || state_json, execution_attempt FROM tasks;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
    }

    /// <summary>A trigger that makes every write of the <c>tasks</c> row fail, the way a full disk would, inside the outcome transaction.</summary>
    private void SetFailingTaskWrites(bool enabled)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        if (enabled)
        {
            command.CommandText = "CREATE TRIGGER fail_task_write BEFORE UPDATE ON tasks BEGIN SELECT RAISE(ABORT, 'disk full'); END;";
        }
        else
        {
            command.CommandText = "DROP TRIGGER fail_task_write;";
        }

        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _filePath, _filePath + "-wal", _filePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
