// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using bOps.Abstractions;

namespace bOps.Packages.Network;

/// <summary>One route: a destination network, and the gateway to reach it (null for a directly connected network).</summary>
public sealed record RouteEntry(string InterfaceName, string Destination, string? Gateway);

/// <summary>
/// Reports each active interface's directly connected subnet and default gateway, cross-platform
/// via <see cref="NetworkInterface"/> — deliberately not the full OS routing table (every
/// destination-specific static route), which on Windows means parsing <c>MIB_IPFORWARD_TABLE2</c>
/// and on Linux <c>/proc/net/route</c>: real complexity for a shape most ops questions ("can this
/// host reach the internet from this interface?") do not need. Read-risk. See HANDOFF.md for the
/// scope note.
/// </summary>
public sealed class NetworkRouteTool : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "network.route",
        Description = "Reports each active network interface's directly connected subnet and default gateway (not the full OS routing table).",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        try
        {
            var entries = CollectRoutes();
            if (entries.Count == 0)
            {
                return Task.FromResult(ToolCallResult.Success("(no routes)"));
            }

            var lines = entries.Select(e => e.Gateway is null
                ? $"{e.InterfaceName}: {e.Destination} (directly connected)"
                : $"{e.InterfaceName}: {e.Destination} via {e.Gateway}");
            return Task.FromResult(ToolCallResult.Success(string.Join('\n', lines)));
        }
        catch (NetworkInformationException ex)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not enumerate routes: {ex.Message}"));
        }
    }

    private static List<RouteEntry> CollectRoutes()
    {
        var entries = new List<RouteEntry>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var properties = nic.GetIPProperties();

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    || unicast.Address.IsIPv6LinkLocal)
                {
                    continue;
                }

                var network = ComputeNetworkAddress(unicast.Address, unicast.PrefixLength);
                if (network is not null)
                {
                    entries.Add(new RouteEntry(nic.Name, $"{network}/{unicast.PrefixLength}", Gateway: null));
                }
            }

            foreach (var gateway in properties.GatewayAddresses)
            {
                if (IPAddress.IsLoopback(gateway.Address)
                    || gateway.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                {
                    continue;
                }

                var defaultDestination = gateway.Address.AddressFamily == AddressFamily.InterNetwork ? "0.0.0.0/0" : "::/0";
                entries.Add(new RouteEntry(nic.Name, defaultDestination, gateway.Address.ToString()));
            }
        }

        return entries;
    }

    private static string? ComputeNetworkAddress(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > bytes.Length * 8)
        {
            return null;
        }

        var maskedBytes = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsForThisByte = Math.Clamp(prefixLength - (i * 8), 0, 8);
            var mask = bitsForThisByte == 0 ? (byte)0 : (byte)(0xFF << (8 - bitsForThisByte));
            maskedBytes[i] = (byte)(bytes[i] & mask);
        }

        return new IPAddress(maskedBytes).ToString();
    }
}
