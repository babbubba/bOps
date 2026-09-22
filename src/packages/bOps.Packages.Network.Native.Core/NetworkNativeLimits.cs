// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Network.Native.Core;

/// <summary>Finite limits for every native network.* tool (V1.3-D, ADR-0035).</summary>
public static class NetworkNativeLimits
{
    /// <summary>Default rows returned by <c>network.sockets</c>, <c>network.routes</c> and <c>network.neighbors</c>.</summary>
    public const int DefaultRows = 500;

    /// <summary>Largest number of rows a caller may request from those three tools.</summary>
    public const int MaximumRows = 5_000;

    /// <summary>Most raw records a collector examines for one call, regardless of the requested limit.</summary>
    public const int CollectionScanCeiling = 20_000;

    /// <summary>Default interval between the two <c>network.interface_stats</c> samples, in milliseconds.</summary>
    public const int DefaultSampleMilliseconds = 500;

    /// <summary>Shortest interface-stats sampling interval a caller may request.</summary>
    public const int MinimumSampleMilliseconds = 100;

    /// <summary>Longest interface-stats sampling interval a caller may request.</summary>
    public const int MaximumSampleMilliseconds = 5_000;

    /// <summary>Default <c>network.dns_query</c> timeout, in milliseconds.</summary>
    public const int DefaultDnsTimeoutMilliseconds = 3_000;

    /// <summary>Shortest <c>network.dns_query</c> timeout a caller may request.</summary>
    public const int MinimumDnsTimeoutMilliseconds = 100;

    /// <summary>Longest <c>network.dns_query</c> timeout a caller may request.</summary>
    public const int MaximumDnsTimeoutMilliseconds = 10_000;

    /// <summary>Most answers <c>network.dns_query</c> reports.</summary>
    public const int MaximumDnsAnswers = 100;

    /// <summary>Default number of hops <c>network.traceroute</c> probes.</summary>
    public const int DefaultMaxHops = 30;

    /// <summary>Fewest hops a caller may request.</summary>
    public const int MinimumMaxHops = 1;

    /// <summary>Most hops a caller may request.</summary>
    public const int MaximumMaxHops = 64;

    /// <summary>Default per-hop <c>network.traceroute</c> timeout, in milliseconds.</summary>
    public const int DefaultTracerouteTimeoutMilliseconds = 1_000;

    /// <summary>Shortest per-hop timeout a caller may request.</summary>
    public const int MinimumTracerouteTimeoutMilliseconds = 100;

    /// <summary>Longest per-hop timeout a caller may request.</summary>
    public const int MaximumTracerouteTimeoutMilliseconds = 5_000;

    /// <summary>Default <c>network.ntp_probe</c> timeout, in milliseconds.</summary>
    public const int DefaultNtpTimeoutMilliseconds = 2_000;

    /// <summary>Shortest <c>network.ntp_probe</c> timeout a caller may request.</summary>
    public const int MinimumNtpTimeoutMilliseconds = 100;

    /// <summary>Longest <c>network.ntp_probe</c> timeout a caller may request.</summary>
    public const int MaximumNtpTimeoutMilliseconds = 5_000;

    /// <summary>Longest interface, host or DNS name accepted or returned.</summary>
    public const int NameCharacters = 256;
}
