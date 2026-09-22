// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.NetworkInformation;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>
/// Samples interface counters via <see cref="NetworkInterface.GetIPStatistics"/> — the "equivalent
/// BCL counters" the task names as an alternative to <c>GetIfEntry2</c> (V1.3-D, ADR-0035).
/// </summary>
internal static class WindowsInterfaceStatsCollector
{
    internal static IReadOnlyList<InterfaceCounterSample> Sample(string? interfaceName)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        if (interfaceName is not null)
        {
            interfaces = interfaces.Where(nic => string.Equals(nic.Name, interfaceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        var results = new List<InterfaceCounterSample>(interfaces.Length);
        foreach (var nic in interfaces)
        {
            IPInterfaceStatistics stats;
            try
            {
                stats = nic.GetIPStatistics();
            }
            catch (NetworkInformationException)
            {
                results.Add(new InterfaceCounterSample(nic.Name, null, null, null, null, null, null, null, null, null, nic.OperationalStatus.ToString()));
                continue;
            }

            double? speedMbps = null;
            try
            {
                speedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000.0 : null;
            }
            catch (NetworkInformationException)
            {
            }

            results.Add(new InterfaceCounterSample(
                nic.Name,
                stats.BytesReceived,
                stats.BytesSent,
                stats.UnicastPacketsReceived + stats.NonUnicastPacketsReceived,
                stats.UnicastPacketsSent + stats.NonUnicastPacketsSent,
                stats.IncomingPacketsWithErrors,
                stats.OutgoingPacketsWithErrors,
                stats.IncomingPacketsDiscarded,
                stats.OutgoingPacketsDiscarded,
                speedMbps,
                nic.OperationalStatus.ToString()));
        }

        return results;
    }
}
