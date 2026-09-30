// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Memory.Tests;

/// <summary>
/// HARDEN-3 / ADR-0040 §4: <see cref="SqliteTaskStore"/>'s atomic transitions, execution-attempt fence and additive
/// migration, against a real SQLite file. Concurrency is proven with separate store instances — separate connections, the
/// way two processes share one <c>tasks.db</c> — never with an in-process lock.
/// </summary>
public sealed class SqliteTaskStoreTransitionTests : IDisposable
{
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bops-transition-tests-{Guid.NewGuid():N}.db");

    private static TaskState Task(AgentTaskStatus status, int executionAttempt = 1) =>
        new(Guid.NewGuid(), NodeId.Local, "check disk", status,
            [new PlanStep(0, "fs.list", new ModelToolCall("call-0", "fs.list", ToolArguments.Empty), ToolCallResult.Success("ok"), "ok", 0)],
            [new AgentPlan(0, "plan", [])],
            DateTimeOffset.UtcNow)
        {
            ExecutionAttempt = executionAttempt,
            Origin = TaskOrigin.Ordinary,
            Accounting = new TaskAccounting(10, 1, 0),
        };

    private static TaskState Acquire(TaskState task) => task with { Status = AgentTaskStatus.Running, ExecutionAttempt = task.ExecutionAttempt + 1 };

    [Fact]
    public async Task TryTransitionAsync_MovesTheExpectedStateToTheNextExecutionAttempt()
    {
        var store = new SqliteTaskStore(_filePath);
        var failed = Task(AgentTaskStatus.Failed);
        await store.SaveAsync(failed);

        Assert.True(await store.TryTransitionAsync(Acquire(failed), AgentTaskStatus.Failed, 1));

        var loaded = (await store.LoadAsync(failed.Id))!;
        Assert.Equal((AgentTaskStatus.Running, 2), (loaded.Status, loaded.ExecutionAttempt));
        Assert.Equal(2, ColumnAttempt(failed.Id));
    }

    // H3-15
    [Theory]
    [InlineData(AgentTaskStatus.Cancelled, 1)]
    [InlineData(AgentTaskStatus.Failed, 2)]
    public async Task TryTransitionAsync_WritesNothing_WhenTheStatusOrTheExecutionAttemptDiffer(AgentTaskStatus expectedStatus, int expectedAttempt)
    {
        var store = new SqliteTaskStore(_filePath);
        var failed = Task(AgentTaskStatus.Failed);
        await store.SaveAsync(failed);

        var next = failed with { Status = AgentTaskStatus.Running, ExecutionAttempt = expectedAttempt + 1 };
        Assert.False(await store.TryTransitionAsync(next, expectedStatus, expectedAttempt));

        var loaded = (await store.LoadAsync(failed.Id))!;
        Assert.Equal((AgentTaskStatus.Failed, 1), (loaded.Status, loaded.ExecutionAttempt));
        Assert.Equal(1, ColumnAttempt(failed.Id));
    }

    [Fact]
    public async Task TryTransitionAsync_WritesNothing_ForATaskThatIsNotStored()
    {
        var store = new SqliteTaskStore(_filePath);
        var absent = Task(AgentTaskStatus.Failed);

        Assert.False(await store.TryTransitionAsync(Acquire(absent), AgentTaskStatus.Failed, 1));
        Assert.Null(await store.LoadAsync(absent.Id));
    }

    [Fact]
    public async Task TryTransitionAsync_RefusesAnExecutionAttemptThatIsNeitherTheOwnerNorItsSuccessor()
    {
        var store = new SqliteTaskStore(_filePath);
        var failed = Task(AgentTaskStatus.Failed);
        await store.SaveAsync(failed);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.TryTransitionAsync(failed with { Status = AgentTaskStatus.Running, ExecutionAttempt = 3 }, AgentTaskStatus.Failed, 1));
        Assert.Equal(1, (await store.LoadAsync(failed.Id))!.ExecutionAttempt);
    }

    [Fact]
    public async Task TryCreateAsync_InsertsOnlyATaskThatIsNotStored()
    {
        var store = new SqliteTaskStore(_filePath);
        var running = Task(AgentTaskStatus.Running);

        Assert.True(await store.TryCreateAsync(running));
        Assert.False(await store.TryCreateAsync(running with { Goal = "overwritten" }));
        Assert.Equal("check disk", (await store.LoadAsync(running.Id))!.Goal);
    }

    // H3-13 (store level), H3-14: the same terminal task acquired from separate connections at once — exactly one wins, one
    // execution attempt is created, and the losers write nothing.
    [Fact]
    public async Task ConcurrentAcquisitionsFromSeparateConnections_ExactlyOneWins()
    {
        for (var round = 0; round < 10; round++)
        {
            var failed = Task(AgentTaskStatus.Failed);
            await new SqliteTaskStore(_filePath).SaveAsync(failed);
            var stores = Enumerable.Range(0, 8).Select(_ => new SqliteTaskStore(_filePath)).ToList();
            using var start = new Barrier(stores.Count);

            var results = await System.Threading.Tasks.Task.WhenAll(stores.Select((store, index) => System.Threading.Tasks.Task.Run(() =>
            {
                start.SignalAndWait();
                return store.TryTransitionAsync(Acquire(failed) with { Goal = $"winner {index}" }, AgentTaskStatus.Failed, 1);
            })));

            Assert.Equal(1, results.Count(won => won));
            var loaded = (await stores[0].LoadAsync(failed.Id))!;
            Assert.Equal((AgentTaskStatus.Running, 2), (loaded.Status, loaded.ExecutionAttempt));
            Assert.Equal($"winner {Array.IndexOf(results, true)}", loaded.Goal);
        }
    }

    // H3-24 (store level): a save by an older execution attempt is refused and writes nothing.
    [Fact]
    public async Task SaveAsync_RefusesAnOlderExecutionAttempt_AndWritesNothing()
    {
        var store = new SqliteTaskStore(_filePath);
        var failed = Task(AgentTaskStatus.Failed);
        await store.SaveAsync(failed);
        Assert.True(await store.TryTransitionAsync(Acquire(failed), AgentTaskStatus.Failed, 1));

        var stale = failed with { Status = AgentTaskStatus.Completed, Goal = "stale write" };
        var refused = await Assert.ThrowsAsync<TaskExecutionSupersededException>(() => store.SaveAsync(stale));

        Assert.Equal((failed.Id, 1), (refused.TaskId, refused.ExecutionAttempt));
        var loaded = (await store.LoadAsync(failed.Id))!;
        Assert.Equal((AgentTaskStatus.Running, 2, "check disk"), (loaded.Status, loaded.ExecutionAttempt, loaded.Goal));
    }

    // H3-16, H3-33: a database written before ADR-0040 — no execution_attempt column, rows without the new JSON members — is
    // migrated additively: every row and its whole history is kept, loads as execution attempt 1 with an unknown origin and
    // no accounting, and can then be transitioned.
    [Fact]
    public async Task APreAdrDatabase_IsMigratedAdditively_AndItsRowsLoadWithTheDocumentedDefaults()
    {
        var legacyId = Guid.NewGuid();
        var legacyJson =
            $$$"""
            {"Id":"{{{legacyId}}}","Node":"local","Goal":"legacy goal","Status":6,
             "Steps":[{"Index":0,"Description":"fs.list","ToolCall":{"Id":"c0","ToolName":"fs.list","Arguments":{}},"Result":null,"Observation":"ok","PlanRevision":0},
                      {"Index":1,"Description":"Model protocol failure","ToolCall":null,"Result":null,"Observation":"boom","PlanRevision":null}],
             "Plans":[{"Revision":0,"Rationale":"legacy plan","Steps":[]}],
             "CreatedAtUtc":"2026-09-20T10:00:00+00:00"}
            """;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE tasks (id TEXT PRIMARY KEY, status TEXT NOT NULL, updated_at_utc TEXT NOT NULL, state_json TEXT NOT NULL);
                CREATE INDEX ix_tasks_status ON tasks(status);
                INSERT INTO tasks (id, status, updated_at_utc, state_json) VALUES ($id, 'Failed', '2026-09-20T10:00:00Z', $json);
                """;
            command.Parameters.AddWithValue("$id", legacyId.ToString());
            command.Parameters.AddWithValue("$json", legacyJson);
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteTaskStore(_filePath);
        _ = new SqliteTaskStore(_filePath); // a second open is idempotent

        var loaded = (await store.LoadAsync(legacyId))!;
        Assert.Equal(AgentTaskStatus.Failed, loaded.Status);
        Assert.Equal(1, loaded.ExecutionAttempt);
        Assert.Equal(TaskOrigin.Unknown, loaded.Origin);
        Assert.Null(loaded.Accounting);
        Assert.Null(loaded.TerminalReason);
        Assert.Equal(2, loaded.Steps.Count);
        Assert.Null(loaded.Steps[0].ExecutionAttempt);
        Assert.Equal("legacy plan", Assert.Single(loaded.Plans).Rationale);
        Assert.Equal(1, ColumnAttempt(legacyId));
        Assert.Single(await store.ListByStatusAsync(AgentTaskStatus.Failed));
        Assert.Equal(legacyJson, StateJson(legacyId)); // the history itself was not rewritten

        Assert.True(await store.TryTransitionAsync(loaded with { Status = AgentTaskStatus.Running, ExecutionAttempt = 2 }, AgentTaskStatus.Failed, 1));
    }

    private int ColumnAttempt(Guid id) => Convert.ToInt32(Scalar(attempt: true, id), System.Globalization.CultureInfo.InvariantCulture);

    private string StateJson(Guid id) => (string)Scalar(attempt: false, id)!;

    private object? Scalar(bool attempt, Guid id)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        if (attempt)
        {
            command.CommandText = "SELECT execution_attempt FROM tasks WHERE id = $id;";
        }
        else
        {
            command.CommandText = "SELECT state_json FROM tasks WHERE id = $id;";
        }

        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar();
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
