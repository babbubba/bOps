// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.NetworkInformation;

namespace bOps.Packages.Network.Native.Core.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly, never silently, when this process cannot set a per-hop TTL on an ICMP
/// echo (agentic/04-testing-rules.md: skipped tests must say why). On Linux that needs a raw socket, which an unprivileged
/// process without <c>CAP_NET_RAW</c> does not have — the common case on a CI runner. The capability is probed once, at
/// construction, by actually attempting a single loopback ping with a hop limit set.
/// </summary>
internal sealed class RawPingCapableFactAttribute : FactAttribute
{
    public RawPingCapableFactAttribute()
    {
        try
        {
            using var ping = new Ping();
            ping.Send(IPAddress.Loopback, 200, [0], new PingOptions(1, dontFragment: true));
        }
        catch (PlatformNotSupportedException ex)
        {
            Skip = $"This process cannot set a per-hop TTL: {ex.Message}";
        }
    }
}
