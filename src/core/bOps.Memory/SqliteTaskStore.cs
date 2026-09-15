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
}
