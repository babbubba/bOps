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
public sealed class SqliteTaskStore : ITaskStore
{
    private readonly string _connectionString;

    /// <summary>Opens (creating if needed) a SQLite-backed task store at <paramref name="filePath"/>.</summary>
    public SqliteTaskStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = filePath }.ToString();
        EnsureSchema();
    }

    /// <inheritdoc />
    public async Task SaveAsync(TaskState task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tasks (id, status, updated_at_utc, state_json)
            VALUES ($id, $status, $updatedAtUtc, $stateJson)
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                updated_at_utc = excluded.updated_at_utc,
                state_json = excluded.state_json;
            """;
        command.Parameters.AddWithValue("$id", task.Id.ToString());
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$stateJson", JsonSerializer.Serialize(task, MemoryJsonContext.Default.TaskState));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
