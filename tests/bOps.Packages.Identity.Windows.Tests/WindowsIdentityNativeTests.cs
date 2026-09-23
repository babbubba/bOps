using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Reflection;
using bOps.Abstractions;
using bOps.Packages.Identity.Windows;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.Identity.Windows.Tests;

[Trait("Platform", "Windows")]
public sealed class WindowsIdentityNativeTests
{
    [Fact] public void DomainControllerBoundaryNeverCallsEnumeration() { var called = false; var users = WindowsNetApi.ReadUsersForRole(true, () => { called = true; return new bOps.Packages.Identity.Core.IdentityObservation<int>([1], true, "fixture"); }); var groups = WindowsNetApi.ReadGroupsForRole(true, () => { called = true; return new bOps.Packages.Identity.Core.IdentityObservation<int>([1], true, "fixture"); }); Assert.False(called); Assert.False(users.Complete); Assert.Empty(users.Rows); Assert.Contains("unsupported", users.Source, StringComparison.Ordinal); Assert.False(groups.Complete); Assert.Empty(groups.Rows); }
    [Fact] public void DsRoleLayoutAndDomainControllerRoleValuesAreExact() { var type = typeof(WindowsNetApi.DomainRoleInfo); Assert.Equal(IntPtr.Size == 8 ? 48 : 36, Marshal.SizeOf<WindowsNetApi.DomainRoleInfo>()); Assert.Equal(0, Marshal.OffsetOf<WindowsNetApi.DomainRoleInfo>(nameof(WindowsNetApi.DomainRoleInfo.MachineRole)).ToInt32()); Assert.Equal(4, Marshal.OffsetOf<WindowsNetApi.DomainRoleInfo>(nameof(WindowsNetApi.DomainRoleInfo.Flags)).ToInt32()); Assert.Equal(8, Marshal.OffsetOf<WindowsNetApi.DomainRoleInfo>(nameof(WindowsNetApi.DomainRoleInfo.DomainNameFlat)).ToInt32()); Assert.Equal(8 + (2 * IntPtr.Size), Marshal.OffsetOf<WindowsNetApi.DomainRoleInfo>(nameof(WindowsNetApi.DomainRoleInfo.DomainForest)).ToInt32()); Assert.Equal(8 + (3 * IntPtr.Size), Marshal.OffsetOf<WindowsNetApi.DomainRoleInfo>(nameof(WindowsNetApi.DomainRoleInfo.DomainGuid)).ToInt32()); Assert.Equal(6, type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length); Assert.False(WindowsNetApi.IsDomainControllerRole(3)); Assert.True(WindowsNetApi.IsDomainControllerRole(4)); Assert.True(WindowsNetApi.IsDomainControllerRole(5)); }
    [Fact] public void NetApiEnumerationUsesPointerSizedResumeHandles() { Assert.Equal(3, WindowsNetApi.MembersInfoLevel); foreach (var name in new[] { "NetUserEnum", "NetLocalGroupEnum", "NetLocalGroupGetMembers" }) { var method = typeof(WindowsNetApi).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!; Assert.Equal(typeof(UIntPtr).MakeByRefType(), method.GetParameters()[^1].ParameterType); } Assert.Equal(IntPtr.Size, Marshal.SizeOf<UIntPtr>()); }
    [Fact] public void WtsEnumsAndAddressLayoutMatchNativeValues() { Assert.Equal(9, (int)WindowsSessionsNative.WtsInfoClass.ClientBuildNumber); Assert.Equal(10, (int)WindowsSessionsNative.WtsInfoClass.ClientName); Assert.Equal(14, (int)WindowsSessionsNative.WtsInfoClass.ClientAddress); Assert.Equal(29, (int)WindowsSessionsNative.WtsInfoClass.IsRemoteSession); Assert.Equal(4, Marshal.OffsetOf<WindowsSessionsNative.WtsClientAddress>(nameof(WindowsSessionsNative.WtsClientAddress.Address)).ToInt32()); var addr = new WindowsSessionsNative.WtsClientAddress { AddressFamily = 2, Address = [0, 0, 192, 168, 0, 1, .. new byte[14]] }; Assert.Equal("192.168.0.1", WindowsSessionsNative.ParseClientAddress(addr)); }
    [Fact] public void WtsInfoExLogonTimeOffsetMatchesNativeLayout() { Assert.Equal(160, Marshal.OffsetOf<WindowsSessionsNative.WtsInfoExLevel1>(nameof(WindowsSessionsNative.WtsInfoExLevel1.LogonTime)).ToInt32()); Assert.Equal(8, Marshal.OffsetOf<WindowsSessionsNative.WtsInfoEx>(nameof(WindowsSessionsNative.WtsInfoEx.Level1)).ToInt32()); }
    [Fact] public void WtsLogonTimeRejectsEmptyAndParsesFileTime() { Assert.Null(WindowsSessionsNative.ParseLogonTime(0)); var now = DateTimeOffset.UtcNow; Assert.InRange((WindowsSessionsNative.ParseLogonTime(now.UtcDateTime.ToFileTimeUtc()) - now).GetValueOrDefault().Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1)); }
    [Fact] public void WtsZeroSessionsAndUnavailableSourceHaveHonestCompleteness() { Assert.True(WindowsSessionsNative.CreateObservation([], true, false).Complete); Assert.False(WindowsSessionsNative.CreateObservation([], false, false).Complete); Assert.False(WindowsSessionsNative.CreateObservation([], true, true).Complete); }
    [Fact] public void MemberServerBoundaryInvokesEnumeration() { var called = false; var result = WindowsNetApi.ReadGroupsForRole(false, () => { called = true; return new bOps.Packages.Identity.Core.IdentityObservation<int>([1], true, "fixture"); }); Assert.True(called); Assert.True(result.Complete); }
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
        Assert.All(json["groups"]!.AsArray(), row => { var members = row!["members"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(); Assert.InRange(members.Length, 0, 100); Assert.All(members, name => Assert.False(string.IsNullOrWhiteSpace(name))); Assert.Equal(members.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), members); });
        Assert.Contains(json["groups"]!.AsArray(), row => row!["members"]!.AsArray().Count > 0);
    }

    [WindowsOnlyFact]
    public async Task Sessions_UsesWtsAndKeepsNormalizedRowsBounded()
    {
        var result = await Tool("identity.sessions").ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 50 }));
        Assert.True(result.Succeeded); var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.InRange(json["sessions"]!.AsArray().Count, 0, 50);
        Assert.All(json["sessions"]!.AsArray(), row => { Assert.False(row!["source"]!.GetValue<string>()!.Contains("password", StringComparison.OrdinalIgnoreCase)); Assert.False(row["clientAddress"]?.GetValue<string>()?.Contains("ClientName", StringComparison.OrdinalIgnoreCase) == true); if (row["loginTimeUtc"] is JsonValue login) { var time = login.GetValue<DateTimeOffset>(); Assert.InRange(time, DateTimeOffset.UtcNow.AddYears(-80), DateTimeOffset.UtcNow.AddMinutes(1)); } });
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
