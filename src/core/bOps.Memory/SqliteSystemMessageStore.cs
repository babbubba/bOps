// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Memory;

/// <summary>
/// Node-local SQLite storage for operator-facing <see cref="SystemMessage"/>s and the last observation of each prerequisite
/// (ADR-0049 section 8). Deliberately separate from the audit chain, task state and logs. Queries are keyset-paginated over
/// <c>TimestampUtc DESC, Id DESC</c>; text search is case-insensitive through a stored invariant-folded copy of each message.
/// </summary>
/// <remarks>
/// Every write is synced before it returns. The file is readable by its owner only on Linux and macOS; on Windows it
/// inherits the permissions of its directory, like the task store. Not tamper-evident.
/// </remarks>
public sealed class SqliteSystemMessageStore : ISystemMessageStore, IPrerequisiteStateStore
{
    /// <summary>The schema version this store writes and reads (<c>PRAGMA user_version</c>).</summary>
    public const int SchemaVersion = 1;

    private const string CursorVersion = "v1";
    private const int MaxCursorLength = 128;
    private const string MessageColumns =
        "id, timestamp_utc_ticks, node, source, severity, code, message, metadata_json, task_id, component_type, component_id";

    private readonly string _connectionString;
    private readonly string _filePath;

    /// <summary>Opens (creating if needed) the store at <paramref name="filePath"/>. A file with a newer schema is refused.</summary>
    public SqliteSystemMessageStore(string filePath)
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
    public async Task AppendAsync(SystemMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.Validate();

        using var connection = await OpenAsync(ct);
        await InsertMessageAsync(connection, message, ct);
    }

    /// <inheritdoc />
    public async Task<SystemMessagePage> QueryAsync(SystemMessageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var after = query.Cursor is null ? ((long Ticks, string Id)?)null : DecodeCursor(query.Cursor);

        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();

        // One fixed statement: an absent filter binds NULL and drops out. instr, not LIKE: search text is data, never a pattern.
        command.CommandText =
            $"""
            SELECT {MessageColumns} FROM system_messages
            WHERE ($from IS NULL OR timestamp_utc_ticks >= $from)
              AND ($to IS NULL OR timestamp_utc_ticks <= $to)
              AND ($severity IS NULL OR severity = $severity)
              AND ($text IS NULL OR instr(message_folded, $text) > 0)
              AND ($afterTicks IS NULL
                   OR timestamp_utc_ticks < $afterTicks
                   OR (timestamp_utc_ticks = $afterTicks AND id < $afterId))
            ORDER BY timestamp_utc_ticks DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", query.FromUtc is { } from ? from.UtcTicks : DBNull.Value);
        command.Parameters.AddWithValue("$to", query.ToUtc is { } to ? to.UtcTicks : DBNull.Value);
        command.Parameters.AddWithValue("$severity", query.Severity is { } severity ? (int)severity : DBNull.Value);
        command.Parameters.AddWithValue("$text", query.Text is { } text ? Fold(text) : DBNull.Value);
        command.Parameters.AddWithValue("$afterTicks", after is { } position ? position.Ticks : DBNull.Value);
        command.Parameters.AddWithValue("$afterId", after is { } cursorPosition ? cursorPosition.Id : DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.PageSize + 1);

        var items = new List<SystemMessage>(query.PageSize + 1);
        using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                items.Add(ReadMessage(reader));
            }
        }

        if (items.Count <= query.PageSize)
        {
            return new SystemMessagePage(items, NextCursor: null);
        }

        items.RemoveAt(items.Count - 1);
        return new SystemMessagePage(items, EncodeCursor(items[^1]));
    }

    /// <inheritdoc />
    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM system_messages WHERE timestamp_utc_ticks < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.UtcTicks);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PrerequisiteStateRecord?> LoadStateAsync(NodeId node, string prerequisiteId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prerequisiteId);

        using var connection = await OpenAsync(ct);
        return await LoadStateAsync(connection, node, prerequisiteId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PrerequisiteStateRecord>> ListStatesAsync(NodeId node, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT node, prerequisite_id, state, code, message, checked_at_utc_ticks, changed_at_utc_ticks, metadata_json " +
            "FROM prerequisite_states WHERE node = $node ORDER BY prerequisite_id;";
        command.Parameters.AddWithValue("$node", node.Value);

        var states = new List<PrerequisiteStateRecord>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            states.Add(ReadState(reader));
        }

        return states;
    }

    /// <inheritdoc />
    public async Task<bool> SaveStateAsync(
        PrerequisiteStateRecord state,
        string? expectedFingerprint,
        SystemMessage? transitionMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);
        transitionMessage?.Validate();

        using var connection = await OpenAsync(ct);

        // IMMEDIATE takes the write lock up front, so two writers cannot both read the same fingerprint and both write.
        await BeginImmediateAsync(connection, ct);
        try
        {
            var current = await LoadStateAsync(connection, state.Node, state.PrerequisiteId, ct);
            if (!string.Equals(current?.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                await RollbackAsync(connection);
                return false;
            }

            using (var upsert = connection.CreateCommand())
            {
                upsert.CommandText =
                    """
                    INSERT INTO prerequisite_states
                        (node, prerequisite_id, state, code, message, fingerprint, checked_at_utc_ticks, changed_at_utc_ticks, metadata_json)
                    VALUES ($node, $id, $state, $code, $message, $fingerprint, $checked, $changed, $metadata)
                    ON CONFLICT (node, prerequisite_id) DO UPDATE SET
                        state = excluded.state, code = excluded.code, message = excluded.message, fingerprint = excluded.fingerprint,
                        checked_at_utc_ticks = excluded.checked_at_utc_ticks, changed_at_utc_ticks = excluded.changed_at_utc_ticks,
                        metadata_json = excluded.metadata_json;
                    """;
                upsert.Parameters.AddWithValue("$node", state.Node.Value);
                upsert.Parameters.AddWithValue("$id", state.PrerequisiteId);
                upsert.Parameters.AddWithValue("$state", state.State.ToString());
                upsert.Parameters.AddWithValue("$code", state.Code);
                upsert.Parameters.AddWithValue("$message", state.Message);
                upsert.Parameters.AddWithValue("$fingerprint", state.Fingerprint);
                upsert.Parameters.AddWithValue("$checked", state.CheckedAtUtc.UtcTicks);
                upsert.Parameters.AddWithValue("$changed", state.ChangedAtUtc.UtcTicks);
                upsert.Parameters.AddWithValue("$metadata", state.Metadata.ToString());
                await upsert.ExecuteNonQueryAsync(ct);
            }

            if (transitionMessage is not null)
            {
                await InsertMessageAsync(connection, transitionMessage, ct);
            }

            await CommitAsync(connection, ct);
            return true;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    private static void ValidateState(PrerequisiteStateRecord state)
    {
        if (string.IsNullOrWhiteSpace(state.Node.Value)
            || !OperationalIdentifier.IsValidPrerequisiteId(state.PrerequisiteId)
            || state.State is PrerequisiteState.Unknown
            || !Enum.IsDefined(state.State)
            || !OperationalIdentifier.IsValidCode(state.Code)
            || string.IsNullOrWhiteSpace(state.Message)
            || state.Message.Length > PrerequisiteCheckOutcome.MaxMessageLength
            || state.Metadata is null)
        {
            throw new ArgumentException("The prerequisite state is incomplete or out of bounds.", nameof(state));
        }
    }

    private static async Task InsertMessageAsync(SqliteConnection connection, SystemMessage message, CancellationToken ct)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText =
            $"""
            INSERT INTO system_messages ({MessageColumns}, message_folded)
            VALUES ($id, $ticks, $node, $source, $severity, $code, $message, $metadata, $taskId, $componentType, $componentId, $folded);
            """;
        insert.Parameters.AddWithValue("$id", FormatId(message.Id));
        insert.Parameters.AddWithValue("$ticks", message.TimestampUtc.UtcTicks);
        insert.Parameters.AddWithValue("$node", message.Node.Value);
        insert.Parameters.AddWithValue("$source", message.Source);
        insert.Parameters.AddWithValue("$severity", (int)message.Severity);
        insert.Parameters.AddWithValue("$code", message.Code);
        insert.Parameters.AddWithValue("$message", message.Message);
        insert.Parameters.AddWithValue("$metadata", message.Metadata.ToString());
        insert.Parameters.AddWithValue("$taskId", message.TaskId is { } taskId ? FormatId(taskId) : DBNull.Value);
        insert.Parameters.AddWithValue("$componentType", message.ComponentType is { } type ? (int)type : DBNull.Value);
        insert.Parameters.AddWithValue("$componentId", (object?)message.ComponentId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$folded", Fold(message.Message));

        try
        {
            await insert.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"A system message with id {message.Id} is already stored.", ex);
        }
    }

    private static async Task<PrerequisiteStateRecord?> LoadStateAsync(SqliteConnection connection, NodeId node, string prerequisiteId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT node, prerequisite_id, state, code, message, checked_at_utc_ticks, changed_at_utc_ticks, metadata_json " +
            "FROM prerequisite_states WHERE node = $node AND prerequisite_id = $id;";
        command.Parameters.AddWithValue("$node", node.Value);
        command.Parameters.AddWithValue("$id", prerequisiteId);

        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadState(reader) : null;
    }

    private static PrerequisiteStateRecord ReadState(SqliteDataReader reader) => new()
    {
        Node = new NodeId(reader.GetString(0)),
        PrerequisiteId = reader.GetString(1),
        State = Enum.Parse<PrerequisiteState>(reader.GetString(2)),
        Code = reader.GetString(3),
        Message = reader.GetString(4),
        CheckedAtUtc = new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
        ChangedAtUtc = new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
        Metadata = ReadMetadata(reader.GetString(7)),
    };

    private static SystemMessage ReadMessage(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        TimestampUtc = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
        Node = new NodeId(reader.GetString(2)),
        Source = reader.GetString(3),
        Severity = (SystemMessageSeverity)reader.GetInt32(4),
        Code = reader.GetString(5),
        Message = reader.GetString(6),
        Metadata = ReadMetadata(reader.GetString(7)),
        TaskId = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
        ComponentType = reader.IsDBNull(9) ? null : (SystemComponentType)reader.GetInt32(9),
        ComponentId = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private static OperationalMetadata ReadMetadata(string json) =>
        JsonNode.Parse(json) is JsonObject values ? OperationalMetadata.From(values) : OperationalMetadata.Empty;

    // The invariant upper-case fold gives Unicode-aware case-insensitive matching that SQLite's ASCII-only lower() cannot.
    private static string Fold(string text) => text.ToUpperInvariant();

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string EncodeCursor(SystemMessage last) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"{CursorVersion}:{last.TimestampUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{FormatId(last.Id)}"));

    private static (long Ticks, string Id) DecodeCursor(string cursor)
    {
        var buffer = new byte[Base64Url.GetMaxDecodedLength(Math.Min(cursor.Length, MaxCursorLength))];
        if (cursor.Length <= MaxCursorLength && Base64Url.TryDecodeFromChars(cursor, buffer, out var written))
        {
            var parts = Encoding.UTF8.GetString(buffer, 0, written).Split(':');
            if (parts.Length == 3
                && parts[0] == CursorVersion
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && Guid.TryParseExact(parts[2], "D", out var id))
            {
                return (ticks, FormatId(id));
            }
        }

        throw new ArgumentException("The cursor is not valid.", nameof(cursor));
    }

    private static async Task BeginImmediateAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task CommitAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "COMMIT;";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task RollbackAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ROLLBACK;";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);

            // Per-connection settings: wait for a writer rather than fail, and sync every commit to disk.
            using var pragmas = connection.CreateCommand();
            pragmas.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
            await pragmas.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
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

        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            var current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (current > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The system message store '{_filePath}' has schema version {current}; this build supports up to {SchemaVersion}.");
            }
        }

        using var command = connection.CreateCommand();
        // user_version must equal SchemaVersion; a PRAGMA cannot take a parameter.
        command.CommandText =
            """
            BEGIN IMMEDIATE;
            CREATE TABLE IF NOT EXISTS system_messages (
                id TEXT PRIMARY KEY,
                timestamp_utc_ticks INTEGER NOT NULL,
                node TEXT NOT NULL,
                source TEXT NOT NULL,
                severity INTEGER NOT NULL,
                code TEXT NOT NULL,
                message TEXT NOT NULL,
                message_folded TEXT NOT NULL,
                metadata_json TEXT NOT NULL,
                task_id TEXT NULL,
                component_type INTEGER NULL,
                component_id TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_system_messages_order ON system_messages (timestamp_utc_ticks DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_system_messages_severity_order ON system_messages (severity, timestamp_utc_ticks DESC, id DESC);
            CREATE TABLE IF NOT EXISTS prerequisite_states (
                node TEXT NOT NULL,
                prerequisite_id TEXT NOT NULL,
                state TEXT NOT NULL,
                code TEXT NOT NULL,
                message TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                checked_at_utc_ticks INTEGER NOT NULL,
                changed_at_utc_ticks INTEGER NOT NULL,
                metadata_json TEXT NOT NULL,
                PRIMARY KEY (node, prerequisite_id)
            );
            PRAGMA user_version = 1;
            COMMIT;
            """;
        command.ExecuteNonQuery();

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
