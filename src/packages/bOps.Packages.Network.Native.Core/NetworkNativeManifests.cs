// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Network.Native.Core;

/// <summary>
/// The manifests for every native network.* tool (V1.3-D, ADR-0035), shared between the Windows
/// and Linux packages (agentic/01-architecture-rules.md, rule A8) — each OS package contributes
/// the same manifest with its own platform id.
/// </summary>
public static class NetworkNativeManifests
{
    /// <summary>The manifest for <c>network.sockets</c> on the given platform.</summary>
    public static ToolManifest Sockets(string platform) => new()
    {
        Name = "network.sockets",
        Description = "Lists TCP and UDP sockets with local/remote endpoints, TCP state, and owning PID/process name where this identity can map one, as single-line JSON rows. "
            + "UDP sockets have no remote endpoint or state. A PID this identity could not map is null and pidMappingComplete is false.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters =
        [
            new ToolParameter("protocol", ToolParameterType.Enum, "Filter by transport protocol. Default all.", Required: false, AllowedValues: ["all", "tcp", "udp"]),
            new ToolParameter("state", ToolParameterType.String, "Filter by exact, case-insensitive TCP state (for example Established, Listen).", Required: false),
            new ToolParameter("pid", ToolParameterType.Integer, "Filter by owning process id.", Required: false),
            new ToolParameter("localPort", ToolParameterType.Integer, "Filter by local port (1-65535).", Required: false),
            new ToolParameter("limit", ToolParameterType.Integer, $"Maximum rows to return (1-{NetworkNativeLimits.MaximumRows}, default {NetworkNativeLimits.DefaultRows}).", Required: false),
        ],
    };

    /// <summary>The manifest for <c>network.routes</c> on the given platform.</summary>
    public static ToolManifest Routes(string platform) => new()
    {
        Name = "network.routes",
        Description = "Reports the full OS routing table as single-line JSON rows: destination, prefix length, gateway, interface, metric, address family and protocol. "
            + "Complements the existing network.route, which only summarizes the default gateway and directly connected subnets.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters =
        [
            new ToolParameter("addressFamily", ToolParameterType.Enum, "Filter by address family. Default all.", Required: false, AllowedValues: ["all", "ipv4", "ipv6"]),
            new ToolParameter("limit", ToolParameterType.Integer, $"Maximum rows to return (1-{NetworkNativeLimits.MaximumRows}, default {NetworkNativeLimits.DefaultRows}).", Required: false),
        ],
    };

    /// <summary>The manifest for <c>network.neighbors</c> on the given platform.</summary>
    public static ToolManifest Neighbors(string platform) => new()
    {
        Name = "network.neighbors",
        Description = "Reports the OS neighbor cache (ARP for IPv4, NDP for IPv6) as single-line JSON rows: IP, MAC, interface, entry state and address family.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters =
        [
            new ToolParameter("addressFamily", ToolParameterType.Enum, "Filter by address family. Default all.", Required: false, AllowedValues: ["all", "ipv4", "ipv6"]),
            new ToolParameter("interfaceName", ToolParameterType.String, "Filter by exact, case-insensitive interface name.", Required: false),
            new ToolParameter("limit", ToolParameterType.Integer, $"Maximum rows to return (1-{NetworkNativeLimits.MaximumRows}, default {NetworkNativeLimits.DefaultRows}).", Required: false),
        ],
    };

    /// <summary>The manifest for <c>network.interface_stats</c> on the given platform.</summary>
    public static ToolManifest InterfaceStats(string platform) => new()
    {
        Name = "network.interface_stats",
        Description = "Samples every network interface twice over a short interval and reports per-second rates for bytes, packets, receive/send errors and drops, plus link speed and operational status, as single-line JSON. "
            + "A counter the platform does not report is null, never zero.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters =
        [
            new ToolParameter("interfaceName", ToolParameterType.String, "Report only this exact, case-insensitive interface name. Omit for every interface.", Required: false),
            new ToolParameter("sampleMilliseconds", ToolParameterType.Integer,
                $"Interval between the two samples ({NetworkNativeLimits.MinimumSampleMilliseconds}-{NetworkNativeLimits.MaximumSampleMilliseconds}, default {NetworkNativeLimits.DefaultSampleMilliseconds}).",
                Required: false),
        ],
    };

    /// <summary>The manifest for <c>network.dns_query</c> on the given platform.</summary>
    public static ToolManifest DnsQuery(string platform) => new()
    {
        Name = "network.dns_query",
        Description = "Resolves a single name with the requested record type (A, AAAA or PTR) and reports answers, TTL where directly reported, the resolver used and elapsed time, as single-line JSON. "
            + "Uses the system resolver by default, or a minimal typed UDP DNS client when a server is given. No other query type, class or option is supported.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters =
        [
            new ToolParameter("name", ToolParameterType.String, "The name to resolve (a hostname for A/AAAA, an IP address for PTR)."),
            new ToolParameter("recordType", ToolParameterType.Enum, "The record type to query.", AllowedValues: ["A", "AAAA", "PTR"]),
            new ToolParameter("server", ToolParameterType.String, "An explicit DNS server to query instead of the system resolver.", Required: false),
            new ToolParameter("timeout", ToolParameterType.Integer,
                $"Query timeout in milliseconds ({NetworkNativeLimits.MinimumDnsTimeoutMilliseconds}-{NetworkNativeLimits.MaximumDnsTimeoutMilliseconds}, default {NetworkNativeLimits.DefaultDnsTimeoutMilliseconds}).",
                Required: false),
        ],
    };

    /// <summary>The manifest for <c>network.traceroute</c> on the given platform.</summary>
    public static ToolManifest Traceroute(string platform) => new()
    {
        Name = "network.traceroute",
        Description = "Traces the path to a host using ICMP echo with increasing TTL/hop-limit (no tracert or traceroute executable), reporting one probe per hop as single-line JSON: "
            + "hop number, responding address, round-trip time and status. Stops at the destination, the hop limit or a run of timeouts.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters =
        [
            new ToolParameter("host", ToolParameterType.String, "The destination hostname or IP address."),
            new ToolParameter("maxHops", ToolParameterType.Integer,
                $"Maximum hops to probe ({NetworkNativeLimits.MinimumMaxHops}-{NetworkNativeLimits.MaximumMaxHops}, default {NetworkNativeLimits.DefaultMaxHops}).", Required: false),
            new ToolParameter("timeout", ToolParameterType.Integer,
                $"Per-hop timeout in milliseconds ({NetworkNativeLimits.MinimumTracerouteTimeoutMilliseconds}-{NetworkNativeLimits.MaximumTracerouteTimeoutMilliseconds}, default {NetworkNativeLimits.DefaultTracerouteTimeoutMilliseconds}).",
                Required: false),
            new ToolParameter("addressFamily", ToolParameterType.Enum, "Which address family to trace. Default auto (resolves to whichever the host answers first).", Required: false, AllowedValues: ["auto", "ipv4", "ipv6"]),
        ],
    };

    /// <summary>The manifest for <c>network.ntp_probe</c> on the given platform.</summary>
    public static ToolManifest NtpProbe(string platform) => new()
    {
        Name = "network.ntp_probe",
        Description = "Sends one SNTP/NTP request over UDP/123 and reports the server and local UTC time, the estimated clock offset and round-trip time, and the server's stratum and version, as single-line JSON. "
            + "A malformed or unsynchronized reply (stratum 0, or the leap indicator signaling an unsynchronized clock) is reported as valid false with a detail, never as a trustworthy offset.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters =
        [
            new ToolParameter("host", ToolParameterType.String, "The NTP server to query."),
            new ToolParameter("timeout", ToolParameterType.Integer,
                $"Query timeout in milliseconds ({NetworkNativeLimits.MinimumNtpTimeoutMilliseconds}-{NetworkNativeLimits.MaximumNtpTimeoutMilliseconds}, default {NetworkNativeLimits.DefaultNtpTimeoutMilliseconds}).",
                Required: false),
        ],
    };
}
