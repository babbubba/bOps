// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;

namespace bOps.Packages.Web;

/// <summary>
/// Resolves a hostname to the addresses <see cref="SsrfSafeConnectCallback"/> validates and
/// connects to. Exists as a seam so tests can make "this hostname resolves to that
/// attacker-chosen address" true without controlling real DNS (ADR-0028) — production always
/// uses <see cref="SystemDnsResolver"/>.
/// </summary>
public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>The production <see cref="IDnsResolver"/>: an ordinary system DNS lookup.</summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        return IPAddress.TryParse(host, out var literal)
            ? Task.FromResult(new[] { literal })
            : Dns.GetHostAddressesAsync(host, ct);
    }
}
