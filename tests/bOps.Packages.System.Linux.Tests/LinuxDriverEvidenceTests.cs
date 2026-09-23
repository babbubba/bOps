// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

public sealed class LinuxDriverEvidenceTests
{
    [Fact]
    public async Task ProcModulesMapsRowsVersionsStatesAndSerializedOrdering()
    {
        var tool = new LinuxDriverEvidenceTool(() => "zeta 12 0 - Live 0x0\nalpha 34 0 - Loading 0x0\nbeta 56 0 - Unloading 0x0\n", module => module == "alpha" ? "1.0" : null);
        var json = JsonNode.Parse((await tool.ExecuteAsync(ToolArguments.Empty)).Output!)!.AsObject();
        Assert.True(json["complete"]!.GetValue<bool>()); Assert.Equal("alpha", json["items"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("1.0", json["items"]![0]!["version"]!.GetValue<string>()); Assert.Equal("34", json["items"]![0]!["addressOrSize"]!.GetValue<string>());
        Assert.Equal("Loading", json["items"]![0]!["state"]!.GetValue<string>()); Assert.Equal("Unloading", json["items"]![1]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task MissingOrInaccessibleMetadataAndMalformedProcArePartialWithoutLosingRows()
    {
        var tool = new LinuxDriverEvidenceTool(() => "one 1 0 - Live 0x0\ntwo 2 0 - Live 0x0\nbroken\n", module => module == "one" ? throw new UnauthorizedAccessException() : null);
        var json = JsonNode.Parse((await tool.ExecuteAsync(ToolArguments.Empty)).Output!)!.AsObject();
        Assert.False(json["complete"]!.GetValue<bool>()); Assert.Equal(2, json["items"]!.AsArray().Count); Assert.Null(json["items"]![0]!["version"]);
        var unavailable = new LinuxDriverEvidenceTool(() => throw new IOException(), _ => null);
        Assert.False(JsonNode.Parse((await unavailable.ExecuteAsync(ToolArguments.Empty)).Output!)!["complete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task LimitAndOversizedInputRemainBoundedAndIncomplete()
    {
        var limited = new LinuxDriverEvidenceTool(() => "two 2 0 - Live 0x0\none 1 0 - Live 0x0\n", _ => null);
        var json = JsonNode.Parse((await limited.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 1 }))).Output!)!.AsObject();
        Assert.False(json["complete"]!.GetValue<bool>()); Assert.True(json["truncated"]!.GetValue<bool>()); Assert.Single(json["items"]!.AsArray());
        var oversized = new LinuxDriverEvidenceTool(() => new string('x', 1_048_577), _ => null);
        Assert.False(JsonNode.Parse((await oversized.ExecuteAsync(ToolArguments.Empty)).Output!)!["complete"]!.GetValue<bool>());
    }

    [LinuxOnlyFact]
    public async Task RealLinuxSmokeReadsBoundedProcModulesEvidence()
    {
        var result = await new LinuxDriverEvidenceTool().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 20 }));
        Assert.True(result.Succeeded); Assert.InRange(JsonNode.Parse(result.Output!)!["items"]!.AsArray().Count, 0, 20);
    }
}
