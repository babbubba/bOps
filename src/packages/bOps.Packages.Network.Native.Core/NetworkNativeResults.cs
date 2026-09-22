// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Network.Native.Core;

/// <summary>One socket observed by an OS collector, before argument filtering.</summary>
/// <param name="Protocol"><c>"tcp"</c> or <c>"udp"</c>.</param>
/// <param name="AddressFamily"><c>"ipv4"</c> or <c>"ipv6"</c>.</param>
/// <param name="LocalAddress">The local address, in its canonical string form.</param>
/// <param name="LocalPort">The local port.</param>
/// <param name="RemoteAddress">The remote address, or <c>null</c> for UDP and for a TCP socket not yet connected.</param>
/// <param name="RemotePort">The remote port, or <c>null</c> where <paramref name="RemoteAddress"/> is <c>null</c>.</param>
/// <param name="State">The TCP connection state, or <c>null</c> for UDP, where a state is not meaningful.</param>
/// <param name="Pid">The owning process id, or <c>null</c> when this identity could not map the socket to one.</param>
/// <param name="ProcessName">The owning process name, or <c>null</c> under the same condition as <paramref name="Pid"/>.</param>
public sealed record SocketEntry(
    string Protocol,
    string AddressFamily,
    string LocalAddress,
    int LocalPort,
    string? RemoteAddress,
    int? RemotePort,
    string? State,
    int? Pid,
    string? ProcessName);

/// <summary>
/// Every socket an OS collector could observe for one call. PID mapping is reported as an explicit
/// completeness fact rather than silently leaving <see cref="SocketEntry.Pid"/> null: a host without
/// permission to map every socket to a process must say so.
/// </summary>
/// <param name="Entries">The sockets observed, unfiltered by the caller's arguments.</param>
/// <param name="PidMappingComplete">False when this identity could not map every socket to a process.</param>
/// <param name="PidMappingDetail">Why the mapping was not complete, when it was not.</param>
/// <param name="CollectionTruncated">True when the collector stopped at its scan ceiling.</param>
public sealed record SocketsSnapshot(
    IReadOnlyList<SocketEntry> Entries,
    bool PidMappingComplete = true,
    string? PidMappingDetail = null,
    bool CollectionTruncated = false);

/// <summary>One route table entry.</summary>
/// <param name="Destination">The destination network address.</param>
/// <param name="PrefixLength">The destination prefix length.</param>
/// <param name="Gateway">The next hop, or <c>null</c> for a directly connected network.</param>
/// <param name="InterfaceName">The outgoing interface's name.</param>
/// <param name="InterfaceIndex">The outgoing interface's index.</param>
/// <param name="Metric">The route metric, or <c>null</c> when the platform does not report one.</param>
/// <param name="AddressFamily"><c>"ipv4"</c> or <c>"ipv6"</c>.</param>
/// <param name="Protocol">How the route was learned (for example <c>"kernel"</c>, <c>"static"</c>), or <c>null</c>.</param>
public sealed record RouteRecord(
    string Destination,
    int PrefixLength,
    string? Gateway,
    string InterfaceName,
    int InterfaceIndex,
    int? Metric,
    string AddressFamily,
    string? Protocol);

/// <summary>Every route an OS collector could observe for one call.</summary>
public sealed record RoutesSnapshot(IReadOnlyList<RouteRecord> Entries, bool CollectionTruncated = false);

/// <summary>One neighbor (ARP/NDP cache) entry.</summary>
/// <param name="Ip">The neighbor's IP address.</param>
/// <param name="Mac">The neighbor's link-layer address, or <c>null</c> when unresolved.</param>
/// <param name="InterfaceName">The interface the entry was learned on.</param>
/// <param name="State">The cache entry state (for example <c>"reachable"</c>, <c>"stale"</c>, <c>"permanent"</c>).</param>
/// <param name="AddressFamily"><c>"ipv4"</c> or <c>"ipv6"</c>.</param>
public sealed record NeighborRecord(string Ip, string? Mac, string InterfaceName, string State, string AddressFamily);

/// <summary>Every neighbor entry an OS collector could observe for one call.</summary>
public sealed record NeighborsSnapshot(IReadOnlyList<NeighborRecord> Entries, bool CollectionTruncated = false);

/// <summary>
/// One instantaneous reading of an interface's cumulative counters. <c>network.interface_stats</c>
/// takes two of these per interface and turns the difference into rates, exactly as
/// <c>process.metrics</c> does for a process (ADR-0034 precedent). A counter the platform does not
/// report is <c>null</c>, never zero.
/// </summary>
public sealed record InterfaceCounterSample(
    string InterfaceName,
    long? BytesReceived,
    long? BytesSent,
    long? PacketsReceived,
    long? PacketsSent,
    long? ReceiveErrors,
    long? SendErrors,
    long? ReceiveDrops,
    long? SendDrops,
    double? SpeedMbps,
    string OperationalStatus);

/// <summary>The rates <c>network.interface_stats</c> reports for one interface.</summary>
public sealed record InterfaceStatsResult(
    string InterfaceName,
    double? BytesReceivedPerSec,
    double? BytesSentPerSec,
    double? PacketsReceivedPerSec,
    double? PacketsSentPerSec,
    double? ReceiveErrorsPerSec,
    double? SendErrorsPerSec,
    double? ReceiveDropsPerSec,
    double? SendDropsPerSec,
    double? SpeedMbps,
    string OperationalStatus);

/// <summary>One answer record of a <c>network.dns_query</c> result.</summary>
public sealed record DnsAnswer(string Name, string RecordType, string Data, int? TtlSeconds);

/// <summary>The result of one <c>network.dns_query</c> call.</summary>
public sealed record DnsQueryResult(
    string Name,
    string RecordType,
    string Resolver,
    IReadOnlyList<DnsAnswer> Answers,
    long ElapsedMilliseconds,
    bool Success,
    string? ErrorMessage);

/// <summary>One probed hop of a <c>network.traceroute</c> run.</summary>
/// <param name="Hop">The 1-based hop (TTL) number.</param>
/// <param name="Address">The responding address, or <c>null</c> when the probe timed out or errored.</param>
/// <param name="RoundTripMilliseconds">The measured round-trip time, or <c>null</c> when there was no reply.</param>
/// <param name="Status"><c>"ttlExpired"</c>, <c>"reached"</c>, <c>"timeout"</c> or <c>"error"</c>.</param>
public sealed record TracerouteHop(int Hop, string? Address, double? RoundTripMilliseconds, string Status);

/// <summary>The result of one <c>network.traceroute</c> run.</summary>
public sealed record TracerouteResult(
    string Host,
    string? ResolvedAddress,
    string AddressFamily,
    int MaxHops,
    int TimeoutMilliseconds,
    bool DestinationReached,
    IReadOnlyList<TracerouteHop> Hops,
    string? ErrorMessage);

/// <summary>The result of one <c>network.ntp_probe</c> call.</summary>
public sealed record NtpProbeResult(
    string Server,
    DateTimeOffset LocalUtc,
    DateTimeOffset? ServerUtc,
    double? OffsetMilliseconds,
    double? RoundTripMilliseconds,
    int? Stratum,
    int? Version,
    bool Valid,
    string? ErrorMessage);
