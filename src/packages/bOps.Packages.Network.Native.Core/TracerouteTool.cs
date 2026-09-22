// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using bOps.Abstractions;

namespace bOps.Packages.Network.Native.Core;

/// <summary>
/// <c>network.traceroute</c> (V1.3-D, ADR-0035): one ICMP echo probe per hop, with an increasing
/// TTL/hop-limit via <see cref="Ping"/> — never a <c>tracert</c>/<c>traceroute</c> executable
/// (rule S1). Needs no OS-specific collection: <see cref="Ping"/> is cross-platform BCL. Both
/// native packages contribute the same instance under their own platform id.
/// </summary>
public sealed class TracerouteTool(string platform) : ITool
{
    private const int PayloadBytes = 32;

    /// <inheritdoc />
    public ToolManifest Manifest { get; } = NetworkNativeManifests.Traceroute(platform);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!NetworkNativeArguments.TryReadTraceroute(arguments, out var host, out var maxHops, out var timeoutMilliseconds, out var addressFamily, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();

        IPAddress? destination;
        try
        {
            destination = await ResolveAsync(host, addressFamily, ct);
        }
        catch (SocketException ex)
        {
            return ToolCallResult.Success(NetworkNativeFormatting.Format(
                new TracerouteResult(host, null, addressFamily, maxHops, timeoutMilliseconds, DestinationReached: false, [], $"Could not resolve {host}: {ex.Message}")));
        }

        if (destination is null)
        {
            return ToolCallResult.Success(NetworkNativeFormatting.Format(
                new TracerouteResult(host, null, addressFamily, maxHops, timeoutMilliseconds, DestinationReached: false, [], $"{host} has no address for the requested address family.")));
        }

        var resolvedFamily = destination.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
        var hops = new List<TracerouteHop>();
        var buffer = new byte[PayloadBytes];
        var reached = false;

        using var ping = new Ping();
        for (var ttl = 1; ttl <= maxHops && !reached; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            var options = new PingOptions(ttl, dontFragment: true);

            PingReply reply;
            try
            {
                reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeoutMilliseconds), buffer, options, ct);
            }
            catch (PingException ex)
            {
                hops.Add(new TracerouteHop(ttl, null, null, "error"));
                if (ex.InnerException is OperationCanceledException)
                {
                    throw;
                }

                continue;
            }
            catch (PlatformNotSupportedException ex)
            {
                // Setting a hop limit needs a raw socket on Linux (CAP_NET_RAW), which an unprivileged process may not have.
                // That is an environment limitation, not a hop-by-hop failure, so the tool reports it once and stops.
                return ToolCallResult.Failure(
                    $"This host cannot set a per-hop TTL without elevated privilege: {ex.Message}");
            }

            hops.Add(reply.Status switch
            {
                IPStatus.Success => new TracerouteHop(ttl, reply.Address.ToString(), reply.RoundtripTime, "reached"),
                IPStatus.TtlExpired => new TracerouteHop(ttl, reply.Address?.ToString(), reply.RoundtripTime, "ttlExpired"),
                IPStatus.TimedOut => new TracerouteHop(ttl, null, null, "timeout"),
                _ => new TracerouteHop(ttl, reply.Address?.ToString(), null, "error"),
            });

            reached = reply.Status == IPStatus.Success;
        }

        return ToolCallResult.Success(NetworkNativeFormatting.Format(
            new TracerouteResult(host, destination.ToString(), resolvedFamily, maxHops, timeoutMilliseconds, reached, hops, ErrorMessage: null)));
    }

    private static async Task<IPAddress?> ResolveAsync(string host, string addressFamily, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return addressFamily switch
            {
                "ipv4" => literal.AddressFamily == AddressFamily.InterNetwork ? literal : null,
                "ipv6" => literal.AddressFamily == AddressFamily.InterNetworkV6 ? literal : null,
                _ => literal,
            };
        }

        var candidates = await Dns.GetHostAddressesAsync(host, ct);
        return addressFamily switch
        {
            "ipv4" => candidates.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork),
            "ipv6" => candidates.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6),
            _ => candidates.FirstOrDefault(),
        };
    }
}
