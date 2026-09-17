// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace bOps.Packages.Web.Tests;

/// <summary>
/// Makes "this hostname resolves to that address" true without controlling real DNS (ADR-0028's
/// testing seam) — the attack this package defends against is exactly a hostname resolving to an
/// address the operator did not expect.
/// </summary>
internal sealed class FakeDnsResolver : IDnsResolver
{
    private readonly Dictionary<string, IPAddress[]> _map = new(StringComparer.OrdinalIgnoreCase);

    public FakeDnsResolver Map(string host, params IPAddress[] addresses)
    {
        _map[host] = addresses;
        return this;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        _map.TryGetValue(host, out var addresses)
            ? Task.FromResult(addresses)
            : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
}
