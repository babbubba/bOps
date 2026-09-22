// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;
using Xunit;

namespace bOps.Packages.Storage.Conformance;

public static class StorageConformance
{
    public static void AssertProvider(IToolProvider provider, string platform)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var tools = provider.GetTools().ToArray();
        Assert.Equal(["storage.disks", "storage.health", "storage.io", "storage.mounts", "storage.partitions"], tools.Select(x => x.Manifest.Name).Order().ToArray());
        Assert.All(tools, tool =>
        {
            Assert.Equal(RiskLevel.Read, tool.Manifest.Risk);
            Assert.Null(tool.Manifest.Verification);
            Assert.Equal([platform], tool.Manifest.Platforms);
        });
    }

    public static async Task AssertShapesAsync(IToolProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        foreach (var tool in provider.GetTools())
        {
            var result = await tool.ExecuteAsync(ToolArguments.Empty);
            Assert.True(result.Succeeded, result.ErrorMessage);
            using var document = JsonDocument.Parse(result.Output!);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(document.RootElement.GetProperty("returned").GetInt32() >= 0);
            Assert.Equal(JsonValueKind.Array, ArrayFor(tool.Manifest.Name, document.RootElement).ValueKind);
        }
    }

    private static JsonElement ArrayFor(string name, JsonElement root) => name switch
    {
        "storage.disks" => root.GetProperty("disks"),
        "storage.partitions" => root.GetProperty("partitions"),
        "storage.mounts" => root.GetProperty("mounts"),
        _ => root.GetProperty("devices"),
    };
}
