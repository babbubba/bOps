using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Identity.Windows;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.Identity.Windows.Tests;

[Trait("Platform", "Windows")]
public sealed class WindowsIdentityNativeTests
{
    [WindowsOnlyFact]
    public async Task Current_UsesRealWindowsIdentityWithoutSensitiveFields()
    {
        var result = await Tool("identity.current").ExecuteAsync(ToolArguments.Empty);
        Assert.True(result.Succeeded); var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.False(string.IsNullOrWhiteSpace(json["user"]?.GetValue<string>()));
        Assert.False(json.ContainsKey("token")); Assert.False(json.ContainsKey("environment")); Assert.False(json.ContainsKey("password"));
        Assert.InRange(json["groups"]!.AsArray().Count, 0, 100);
    }

    [WindowsOnlyFact]
    public async Task Users_EnumeratesBoundedLocalAccountsWithAuthoritativeEnablement()
    {
        var result = await Tool("identity.users").ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 50 }));
        Assert.True(result.Succeeded); var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.InRange(json["users"]!.AsArray().Count, 0, 50);
        Assert.All(json["users"]!.AsArray(), row => { if (row!["enabled"] is not null) _ = row["enabled"]!.GetValue<bool>(); });
        Assert.DoesNotContain("password", result.Output!, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsOnlyFact]
    public async Task Groups_EnumeratesDirectMembersWithBounds()
    {
        var result = await Tool("identity.groups").ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 50 }));
        Assert.True(result.Succeeded); var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.InRange(json["groups"]!.AsArray().Count, 0, 50);
        Assert.All(json["groups"]!.AsArray(), row => Assert.InRange(row!["members"]!.AsArray().Count, 0, 100));
    }

    [WindowsOnlyFact]
    public async Task Sessions_UsesWtsAndKeepsNormalizedRowsBounded()
    {
        var result = await Tool("identity.sessions").ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 50 }));
        Assert.True(result.Succeeded); var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.InRange(json["sessions"]!.AsArray().Count, 0, 50);
        Assert.All(json["sessions"]!.AsArray(), row => { Assert.False(row!["source"]!.GetValue<string>()!.Contains("password", StringComparison.OrdinalIgnoreCase)); Assert.False(row["clientAddress"]?.GetValue<string>()?.Contains("ClientName", StringComparison.OrdinalIgnoreCase) == true); });
    }

    [WindowsOnlyFact]
    public async Task SystemTime_AndRebootPending_AreEnvironmentIndependentSmokes()
    {
        var time = await new WindowsSystemTimeTool().ExecuteAsync(ToolArguments.Empty); Assert.True(time.Succeeded); var timeJson = JsonNode.Parse(time.Output!)!.AsObject();
        Assert.NotEqual(default, timeJson["utcNow"]!.GetValue<DateTimeOffset>()); Assert.False(string.IsNullOrWhiteSpace(timeJson["timeZoneId"]!.GetValue<string>()));
        var reboot = await new WindowsRebootPendingTool().ExecuteAsync(ToolArguments.Empty); Assert.True(reboot.Succeeded); var rebootJson = JsonNode.Parse(reboot.Output!)!.AsObject();
        Assert.InRange(rebootJson["reasons"]!.AsArray().Count, 0, 10); Assert.DoesNotContain("PendingFileRenameOperations", reboot.Output!, StringComparison.Ordinal);
    }

    private static ITool Tool(string name) => new WindowsIdentityToolProvider().GetTools().Single(x => x.Manifest.Name == name);
}
