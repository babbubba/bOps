// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace bOps.Packages.Filesystem;

/// <summary>Metadata returned only after scope-authorized lookup of a ready exact manifest.</summary>
public sealed record FilesystemStoredManifest(
    string Id,
    string RootPath,
    string ContentHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int EntryCount);

/// <summary>SQLite-backed immutable filesystem manifest storage (ADR-0026).</summary>
public sealed class SqliteFilesystemManifestStore
{
    private const int AppendBatchSize = 128;
    private readonly string _connectionString;
    private readonly string _filePath;
    private readonly Lazy<bool> _initialized;

    public SqliteFilesystemManifestStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _filePath = Path.GetFullPath(filePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _filePath,
            Pooling = false,
        }.ToString();
        _initialized = new Lazy<bool>(() =>
        {
            EnsureSchema();
            return true;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal async Task<string> StartAsync(
        FilesystemManifestScope scope,
        string rootPath,
        FilesystemInventoryRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO filesystem_manifests
                (id, node_id, task_id, actor_kind, actor_id, root_path, max_depth, max_entries,
                 status, created_at_utc, expires_at_utc, entry_count, content_hash)
            VALUES
                ($id, $node, $task, $actorKind, $actorId, $root, $maxDepth, $maxEntries,
                 'building', $created, $expires, 0, NULL);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$node", scope.Node.Value);
        command.Parameters.AddWithValue("$task", scope.TaskId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actorKind", scope.ActorKind);
        command.Parameters.AddWithValue("$actorId", scope.ActorId);
        command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$maxDepth", request.MaxDepth);
        command.Parameters.AddWithValue("$maxEntries", request.MaxEntries);
        command.Parameters.AddWithValue("$created", createdAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expires", expiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    internal async Task AppendAsync(
        string manifestId,
        IReadOnlyList<FilesystemInventoryEntry> entries,
        CancellationToken ct)
    {
        for (var offset = 0; offset < entries.Count; offset += AppendBatchSize)
        {
            using var connection = await OpenAsync(ct);
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO filesystem_manifest_entries
                    (manifest_id, relative_path, entry_type, size_bytes, creation_utc_ticks,
                     last_write_utc_ticks, attributes, link_target)
                VALUES
                    ($manifest, $path, $type, $size, $created, $written, $attributes, $linkTarget);
                """;
            var manifest = command.Parameters.Add("$manifest", SqliteType.Text);
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var type = command.Parameters.Add("$type", SqliteType.Text);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            var created = command.Parameters.Add("$created", SqliteType.Integer);
            var written = command.Parameters.Add("$written", SqliteType.Integer);
            var attributes = command.Parameters.Add("$attributes", SqliteType.Integer);
            var linkTarget = command.Parameters.Add("$linkTarget", SqliteType.Text);

            var end = Math.Min(offset + AppendBatchSize, entries.Count);
            for (var index = offset; index < end; index++)
            {
                var entry = entries[index];
                manifest.Value = manifestId;
                path.Value = entry.RelativePath;
                type.Value = entry.Type;
                size.Value = entry.SizeBytes is { } bytes ? bytes : DBNull.Value;
                created.Value = entry.CreationTimeUtcTicks;
                written.Value = entry.LastWriteTimeUtcTicks;
                attributes.Value = entry.Attributes;
                linkTarget.Value = entry.LinkTarget is { } target ? target : DBNull.Value;
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
    }

    internal async Task<FilesystemManifestReference> CompleteAsync(
        string manifestId,
        FilesystemManifestScope scope,
        string rootPath,
        FilesystemInventoryRequest request,
        DateTimeOffset expiresAtUtc,
        int entryCount,
        CancellationToken ct)
    {
        var hash = await ComputeContentHashAsync(manifestId, scope, rootPath, request, ct);
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE filesystem_manifests
            SET status = 'ready', entry_count = $entryCount, content_hash = $hash
            WHERE id = $id AND node_id = $node AND task_id = $task
              AND actor_kind = $actorKind AND actor_id = $actorId AND status = 'building';
            """;
        command.Parameters.AddWithValue("$entryCount", entryCount);
        command.Parameters.AddWithValue("$hash", hash);
        AddScopeParameters(command, manifestId, scope);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException("The filesystem manifest was not in a completable building state.");
        }

        return new FilesystemManifestReference(manifestId, hash, expiresAtUtc, entryCount);
    }

    internal async Task MarkIncompleteAsync(string manifestId, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE filesystem_manifests SET status = 'incomplete' WHERE id = $id AND status = 'building';";
        command.Parameters.AddWithValue("$id", manifestId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<FilesystemStoredManifest?> TryGetReadyAsync(
        string manifestId,
        FilesystemManifestScope scope,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentNullException.ThrowIfNull(scope);
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT root_path, content_hash, created_at_utc, expires_at_utc, entry_count
            FROM filesystem_manifests
            WHERE id = $id AND node_id = $node AND task_id = $task
              AND actor_kind = $actorKind AND actor_id = $actorId
              AND status = 'ready' AND expires_at_utc > $now;
            """;
        AddScopeParameters(command, manifestId, scope);
        command.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new FilesystemStoredManifest(
            manifestId,
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.GetInt32(4));
    }

    internal async IAsyncEnumerable<FilesystemInventoryEntry> ReadEntriesAsync(
        string manifestId,
        FilesystemManifestScope scope,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.relative_path, e.entry_type, e.size_bytes, e.creation_utc_ticks,
                   e.last_write_utc_ticks, e.attributes, e.link_target
            FROM filesystem_manifest_entries e
            INNER JOIN filesystem_manifests m ON m.id = e.manifest_id
            WHERE m.id = $id AND m.node_id = $node AND m.task_id = $task
              AND m.actor_kind = $actorKind AND m.actor_id = $actorId
            ORDER BY e.relative_path COLLATE BINARY;
            """;
        AddScopeParameters(command, manifestId, scope);
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return ReadEntry(reader);
        }
    }

    public async Task<int> CleanupExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        using var deleteEntries = connection.CreateCommand();
        deleteEntries.Transaction = transaction;
        deleteEntries.CommandText =
            "DELETE FROM filesystem_manifest_entries WHERE manifest_id IN (SELECT id FROM filesystem_manifests WHERE expires_at_utc <= $now);";
        deleteEntries.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        await deleteEntries.ExecuteNonQueryAsync(ct);

        using var deleteManifests = connection.CreateCommand();
        deleteManifests.Transaction = transaction;
        deleteManifests.CommandText = "DELETE FROM filesystem_manifests WHERE expires_at_utc <= $now;";
        deleteManifests.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        var deleted = await deleteManifests.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }

    private async Task<string> ComputeContentHashAsync(
        string manifestId,
        FilesystemManifestScope scope,
        string rootPath,
        FilesystemInventoryRequest request,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "bops.filesystem-manifest.v1");
        AppendString(hash, rootPath);
        AppendInt64(hash, request.MaxDepth);
        AppendString(hash, "ordinal-relative-path;no-follow-links");

        await foreach (var entry in ReadEntriesAsync(manifestId, scope, ct))
        {
            AppendString(hash, entry.RelativePath);
            AppendString(hash, entry.Type);
            AppendInt64(hash, entry.SizeBytes ?? -1);
            AppendInt64(hash, entry.CreationTimeUtcTicks);
            AppendInt64(hash, entry.LastWriteTimeUtcTicks);
            AppendInt64(hash, entry.Attributes);
            AppendString(hash, entry.LinkTarget ?? string.Empty);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static FilesystemInventoryEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        reader.GetInt64(3),
        reader.GetInt64(4),
        reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6));

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        _ = _initialized.Value;
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
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
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS filesystem_manifests (
                id TEXT PRIMARY KEY,
                node_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                actor_kind TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                root_path TEXT NOT NULL,
                max_depth INTEGER NOT NULL,
                max_entries INTEGER NOT NULL,
                status TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                entry_count INTEGER NOT NULL,
                content_hash TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS filesystem_manifest_entries (
                manifest_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                entry_type TEXT NOT NULL,
                size_bytes INTEGER NULL,
                creation_utc_ticks INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                attributes INTEGER NOT NULL,
                link_target TEXT NULL,
                PRIMARY KEY (manifest_id, relative_path),
                FOREIGN KEY (manifest_id) REFERENCES filesystem_manifests(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_filesystem_manifests_scope
                ON filesystem_manifests(node_id, task_id, actor_kind, actor_id, status);
            CREATE INDEX IF NOT EXISTS ix_filesystem_manifests_expiry
                ON filesystem_manifests(expires_at_utc);
            """;
        command.ExecuteNonQuery();

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void AddScopeParameters(SqliteCommand command, string manifestId, FilesystemManifestScope scope)
    {
        command.Parameters.AddWithValue("$id", manifestId);
        command.Parameters.AddWithValue("$node", scope.Node.Value);
        command.Parameters.AddWithValue("$task", scope.TaskId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actorKind", scope.ActorKind);
        command.Parameters.AddWithValue("$actorId", scope.ActorId);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
