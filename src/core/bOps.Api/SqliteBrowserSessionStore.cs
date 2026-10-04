// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;

namespace bOps.Api;

/// <summary>
/// One persisted browser session (ADR-0043 §5.2). Never carries the token, the API key or any secret digest: only the token's
/// digest, the credential id and the token-keyed binding. Times are UTC Unix milliseconds.
/// </summary>
internal sealed record BrowserSessionRecord(
    byte[] TokenDigest,
    string CredentialId,
    byte[] CredentialBinding,
    long CreatedAtUnixMs,
    long LastSeenAtUnixMs,
    long AbsoluteExpiresAtUnixMs);

/// <summary>The primary key already holds this token digest (ADR-0043 §3: the caller retries once with a new token).</summary>
internal sealed class DuplicateSessionDigestException : Exception
{
    public DuplicateSessionDigestException() : this("A browser session with the same token digest already exists.")
    {
    }

    public DuplicateSessionDigestException(string message) : base(message)
    {
    }

    public DuplicateSessionDigestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The browser-session store (ADR-0043 §5.1): exactly these operations, no generic query surface.</summary>
internal interface IBrowserSessionStore
{
    /// <summary>Inserts <paramref name="record"/> and, in the same transaction, deletes <paramref name="supersededDigest"/> when given.</summary>
    Task CreateAsync(BrowserSessionRecord record, byte[]? supersededDigest, CancellationToken ct = default);

    Task<BrowserSessionRecord?> FindAsync(byte[] digest, CancellationToken ct = default);

    /// <summary>Advances <c>last_seen</c> to <paramref name="nowUnixMs"/> unless that would move it backwards or revive an expired session.</summary>
    Task<bool> TouchAsync(byte[] digest, long nowUnixMs, long idleMs, long absoluteMs, CancellationToken ct = default);

    Task DeleteAsync(byte[] digest, CancellationToken ct = default);

    Task<int> DeleteExpiredAsync(long nowUnixMs, long idleMs, long absoluteMs, CancellationToken ct = default);

    Task<int> DeleteUnknownCredentialsAsync(IReadOnlyCollection<string> configuredIds, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IBrowserSessionStore"/> in its own SQLite file (ADR-0043 §5.2–5.4): WAL, <c>busy_timeout=5000</c>,
/// <c>synchronous=FULL</c> (a logout's <c>204</c> must not be undone by a crash), owner-only file mode on Unix. Schema version is
/// <c>PRAGMA user_version</c>: 0 creates version 1, 1 opens, anything else refuses to open. No cache: every call reads the file.
/// </summary>
internal sealed class SqliteBrowserSessionStore : IBrowserSessionStore
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString;

    public SqliteBrowserSessionStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FilePath = fullPath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = true }.ToString();
        EnsureSchema();
    }

    public string FilePath { get; }

    public async Task CreateAsync(BrowserSessionRecord record, byte[]? supersededDigest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // One BEGIN IMMEDIATE transaction: the new session and the removal of the one it supersedes commit together or not at all.
        await BeginImmediateAsync(connection, ct).ConfigureAwait(false);
        try
        {
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO browser_sessions
                        (token_digest, credential_id, credential_binding, created_at_unix_ms, last_seen_at_unix_ms, absolute_expires_at_unix_ms)
                    VALUES ($digest, $credentialId, $binding, $created, $lastSeen, $absolute);
                    """;
                insert.Parameters.AddWithValue("$digest", record.TokenDigest);
                insert.Parameters.AddWithValue("$credentialId", record.CredentialId);
                insert.Parameters.AddWithValue("$binding", record.CredentialBinding);
                insert.Parameters.AddWithValue("$created", record.CreatedAtUnixMs);
                insert.Parameters.AddWithValue("$lastSeen", record.LastSeenAtUnixMs);
                insert.Parameters.AddWithValue("$absolute", record.AbsoluteExpiresAtUnixMs);
                try
                {
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.SqliteExtendedErrorCode == 1555)
                {
                    throw new DuplicateSessionDigestException("A browser session with the same token digest already exists.", ex);
                }
            }

            if (supersededDigest is not null)
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM browser_sessions WHERE token_digest = $digest;";
                delete.Parameters.AddWithValue("$digest", supersededDigest);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await CommitAsync(connection, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollbackAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<BrowserSessionRecord?> FindAsync(byte[] digest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(digest);

        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT credential_id, credential_binding, created_at_unix_ms, last_seen_at_unix_ms, absolute_expires_at_unix_ms
            FROM browser_sessions WHERE token_digest = $digest;
            """;
        command.Parameters.AddWithValue("$digest", digest);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new BrowserSessionRecord(
            digest,
            reader.GetString(0),
            (byte[])reader.GetValue(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    public async Task<bool> TouchAsync(byte[] digest, long nowUnixMs, long idleMs, long absoluteMs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(digest);

        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE browser_sessions SET last_seen_at_unix_ms = $now
            WHERE token_digest = $digest
              AND last_seen_at_unix_ms < $now
              AND last_seen_at_unix_ms + $idleMs > $now
              AND absolute_expires_at_unix_ms > $now
              AND created_at_unix_ms + $absoluteMs > $now;
            """;
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$now", nowUnixMs);
        command.Parameters.AddWithValue("$idleMs", idleMs);
        command.Parameters.AddWithValue("$absoluteMs", absoluteMs);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task DeleteAsync(byte[] digest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(digest);

        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM browser_sessions WHERE token_digest = $digest;";
        command.Parameters.AddWithValue("$digest", digest);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteExpiredAsync(long nowUnixMs, long idleMs, long absoluteMs, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM browser_sessions
            WHERE absolute_expires_at_unix_ms <= $now
               OR created_at_unix_ms + $absoluteMs <= $now
               OR last_seen_at_unix_ms + $idleMs <= $now;
            """;
        command.Parameters.AddWithValue("$now", nowUnixMs);
        command.Parameters.AddWithValue("$idleMs", idleMs);
        command.Parameters.AddWithValue("$absoluteMs", absoluteMs);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteUnknownCredentialsAsync(IReadOnlyCollection<string> configuredIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuredIds);

        var configured = configuredIds.ToHashSet(StringComparer.Ordinal);
        using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await BeginImmediateAsync(connection, ct).ConfigureAwait(false);
        try
        {
            var unknown = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT DISTINCT credential_id FROM browser_sessions;";
                using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    if (!configured.Contains(id))
                    {
                        unknown.Add(id);
                    }
                }
            }

            var deleted = 0;
            foreach (var id in unknown)
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM browser_sessions WHERE credential_id = $credentialId;";
                delete.Parameters.AddWithValue("$credentialId", id);
                deleted += await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await CommitAsync(connection, ct).ConfigureAwait(false);
            return deleted;
        }
        catch
        {
            await RollbackAsync(connection).ConfigureAwait(false);
            throw;
        }
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

    private static async Task RollbackAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ROLLBACK;";
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
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

        using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            pragmas.ExecuteNonQuery();
        }

        var version = ReadUserVersion(connection);
        if (version == 0)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            if (ReadUserVersion(connection, transaction) == 0)
            {
                using var create = connection.CreateCommand();
                create.Transaction = transaction;
                create.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS browser_sessions (
                        token_digest                BLOB    NOT NULL PRIMARY KEY CHECK (length(token_digest) = 32),
                        credential_id               TEXT    NOT NULL CHECK (length(credential_id) BETWEEN 1 AND 256),
                        credential_binding          BLOB    NOT NULL CHECK (length(credential_binding) = 32),
                        created_at_unix_ms          INTEGER NOT NULL,
                        last_seen_at_unix_ms        INTEGER NOT NULL,
                        absolute_expires_at_unix_ms INTEGER NOT NULL CHECK (absolute_expires_at_unix_ms > created_at_unix_ms)
                    ) WITHOUT ROWID;
                    CREATE INDEX IF NOT EXISTS ix_browser_sessions_absolute ON browser_sessions(absolute_expires_at_unix_ms);
                    CREATE INDEX IF NOT EXISTS ix_browser_sessions_last_seen ON browser_sessions(last_seen_at_unix_ms);
                    PRAGMA user_version = 1;
                    """;
                create.ExecuteNonQuery();
            }

            transaction.Commit();
            version = ReadUserVersion(connection);
        }

        if (version != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"The browser-session database '{FilePath}' has schema version {version}, which this bOps does not support " +
                $"(expected {SchemaVersion}). It was written by a different bOps version; refusing to start.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static long ReadUserVersion(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
