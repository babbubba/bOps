// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.NetworkInformation;
using System.Text.Json;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>Parses <c>ip -j route show</c> / <c>ip -j -6 route show</c> (V1.3-D, ADR-0035).</summary>
internal static class LinuxRouteCollector
{
    internal static async Task<RoutesSnapshot> CollectAsync(CancellationToken ct)
    {
        var entries = new List<RouteRecord>();
        var interfaceIndexes = ResolveInterfaceIndexes();

        entries.AddRange(await ParseAsync(["-j", "route", "show"], "ipv4", interfaceIndexes, ct));
        entries.AddRange(await ParseAsync(["-j", "-6", "route", "show"], "ipv6", interfaceIndexes, ct));

        var truncated = entries.Count > NetworkNativeLimits.CollectionScanCeiling;
        return new RoutesSnapshot(truncated ? entries.Take(NetworkNativeLimits.CollectionScanCeiling).ToList() : entries, truncated);
    }

    private static async Task<List<RouteRecord>> ParseAsync(
        string[] arguments, string addressFamily, Dictionary<string, int> interfaceIndexes, CancellationToken ct)
    {
        var results = new List<RouteRecord>();
        var output = await LinuxIpCommand.RunAsync(arguments, ct);
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        using var document = JsonDocument.Parse(output);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var (destination, prefixLength) = ParseDestination(element, addressFamily);
            var interfaceName = element.TryGetProperty("dev", out var devElement) ? devElement.GetString() ?? "unknown" : "unknown";
            var gateway = element.TryGetProperty("gateway", out var gatewayElement) ? gatewayElement.GetString() : null;
            var protocol = element.TryGetProperty("protocol", out var protocolElement) ? protocolElement.GetString() : null;
            var metric = element.TryGetProperty("metric", out var metricElement) && metricElement.TryGetInt32(out var metricValue) ? metricValue : (int?)null;
            var interfaceIndex = interfaceIndexes.TryGetValue(interfaceName, out var index) ? index : 0;

            results.Add(new RouteRecord(destination, prefixLength, gateway, interfaceName, interfaceIndex, metric, addressFamily, protocol));
        }

        return results;
    }

    private static (string Destination, int PrefixLength) ParseDestination(JsonElement element, string addressFamily)
    {
        var dst = element.TryGetProperty("dst", out var dstElement) ? dstElement.GetString() : null;
        if (string.IsNullOrEmpty(dst) || dst == "default")
        {
            return (addressFamily == "ipv6" ? "::" : "0.0.0.0", 0);
        }

        var parts = dst.Split('/', 2);
        var prefixLength = parts.Length == 2 && int.TryParse(parts[1], out var parsed)
            ? parsed
            : addressFamily == "ipv6" ? 128 : 32;

        return (parts[0], prefixLength);
    }

    private static Dictionary<string, int> ResolveInterfaceIndexes()
    {
        var indexes = new Dictionary<string, int>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                indexes[nic.Name] = nic.GetIPProperties().GetIPv4Properties()?.Index
                    ?? nic.GetIPProperties().GetIPv6Properties()?.Index
                    ?? 0;
            }
            catch (NetworkInformationException)
            {
                indexes[nic.Name] = 0;
            }
        }

        return indexes;
    }
}
