// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Core.Tests;

public sealed class DnsQueryToolTests
{
    [Fact]
    public async Task ExecuteAsync_ResolvesLocalhostViaSystemResolver()
    {
        var tool = new DnsQueryTool("linux");
        var arguments = ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["name"] = "localhost", ["recordType"] = "A" });

        var result = await tool.ExecuteAsync(arguments);

        Assert.True(result.Succeeded);
        using var document = JsonDocument.Parse(result.Output!);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.True(document.RootElement.GetProperty("answers").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ExecuteAsync_MissingName_ReturnsFailure()
    {
        var tool = new DnsQueryTool("linux");
        var arguments = ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["recordType"] = "A" });

        var result = await tool.ExecuteAsync(arguments);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_HonorsCancellation()
    {
        var tool = new DnsQueryTool("linux");
        var arguments = ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["name"] = "localhost", ["recordType"] = "A" });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(arguments, cts.Token));
    }

    [Fact]
    public void Manifest_IsReadRiskWithNoVerification()
    {
        var tool = new DnsQueryTool("linux");

        Assert.Equal(RiskLevel.Read, tool.Manifest.Risk);
        Assert.Null(tool.Manifest.Verification);
        Assert.Equal("network.dns_query", tool.Manifest.Name);
    }
}
