// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Memory;

/// <summary>
/// <see cref="IDelegationStore"/> backed by a single-table SQLite database: one row per run, the whole
/// <see cref="DelegationRun"/> serialized as a JSON column and replaced in one transaction (ADR-0030 section 7, the same
/// approach as <see cref="SqliteTaskStore"/>, ADR-0017). The status, the actor and the idempotency key are also columns, so
/// finding what is resumable or waiting for an operator and making a start idempotent never need to read a run's JSON.
/// </summary>
/// <remarks>
/// Every write is committed with a full sync before it returns, because the step journal is only worth having if it is on
/// disk before the step it describes runs. The file is readable by its owner only on Linux and macOS; on Windows it
/// inherits the permissions of its directory, like the task store. Not tamper-evident, unlike the audit chain.
/// </remarks>
public sealed class SqliteDelegationStore : IDelegationStore
{
    private readonly string _connectionString;
    private readonly string _filePath;

    /// <summary>Opens (creating if needed) a SQLite-backed delegation store at <paramref name="filePath"/>.</summary>
    public SqliteDelegationStore(string filePath)
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
    public async Task<DelegationStartResult> StartAsync(DelegationRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // IMMEDIATE takes the write lock up front, so two starts with the same key cannot both find nothing and both insert.
        await BeginImmediateAsync(connection, ct).ConfigureAwait(false);
        try
        {
            if (run.IdempotencyKey is { } key)
            {
                using var find = connection.CreateCommand();
                find.CommandText = "SELECT run_json FROM delegations WHERE actor_kind = $kind AND actor_id = $actor AND idempotency_key = $key;";
                find.Parameters.AddWithValue("$kind", run.Actor.Kind);
                find.Parameters.AddWithValue("$actor", run.Actor.Id);
                find.Parameters.AddWithValue("$key", key);
                if (await find.ExecuteScalarAsync(ct).ConfigureAwait(false) is string existing
                    && JsonSerializer.Deserialize(existing, MemoryJsonContext.Default.DelegationRun) is { } earlier)
                {
                    await RollbackAsync(connection, CancellationToken.None).ConfigureAwait(false);
                    return new DelegationStartResult(earlier, Created: false);
                }
            }

            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO delegations (id, status, actor_kind, actor_id, idempotency_key, updated_at_utc, run_json)
                VALUES ($id, $status, $kind, $actor, $key, $updatedAtUtc, $json);
                """;
            Bind(insert, run);
            try
            {
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException($"A delegation run with id {run.Id} is already stored.", ex);
            }

            await CommitAsync(connection, ct).ConfigureAwait(false);
            return new DelegationStartResult(run, Created: true);
        }
        catch
        {
            await RollbackAsync(connection, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(DelegationRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE delegations SET status = $status, updated_at_utc = $updatedAtUtc, run_json = $json
            WHERE id = $id;
            """;
        Bind(command, run);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException($"No delegation run with id {run.Id} is stored; start it before saving it.");
        }
    }

    /// <inheritdoc />
    public async Task<DelegationRun?> LoadAsync(Guid delegationId, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_json FROM delegations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", delegationId.ToString());

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize(json, MemoryJsonContext.Default.DelegationRun)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DelegationRun>> ListByStatusAsync(DelegationStatus status, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_json FROM delegations WHERE status = $status ORDER BY updated_at_utc, id;";
        command.Parameters.AddWithValue("$status", status.ToString());
        return await ReadAsync(command, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DelegationRun>> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_json FROM delegations ORDER BY updated_at_utc DESC, id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadAsync(command, ct).ConfigureAwait(false);
    }

    private static async Task BeginImmediateAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task CommitAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "COMMIT;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task RollbackAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ROLLBACK;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, DelegationRun run)
    {
        command.Parameters.AddWithValue("$id", run.Id.ToString());
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$kind", run.Actor.Kind);
        command.Parameters.AddWithValue("$actor", run.Actor.Id);
        command.Parameters.AddWithValue("$key", (object?)run.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", run.UpdatedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(run, MemoryJsonContext.Default.DelegationRun));
    }

    private static async Task<IReadOnlyList<DelegationRun>> ReadAsync(SqliteCommand command, CancellationToken ct)
    {
        var results = new List<DelegationRun>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (JsonSerializer.Deserialize(reader.GetString(0), MemoryJsonContext.Default.DelegationRun) is { } run)
            {
                results.Add(run);
            }
        }

        return results;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // Per-connection settings: wait for a writer rather than fail, and sync every commit to disk.
            using var pragmas = connection.CreateCommand();
            pragmas.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
            await pragmas.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS delegations (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                actor_kind TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                idempotency_key TEXT NULL,
                updated_at_utc TEXT NOT NULL,
                run_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_delegations_status ON delegations(status);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_delegations_idempotency
                ON delegations(actor_kind, actor_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
            """;
        command.ExecuteNonQuery();

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
