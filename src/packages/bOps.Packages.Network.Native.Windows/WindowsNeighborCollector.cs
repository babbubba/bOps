// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>
/// Reads the neighbor (ARP/NDP) cache via <c>GetIpNetTable2</c> (<c>AF_UNSPEC</c>) (V1.3-D,
/// ADR-0035). <c>MIB_IPNET_ROW2</c> is read by fixed byte offset for the same reason as
/// <see cref="WindowsRouteCollector"/>: it embeds a <c>SOCKADDR_INET</c> union.
/// </summary>
internal static class WindowsNeighborCollector
{
    private const int AfUnspec = 0;
    private const int RowSize = 104;
    private const int InterfaceIndexOffset = 40;
    private const int PhysicalAddressOffset = 44;
    private const int PhysicalAddressLengthOffset = 76;
    private const int StateOffset = 84;

    private static readonly string[] StateNames = ["unreachable", "incomplete", "probe", "delay", "stale", "reachable", "permanent"];

    internal static unsafe NeighborsSnapshot Collect()
    {
        var status = NativeMethods.GetIpNetTable2(AfUnspec, out var table);
        if (status != NativeMethods.NoError || table == 0)
        {
            return new NeighborsSnapshot([]);
        }

        try
        {
            var basePtr = (byte*)table;
            var count = *(uint*)basePtr;
            var rowPtr = basePtr + 8;
            var truncated = count > NetworkNativeLimits.CollectionScanCeiling;
            var effectiveCount = truncated ? (uint)NetworkNativeLimits.CollectionScanCeiling : count;

            var entries = new List<NeighborRecord>((int)effectiveCount);
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

            return new NeighborsSnapshot(entries, truncated);
        }
        finally
        {
            NativeMethods.FreeMibTable(table);
        }
    }

    private static unsafe NeighborRecord? ParseRow(byte* row, Dictionary<int, string> interfaceNames)
    {
        var family = *(ushort*)row;
        var (address, addressFamily) = family switch
        {
            NativeMethods.AfInet => ((IPAddress?)new IPAddress(new ReadOnlySpan<byte>(row + 4, 4)), "ipv4"),
            NativeMethods.AfInet6 => (new IPAddress(new ReadOnlySpan<byte>(row + 8, 16)), "ipv6"),
            _ => (null, "ipv4"),
        };

        if (address is null)
        {
            return null;
        }

        var interfaceIndex = *(int*)(row + InterfaceIndexOffset);
        var physicalAddressLength = *(uint*)(row + PhysicalAddressLengthOffset);
        var state = *(uint*)(row + StateOffset);

        string? mac = null;
        if (physicalAddressLength is > 0 and <= 32)
        {
            var builder = new StringBuilder((int)physicalAddressLength * 3);
            for (var b = 0; b < physicalAddressLength; b++)
            {
                if (b > 0)
                {
                    builder.Append(':');
                }

                builder.Append(row[PhysicalAddressOffset + b].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            mac = builder.ToString();
        }

        var interfaceName = interfaceNames.TryGetValue(interfaceIndex, out var name) ? name : $"if{interfaceIndex}";
        var stateName = state < StateNames.Length ? StateNames[state] : "unknown";

        return new NeighborRecord(address.ToString(), mac, interfaceName, stateName, addressFamily);
    }

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
