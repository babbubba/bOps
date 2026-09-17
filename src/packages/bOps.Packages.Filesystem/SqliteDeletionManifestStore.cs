// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Packages.Filesystem;

public sealed class SqliteDeletionManifestStore
{
    private const int AppendBatchSize = 128;
    private readonly string _connectionString;
    private readonly string _filePath;
    private readonly Lazy<bool> _initialized;

    public SqliteDeletionManifestStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _filePath = Path.GetFullPath(filePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _filePath, Pooling = false }.ToString();
        _initialized = new Lazy<bool>(() =>
        {
            EnsureSchema();
            return true;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal async Task<string> StartAsync(
        FilesystemManifestScope scope,
        IReadOnlyList<string> roots,
        IReadOnlyList<string> warnings,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset retainUntilUtc,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using var connection = await OpenAsync(ct);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO deletion_manifests
                    (id, node_id, task_id, actor_kind, actor_id, status, created_at_utc,
                     expires_at_utc, retain_until_utc, approval_hash, entry_count, file_count,
                     directory_count, link_count, total_bytes, deleted_count, failure_count)
                VALUES
                    ($id, $node, $task, $actorKind, $actorId, 'building', $created,
                     $expires, $retain, NULL, 0, 0, 0, 0, 0, 0, 0);
                """;
            AddScopeParameters(command, id, scope);
            command.Parameters.AddWithValue("$created", createdAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$expires", expiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$retain", retainUntilUtc.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct);
        }

        await InsertStringsAsync(connection, transaction, "deletion_manifest_roots", "root_path", id, roots, ct);
        await InsertStringsAsync(connection, transaction, "deletion_manifest_warnings", "warning", id, warnings, ct);
        await transaction.CommitAsync(ct);
        return id;
    }

    internal async Task AppendEntriesAsync(
        string manifestId,
        IReadOnlyList<DeletionManifestEntry> entries,
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
                INSERT INTO deletion_manifest_entries
                    (manifest_id, ordinal, absolute_path, root_path, relative_path, entry_type,
                     size_bytes, creation_utc_ticks, last_write_utc_ticks, attributes, link_target, depth)
                VALUES
                    ($manifest, $ordinal, $absolute, $root, $relative, $type,
                     $size, $created, $written, $attributes, $linkTarget, $depth);
                """;
            var manifest = command.Parameters.Add("$manifest", SqliteType.Text);
            var ordinal = command.Parameters.Add("$ordinal", SqliteType.Integer);
            var absolute = command.Parameters.Add("$absolute", SqliteType.Text);
            var root = command.Parameters.Add("$root", SqliteType.Text);
            var relative = command.Parameters.Add("$relative", SqliteType.Text);
            var type = command.Parameters.Add("$type", SqliteType.Text);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            var created = command.Parameters.Add("$created", SqliteType.Integer);
            var written = command.Parameters.Add("$written", SqliteType.Integer);
            var attributes = command.Parameters.Add("$attributes", SqliteType.Integer);
            var linkTarget = command.Parameters.Add("$linkTarget", SqliteType.Text);
            var depth = command.Parameters.Add("$depth", SqliteType.Integer);

            var end = Math.Min(offset + AppendBatchSize, entries.Count);
            for (var index = offset; index < end; index++)
            {
                var entry = entries[index];
                manifest.Value = manifestId;
                ordinal.Value = entry.Ordinal;
                absolute.Value = entry.AbsolutePath;
                root.Value = entry.RootPath;
                relative.Value = entry.RelativePath;
                type.Value = entry.Type;
                size.Value = entry.SizeBytes is { } bytes ? bytes : DBNull.Value;
                created.Value = entry.CreationTimeUtcTicks;
                written.Value = entry.LastWriteTimeUtcTicks;
                attributes.Value = entry.Attributes;
                linkTarget.Value = entry.LinkTarget is { } target ? target : DBNull.Value;
                depth.Value = entry.Depth;
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
    }

    internal async Task<DeletionManifestSummary> CompleteAsync(
        string manifestId,
        FilesystemManifestScope scope,
        int entryCount,
        int fileCount,
        int directoryCount,
        int linkCount,
        long totalBytes,
        CancellationToken ct)
    {
        var hash = await ComputeApprovalHashAsync(
            manifestId, scope, entryCount, fileCount, directoryCount, linkCount, totalBytes, ct);
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE deletion_manifests
            SET status = 'ready', approval_hash = $hash, entry_count = $entryCount,
                file_count = $files, directory_count = $directories, link_count = $links,
                total_bytes = $bytes
            WHERE id = $id AND node_id = $node AND task_id = $task
              AND actor_kind = $actorKind AND actor_id = $actorId AND status = 'building';
            """;
        AddScopeParameters(command, manifestId, scope);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$entryCount", entryCount);
        command.Parameters.AddWithValue("$files", fileCount);
        command.Parameters.AddWithValue("$directories", directoryCount);
        command.Parameters.AddWithValue("$links", linkCount);
        command.Parameters.AddWithValue("$bytes", totalBytes);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException("The deletion manifest was not in a completable building state.");
        }

        return (await TryGetSummaryAsync(manifestId, scope, DateTimeOffset.MinValue, ct))!;
    }

    internal async Task MarkRejectedAsync(
        string manifestId,
        FilesystemManifestScope scope,
        CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE deletion_manifests SET status = 'rejected'
            WHERE id = $id AND node_id = $node AND task_id = $task
              AND actor_kind = $actorKind AND actor_id = $actorId
              AND status IN ('building', 'ready', 'approved');
            """;
        AddScopeParameters(command, manifestId, scope);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task<DeletionApprovalBindingResult> BindDecisionAsync(
        string manifestId,
        string approvalHash,
        FilesystemManifestScope scope,
        bool approved,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT status, approval_hash, expires_at_utc
            FROM deletion_manifests
            WHERE id = $id AND node_id = $node AND task_id = $task
              AND actor_kind = $actorKind AND actor_id = $actorId;
            """;
        AddScopeParameters(select, manifestId, scope);
        using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return DeletionApprovalBindingResult.NotFound;
        }

        var status = reader.GetString(0);
        var storedHash = await reader.IsDBNullAsync(1, ct) ? string.Empty : reader.GetString(1);
        var expiresAtUtc = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
        await reader.DisposeAsync();

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(approvalHash)))
        {
            return DeletionApprovalBindingResult.HashMismatch;
        }

        if (expiresAtUtc <= nowUtc)
        {
            await UpdateStatusAsync(connection, transaction, manifestId, scope, "expired", ct);
            await transaction.CommitAsync(ct);
            return DeletionApprovalBindingResult.Expired;
        }

        if (!string.Equals(status, "ready", StringComparison.Ordinal))
        {
            return DeletionApprovalBindingResult.InvalidState;
        }

        await UpdateStatusAsync(connection, transaction, manifestId, scope, approved ? "approved" : "rejected", ct);
        await transaction.CommitAsync(ct);
        return approved ? DeletionApprovalBindingResult.Bound : DeletionApprovalBindingResult.Rejected;
    }

    internal async Task<bool> TryBeginExecutionAsync(
        string manifestId,
        string approvalHash,
        FilesystemManifestScope scope,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE deletion_manifests SET status = 'executing'
            WHERE id = $id AND node_id = $node AND task_id = $task
              AND actor_kind = $actorKind AND actor_id = $actorId
              AND status = 'approved' AND approval_hash = $hash AND expires_at_utc > $now;
            """;
        AddScopeParameters(command, manifestId, scope);
        command.Parameters.AddWithValue("$hash", approvalHash);
        command.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    internal async Task RecordResultAsync(
        string manifestId,
        FilesystemManifestScope scope,
        int ordinal,
        string outcome,
        string? error,
        DateTimeOffset attemptedAtUtc,
        CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO deletion_manifest_results(manifest_id, entry_ordinal, outcome, error, attempted_at_utc)
                SELECT $id, $ordinal, $outcome, $error, $attempted
                WHERE EXISTS (
                    SELECT 1 FROM deletion_manifests
                    WHERE id = $id AND node_id = $node AND task_id = $task
                      AND actor_kind = $actorKind AND actor_id = $actorId)
                ON CONFLICT(manifest_id, entry_ordinal) DO UPDATE SET
                    outcome = excluded.outcome, error = excluded.error, attempted_at_utc = excluded.attempted_at_utc;
                """;
            AddScopeParameters(insert, manifestId, scope);
            insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$outcome", outcome);
            insert.Parameters.AddWithValue("$error", error is null ? DBNull.Value : error);
            insert.Parameters.AddWithValue("$attempted", attemptedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(ct);
        }

        using (var aggregate = connection.CreateCommand())
        {
            aggregate.Transaction = transaction;
            aggregate.CommandText =
                """
                UPDATE deletion_manifests
                SET deleted_count = (SELECT COUNT(*) FROM deletion_manifest_results WHERE manifest_id = $id AND outcome = 'deleted'),
                    failure_count = (SELECT COUNT(*) FROM deletion_manifest_results WHERE manifest_id = $id AND outcome <> 'deleted')
                WHERE id = $id;
                """;
            aggregate.Parameters.AddWithValue("$id", manifestId);
            await aggregate.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    internal Task MarkPartiallyCompletedAsync(
        string manifestId,
        FilesystemManifestScope scope,
        CancellationToken ct = default) =>
        SetStatusAsync(manifestId, scope, "partially_completed", ["executing"], ct);

    internal Task SetVerificationStatusAsync(
        string manifestId,
        FilesystemManifestScope scope,
        VerificationStatus status,
        CancellationToken ct = default) =>
        SetStatusAsync(
            manifestId,
            scope,
            status switch
            {
                VerificationStatus.Confirmed => "verified",
                VerificationStatus.Refuted => "refuted",
                _ => "inconclusive",
            },
            ["executing", "partially_completed", "rejected"],
            ct);

    public async Task<DeletionManifestSummary?> TryGetSummaryAsync(
        string manifestId,
        FilesystemManifestScope scope,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentNullException.ThrowIfNull(scope);
        if (nowUtc != DateTimeOffset.MinValue)
        {
            await ExpireAsync(manifestId, scope, nowUtc, ct);
        }

        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT status, approval_hash, created_at_utc, expires_at_utc, entry_count,
                   file_count, directory_count, link_count, total_bytes, deleted_count, failure_count
            FROM deletion_manifests
            WHERE id = $id AND node_id = $node AND task_id = $task
              AND actor_kind = $actorKind AND actor_id = $actorId;
            """;
        AddScopeParameters(command, manifestId, scope);
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var summary = new
        {
            Status = ParseStatus(reader.GetString(0)),
            Hash = await reader.IsDBNullAsync(1, ct) ? string.Empty : reader.GetString(1),
            Created = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            Expires = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            EntryCount = reader.GetInt32(4),
            Files = reader.GetInt32(5),
            Directories = reader.GetInt32(6),
            Links = reader.GetInt32(7),
            Bytes = reader.GetInt64(8),
            Deleted = reader.GetInt32(9),
            Failures = reader.GetInt32(10),
        };
        await reader.DisposeAsync();

        var roots = await ReadStringsAsync(connection, "deletion_manifest_roots", "root_path", manifestId, ct);
        var warnings = await ReadStringsAsync(connection, "deletion_manifest_warnings", "warning", manifestId, ct);
        return new DeletionManifestSummary(
            manifestId,
            summary.Status,
            roots,
            warnings,
            summary.Hash,
            summary.Created,
            summary.Expires,
            summary.EntryCount,
            summary.Files,
            summary.Directories,
            summary.Links,
            summary.Bytes,
            summary.Deleted,
            summary.Failures);
    }

    public async Task<DeletionManifestPage?> TryGetPageAsync(
        string manifestId,
        FilesystemManifestScope scope,
        DateTimeOffset nowUtc,
        string? cursor,
        int limit,
        string? search,
        CancellationToken ct = default)
    {
        if (await TryGetSummaryAsync(manifestId, scope, nowUtc, ct) is null)
        {
            return null;
        }

        var afterOrdinal = DecodeCursor(cursor);
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.ordinal, e.absolute_path, e.root_path, e.relative_path, e.entry_type,
                   e.size_bytes, e.creation_utc_ticks, e.last_write_utc_ticks, e.attributes,
                   e.link_target, e.depth, r.outcome, r.error
            FROM deletion_manifest_entries e
            LEFT JOIN deletion_manifest_results r
              ON r.manifest_id = e.manifest_id AND r.entry_ordinal = e.ordinal
            INNER JOIN deletion_manifests m ON m.id = e.manifest_id
            WHERE e.manifest_id = $id AND m.node_id = $node AND m.task_id = $task
              AND m.actor_kind = $actorKind AND m.actor_id = $actorId
              AND e.ordinal > $after
              AND ($search = '' OR instr(lower(e.absolute_path), lower($search)) > 0)
            ORDER BY e.ordinal
            LIMIT $limit;
            """;
        AddScopeParameters(command, manifestId, scope);
        command.Parameters.AddWithValue("$after", afterOrdinal);
        command.Parameters.AddWithValue("$search", search ?? string.Empty);
        command.Parameters.AddWithValue("$limit", limit + 1);
        using var reader = await command.ExecuteReaderAsync(ct);
        var entries = new List<DeletionManifestEntry>(limit + 1);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(ReadEntry(reader));
        }

        var hasMore = entries.Count > limit;
        if (hasMore)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        var nextCursor = hasMore && entries.Count > 0 ? EncodeCursor(entries[^1].Ordinal) : null;
        return new DeletionManifestPage(entries, nextCursor);
    }

    internal async IAsyncEnumerable<DeletionManifestEntry> ReadEntriesAsync(
        string manifestId,
        FilesystemManifestScope scope,
        bool childrenFirst,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            SELECT e.ordinal, e.absolute_path, e.root_path, e.relative_path, e.entry_type,
                   e.size_bytes, e.creation_utc_ticks, e.last_write_utc_ticks, e.attributes,
                   e.link_target, e.depth, r.outcome, r.error
            FROM deletion_manifest_entries e
            LEFT JOIN deletion_manifest_results r
              ON r.manifest_id = e.manifest_id AND r.entry_ordinal = e.ordinal
            INNER JOIN deletion_manifests m ON m.id = e.manifest_id
            WHERE e.manifest_id = $id AND m.node_id = $node AND m.task_id = $task
              AND m.actor_kind = $actorKind AND m.actor_id = $actorId
            ORDER BY {{(childrenFirst ? "e.depth DESC, e.absolute_path COLLATE BINARY DESC" : "e.ordinal")}};
            """;
        AddScopeParameters(command, manifestId, scope);
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return ReadEntry(reader);
        }
    }

    public async Task<int> CleanupAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM deletion_manifests WHERE retain_until_utc <= $now;";
        command.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> ComputeApprovalHashAsync(
        string manifestId,
        FilesystemManifestScope scope,
        int entryCount,
        int fileCount,
        int directoryCount,
        int linkCount,
        long totalBytes,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "bops.deletion-manifest.v1");
        AppendString(hash, "permanent;children-first;no-follow-links;full-preflight-reconciliation");
        AppendString(hash, manifestId);
        AppendString(hash, scope.Node.Value);
        AppendString(hash, scope.TaskId.ToString("D", CultureInfo.InvariantCulture));
        AppendString(hash, scope.ActorKind);
        AppendString(hash, scope.ActorId);

        using (var connection = await OpenAsync(ct))
        {
            using var metadata = connection.CreateCommand();
            metadata.CommandText = "SELECT created_at_utc, expires_at_utc FROM deletion_manifests WHERE id = $id;";
            metadata.Parameters.AddWithValue("$id", manifestId);
            using var metadataReader = await metadata.ExecuteReaderAsync(ct);
            if (!await metadataReader.ReadAsync(ct))
            {
                throw new InvalidOperationException("Deletion manifest metadata disappeared while hashing.");
            }

            AppendString(hash, metadataReader.GetString(0));
            AppendString(hash, metadataReader.GetString(1));
            await metadataReader.DisposeAsync();
            foreach (var root in await ReadStringsAsync(connection, "deletion_manifest_roots", "root_path", manifestId, ct))
            {
                AppendString(hash, root);
            }
        }

        AppendInt64(hash, entryCount);
        AppendInt64(hash, fileCount);
        AppendInt64(hash, directoryCount);
        AppendInt64(hash, linkCount);
        AppendInt64(hash, totalBytes);
        await foreach (var entry in ReadEntriesAsync(manifestId, scope, childrenFirst: false, ct))
        {
            AppendString(hash, entry.AbsolutePath);
            AppendString(hash, entry.Type);
            AppendInt64(hash, entry.SizeBytes ?? -1);
            AppendInt64(hash, entry.CreationTimeUtcTicks);
            AppendInt64(hash, entry.LastWriteTimeUtcTicks);
            AppendInt64(hash, entry.Attributes);
            AppendString(hash, entry.LinkTarget ?? string.Empty);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task ExpireAsync(
        string manifestId,
        FilesystemManifestScope scope,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE deletion_manifests SET status = 'expired'
            WHERE id = $id AND node_id = $node AND task_id = $task
              AND actor_kind = $actorKind AND actor_id = $actorId
              AND status IN ('building', 'ready', 'approved') AND expires_at_utc <= $now;
            """;
        AddScopeParameters(command, manifestId, scope);
        command.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task SetStatusAsync(
        string manifestId,
        FilesystemManifestScope scope,
        string status,
        IReadOnlyList<string> fromStatuses,
        CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        if (fromStatuses.Count == 1 && fromStatuses[0] == "executing")
        {
            command.CommandText =
                """
                UPDATE deletion_manifests SET status = $status
                WHERE id = $id AND node_id = $node AND task_id = $task
                  AND actor_kind = $actorKind AND actor_id = $actorId AND status = 'executing';
                """;
        }
        else
        {
            command.CommandText =
                """
                UPDATE deletion_manifests SET status = $status
                WHERE id = $id AND node_id = $node AND task_id = $task
                  AND actor_kind = $actorKind AND actor_id = $actorId
                  AND status IN ('executing', 'partially_completed', 'rejected');
                """;
        }
        AddScopeParameters(command, manifestId, scope);
        command.Parameters.AddWithValue("$status", status);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string manifestId,
        FilesystemManifestScope scope,
        string status,
        CancellationToken ct)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE deletion_manifests SET status = $status
            WHERE id = $id AND node_id = $node AND task_id = $task
              AND actor_kind = $actorKind AND actor_id = $actorId;
            """;
        AddScopeParameters(update, manifestId, scope);
        update.Parameters.AddWithValue("$status", status);
        await update.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertStringsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string manifestId,
        IReadOnlyList<string> values,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (table == "deletion_manifest_roots" && column == "root_path")
        {
            command.CommandText =
                "INSERT INTO deletion_manifest_roots(manifest_id, ordinal, root_path) VALUES ($id, $ordinal, $value);";
        }
        else if (table == "deletion_manifest_warnings" && column == "warning")
        {
            command.CommandText =
                "INSERT INTO deletion_manifest_warnings(manifest_id, ordinal, warning) VALUES ($id, $ordinal, $value);";
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var ordinal = command.Parameters.Add("$ordinal", SqliteType.Integer);
        var value = command.Parameters.Add("$value", SqliteType.Text);
        for (var index = 0; index < values.Count; index++)
        {
            id.Value = manifestId;
            ordinal.Value = index;
            value.Value = values[index];
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        SqliteConnection connection,
        string table,
        string column,
        string manifestId,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        if (table == "deletion_manifest_roots" && column == "root_path")
        {
            command.CommandText =
                "SELECT root_path FROM deletion_manifest_roots WHERE manifest_id = $id ORDER BY ordinal;";
        }
        else if (table == "deletion_manifest_warnings" && column == "warning")
        {
            command.CommandText =
                "SELECT warning FROM deletion_manifest_warnings WHERE manifest_id = $id ORDER BY ordinal;";
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }
        command.Parameters.AddWithValue("$id", manifestId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static DeletionManifestEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5),
        reader.GetInt64(6),
        reader.GetInt64(7),
        reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12));

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
            CREATE TABLE IF NOT EXISTS deletion_manifests (
                id TEXT PRIMARY KEY,
                node_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                actor_kind TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                retain_until_utc TEXT NOT NULL,
                approval_hash TEXT NULL,
                entry_count INTEGER NOT NULL,
                file_count INTEGER NOT NULL,
                directory_count INTEGER NOT NULL,
                link_count INTEGER NOT NULL,
                total_bytes INTEGER NOT NULL,
                deleted_count INTEGER NOT NULL,
                failure_count INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS deletion_manifest_roots (
                manifest_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                root_path TEXT NOT NULL,
                PRIMARY KEY(manifest_id, ordinal),
                FOREIGN KEY(manifest_id) REFERENCES deletion_manifests(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS deletion_manifest_warnings (
                manifest_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                warning TEXT NOT NULL,
                PRIMARY KEY(manifest_id, ordinal),
                FOREIGN KEY(manifest_id) REFERENCES deletion_manifests(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS deletion_manifest_entries (
                manifest_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                absolute_path TEXT NOT NULL,
                root_path TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                entry_type TEXT NOT NULL,
                size_bytes INTEGER NULL,
                creation_utc_ticks INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                attributes INTEGER NOT NULL,
                link_target TEXT NULL,
                depth INTEGER NOT NULL,
                PRIMARY KEY(manifest_id, ordinal),
                UNIQUE(manifest_id, absolute_path),
                FOREIGN KEY(manifest_id) REFERENCES deletion_manifests(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS deletion_manifest_results (
                manifest_id TEXT NOT NULL,
                entry_ordinal INTEGER NOT NULL,
                outcome TEXT NOT NULL,
                error TEXT NULL,
                attempted_at_utc TEXT NOT NULL,
                PRIMARY KEY(manifest_id, entry_ordinal),
                FOREIGN KEY(manifest_id) REFERENCES deletion_manifests(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_deletion_manifests_scope
                ON deletion_manifests(node_id, task_id, actor_kind, actor_id, status);
            CREATE INDEX IF NOT EXISTS ix_deletion_manifests_retention
                ON deletion_manifests(retain_until_utc);
            CREATE INDEX IF NOT EXISTS ix_deletion_entries_path
                ON deletion_manifest_entries(manifest_id, absolute_path);
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

    private static DeletionManifestStatus ParseStatus(string value) => value switch
    {
        "building" => DeletionManifestStatus.Building,
        "ready" => DeletionManifestStatus.Ready,
        "approved" => DeletionManifestStatus.Approved,
        "executing" => DeletionManifestStatus.Executing,
        "partially_completed" => DeletionManifestStatus.PartiallyCompleted,
        "verified" => DeletionManifestStatus.Verified,
        "refuted" => DeletionManifestStatus.Refuted,
        "inconclusive" => DeletionManifestStatus.Inconclusive,
        "expired" => DeletionManifestStatus.Expired,
        "rejected" => DeletionManifestStatus.Rejected,
        _ => throw new InvalidOperationException($"Unknown deletion manifest status '{value}'."),
    };

    private static string EncodeCursor(int ordinal)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, ordinal);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return -1;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(normalized);
            return bytes.Length == 4 ? BinaryPrimitives.ReadInt32BigEndian(bytes) : -1;
        }
        catch (FormatException)
        {
            return -1;
        }
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
