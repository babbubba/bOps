// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;
using Xunit;

namespace bOps.Packages.Network.Native.Conformance;

/// <summary>
/// Shared assertions any native network.* implementation must satisfy, regardless of OS
/// (agentic/04-testing-rules.md — "The conformance suite for platform packages"). Asserts
/// structure and invariants, never platform-specific values.
/// </summary>
public static class NetworkNativeConformance
{
    /// <summary>Every native network.* tool is Read-risk with no declared verification (ADR-0035).</summary>
    public static void AssertManifestIsWellFormed(ITool tool, string expectedPlatform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        Assert.Equal(RiskLevel.Read, tool.Manifest.Risk);
        Assert.Null(tool.Manifest.Verification);
        Assert.False(tool.Manifest.RequiresExplicitApproval);
        Assert.Equal([expectedPlatform], tool.Manifest.Platforms);
        Assert.StartsWith("network.", tool.Manifest.Name, StringComparison.Ordinal);
    }

    /// <summary>Asserts a <c>network.sockets</c> result has the documented envelope and row shape.</summary>
    public static async Task AssertSocketsShapeAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var result = await tool.ExecuteAsync(ToolArguments.Empty);
        Assert.True(result.Succeeded);

        using var document = JsonDocument.Parse(result.Output!);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("sockets", out var sockets));
        Assert.Equal(JsonValueKind.Array, sockets.ValueKind);
        Assert.True(root.TryGetProperty("pidMappingComplete", out _));

        foreach (var row in sockets.EnumerateArray())
        {
            var protocol = row.GetProperty("protocol").GetString();
            Assert.True(protocol is "tcp" or "udp");
            var family = row.GetProperty("addressFamily").GetString();
            Assert.True(family is "ipv4" or "ipv6");
            Assert.True(row.GetProperty("localPort").GetInt32() is >= 0 and <= 65_535);

            if (protocol == "udp")
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty("state").ValueKind);
            }
        }
    }

    /// <summary>Asserts a <c>network.routes</c> result has the documented envelope and row shape.</summary>
    public static async Task AssertRoutesShapeAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var result = await tool.ExecuteAsync(ToolArguments.Empty);
        Assert.True(result.Succeeded);

        using var document = JsonDocument.Parse(result.Output!);
        var routes = document.RootElement.GetProperty("routes");
        Assert.Equal(JsonValueKind.Array, routes.ValueKind);

        foreach (var row in routes.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(row.GetProperty("destination").GetString()));
            Assert.True(row.GetProperty("prefixLength").GetInt32() >= 0);
            var family = row.GetProperty("addressFamily").GetString();
            Assert.True(family is "ipv4" or "ipv6");
        }
    }

    /// <summary>Asserts a <c>network.neighbors</c> result has the documented envelope and row shape.</summary>
    public static async Task AssertNeighborsShapeAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var result = await tool.ExecuteAsync(ToolArguments.Empty);
        Assert.True(result.Succeeded);

        using var document = JsonDocument.Parse(result.Output!);
        var neighbors = document.RootElement.GetProperty("neighbors");
        Assert.Equal(JsonValueKind.Array, neighbors.ValueKind);

        foreach (var row in neighbors.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(row.GetProperty("ip").GetString()));
            var family = row.GetProperty("addressFamily").GetString();
            Assert.True(family is "ipv4" or "ipv6");
        }
    }

    /// <summary>Asserts a <c>network.interface_stats</c> result has the documented envelope and row shape.</summary>
    public static async Task AssertInterfaceStatsShapeAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var result = await tool.ExecuteAsync(ToolArguments.Empty);
        Assert.True(result.Succeeded);

        using var document = JsonDocument.Parse(result.Output!);
        var interfaces = document.RootElement.GetProperty("interfaces");
        Assert.Equal(JsonValueKind.Array, interfaces.ValueKind);
        Assert.True(interfaces.GetArrayLength() > 0);

        foreach (var row in interfaces.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(row.GetProperty("interfaceName").GetString()));
            Assert.True(row.TryGetProperty("operationalStatus", out _));
        }
    }
}
