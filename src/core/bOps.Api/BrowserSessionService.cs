// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using System.Security.Cryptography;

namespace bOps.Api;

/// <summary>
/// Request feature set by the browser-session handler on success (ADR-0043 §8 step 6). Its presence is what the CSRF gate, the idle
/// touch and logout recognise as "authenticated by the browser session". Holds the token digest, never the token.
/// </summary>
internal sealed class BrowserSessionFeature(byte[] digest, long lastSeenAtUnixMs)
{
    public byte[] Digest { get; } = digest;

    public long LastSeenAtUnixMs { get; } = lastSeenAtUnixMs;
}

/// <summary>The outcome of validating a presented session cookie.</summary>
internal readonly record struct BrowserSessionValidation(ClaimsPrincipal? Principal, BrowserSessionFeature? Feature)
{
    public bool Succeeded => Principal is not null;
}

/// <summary>
/// The browser-session lifecycle (ADR-0043 §3–§6): validation in the normative order, creation with atomic supersede, expiry, touch
/// and bounded cleanup. Holds no cache: every validation reads the store, so revocation is effective at the next request.
/// </summary>
internal sealed partial class BrowserSessionService(
    IBrowserSessionStore store,
    ApiCredentialAuthority authority,
    BrowserSessionSettings settings,
    TimeProvider timeProvider,
    ILogger<BrowserSessionService> logger)
{
    public const string SchemeName = "bops-browser-session";

    /// <summary>Idle sliding granularity (ADR-0043 §6.1): <c>last_seen</c> moves at most once a minute.</summary>
    public const long TouchGranularityMs = 60_000;

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private long _lastCleanupUnixMs = long.MinValue;

    public BrowserSessionSettings Settings => settings;

    public long NowUnixMs() => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>
    /// ADR-0043 §4 validation order: canonical token → row by digest → expiry → candidate + binding → Bearer equivalence → current
    /// claims. Any failure after the row was found deletes the row. Store read failures propagate (never authenticate).
    /// </summary>
    public async Task<BrowserSessionValidation> ValidateAsync(HttpRequest request, CancellationToken ct)
    {
        if (BrowserSessionCookie.CountValues(request, out var value) != 1)
        {
            return default;
        }

        var token = new byte[BrowserSessionCookie.TokenBytes];
        try
        {
            if (!BrowserSessionCookie.TryDecode(value, token))
            {
                return default;
            }

            var digest = BrowserSessionCookie.Digest(token);
            if (await store.FindAsync(digest, ct).ConfigureAwait(false) is not { } record)
            {
                return default;
            }

            var now = NowUnixMs();
            if (IsExpired(record, now))
            {
                await DeleteBestEffortAsync(digest, record.CredentialId, RevocationReason.Expired).ConfigureAwait(false);
                return default;
            }

            var match = authority.FindBySessionBinding(record.CredentialId, token, record.CredentialBinding, out var failure);
            if (match is null)
            {
                await DeleteBestEffortAsync(
                    digest,
                    record.CredentialId,
                    failure == SessionCredentialFailure.CredentialRemoved ? RevocationReason.CredentialRemoved : RevocationReason.CredentialChanged)
                    .ConfigureAwait(false);
                return default;
            }

            return new BrowserSessionValidation(
                ApiCredentialAuthority.CreatePrincipal(match, SchemeName),
                new BrowserSessionFeature(digest, record.LastSeenAtUnixMs));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    /// <summary>ADR-0043 §6.2: expired at <paramref name="nowUnixMs"/> if any limit is reached; equality is expired.</summary>
    public bool IsExpired(BrowserSessionRecord record, long nowUnixMs)
    {
        ArgumentNullException.ThrowIfNull(record);
        return nowUnixMs >= record.AbsoluteExpiresAtUnixMs
            || nowUnixMs >= record.CreatedAtUnixMs + settings.AbsoluteTimeoutMs
            || nowUnixMs >= record.LastSeenAtUnixMs + settings.IdleTimeoutMs;
    }

    /// <summary>
    /// Creates a session for <paramref name="match"/> and, atomically, removes <paramref name="supersededDigest"/>. Returns the encoded
    /// token for the cookie and the cookie's <c>Max-Age</c> (only when <paramref name="keepSignedIn"/>).
    /// </summary>
    public async Task<(string EncodedToken, TimeSpan? MaxAge)> CreateAsync(
        ApiCredentialMatch match, bool keepSignedIn, byte[]? supersededDigest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(match);

        for (var attempt = 0; ; attempt++)
        {
            var token = BrowserSessionCookie.NewToken();
            try
            {
                var now = NowUnixMs();
                var binding = new byte[32];
                ApiCredentialAuthority.ComputeBinding(token, match.Id, match.SecretDigest, binding);
                var absoluteExpiresAt = now + settings.AbsoluteTimeoutMs;
                var record = new BrowserSessionRecord(BrowserSessionCookie.Digest(token), match.Id, binding, now, now, absoluteExpiresAt);
                try
                {
                    await store.CreateAsync(record, supersededDigest, ct).ConfigureAwait(false);
                }
                catch (DuplicateSessionDigestException) when (attempt == 0)
                {
                    continue;
                }

                LogSessionCreated(logger, match.Id, keepSignedIn);
                if (supersededDigest is not null)
                {
                    LogSessionRevoked(logger, RevocationReason.Superseded, match.Id);
                }

                TimeSpan? maxAge = keepSignedIn ? TimeSpan.FromSeconds((absoluteExpiresAt - now) / 1000) : null;
                return (BrowserSessionCookie.Encode(token), maxAge);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }
    }

    /// <summary>Advances idle when the request reached its endpoint and a minute has passed since <c>last_seen</c> (ADR-0043 §6.3).</summary>
    public async Task TouchIfDueAsync(BrowserSessionFeature feature, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(feature);

        var now = NowUnixMs();
        if (now - feature.LastSeenAtUnixMs < TouchGranularityMs)
        {
            return;
        }

        try
        {
            await store.TouchAsync(feature.Digest, now, settings.IdleTimeoutMs, settings.AbsoluteTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStoreFailure(logger, "touch", ex.GetType().Name);
        }
    }

    /// <summary>Logout: deletes the caller's own session (idempotent).</summary>
    public async Task LogoutAsync(BrowserSessionFeature feature, string? credentialId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(feature);
        await store.DeleteAsync(feature.Digest, ct).ConfigureAwait(false);
        LogSessionRevoked(logger, RevocationReason.Logout, credentialId);
    }

    /// <summary>Startup sweep (ADR-0043 §5.6): expired rows, then rows whose credential id is no longer configured. Failures are logged.</summary>
    public async Task StartupCleanupAsync(CancellationToken ct)
    {
        var now = NowUnixMs();
        try
        {
            await store.DeleteExpiredAsync(now, settings.IdleTimeoutMs, settings.AbsoluteTimeoutMs, ct).ConfigureAwait(false);
            await store.DeleteUnknownCredentialsAsync(authority.ConfiguredIds(), ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastCleanupUnixMs, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStoreFailure(logger, "startup cleanup", ex.GetType().Name);
        }
    }

    /// <summary>After a successful login, at most once per five minutes in this process (ADR-0043 §5.6). Failures are logged.</summary>
    public async Task OpportunisticCleanupAsync(CancellationToken ct)
    {
        var now = NowUnixMs();
        var last = Interlocked.Read(ref _lastCleanupUnixMs);
        if (last != long.MinValue && now - last < (long)CleanupInterval.TotalMilliseconds)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupUnixMs, now, last) != last)
        {
            return;
        }

        try
        {
            await store.DeleteExpiredAsync(now, settings.IdleTimeoutMs, settings.AbsoluteTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStoreFailure(logger, "cleanup", ex.GetType().Name);
        }
    }

    public void LoginRejected(string category) => LogLoginRejected(logger, category);

    private async Task DeleteBestEffortAsync(byte[] digest, string credentialId, string reason)
    {
        try
        {
            await store.DeleteAsync(digest, CancellationToken.None).ConfigureAwait(false);
            LogSessionRevoked(logger, reason, credentialId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStoreFailure(logger, "delete", ex.GetType().Name);
        }
    }

    /// <summary>Revocation categories logged (ADR-0043 §13). Never a token, digest, binding or key.</summary>
    private static class RevocationReason
    {
        public const string Logout = "logout";
        public const string Superseded = "superseded";
        public const string Expired = "expired";
        public const string CredentialChanged = "credential_changed";
        public const string CredentialRemoved = "credential_removed";
    }

    [LoggerMessage(EventId = 4301, Level = LogLevel.Information, Message = "Browser session created for credential '{CredentialId}' (persistent: {Persistent}).")]
    private static partial void LogSessionCreated(ILogger logger, string credentialId, bool persistent);

    [LoggerMessage(EventId = 4302, Level = LogLevel.Information, Message = "Browser session revoked ({Reason}) for credential '{CredentialId}'.")]
    private static partial void LogSessionRevoked(ILogger logger, string reason, string? credentialId);

    [LoggerMessage(EventId = 4303, Level = LogLevel.Information, Message = "Browser sign-in rejected ({Category}).")]
    private static partial void LogLoginRejected(ILogger logger, string category);

    [LoggerMessage(EventId = 4304, Level = LogLevel.Warning, Message = "Browser-session store {Operation} failed ({ExceptionType}); the authentication outcome is unchanged.")]
    private static partial void LogStoreFailure(ILogger logger, string operation, string exceptionType);
}
