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
        Assert.Contains("\"membersTruncated\":true", result.Output!, StringComparison.Ordinal);
    }

    private sealed class StubUsers() : IdentityUsersToolBase("test")
    {
        protected override Task<IReadOnlyList<IdentityUser>> CollectAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IdentityUser>>([
            new("unknown", "1", null, true, null, "/bin/false", null, "fixture"), new("disabled", "2", false, true, null, null, null, "fixture")]);
    }
    private sealed class StubGroups() : IdentityGroupsToolBase("test")
    {
        protected override Task<IReadOnlyList<IdentityGroup>> CollectAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IdentityGroup>>([new("g", "1", 101, Enumerable.Range(0, 101).Select(x => x.ToString()).ToArray(), true)]);
    }
}
