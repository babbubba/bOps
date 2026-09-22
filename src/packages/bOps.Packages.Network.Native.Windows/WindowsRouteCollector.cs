// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.NetworkInformation;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>
/// Reads the full routing table via <c>GetIpForwardTable2</c> (<c>AF_UNSPEC</c> — both families in
/// one call) (V1.3-D, ADR-0035). <c>MIB_IPFORWARD_ROW2</c> is read by fixed byte offset rather than
/// a marshalled struct, because it embeds a <c>SOCKADDR_INET</c> union whose two shapes
/// (<c>sockaddr_in</c>, <c>sockaddr_in6</c>) do not map onto one C# layout.
/// </summary>
internal static class WindowsRouteCollector
{
    private const int AfUnspec = 0;
    private const int RowSize = 104;
    private const int DestinationPrefixOffset = 12;
    private const int NextHopOffset = 44;
    private const int PrefixLengthOffset = 40;
    private const int InterfaceIndexOffset = 8;
    private const int MetricOffset = 84;
    private const int ProtocolOffset = 88;

    private static readonly Dictionary<uint, string> ProtocolNames = new()
    {
        [1] = "other",
        [2] = "local",
        [3] = "netMgmt",
        [4] = "icmp",
        [8] = "rip",
        [13] = "ospf",
        [14] = "bgp",
        [10002] = "autoStatic",
        [10006] = "static",
    };

    internal static unsafe RoutesSnapshot Collect()
    {
        var status = NativeMethods.GetIpForwardTable2(AfUnspec, out var table);
        if (status != NativeMethods.NoError || table == 0)
        {
            return new RoutesSnapshot([]);
        }

        try
        {
            var basePtr = (byte*)table;
            var count = *(uint*)basePtr;
            var rowPtr = basePtr + 8;
            var truncated = count > NetworkNativeLimits.CollectionScanCeiling;
            var effectiveCount = truncated ? (uint)NetworkNativeLimits.CollectionScanCeiling : count;

            var entries = new List<RouteRecord>((int)effectiveCount);
            var interfaceNames = ResolveInterfaceNames();

            for (var i = 0u; i < effectiveCount; i++)
            {
                var row = rowPtr + (i * RowSize);
                var entry = ParseRow(row, interfaceNames);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            return new RoutesSnapshot(entries, truncated);
        }
        finally
        {
            NativeMethods.FreeMibTable(table);
        }
    }

    private static unsafe RouteRecord? ParseRow(byte* row, Dictionary<int, string> interfaceNames)
    {
        var destinationFamily = *(ushort*)(row + DestinationPrefixOffset);
        var (destination, addressFamily) = ReadAddress(row + DestinationPrefixOffset, destinationFamily);
        if (destination is null)
        {
            return null;
        }

        var prefixLength = row[PrefixLengthOffset];
        var (gateway, _) = ReadAddress(row + NextHopOffset, *(ushort*)(row + NextHopOffset));
        var interfaceIndex = *(int*)(row + InterfaceIndexOffset);
        var metric = *(uint*)(row + MetricOffset);
        var protocol = *(uint*)(row + ProtocolOffset);

        var interfaceName = interfaceNames.TryGetValue(interfaceIndex, out var name) ? name : $"if{interfaceIndex}";
        var gatewayText = gateway is null || IPAddress.Any.Equals(gateway) || IPAddress.IPv6Any.Equals(gateway) ? null : gateway.ToString();

        return new RouteRecord(
            destination.ToString(), prefixLength, gatewayText, interfaceName, interfaceIndex, (int)metric, addressFamily,
            ProtocolNames.TryGetValue(protocol, out var protocolName) ? protocolName : protocol.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static unsafe (IPAddress? Address, string Family) ReadAddress(byte* sockaddrInet, ushort family) => family switch
    {
        NativeMethods.AfInet => (new IPAddress(new ReadOnlySpan<byte>(sockaddrInet + 4, 4)), "ipv4"),
        NativeMethods.AfInet6 => (new IPAddress(new ReadOnlySpan<byte>(sockaddrInet + 8, 16)), "ipv6"),
        _ => (null, "ipv4"),
    };

    private static Dictionary<int, string> ResolveInterfaceNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            TryAddIndex(names, TryIndex(() => properties.GetIPv4Properties()?.Index), nic.Name);
            TryAddIndex(names, TryIndex(() => properties.GetIPv6Properties()?.Index), nic.Name);
        }

        return names;
    }

    private static int? TryIndex(Func<int?> read)
    {
        try
        {
            return read();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static void TryAddIndex(Dictionary<int, string> names, int? index, string name)
    {
        if (index is { } value)
        {
            names.TryAdd(value, name);
        }
    }
}
