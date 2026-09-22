// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Core.Tests;

public sealed class TracerouteToolTests
{
    [Fact]
    public async Task ExecuteAsync_ToLoopback_ReachesDestinationOnFirstHop()
    {
        var tool = new TracerouteTool("linux");
        var arguments = ToolArguments.FromJson(new JsonObject { ["host"] = "127.0.0.1", ["maxHops"] = 3, ["timeout"] = 2_000 });

        var result = await tool.ExecuteAsync(arguments);

        Assert.True(result.Succeeded);
        using var document = JsonDocument.Parse(result.Output!);
        Assert.True(document.RootElement.GetProperty("destinationReached").GetBoolean());
        Assert.True(document.RootElement.GetProperty("hopCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task ExecuteAsync_TerminatesAtHopLimit_WhenUnreachable()
    {
        var tool = new TracerouteTool("linux");
        // TEST-NET-1 (RFC 5737): reserved for documentation, guaranteed never to reply.
        var arguments = ToolArguments.FromJson(new JsonObject { ["host"] = "192.0.2.1", ["maxHops"] = 2, ["timeout"] = 300 });

        var result = await tool.ExecuteAsync(arguments);

        Assert.True(result.Succeeded);
        using var document = JsonDocument.Parse(result.Output!);
        Assert.Equal(2, document.RootElement.GetProperty("hopCount").GetInt32());
        Assert.False(document.RootElement.GetProperty("destinationReached").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_UnresolvableHost_ReportsError()
    {
        var tool = new TracerouteTool("linux");
        var arguments = ToolArguments.FromJson(new JsonObject { ["host"] = "this-host-does-not-resolve.invalid" });

        var result = await tool.ExecuteAsync(arguments);

        Assert.True(result.Succeeded);
        using var document = JsonDocument.Parse(result.Output!);
        Assert.NotNull(document.RootElement.GetProperty("errorMessage").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("hopCount").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_HonorsCancellation()
    {
        var tool = new TracerouteTool("linux");
        var arguments = ToolArguments.FromJson(new JsonObject { ["host"] = "192.0.2.1", ["maxHops"] = 30, ["timeout"] = 3_000 });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(arguments, cts.Token));
    }
}
