// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Api;

/// <summary>
/// A browser origin as the comparable tuple <c>(scheme, host, port)</c> (ADR-0043 §9): scheme lower-case, host the IDN host lower-cased
/// (IPv6 literals keep their brackets), port the effective port (an omitted port is the scheme default). The one normalization used
/// for the configured <c>BrowserSession:Origins</c> and for every presented <c>Origin</c> and <c>Referer</c>; compared ordinally —
/// no prefix, suffix, wildcard or DNS matching.
/// </summary>
internal readonly record struct BrowserOrigin(string Scheme, string Host, int Port)
{
    private static readonly System.Buffers.SearchValues<char> ForbiddenInAuthority = System.Buffers.SearchValues.Create("/\\?#@*,");

    public override string ToString() => $"{Scheme}://{Host}:{Port}";

    /// <summary>
    /// Parses a serialized origin, <c>scheme://host[:port]</c> exactly: <c>http</c> or <c>https</c>, no user-info, path (not even
    /// <c>/</c>), query, fragment, whitespace or wildcard.
    /// </summary>
    public static bool TryParseOrigin(string? value, out BrowserOrigin origin)
    {
        origin = default;
        if (string.IsNullOrEmpty(value) || value.Length > 2048)
        {
            return false;
        }

        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var authority = value.AsSpan(separator + 3);
        if (authority.IsEmpty || authority.IndexOfAny(ForbiddenInAuthority) >= 0 || ContainsWhitespaceOrControl(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.AbsolutePath != "/"
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            return false;
        }

        return TryFromUri(uri, out origin);
    }

    /// <summary>The origin of an absolute <c>http</c>/<c>https</c> URL without user-info (path and query ignored), as for <c>Referer</c>.</summary>
    public static bool TryParseUrlOrigin(string? value, out BrowserOrigin origin)
    {
        origin = default;
        if (string.IsNullOrEmpty(value) || value.Length > 8192 || ContainsWhitespaceOrControl(value))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && TryFromUri(uri, out origin);
    }

    private static bool TryFromUri(Uri uri, out BrowserOrigin origin)
    {
        origin = default;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
        {
            return false;
        }

        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }

        origin = new BrowserOrigin(scheme, host.ToLowerInvariant(), uri.Port);
        return true;
    }

    private static bool ContainsWhitespaceOrControl(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }
}
