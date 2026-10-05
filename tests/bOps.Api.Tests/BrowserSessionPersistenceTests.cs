// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace bOps.Api.Tests;

/// <summary>ADR-0043 §5: the session store, its file, schema version, restart, cleanup and failure modes.</summary>
public sealed class BrowserSessionPersistenceTests
{
    [Fact]
    public void FreshFile_IsCreatedAtSchemaVersion1_InItsOwnFile()
    {
        var directory = BrowserSessionCredentialTests.NewDirectory();
        var path = Path.Combine(directory, "nested", "sessions.db");

        _ = new SqliteBrowserSessionStore(path);

        Assert.True(File.Exists(path));
        Assert.Equal(1L, Scalar(path, Query.UserVersion));
        Assert.Equal("wal", Scalar(path, Query.JournalMode));
        Assert.Equal(1L, Scalar(path, Query.SessionTable));
        _ = new SqliteBrowserSessionStore(path);
        Assert.Equal(1L, Scalar(path, Query.UserVersion));
    }

    [Fact]
    public void NewerSchemaVersion_RefusesToOpen()
    {
        var path = Path.Combine(BrowserSessionCredentialTests.NewDirectory(), "sessions.db");
        WriteUserVersion(path, 2);

        var error = Assert.Throws<InvalidOperationException>(() => new SqliteBrowserSessionStore(path));

        Assert.Contains("schema version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewerSchemaVersion_FailsHostStartup()
    {
        var directory = BrowserSessionCredentialTests.NewDirectory();
        WriteUserVersion(Path.Combine(directory, "sessions.db"), 7);
        using var factory = new TestAppFactory { TempDirectory = directory };

        var error = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains("schema version 7", Flatten(error), StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptFile_FailsHostStartup_InsteadOfAuthenticating()
    {
        var directory = BrowserSessionCredentialTests.NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "sessions.db"), RandomNumberGenerator.GetBytes(8192));
        using var factory = new TestAppFactory { TempDirectory = directory };

        Assert.ThrowsAny<Exception>(() => factory.Services);
    }

    [Fact]
    public async Task ExistingInstallationWithoutSessionsDb_StartsAndLeavesTheTaskDatabaseAlone()
    {
        var directory = BrowserSessionCredentialTests.NewDirectory();
        using (var first = new TestAppFactory { TempDirectory = directory, KeepTempDirectory = true, VaultMasterKey = "vault-master-key-existing-install" })
        {
            _ = first.Services;
        }

        SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(directory, "sessions.db"));
        foreach (var sidecar in Directory.GetFiles(directory, "sessions.db-*"))
        {
            File.Delete(sidecar);
        }

        using var factory = new TestAppFactory { TempDirectory = directory, VaultMasterKey = "vault-master-key-existing-install" };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var me = await BrowserSession.GetMeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(0L, Scalar(Path.Combine(directory, "tasks.db"), Query.SessionTable));
    }

    [Fact]
    public async Task ASession_SurvivesAnApiRestart()
    {
        var directory = BrowserSessionCredentialTests.NewDirectory();
        var key = $"k-{Guid.NewGuid():N}";
        string cookie;
        using (var before = new TestAppFactory { TempDirectory = directory, KeepTempDirectory = true, ApiKey = key, VaultMasterKey = "vault-master-key-restart-session" })
        {
            using var client = before.CreateSessionClient();
            cookie = await BrowserSession.SignInAsync(client, key);
        }

        using var after = new TestAppFactory { TempDirectory = directory, ApiKey = key, VaultMasterKey = "vault-master-key-restart-session" };
        using var http = after.CreateSessionClient();

        using var me = await BrowserSession.GetMeAsync(http, cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task DeleteExpired_RemovesExactlyTheExpiredRows()
    {
        var store = new SqliteBrowserSessionStore(Path.Combine(BrowserSessionCredentialTests.NewDirectory(), "sessions.db"));
        const long now = 10_000_000;
        const long idle = 3_600_000;
        const long absolute = 43_200_000;
        var live = Record(created: now - 1000, lastSeen: now - 1000, absoluteAt: now + 1000);
        var idleAtBoundary = Record(created: now - idle, lastSeen: now - idle, absoluteAt: now + 1000);
        var absoluteAtBoundary = Record(created: now - 1000, lastSeen: now - 1000, absoluteAt: now);
        var configuredAbsolute = Record(created: now - absolute, lastSeen: now - 1000, absoluteAt: now + 1000);
        var idleJustInside = Record(created: now - idle + 1, lastSeen: now - idle + 1, absoluteAt: now + 1000);
        foreach (var record in new[] { live, idleAtBoundary, absoluteAtBoundary, configuredAbsolute, idleJustInside })
        {
            await store.CreateAsync(record, supersededDigest: null);
        }

        var deleted = await store.DeleteExpiredAsync(now, idle, absolute);

        Assert.Equal(3, deleted);
        Assert.NotNull(await store.FindAsync(live.TokenDigest));
        Assert.NotNull(await store.FindAsync(idleJustInside.TokenDigest));
        Assert.Null(await store.FindAsync(idleAtBoundary.TokenDigest));
        Assert.Null(await store.FindAsync(absoluteAtBoundary.TokenDigest));
        Assert.Null(await store.FindAsync(configuredAbsolute.TokenDigest));
    }

    [Fact]
    public async Task Touch_IsMonotonic_AndNeverRevivesAnExpiredRow()
    {
        var store = new SqliteBrowserSessionStore(Path.Combine(BrowserSessionCredentialTests.NewDirectory(), "sessions.db"));
        const long idle = 3_600_000;
        const long absolute = 43_200_000;
        var record = Record(created: 1_000, lastSeen: 1_000, absoluteAt: 1_000 + absolute);
        await store.CreateAsync(record, supersededDigest: null);

        Assert.True(await store.TouchAsync(record.TokenDigest, 100_000, idle, absolute));
        Assert.False(await store.TouchAsync(record.TokenDigest, 50_000, idle, absolute));
        Assert.Equal(100_000, (await store.FindAsync(record.TokenDigest))!.LastSeenAtUnixMs);
        Assert.False(await store.TouchAsync(record.TokenDigest, 100_000 + idle, idle, absolute));
        Assert.Equal(100_000, (await store.FindAsync(record.TokenDigest))!.LastSeenAtUnixMs);
    }

    [Fact]
    public async Task Create_WithASupersededDigest_ReplacesItAtomically_AndADuplicateDigestIsRefused()
    {
        var store = new SqliteBrowserSessionStore(Path.Combine(BrowserSessionCredentialTests.NewDirectory(), "sessions.db"));
        var old = Record(1, 1, 100_000);
        var renewed = Record(2, 2, 100_000);
        await store.CreateAsync(old, supersededDigest: null);

        await store.CreateAsync(renewed, old.TokenDigest);

        Assert.Null(await store.FindAsync(old.TokenDigest));
        Assert.NotNull(await store.FindAsync(renewed.TokenDigest));
        await Assert.ThrowsAsync<DuplicateSessionDigestException>(() => store.CreateAsync(renewed with { CredentialId = "other" }, old.TokenDigest));
        Assert.Equal("ops", (await store.FindAsync(renewed.TokenDigest))!.CredentialId);
    }

    [Fact]
    public async Task DeleteUnknownCredentials_KeepsOnlyConfiguredIds()
    {
        var store = new SqliteBrowserSessionStore(Path.Combine(BrowserSessionCredentialTests.NewDirectory(), "sessions.db"));
        var kept = Record(1, 1, 100_000);
        var removed = Record(1, 1, 100_000) with { CredentialId = "gone" };
        await store.CreateAsync(kept, null);
        await store.CreateAsync(removed, null);

        Assert.Equal(1, await store.DeleteUnknownCredentialsAsync(["ops", "someone-else"]));
        Assert.NotNull(await store.FindAsync(kept.TokenDigest));
        Assert.Null(await store.FindAsync(removed.TokenDigest));
        Assert.Equal(1, await store.DeleteUnknownCredentialsAsync([]));
    }

    [Fact]
    public async Task TheDatabase_HoldsNoKeyNoTokenAndNoCookieString()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var first = await BrowserSession.SignInAsync(client, factory.ApiKey, keepSignedIn: true);
        var second = await BrowserSession.SignInAsync(client, factory.ApiKey);
        (await BrowserSession.GetMeAsync(client, second)).Dispose();

        var bytes = BrowserSession.DatabaseBytes(factory);
        BrowserSession.AssertAbsent(bytes, factory.ApiKey, "sessions.db");
        foreach (var cookie in new[] { first, second })
        {
            BrowserSession.AssertAbsent(bytes, cookie, "sessions.db");
            BrowserSession.AssertAbsent(bytes, $"{BrowserSession.CookieName}={cookie}", "sessions.db");
            var token = System.Buffers.Text.Base64Url.DecodeFromChars(cookie);
            Assert.True(bytes.AsSpan().IndexOf(token) < 0, "raw token bytes were found in sessions.db");
            Assert.True(bytes.AsSpan().IndexOf(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(factory.ApiKey))) < 0, "an API-key digest was found in sessions.db");
        }

        using var connection = new SqliteConnection($"Data Source={BrowserSession.SessionsDb(factory)};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM browser_sessions;";
        using var reader = await command.ExecuteReaderAsync();
        Assert.Equal(6, reader.FieldCount);
    }

    // ADR-0043 §5.4: a store read failure never authenticates; Bearer does not depend on the store.
    [Fact]
    public async Task StoreReadFailure_IsAServerError_NeverAuthenticated_AndBearerIsUnaffected()
    {
        using var factory = new TestAppFactory
        {
            ConfigureExtraServices = services => services.Replace(ServiceDescriptor.Singleton<IBrowserSessionStore>(sp =>
                new FailingFindStore(new SqliteBrowserSessionStore(sp.GetRequiredService<BrowserSessionSettings>().FilePath)))),
        };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var cookieRequest = await BrowserSession.GetMeAsync(client, cookie);
        using var bearerRequest = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/session/me", null, bearer: $"Bearer {factory.ApiKey}");

        Assert.Equal(HttpStatusCode.InternalServerError, cookieRequest.StatusCode);
        var body = await cookieRequest.Content.ReadAsStringAsync();
        BrowserSession.AssertAbsent(body, cookie, "the 500 body");
        Assert.DoesNotContain("test-user", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, bearerRequest.StatusCode);
    }

    internal sealed class FailingFindStore(IBrowserSessionStore inner) : IBrowserSessionStore
    {
        public Task CreateAsync(BrowserSessionRecord record, byte[]? supersededDigest, CancellationToken ct = default) => inner.CreateAsync(record, supersededDigest, ct);

        public Task<BrowserSessionRecord?> FindAsync(byte[] digest, CancellationToken ct = default) =>
            throw new SqliteException("simulated store failure", 10);

        public Task<bool> TouchAsync(byte[] digest, long nowUnixMs, long idleMs, long absoluteMs, CancellationToken ct = default) =>
            inner.TouchAsync(digest, nowUnixMs, idleMs, absoluteMs, ct);

        public Task DeleteAsync(byte[] digest, CancellationToken ct = default) => inner.DeleteAsync(digest, ct);

        public Task<int> DeleteExpiredAsync(long nowUnixMs, long idleMs, long absoluteMs, CancellationToken ct = default) =>
            inner.DeleteExpiredAsync(nowUnixMs, idleMs, absoluteMs, ct);

        public Task<int> DeleteUnknownCredentialsAsync(IReadOnlyCollection<string> configuredIds, CancellationToken ct = default) =>
            inner.DeleteUnknownCredentialsAsync(configuredIds, ct);
    }

    private static BrowserSessionRecord Record(long created, long lastSeen, long absoluteAt) =>
        new(RandomNumberGenerator.GetBytes(32), "ops", RandomNumberGenerator.GetBytes(32), created, lastSeen, absoluteAt);

    private enum Query
    {
        UserVersion,
        JournalMode,
        SessionTable,
    }

    private static object? Scalar(string path, Query query)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        if (query == Query.UserVersion)
        {
            command.CommandText = "PRAGMA user_version;";
        }
        else if (query == Query.JournalMode)
        {
            command.CommandText = "PRAGMA journal_mode;";
        }
        else
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'browser_sessions';";
        }
        return command.ExecuteScalar();
    }

    private static void WriteUserVersion(string path, int version)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        if (version == 2)
        {
            command.CommandText = "PRAGMA user_version = 2;";
        }
        else
        {
            command.CommandText = "PRAGMA user_version = 7;";
        }
        command.ExecuteNonQuery();
    }

    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(Flatten));
            }
        }

        return string.Join(" | ", messages);
    }
}
