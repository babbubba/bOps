// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Memory;

/// <summary>
/// <see cref="ITaskStore"/> backed by a single-table SQLite database: one row per task, the
/// whole <see cref="TaskState"/> serialized as a JSON column (V0.7, ADR-0017). Deliberately not
/// EF Core — there is exactly one aggregate with no relational queries beyond "by id" and "by
/// status," so a parameterized upsert and a JSON column are the entire feature, not a simplified
/// version of a larger one.
///
/// Every connection opens in WAL journal mode with a busy timeout (V0.9, ADR-0018): SQLite's
/// default journal mode blocks a reader behind an in-progress writer and, with no busy timeout
/// set, fails that read immediately with <c>SQLITE_BUSY</c> instead of waiting briefly. That was
/// invisible while <c>bOps.Cli</c> was the only consumer — one process, one task, no concurrent
/// access to the same file — but <c>bOps.Api</c> writes a task's <c>Running</c> snapshot from a
/// detached background operation while an HTTP request can read the same task at the same moment,
/// which is exactly the access pattern WAL mode plus a busy timeout exists to make safe.
/// </summary>
public sealed class SqliteTaskStore : ITaskStore, ITaskTransitionStore
{
    private readonly string _connectionString;
    private readonly string _filePath;

    /// <summary>Opens (creating if needed) a SQLite-backed task store at <paramref name="filePath"/>.</summary>
    public SqliteTaskStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _filePath = Path.GetFullPath(filePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString();
        EnsureSchema();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Fenced by execution attempt (ADR-0040 §4.1): a save whose <see cref="TaskState.ExecutionAttempt"/> is lower than the
    /// stored one writes nothing and throws <see cref="TaskExecutionSupersededException"/>.
    /// </remarks>
    public async Task SaveAsync(TaskState task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tasks (id, status, execution_attempt, updated_at_utc, state_json)
            VALUES ($id, $status, $executionAttempt, $updatedAtUtc, $stateJson)
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                execution_attempt = excluded.execution_attempt,
                updated_at_utc = excluded.updated_at_utc,
                state_json = excluded.state_json
            WHERE tasks.execution_attempt <= excluded.execution_attempt;
            """;
        AddStateParameters(command, task);

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new TaskExecutionSupersededException(task.Id, task.ExecutionAttempt);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(TaskState task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tasks (id, status, execution_attempt, updated_at_utc, state_json)
            VALUES ($id, $status, $executionAttempt, $updatedAtUtc, $stateJson)
            ON CONFLICT(id) DO NOTHING;
            """;
        AddStateParameters(command, task);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One conditional <c>UPDATE</c>: the comparison and the write are a single statement, and SQLite serializes writers on
    /// the database file, so no other thread or process sharing the file can write between them (ADR-0040 §4.2).
    /// </remarks>
    public async Task<bool> TryTransitionAsync(
        TaskState task, AgentTaskStatus expectedStatus, int expectedExecutionAttempt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.ExecutionAttempt != expectedExecutionAttempt && task.ExecutionAttempt != expectedExecutionAttempt + 1)
        {
            throw new ArgumentException(
                $"A transition from execution attempt {expectedExecutionAttempt} may write attempt {expectedExecutionAttempt} or {expectedExecutionAttempt + 1}, not {task.ExecutionAttempt}.",
                nameof(task));
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE tasks SET
                status = $status,
                execution_attempt = $executionAttempt,
                updated_at_utc = $updatedAtUtc,
                state_json = $stateJson
            WHERE id = $id AND status = $expectedStatus AND execution_attempt = $expectedExecutionAttempt;
            """;
        AddStateParameters(command, task);
        command.Parameters.AddWithValue("$expectedStatus", expectedStatus.ToString());
        command.Parameters.AddWithValue("$expectedExecutionAttempt", expectedExecutionAttempt);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static void AddStateParameters(SqliteCommand command, TaskState task)
    {
        command.Parameters.AddWithValue("$id", task.Id.ToString());
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$executionAttempt", task.ExecutionAttempt);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$stateJson", JsonSerializer.Serialize(task, MemoryJsonContext.Default.TaskState));
    }
    /// <inheritdoc />
    public async Task<TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", taskId.ToString());

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is string json ? JsonSerializer.Deserialize(json, MemoryJsonContext.Default.TaskState) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM tasks WHERE status = $status ORDER BY updated_at_utc;";
        command.Parameters.AddWithValue("$status", status.ToString());

        var results = new List<TaskState>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (JsonSerializer.Deserialize(reader.GetString(0), MemoryJsonContext.Default.TaskState) is { } state)
            {
                results.Add(state);
            }
        }

        return results;
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var busyCommand = connection.CreateCommand())
        {
            // The schema migration below can meet another process opening the same file.
            busyCommand.CommandText = "PRAGMA busy_timeout=5000;";
            busyCommand.ExecuteNonQuery();
        }

        // WAL is a durable, once-per-file setting (persisted in the database itself), so it only
        // needs setting here — every later connection this store opens inherits it automatically.
        using (var walCommand = connection.CreateCommand())
        {
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            walCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                state_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_status ON tasks(status);
            """;
        command.ExecuteNonQuery();

        EnsureExecutionAttemptColumn(connection);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// The ADR-0040 §4.2 migration, additive and idempotent: <c>execution_attempt</c> mirrors
    /// <see cref="TaskState.ExecutionAttempt"/> so a transition can compare it in the same statement that writes. Existing
    /// rows get 1, which is what their JSON loads as; nothing is rewritten, dropped or recreated. A process that loses the
    /// race to add the column sees "duplicate column" and re-checks.
    /// </summary>
    private static void EnsureExecutionAttemptColumn(SqliteConnection connection)
    {
        if (HasExecutionAttemptColumn(connection))
        {
            return;
        }

        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE tasks ADD COLUMN execution_attempt INTEGER NOT NULL DEFAULT 1;";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) when (HasExecutionAttemptColumn(connection))
        {
            // Another process sharing the file added it first.
        }
    }

    private static bool HasExecutionAttemptColumn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(tasks);";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "execution_attempt", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Unlike WAL mode, <c>busy_timeout</c> is a per-connection setting — it must be set again on
    /// every new <see cref="SqliteConnection"/> this store opens, immediately after opening it.
    /// </summary>
    private static async Task SetBusyTimeoutAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
