// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Windows;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// Integration tests against the real Windows OS (never mocked — agentic/04-testing-rules.md,
/// "Never mock the operating system"), run through the shared conformance suite so a difference
/// in output shape from the Linux package would be caught here.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WindowsSystemToolsTests
{
    [Fact] public void RebootEvidenceDistinguishesMissingDeniedAndMultipleReasons() { Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", WindowsRebootPendingTool.CbsKey); Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", WindowsRebootPendingTool.WindowsUpdateKey); Assert.Equal(@"SYSTEM\CurrentControlSet\Control\Session Manager", WindowsRebootPendingTool.PendingFileRenameKey); Assert.Equal(@"SYSTEM\CurrentControlSet\Control\ComputerName", WindowsRebootPendingTool.ComputerNameKey); Assert.Equal(@"SYSTEM\CurrentControlSet\Services\Netlogon\Parameters", WindowsRebootPendingTool.NetlogonParametersKey); var absent = WindowsRebootEvidence.Combine(new Dictionary<string, bool?> { ["cbs"] = false, ["wu"] = false }); Assert.True(absent.Complete); Assert.False(absent.Pending); var denied = WindowsRebootEvidence.Combine(new Dictionary<string, bool?> { ["cbs"] = null }); Assert.False(denied.Complete); var multiple = WindowsRebootEvidence.Combine(new Dictionary<string, bool?> { ["windows.cbs"] = true, ["windows.pending-file-rename"] = true, ["windows.pending-computer-rename"] = false, ["windows.pending-domain-change"] = false, ["windows.update"] = false }); Assert.True(multiple.Complete); Assert.Equal(2, multiple.Reasons.Count); }
    [Theory] [InlineData("NoSync", false)] [InlineData("NTP", true)] [InlineData("NT5DS", true)] [InlineData("AllSync", true)] [InlineData("unknown", null)] [InlineData(null, null)]
    public void W32TimeConfigurationTypeHasDocumentedSemantics(string? type, bool? expected) => Assert.Equal(expected, WindowsTimeConfiguration.ParseType(type));
    [Fact] public void W32TimeMissingOrDeniedConfigurationIsUnknownAndIncomplete() { var missing = WindowsTimeConfiguration.Evaluate(null, true, false); Assert.Null(missing.Configured); Assert.False(missing.Complete); var denied = WindowsTimeConfiguration.Evaluate(null, false, false); Assert.Null(denied.Configured); Assert.False(denied.Complete); }
    [WindowsOnlyFact]
    public Task SystemInfo_Conforms() =>
        SystemToolConformance.AssertSystemInfoConformsAsync(new WindowsSystemInfoTool(), "windows");

    [WindowsOnlyFact]
    public Task Applications_Conform() =>
        SystemToolConformance.AssertApplicationsConformAsync(new WindowsApplicationInventoryTool(), "windows");

    [WindowsOnlyFact]
    public Task Devices_Conform() =>
        SystemToolConformance.AssertDevicesConformAsync(new WindowsDeviceInventoryTool(), "windows");

    [WindowsOnlyFact]
    public Task Cpu_Conforms() =>
        SystemToolConformance.AssertCpuUsageConformsAsync(new WindowsCpuUsageTool(), "windows");

    [WindowsOnlyFact]
    public Task Memory_Conforms() =>
        SystemToolConformance.AssertMemoryUsageConformsAsync(new WindowsMemoryUsageTool(), "windows");

    [WindowsOnlyFact]
    public Task Disk_Conforms() =>
        SystemToolConformance.AssertDiskUsageConformsAsync(new WindowsDiskUsageTool(), "windows");

    [WindowsOnlyFact]
    public Task ProcessList_Conforms() =>
        SystemToolConformance.AssertProcessListConformsAsync(new WindowsProcessListTool(), "windows");

    [WindowsOnlyFact]
    public Task ProcessList_RespectsLimit() =>
        SystemToolConformance.AssertProcessListRespectsLimitAsync(new WindowsProcessListTool());

    [WindowsOnlyFact]
    public Task Swap_Conforms() =>
        SystemToolConformance.AssertSwapUsageConformsAsync(new WindowsSwapUsageTool(), "windows");

    [WindowsOnlyFact]
    public Task Io_Conforms() =>
        SystemToolConformance.AssertIoUsageConformsAsync(new WindowsIoUsageTool(), "windows");

    [WindowsOnlyFact]
    public Task ProcessInspect_Conforms() =>
        SystemToolConformance.AssertProcessInspectConformsAsync(new WindowsProcessInspectTool(), "windows");

    [WindowsOnlyFact]
    public Task ProcessInspect_ReportsMissing_ForAnUnlikelyPid() =>
        SystemToolConformance.AssertProcessInspectReportsMissingAsync(new WindowsProcessInspectTool());

    [WindowsOnlyFact]
    public async Task SystemTime_RealSmoke_IsTimezoneSafe()
    {
        var result = await new WindowsSystemTimeTool().ExecuteAsync(ToolArguments.Empty);
        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.NotEqual(default, json["utcNow"]!.GetValue<DateTimeOffset>());
        Assert.False(string.IsNullOrWhiteSpace(json["localNow"]!.GetValue<DateTimeOffset>().ToString()));
        Assert.False(string.IsNullOrWhiteSpace(json["timeZoneId"]!.GetValue<string>()));
    }

    [WindowsOnlyFact]
    public async Task RebootPending_RealSmoke_IsBounded()
    {
        var result = await new WindowsRebootPendingTool().ExecuteAsync(ToolArguments.Empty);
        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.InRange(json["reasons"]!.AsArray().Count, 0, 10);
        Assert.DoesNotContain("PendingFileRenameOperations", result.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolProvider_ContributesExactlyTheEighteenSystemAndProcessTools()
    {
        var names = new WindowsSystemToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(
            [
                "system.info", "system.time", "system.reboot_pending", "system.apps", "system.devices", "system.events", "system.cpu", "system.memory", "system.disk", "process.list",
                "system.swap", "system.io", "process.inspect", "process.metrics", "process.tree", "process.modules",
                "process.stop", "process.kill",
            ],
            names);
    }
}
