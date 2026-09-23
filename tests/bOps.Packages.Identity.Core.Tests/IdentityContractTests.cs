using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Identity.Core;

namespace bOps.Packages.Identity.Core.Tests;

public sealed class IdentityContractTests
{
    [Fact]
    public async Task Users_RejectsOutOfRangeLimit()
    {
        var result = await new StubUsers().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 2001 }));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Users_UnknownEnabledRemainsWhenFilteringDisabled()
    {
        var result = await new StubUsers().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["includeDisabled"] = false }));
        Assert.Contains("unknown", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", result.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_AreBoundedAndExplicitlyTruncated()
    {
        var result = await new StubGroups().ExecuteAsync(ToolArguments.Empty);
        var group = System.Text.Json.Nodes.JsonNode.Parse(result.Output!)!["groups"]![0]!;
        Assert.Equal(100, group["members"]!.AsArray().Count);
        Assert.Equal(101, group["memberCount"]!.GetValue<int>());
        Assert.True(group["membersTruncated"]!.GetValue<bool>());
    }
    [Fact] public async Task Current_DefaultGroupsLimitIs100And500IsAccepted() { var tool = new StubCurrent(); var first = System.Text.Json.Nodes.JsonNode.Parse((await tool.ExecuteAsync(ToolArguments.Empty)).Output!)!; Assert.Equal(100, first["groups"]!.AsArray().Count); Assert.Equal(501, first["groupCount"]!.GetValue<int>()); Assert.True(first["groupsTruncated"]!.GetValue<bool>()); Assert.Equal("g0", first["groups"]![0]!.GetValue<string>()); var max = System.Text.Json.Nodes.JsonNode.Parse((await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 500 }))).Output!)!; Assert.Equal(500, max["groups"]!.AsArray().Count); Assert.Equal(501, max["groupCount"]!.GetValue<int>()); Assert.True(max["groupsTruncated"]!.GetValue<bool>()); Assert.Contains(max["groups"]!.AsArray(), x => x!.GetValue<string>() == "g200"); }
    [Fact] public async Task Current_RejectsLimitAbove500() { var result = await new StubCurrent().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 501 })); Assert.False(result.Succeeded); }
    [Fact] public async Task FailedSourceCannotAppearComplete() { var result = await new StubUsers().ExecuteAsync(ToolArguments.Empty); var json = System.Text.Json.Nodes.JsonNode.Parse(result.Output!)!; Assert.False(json["complete"]!.GetValue<bool>()); }
    [Fact] public async Task SessionSourceFailureAndEvidenceStateAreSerializedPerExecution() { var result = await new StubSessions().ExecuteAsync(ToolArguments.Empty); var json = System.Text.Json.Nodes.JsonNode.Parse(result.Output!)!; Assert.Empty(json["sessions"]!.AsArray()); Assert.False(json["complete"]!.GetValue<bool>()); Assert.Equal("fixture.timeout", json["source"]!.GetValue<string>()); }
    [Fact]
    public async Task Groups_PreservesUnknownMemberCount()
    {
        var result = await new UnknownCountGroups().ExecuteAsync(ToolArguments.Empty);
        var group = System.Text.Json.Nodes.JsonNode.Parse(result.Output!)!["groups"]![0]!;
        Assert.Null(group["memberCount"]);
        Assert.False(group["membersTruncated"]!.GetValue<bool>());
        Assert.False(System.Text.Json.Nodes.JsonNode.Parse(result.Output!)!["complete"]!.GetValue<bool>());
    }

    private sealed class StubUsers() : IdentityUsersToolBase("test")
    {
        protected override Task<IdentityObservation<IdentityUser>> CollectObservationAsync(CancellationToken ct) => Task.FromResult(new IdentityObservation<IdentityUser>([
            new("unknown", "1", null, true, null, "/bin/false", null, "fixture"), new("disabled", "2", false, true, null, null, null, "fixture")], false, "fixture.denied"));
    }
    private sealed class StubGroups() : IdentityGroupsToolBase("test")
    {
        protected override Task<IdentityObservation<IdentityGroup>> CollectObservationAsync(CancellationToken ct) => Task.FromResult(new IdentityObservation<IdentityGroup>([new("g", "1", 101, Enumerable.Range(0, 101).Select(x => x.ToString()).ToArray(), false)], false, "fixture"));
    }
    private sealed class UnknownCountGroups() : IdentityGroupsToolBase("test")
    {
        protected override Task<IdentityObservation<IdentityGroup>> CollectObservationAsync(CancellationToken ct) => Task.FromResult(new IdentityObservation<IdentityGroup>([new("g", "1", null, [], false)], false, "fixture.error"));
    }
    private sealed class StubCurrent() : IdentityCurrentToolBase("test") { protected override Task<IdentityCurrentResult> CollectAsync(CancellationToken ct) => Task.FromResult(new IdentityCurrentResult("u", "1", false, null, Enumerable.Range(0, 501).Select(x => "g" + x).ToArray(), 501, false, "test")); }
    private sealed class StubSessions() : IdentitySessionsToolBase("test") { protected override Task<IdentityObservation<IdentitySession>> CollectObservationAsync(CancellationToken ct) => Task.FromResult(new IdentityObservation<IdentitySession>([], false, "fixture.timeout")); }
}
