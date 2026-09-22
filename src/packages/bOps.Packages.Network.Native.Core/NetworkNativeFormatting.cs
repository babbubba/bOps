// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace bOps.Packages.Network.Native.Core;

/// <summary>
/// Creates the bounded, deterministic JSON output of every native network.* tool (V1.3-D,
/// ADR-0035). It lives here, not in either OS package, because the LLM reads this output and a
/// difference in shape between two operating systems is a difference in behaviour
/// (agentic/01-architecture-rules.md, rule A8).
/// </summary>
public static class NetworkNativeFormatting
{
    /// <summary>Filters, limits and formats a <see cref="SocketsSnapshot"/> as single-line JSON.</summary>
    public static string FormatSockets(SocketsSnapshot snapshot, string protocol, string? state, int? pid, int? localPort, int limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var filtered = snapshot.Entries.Where(entry =>
            (protocol == "all" || string.Equals(entry.Protocol, protocol, StringComparison.OrdinalIgnoreCase))
            && (state is null || string.Equals(entry.State, state, StringComparison.OrdinalIgnoreCase))
            && (pid is null || entry.Pid == pid)
            && (localPort is null || entry.LocalPort == localPort))
            .ToArray();

        var selected = filtered.Take(limit).ToArray();

        var rows = new JsonArray(selected.Select(entry => (JsonNode)new JsonObject
        {
            ["protocol"] = entry.Protocol,
            ["addressFamily"] = entry.AddressFamily,
            ["localAddress"] = entry.LocalAddress,
            ["localPort"] = entry.LocalPort,
            ["remoteAddress"] = entry.RemoteAddress,
            ["remotePort"] = entry.RemotePort,
            ["state"] = entry.State,
            ["pid"] = entry.Pid,
            ["processName"] = Bounded(entry.ProcessName, NetworkNativeLimits.NameCharacters),
        }).ToArray());

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["matchedSockets"] = filtered.Length,
            ["returnedSockets"] = selected.Length,
            ["truncated"] = snapshot.CollectionTruncated || selected.Length < filtered.Length,
            ["pidMappingComplete"] = snapshot.PidMappingComplete,
            ["pidMappingDetail"] = Bounded(snapshot.PidMappingDetail, 256),
            ["sockets"] = rows,
        };
        return root.ToJsonString();
    }

    /// <summary>Filters, limits and formats a <see cref="RoutesSnapshot"/> as single-line JSON.</summary>
    public static string FormatRoutes(RoutesSnapshot snapshot, string addressFamily, int limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var filtered = snapshot.Entries
            .Where(entry => addressFamily == "all" || string.Equals(entry.AddressFamily, addressFamily, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var selected = filtered.Take(limit).ToArray();

        var rows = new JsonArray(selected.Select(entry => (JsonNode)new JsonObject
        {
            ["destination"] = entry.Destination,
            ["prefixLength"] = entry.PrefixLength,
            ["gateway"] = entry.Gateway,
            ["interfaceName"] = Bounded(entry.InterfaceName, NetworkNativeLimits.NameCharacters),
            ["interfaceIndex"] = entry.InterfaceIndex,
            ["metric"] = entry.Metric,
            ["addressFamily"] = entry.AddressFamily,
            ["protocol"] = entry.Protocol,
        }).ToArray());

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["matchedRoutes"] = filtered.Length,
            ["returnedRoutes"] = selected.Length,
            ["truncated"] = snapshot.CollectionTruncated || selected.Length < filtered.Length,
            ["routes"] = rows,
        };
        return root.ToJsonString();
    }

    /// <summary>Filters, limits and formats a <see cref="NeighborsSnapshot"/> as single-line JSON.</summary>
    public static string FormatNeighbors(NeighborsSnapshot snapshot, string addressFamily, string? interfaceName, int limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var filtered = snapshot.Entries.Where(entry =>
            (addressFamily == "all" || string.Equals(entry.AddressFamily, addressFamily, StringComparison.OrdinalIgnoreCase))
            && (interfaceName is null || string.Equals(entry.InterfaceName, interfaceName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var selected = filtered.Take(limit).ToArray();

        var rows = new JsonArray(selected.Select(entry => (JsonNode)new JsonObject
        {
            ["ip"] = entry.Ip,
            ["mac"] = entry.Mac,
            ["interfaceName"] = Bounded(entry.InterfaceName, NetworkNativeLimits.NameCharacters),
            ["state"] = entry.State,
            ["addressFamily"] = entry.AddressFamily,
        }).ToArray());

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["matchedNeighbors"] = filtered.Length,
            ["returnedNeighbors"] = selected.Length,
            ["truncated"] = snapshot.CollectionTruncated || selected.Length < filtered.Length,
            ["neighbors"] = rows,
        };
        return root.ToJsonString();
    }

    /// <summary>Formats the per-interface rates of <c>network.interface_stats</c> as single-line JSON.</summary>
    public static string FormatInterfaceStats(IReadOnlyList<InterfaceStatsResult> results, int sampleMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(results);

        var rows = new JsonArray(results.Select(result => (JsonNode)new JsonObject
        {
            ["interfaceName"] = Bounded(result.InterfaceName, NetworkNativeLimits.NameCharacters),
            ["bytesReceivedPerSec"] = Rounded(result.BytesReceivedPerSec),
            ["bytesSentPerSec"] = Rounded(result.BytesSentPerSec),
            ["packetsReceivedPerSec"] = Rounded(result.PacketsReceivedPerSec),
            ["packetsSentPerSec"] = Rounded(result.PacketsSentPerSec),
            ["receiveErrorsPerSec"] = Rounded(result.ReceiveErrorsPerSec),
            ["sendErrorsPerSec"] = Rounded(result.SendErrorsPerSec),
            ["receiveDropsPerSec"] = Rounded(result.ReceiveDropsPerSec),
            ["sendDropsPerSec"] = Rounded(result.SendDropsPerSec),
            ["speedMbps"] = Rounded(result.SpeedMbps),
            ["operationalStatus"] = result.OperationalStatus,
        }).ToArray());

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["sampleMilliseconds"] = sampleMilliseconds,
            ["interfaces"] = rows,
        };
        return root.ToJsonString();
    }

    /// <summary>Formats a <see cref="DnsQueryResult"/> as single-line JSON.</summary>
    public static string Format(DnsQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var answers = result.Answers.Take(NetworkNativeLimits.MaximumDnsAnswers).Select(answer => (JsonNode)new JsonObject
        {
            ["name"] = Bounded(answer.Name, NetworkNativeLimits.NameCharacters),
            ["recordType"] = answer.RecordType,
            ["data"] = Bounded(answer.Data, NetworkNativeLimits.NameCharacters),
            ["ttlSeconds"] = answer.TtlSeconds,
        }).ToArray();

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["name"] = Bounded(result.Name, NetworkNativeLimits.NameCharacters),
            ["recordType"] = result.RecordType,
            ["resolver"] = Bounded(result.Resolver, NetworkNativeLimits.NameCharacters),
            ["success"] = result.Success,
            ["errorMessage"] = Bounded(result.ErrorMessage, 512),
            ["elapsedMilliseconds"] = result.ElapsedMilliseconds,
            ["answerCount"] = result.Answers.Count,
            ["answers"] = new JsonArray(answers),
        };
        return root.ToJsonString();
    }

    /// <summary>Formats a <see cref="TracerouteResult"/> as single-line JSON.</summary>
    public static string Format(TracerouteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var hops = new JsonArray(result.Hops.Select(hop => (JsonNode)new JsonObject
        {
            ["hop"] = hop.Hop,
            ["address"] = hop.Address,
            ["roundTripMilliseconds"] = Rounded(hop.RoundTripMilliseconds),
            ["status"] = hop.Status,
        }).ToArray());

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["host"] = Bounded(result.Host, NetworkNativeLimits.NameCharacters),
            ["resolvedAddress"] = result.ResolvedAddress,
            ["addressFamily"] = result.AddressFamily,
            ["maxHops"] = result.MaxHops,
            ["timeoutMilliseconds"] = result.TimeoutMilliseconds,
            ["destinationReached"] = result.DestinationReached,
            ["errorMessage"] = Bounded(result.ErrorMessage, 512),
            ["hopCount"] = result.Hops.Count,
            ["hops"] = hops,
        };
        return root.ToJsonString();
    }

    /// <summary>Formats an <see cref="NtpProbeResult"/> as single-line JSON.</summary>
    public static string Format(NtpProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["server"] = Bounded(result.Server, NetworkNativeLimits.NameCharacters),
            ["localUtc"] = result.LocalUtc.ToString("O"),
            ["serverUtc"] = result.ServerUtc?.ToString("O"),
            ["offsetMilliseconds"] = Rounded(result.OffsetMilliseconds),
            ["roundTripMilliseconds"] = Rounded(result.RoundTripMilliseconds),
            ["stratum"] = result.Stratum,
            ["version"] = result.Version,
            ["valid"] = result.Valid,
            ["errorMessage"] = Bounded(result.ErrorMessage, 512),
        };
        return root.ToJsonString();
    }

    private static double? Rounded(double? value) => value is { } number ? Math.Round(number, 2, MidpointRounding.AwayFromZero) : null;

    private static string? Bounded(string? value, int maximum)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum), "…");
    }
}
