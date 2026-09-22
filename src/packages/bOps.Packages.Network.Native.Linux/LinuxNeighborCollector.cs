// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>Parses <c>ip -j neighbor show</c> / <c>ip -j -6 neighbor show</c> (V1.3-D, ADR-0035).</summary>
internal static class LinuxNeighborCollector
{
    internal static async Task<NeighborsSnapshot> CollectAsync(CancellationToken ct)
    {
        var entries = new List<NeighborRecord>();
        entries.AddRange(await ParseAsync(["-j", "neighbor", "show"], "ipv4", ct));
        entries.AddRange(await ParseAsync(["-j", "-6", "neighbor", "show"], "ipv6", ct));

        var truncated = entries.Count > NetworkNativeLimits.CollectionScanCeiling;
        return new NeighborsSnapshot(truncated ? entries.Take(NetworkNativeLimits.CollectionScanCeiling).ToList() : entries, truncated);
    }

    private static async Task<List<NeighborRecord>> ParseAsync(string[] arguments, string addressFamily, CancellationToken ct)
    {
        var results = new List<NeighborRecord>();
        var output = await LinuxIpCommand.RunAsync(arguments, ct);
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        using var document = JsonDocument.Parse(output);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var ip = element.TryGetProperty("dst", out var dstElement) ? dstElement.GetString() : null;
            if (ip is null)
            {
                continue;
            }

            var interfaceName = element.TryGetProperty("dev", out var devElement) ? devElement.GetString() ?? "unknown" : "unknown";
            var mac = element.TryGetProperty("lladdr", out var macElement) ? macElement.GetString() : null;
            var state = element.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.Array
                ? string.Join(',', stateElement.EnumerateArray().Select(item => item.GetString()?.ToLowerInvariant()))
                : "unknown";

            results.Add(new NeighborRecord(ip, mac, interfaceName, state, addressFamily));
        }

        return results;
    }
}
