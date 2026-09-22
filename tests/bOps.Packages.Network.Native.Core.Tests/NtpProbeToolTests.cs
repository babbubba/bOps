// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Core.Tests;

public sealed class NtpProbeToolTests
{
    [Fact]
    public async Task ExecuteAsync_UnreachableHost_ReportsInvalidNotSuccess()
    {
        var tool = new NtpProbeTool("linux");
        // TEST-NET-1 (RFC 5737): reserved, guaranteed not to answer UDP/123.
        var arguments = ToolArguments.FromJson(new JsonObject { ["host"] = "192.0.2.1", ["timeout"] = 300 });

        var result = await tool.ExecuteAsync(arguments);

        Assert.True(result.Succeeded); // the tool call itself succeeds; the probe result says valid:false
        using var document = JsonDocument.Parse(result.Output!);
        Assert.False(document.RootElement.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public void Manifest_IsReadRiskWithNoVerification()
    {
        var tool = new NtpProbeTool("linux");

        Assert.Equal(RiskLevel.Read, tool.Manifest.Risk);
        Assert.Null(tool.Manifest.Verification);
        Assert.Equal("network.ntp_probe", tool.Manifest.Name);
    }

    [Trait("Category", "LiveModel")]
    [Fact]
    public async Task ExecuteAsync_LiveSmoke_AgainstPublicNtpPool_ReportsValidOffset()
    {
        var tool = new NtpProbeTool("linux");
        var arguments = ToolArguments.FromJson(new JsonObject { ["host"] = "pool.ntp.org", ["timeout"] = 5_000 });

        var result = await tool.ExecuteAsync(arguments);

        Assert.True(result.Succeeded);
        using var document = JsonDocument.Parse(result.Output!);
        Assert.True(document.RootElement.GetProperty("valid").GetBoolean());
    }
}
