// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using bOps.Abstractions;
using Microsoft.Extensions.Options;

namespace bOps.Api;

/// <summary>
/// A configured credential an API key or a browser session resolved to (ADR-0043 §2). <see cref="Index"/> is the entry's position in
/// <c>Authentication:ApiKeys</c>, so "the same entry" is never confused with "an entry with the same id". Carries the digest of the
/// resolved secret, never the secret itself.
/// </summary>
internal sealed class ApiCredentialMatch(int index, string id, string? displayName, IReadOnlyList<string> roles, byte[] secretDigest)
{
    public int Index { get; } = index;

    public string Id { get; } = id;

    public string? DisplayName { get; } = displayName;

    public IReadOnlyList<string> Roles { get; } = roles;

    /// <summary><c>SHA-256(UTF-8(resolved secret))</c>.</summary>
    public ReadOnlySpan<byte> SecretDigest => secretDigest;
}

/// <summary>Why a browser session's credential no longer authenticates (ADR-0043 §4, §13 revocation categories).</summary>
internal enum SessionCredentialFailure
{
    None,

    /// <summary>No configured entry has the session's credential id.</summary>
    CredentialRemoved,

    /// <summary>An entry with the id exists, but its current secret does not match the binding, resolves empty, or Bearer resolution of it selects another entry.</summary>
    CredentialChanged,
}

/// <summary>
/// The one ordered credential resolution shared by the Bearer handler, the browser login and the browser-session handler
/// (ADR-0043 §2). Configuration order, entries with a blank <c>Id</c> skipped, the secret resolved through
/// <see cref="ISecretProvider"/>, empty secrets skipped, SHA-256 of both values compared in constant time, first match wins —
/// the algorithm <see cref="ApiKeyAuthenticationHandler"/> always used. Neither ids nor secrets are required to be unique.
/// </summary>
internal sealed class ApiCredentialAuthority(IOptions<ApiAuthenticationOptions> options, ISecretProvider secretProvider)
{
    private static readonly byte[] BindingDomain = Encoding.ASCII.GetBytes("bops-session-binding-v1");

    /// <summary>The first configured entry whose secret equals <paramref name="presented"/> (already trimmed by the caller), or <c>null</c>.</summary>
    public ApiCredentialMatch? FindByPresentedKey(string presented)
    {
        ArgumentNullException.ThrowIfNull(presented);
        var presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return FindFirstBySecretDigest(presentedDigest);
    }

    /// <summary>
    /// ADR-0043 §4 validation-order steps 4–7: the first entry with <paramref name="credentialId"/> whose current secret reproduces
    /// <paramref name="storedBinding"/> under <paramref name="token"/> is the candidate; it is returned only if Bearer resolution of
    /// its secret selects that same entry. Otherwise <c>null</c> with the reason in <paramref name="failure"/>; there is no fallback
    /// to any other entry.
    /// </summary>
    public ApiCredentialMatch? FindBySessionBinding(
        string credentialId, ReadOnlySpan<byte> token, ReadOnlySpan<byte> storedBinding, out SessionCredentialFailure failure)
    {
        ArgumentNullException.ThrowIfNull(credentialId);

        var candidate = FindCandidate(credentialId, token, storedBinding, out var idConfigured);
        if (candidate is null)
        {
            failure = idConfigured ? SessionCredentialFailure.CredentialChanged : SessionCredentialFailure.CredentialRemoved;
            return null;
        }

        // B-1: a valid binding proves only that the candidate's secret is unchanged. The session keeps authority only while the
        // ordered Bearer resolution of that secret selects this very entry (same position), never an earlier one sharing it.
        if (FindFirstBySecretDigest(candidate.SecretDigest) is not { } bearerSelected || bearerSelected.Index != candidate.Index)
        {
            failure = SessionCredentialFailure.CredentialChanged;
            return null;
        }

        failure = SessionCredentialFailure.None;
        return candidate;
    }

    /// <summary>The claims the Bearer handler always issued; only the identity's authentication type names the scheme.</summary>
    public static ClaimsPrincipal CreatePrincipal(ApiCredentialMatch match, string scheme)
    {
        ArgumentNullException.ThrowIfNull(match);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, match.Id),
            new(ClaimTypes.Name, match.DisplayName ?? match.Id),
        };
        claims.AddRange(match.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    /// <summary>
    /// <c>HMAC-SHA256(key = token, "bops-session-binding-v1" ‖ UInt32BE(len(UTF-8(id))) ‖ UTF-8(id) ‖ secretDigest)</c> (ADR-0043 §4).
    /// </summary>
    public static void ComputeBinding(ReadOnlySpan<byte> token, string credentialId, ReadOnlySpan<byte> secretDigest, Span<byte> destination)
    {
        var id = Encoding.UTF8.GetBytes(credentialId);
        var message = new byte[BindingDomain.Length + sizeof(uint) + id.Length + secretDigest.Length];
        var span = message.AsSpan();
        BindingDomain.CopyTo(span);
        span = span[BindingDomain.Length..];
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)id.Length);
        span = span[sizeof(uint)..];
        id.CopyTo(span);
        span = span[id.Length..];
        secretDigest.CopyTo(span);

        HMACSHA256.HashData(token, message, destination);
    }

    /// <summary>The ids of every entry Bearer could ever select (blank ids excluded), for the startup sweep (ADR-0043 §5.6).</summary>
    public IReadOnlyCollection<string> ConfiguredIds() =>
        options.Value.ApiKeys
            .Where(credential => !string.IsNullOrWhiteSpace(credential.Id))
            .Select(credential => credential.Id)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The first entry with <paramref name="credentialId"/> whose current secret reproduces <paramref name="storedBinding"/> under <paramref name="token"/>.</summary>
    private ApiCredentialMatch? FindCandidate(
        string credentialId, ReadOnlySpan<byte> token, ReadOnlySpan<byte> storedBinding, out bool idConfigured)
    {
        idConfigured = false;
        Span<byte> computed = stackalloc byte[HMACSHA256.HashSizeInBytes];
        foreach (var entry in ResolveInOrder())
        {
            if (!string.Equals(entry.Id, credentialId, StringComparison.Ordinal))
            {
                continue;
            }

            idConfigured = true;
            if (entry.Match is not { } match)
            {
                continue;
            }

            ComputeBinding(token, match.Id, match.SecretDigest, computed);
            if (CryptographicOperations.FixedTimeEquals(computed, storedBinding))
            {
                return match;
            }
        }

        return null;
    }

    private ApiCredentialMatch? FindFirstBySecretDigest(ReadOnlySpan<byte> secretDigest)
    {
        foreach (var entry in ResolveInOrder())
        {
            if (entry.Match is { } match && CryptographicOperations.FixedTimeEquals(match.SecretDigest, secretDigest))
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Every entry with a non-blank id, in configuration order, resolving its secret lazily (as the Bearer loop always did, so an entry
    /// after the first match is never resolved). <see cref="ResolvedEntry.Match"/> is <c>null</c> when the secret resolves empty.
    /// </summary>
    private IEnumerable<ResolvedEntry> ResolveInOrder()
    {
        var credentials = options.Value.ApiKeys;
        for (var index = 0; index < credentials.Count; index++)
        {
            var credential = credentials[index];
            if (string.IsNullOrWhiteSpace(credential.Id))
            {
                continue;
            }

            var secret = secretProvider.GetSecret(credential.Secret);
            if (string.IsNullOrEmpty(secret))
            {
                yield return new ResolvedEntry(credential.Id, null);
                continue;
            }

            var roles = credential.Roles
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            yield return new ResolvedEntry(
                credential.Id,
                new ApiCredentialMatch(index, credential.Id, credential.DisplayName, roles, SHA256.HashData(Encoding.UTF8.GetBytes(secret))));
        }
    }

    private readonly record struct ResolvedEntry(string Id, ApiCredentialMatch? Match);
}
