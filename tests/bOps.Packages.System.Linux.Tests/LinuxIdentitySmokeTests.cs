using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Identity.Linux;

namespace bOps.Packages.System.Linux.Tests;

public sealed class LinuxIdentitySmokeTests
{
    [LinuxOnlyFact]
    public async Task LocalIdentitySourcesAreBoundedAndDoNotExposeCredentialFiles()
    {
        var tools = new LinuxIdentityToolProvider().GetTools().ToDictionary(x => x.Manifest.Name, StringComparer.Ordinal);
        var users = await tools["identity.users"].ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 20 }));
        var groups = await tools["identity.groups"].ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 20 }));
        var sessions = await tools["identity.sessions"].ExecuteAsync(ToolArguments.Empty);
        Assert.True(users.Succeeded && groups.Succeeded && sessions.Succeeded);
        Assert.DoesNotContain("shadow", users.Output!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", users.Output!, StringComparison.OrdinalIgnoreCase);
        Assert.False(JsonNode.Parse(users.Output!)!["complete"]!.GetValue<bool>());
        Assert.False(JsonNode.Parse(groups.Output!)!["complete"]!.GetValue<bool>());
        var sessionJson = JsonNode.Parse(sessions.Output!)!; Assert.NotNull(sessionJson["source"]); Assert.InRange(sessionJson["sessions"]!.AsArray().Count, 0, 100);
    }
    [LinuxOnlyFact]
    public async Task CurrentUsesEffectiveUidAndPosixSupplementaryGroups()
    { var result = await new LinuxCurrentIdentityTool().ExecuteAsync(ToolArguments.Empty); Assert.True(result.Succeeded); var json = JsonNode.Parse(result.Output!)!; var status = await File.ReadAllTextAsync("/proc/self/status"); var euid = uint.Parse(status.Split('\n').Single(x => x.StartsWith("Uid:", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]); Assert.Equal(euid == 0, json["elevated"]!.GetValue<bool>()); Assert.InRange(json["groups"]!.AsArray().Count, 0, 100); Assert.Null(json["serviceAccount"]); }
}
