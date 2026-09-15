// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Network.Tests;

/// <summary>
/// Integration tests against the real network stack — loopback and actual DNS resolution, never
/// mocked (agentic/04-testing-rules.md: Network is a platform package). Every <c>network.*</c>
/// tool is Read-risk, so there is no verification to test here — only that each tool observes
/// something real without throwing (rule C1: a tool that throws is a bug in the tool).
/// </summary>
public sealed class NetworkToolsTests
{
    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = JsonValue.Create(value);
        }

        return ToolArguments.FromJson(json);
    }

    [Fact]
    public async Task Interfaces_ReturnsAtLeastTheLoopbackInterface()
    {
        var result = await new NetworkInterfacesTool().ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Output);
        Assert.NotEqual("(no interfaces)", result.Output);
    }

    [Fact]
    public async Task Dns_ResolvesLocalhost()
    {
        var result = await new NetworkDnsTool().ExecuteAsync(Args(("host", "localhost")));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Output);
    }

    [Fact]
    public async Task Dns_Fails_ForAHostnameUnderTheReservedInvalidTld()
    {
        // ".invalid" is reserved by RFC 2606 to never resolve — deterministic, unlike a made-up
        // name under a real TLD, which some networks silently redirect (NXDOMAIN hijacking)
        // instead of failing.
        var result = await new NetworkDnsTool().ExecuteAsync(Args(("host", "this-host-does-not-exist.invalid")));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [Fact]
    public async Task Ping_DoesNotThrow_ForLoopback()
    {
        // Deliberately not asserting Success: some sandboxed CI containers restrict unprivileged
        // ICMP even to loopback. The contract this test protects is rule C1 (never throws) and
        // that the tool produces a real, non-empty observation either way.
        var result = await new NetworkPingTool().ExecuteAsync(Args(("host", "127.0.0.1")));

        Assert.True(result.Outcome is ToolOutcome.Success or ToolOutcome.Failure);
        Assert.False(string.IsNullOrEmpty(result.Succeeded ? result.Output : result.ErrorMessage));
    }

    [Fact]
    public async Task Connections_DoesNotThrow()
    {
        var result = await new NetworkConnectionsTool().ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Output);
    }

    [Fact]
    public void ToolProvider_ContributesExactlyTheFourNetworkTools_AllReadRisk()
    {
        var tools = new NetworkToolProvider().GetTools().ToList();

        Assert.Equal(["network.interfaces", "network.dns", "network.ping", "network.connections"], tools.Select(t => t.Manifest.Name));
        Assert.All(tools, t => Assert.Equal(RiskLevel.Read, t.Manifest.Risk));
        Assert.All(tools, t => Assert.Null(t.Manifest.Verification));
    }
}
