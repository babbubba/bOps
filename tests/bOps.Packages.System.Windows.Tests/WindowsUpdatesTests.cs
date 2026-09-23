// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

public sealed class WindowsUpdatesTests
{
    private static readonly WindowsUpdateNativeRecord Security = new("update-security", 7, "Security patch", [WindowsUpdateRecordMapper.SecurityCategoryId], true);
    private static JsonObject Json(ToolCallResult result) => JsonNode.Parse(result.Output!)!.AsObject();

    [Fact]
    public void WuaMapping_PreservesStableIdentityTitleAndUnknownVersions()
    {
        var row = WindowsUpdateRecordMapper.Map(Security);
        Assert.Equal("update-security:7", row.Id);
        Assert.Equal("Security patch", row.Name);
        Assert.Null(row.CurrentVersion);
        Assert.Null(row.AvailableVersion);
        Assert.Equal("windows-update-agent", row.Source);
        Assert.Null(row.PublishedUtc);
    }

    [Fact]
    public void WuaCategoryClassification_UsesOnlyAuthoritativeCategoryIds()
    {
        Assert.Equal("security", WindowsUpdateRecordMapper.Classify([WindowsUpdateRecordMapper.SecurityCategoryId]));
        Assert.Equal("critical", WindowsUpdateRecordMapper.Classify([WindowsUpdateRecordMapper.CriticalCategoryId]));
        Assert.Equal("other", WindowsUpdateRecordMapper.Classify(["not-a-wua-security-category"]));
    }

    [Fact]
    public async Task KindFilter_IsAppliedToNormalizedClassification()
    {
        var security = WindowsUpdateRecordMapper.Map(Security);
        var critical = WindowsUpdateRecordMapper.Map(new("update-critical", 1, "Critical patch", [WindowsUpdateRecordMapper.CriticalCategoryId], false));
        Assert.True(WindowsUpdateRecordMapper.MatchesKind(security, "security"));
        Assert.False(WindowsUpdateRecordMapper.MatchesKind(critical, "security"));
        var result = await Tool("security", [security]).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["kind"] = "security" }));
        Assert.Equal("security", Json(result)["items"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task LimitAndExtraNativeEvidence_AreIncompleteAndTruncated()
    {
        var rows = new[] { WindowsUpdateRecordMapper.Map(Security), WindowsUpdateRecordMapper.Map(new("second", 1, "Second", [], null)) };
        var tool = Tool("all", rows, truncated: true);
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 1 }));
        var json = Json(result);
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Single(json["items"]!.AsArray());
    }

    [Fact]
    public async Task ZeroRowsAfterValidWuaSearch_AreComplete()
    {
        var result = await Tool("all", []).ExecuteAsync(ToolArguments.Empty);
        var json = Json(result);
        Assert.True(result.Succeeded);
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Empty(json["items"]!.AsArray());
    }

    [Fact]
    public async Task UnavailableAndSearchFailure_AreExplicitlyIncomplete()
    {
        foreach (var detail in new[] { "windows-update-agent.unavailable", "windows-update-agent.search-failed" })
        {
            var result = await Tool("all", [], InventorySourceStatus.Unavailable, detail).ExecuteAsync(ToolArguments.Empty);
            var json = Json(result);
            Assert.False(json["complete"]!.GetValue<bool>());
            Assert.Equal("unavailable", json["status"]!.GetValue<string>());
            Assert.Contains(detail, json["warnings"]!.ToJsonString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PartialOrMalformedNativeEvidence_CannotBecomeComplete()
    {
        var result = await Tool("all", [], InventorySourceStatus.Partial, "windows-update-agent.helper-invalid-response").ExecuteAsync(ToolArguments.Empty);
        var json = Json(result);
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal("partial", json["status"]!.GetValue<string>());
    }

    [WindowsOnlyFact]
    public async Task Timeout_KillsTheIsolatedChildAndWaitsForExit()
    {
        var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var result = await WindowsUpdateHelperClient.RunTestChildAsync(command, ["/c", "ping 127.0.0.1 -n 30 > nul"], TimeSpan.FromMilliseconds(250), CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.True(result.Exited);
        Assert.False(ProcessExists(result.ProcessId));
    }

    [WindowsOnlyFact]
    public async Task RealWuaRead_ProducesEitherCompleteShapeOrExplicitUnavailableEvidence()
    {
        var result = await new WindowsUpdatesTool().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 1 }));
        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = Json(result);
        Assert.Equal("windows-update-agent", json["sources"]![0]!["name"]!.GetValue<string>());
        Assert.InRange(json["returnedItems"]!.GetValue<int>(), 0, 1);
        if (json["complete"]!.GetValue<bool>()) return;
        Assert.Contains("windows-update-agent", json["warnings"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionUpdatesSources_ContainNoMutationOrCallerControlledCommandSurface()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "packages"));
        var files = new[]
        {
            Path.Combine(root, "bOps.Packages.System.Windows", "WindowsUpdatesTool.cs"),
            Path.Combine(root, "bOps.Packages.System.Windows", "WindowsUpdateHelperClient.cs"),
            Path.Combine(root, "bOps.Packages.System.Windows", "WindowsUpdateRecordMapper.cs"),
            Path.Combine(root, "bOps.Packages.System.Windows.Updates.Helper", "Program.cs"),
        };
        var source = string.Join('\n', files.Select(File.ReadAllText));
        foreach (var forbidden in new[] { @"\bInstall\s*\(", @"\bDownload\s*\(", @"\bAcceptEula\s*\(", "PowerShell", "winget", "wuauclt", "UsoClient", "cmd.exe", "IsHidden =" })
            Assert.DoesNotMatch(new Regex(forbidden, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), source);
        Assert.DoesNotContain("Search(args", source, StringComparison.Ordinal);
    }

    private static WindowsUpdatesTool Tool(string kind, IReadOnlyList<UpdateRecord> rows, InventorySourceStatus status = InventorySourceStatus.Available, string? detail = null, bool truncated = false) =>
        new(new FakeCollector(new MaintenanceSnapshot<UpdateRecord>(rows, [new("windows-update-agent", status, detail)], detail is null ? null : [detail], truncated)));

    private static bool ProcessExists(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class FakeCollector(MaintenanceSnapshot<UpdateRecord> snapshot) : IWindowsUpdateCollector
    {
        public Task<MaintenanceSnapshot<UpdateRecord>> CollectAsync(string kind, int limit, CancellationToken ct) => Task.FromResult(snapshot);
    }
}
