// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

namespace bOps.Api;

/// <summary>
/// The browser-session token and its cookie (ADR-0043 §3, §7). The token is 32 CSPRNG bytes carried as exactly 43 characters of
/// unpadded base64url; a presented value is used only in that canonical form. Only <c>SHA-256(token)</c> is ever stored.
/// </summary>
internal static class BrowserSessionCookie
{
    public const string Name = "__Host-bops_session";
    public const int TokenBytes = 32;
    public const int EncodedLength = 43;

    /// <summary>A fresh token from <see cref="RandomNumberGenerator"/>; never derived from anything.</summary>
    public static byte[] NewToken() => RandomNumberGenerator.GetBytes(TokenBytes);

    public static string Encode(ReadOnlySpan<byte> token) => Base64Url.EncodeToString(token);

    /// <summary>
    /// Decodes <paramref name="value"/> into <paramref name="token"/> only if it is exactly 43 characters of <c>[A-Za-z0-9_-]</c> that
    /// decode to 32 bytes and re-encode to the identical string.
    /// </summary>
    public static bool TryDecode(string? value, Span<byte> token)
    {
        if (value is null || value.Length != EncodedLength || token.Length != TokenBytes)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                return false;
            }
        }

        // TryDecodeFromChars throws (rather than returning false) for non-zero trailing bits, e.g. a last character of 'B'.
        int written;
        try
        {
            if (!Base64Url.TryDecodeFromChars(value, token, out written))
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        if (written != TokenBytes)
        {
            return false;
        }

        Span<char> reencoded = stackalloc char[EncodedLength];
        return Base64Url.TryEncodeToChars(token, reencoded, out var charsWritten)
            && charsWritten == EncodedLength
            && reencoded.SequenceEqual(value);
    }

    public static byte[] Digest(ReadOnlySpan<byte> token) => SHA256.HashData(token);

    /// <summary>Whether any <c>Cookie</c> header field names the session cookie (the scheme selector, ADR-0043 §8).</summary>
    public static bool IsPresented(HttpRequest request) => CountValues(request, out _) > 0;

    /// <summary>
    /// The number of <c>__Host-bops_session</c> pairs across every raw <c>Cookie</c> header field, and the value when there is exactly
    /// one. Parsed from the raw header (not <see cref="HttpRequest.Cookies"/>, which collapses duplicates) so a tossed second cookie is
    /// detected rather than silently ignored.
    /// </summary>
    public static int CountValues(HttpRequest request, out string? value)
    {
        ArgumentNullException.ThrowIfNull(request);

        value = null;
        var count = 0;
        foreach (var field in request.Headers.Cookie)
        {
            if (string.IsNullOrEmpty(field))
            {
                continue;
            }

            foreach (var pair in field.Split(';'))
            {
                var trimmed = pair.Trim();
                var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
                var name = (equals < 0 ? trimmed : trimmed[..equals]).Trim();
                if (!string.Equals(name, Name, StringComparison.Ordinal))
                {
                    continue;
                }

                count++;
                value = equals < 0 ? string.Empty : trimmed[(equals + 1)..].Trim();
            }
        }

        if (count != 1)
        {
            value = null;
        }

        return count;
    }

    /// <summary>The digest of the presented cookie when exactly one canonical value was presented, else <c>null</c> (login supersede, ADR-0043 §10.1).</summary>
    public static byte[]? PresentedDigest(HttpRequest request)
    {
        if (CountValues(request, out var value) != 1)
        {
            return null;
        }

        Span<byte> token = stackalloc byte[TokenBytes];
        try
        {
            return TryDecode(value, token) ? Digest(token) : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    /// <summary>Sets the session cookie: <c>HttpOnly</c>, <c>Secure</c> (always), <c>SameSite=Strict</c>, <c>Path=/</c>, no <c>Domain</c>; <c>Max-Age</c> only when persistent.</summary>
    public static void Append(HttpResponse response, string encodedToken, TimeSpan? maxAge)
    {
        ArgumentNullException.ThrowIfNull(response);
        var options = BaseOptions();
        options.MaxAge = maxAge;
        response.Cookies.Append(Name, encodedToken, options);
    }

    /// <summary>Expires the cookie with the same path and attributes it was set with (ADR-0043 §7).</summary>
    public static void Delete(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(Name, BaseOptions());
    }

    public static void NoStore(HttpResponse response) => response.Headers[HeaderNames.CacheControl] = "no-store";

    private static CookieOptions BaseOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
        Path = "/",
        Domain = null,
        IsEssential = true,
    };
}
