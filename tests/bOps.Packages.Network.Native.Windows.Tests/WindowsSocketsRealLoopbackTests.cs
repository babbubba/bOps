// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Network.Native.Windows.Tests;

/// <summary>
/// Real loopback sockets proving <c>network.sockets</c> maps a live TCP/UDP socket to this
/// process's PID and port (agentic/04-testing-rules.md — "never mock the operating system").
/// </summary>
public sealed class WindowsSocketsRealLoopbackTests
{
    [WindowsOnlyFact]
    public async Task ExecuteAsync_FindsOwnListeningTcpSocketByPortAndPid()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var tool = new WindowsSocketsTool();
        var arguments = ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject
        {
            ["protocol"] = "tcp",
            ["localPort"] = port,
        });

        var result = await tool.ExecuteAsync(arguments);
        Assert.True(result.Succeeded);

        using var document = JsonDocument.Parse(result.Output!);
        var sockets = document.RootElement.GetProperty("sockets").EnumerateArray().ToArray();
        var row = Assert.Single(sockets);

        Assert.Equal("tcp", row.GetProperty("protocol").GetString());
        Assert.Equal(port, row.GetProperty("localPort").GetInt32());
        Assert.Equal("listen", row.GetProperty("state").GetString());
        Assert.Equal(Environment.ProcessId, row.GetProperty("pid").GetInt32());
    }

    [WindowsOnlyFact]
    public async Task ExecuteAsync_FindsOwnUdpSocket_WithNoRemoteEndpointOrState()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        var tool = new WindowsSocketsTool();
        var arguments = ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject
        {
            ["protocol"] = "udp",
            ["localPort"] = port,
        });

        var result = await tool.ExecuteAsync(arguments);
        Assert.True(result.Succeeded);

        using var document = JsonDocument.Parse(result.Output!);
        var sockets = document.RootElement.GetProperty("sockets").EnumerateArray().ToArray();
        var row = Assert.Single(sockets);

        Assert.Equal("udp", row.GetProperty("protocol").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("remoteAddress").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("state").ValueKind);
        Assert.Equal(Environment.ProcessId, row.GetProperty("pid").GetInt32());
    }

    [WindowsOnlyFact]
    public async Task ExecuteAsync_PidFilter_ReturnsOnlyOwnSockets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var tool = new WindowsSocketsTool();
        var arguments = ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["pid"] = Environment.ProcessId });

        var result = await tool.ExecuteAsync(arguments);
        Assert.True(result.Succeeded);

        using var document = JsonDocument.Parse(result.Output!);
        foreach (var row in document.RootElement.GetProperty("sockets").EnumerateArray())
        {
            Assert.Equal(Environment.ProcessId, row.GetProperty("pid").GetInt32());
        }

        Assert.True(document.RootElement.GetProperty("matchedSockets").GetInt32() >= 1);
    }

    [WindowsOnlyFact]
    public async Task ExecuteAsync_HonorsCancellation()
    {
        var tool = new WindowsSocketsTool();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(ToolArguments.Empty, cts.Token));
    }
}
