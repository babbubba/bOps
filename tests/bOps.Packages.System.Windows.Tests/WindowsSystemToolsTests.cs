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
    [Fact] public void RebootDetectorsReadRegistryEvidenceForPresentAbsentAndDeniedStates() { Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", WindowsRebootPendingTool.CbsKey); Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", WindowsRebootPendingTool.WindowsUpdateKey); var absent = WindowsRebootDetector.Observe(new RegistryFixture(false, false, false, "host", "host", null)); Assert.True(absent.Complete); Assert.False(absent.Pending); var present = WindowsRebootDetector.Observe(new RegistryFixture(true, true, true, "old-host", "new-host", "contoso")); Assert.True(present.Complete); Assert.Equal(["windows.cbs", "windows.pending-computer-rename", "windows.pending-domain-change", "windows.pending-file-rename", "windows.update"], present.Reasons); var denied = WindowsRebootDetector.Observe(new RegistryFixture(null, null, null, null, null, null)); Assert.False(denied.Complete); Assert.False(denied.Pending); var missingComputerName = WindowsRebootDetector.Observe(new RegistryFixture(false, false, false, null, null, null)); Assert.False(missingComputerName.Complete); }
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
    public void ToolProvider_ContributesSystemUpdatesAndTheExistingSystemAndProcessTools()
    {
        var names = new WindowsSystemToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(
            [
                "system.info", "system.time", "system.reboot_pending", "system.updates", "system.update_history", "system.crashes", "system.drivers", "system.apps", "system.devices", "system.events", "system.cpu", "system.memory", "system.disk", "process.list",
                "system.swap", "system.io", "process.inspect", "process.metrics", "process.tree", "process.modules",
                "process.stop", "process.kill",
            ],
            names);
    }

    private sealed class RegistryFixture(bool? cbs, bool? update, bool? pendingFileRename, string? activeName, string? pendingName, string? joinDomain) : IWindowsRegistryReader
    {
        public bool? HasSubKey(string path) => path == WindowsRebootPendingTool.CbsKey ? cbs : update;
        public bool? HasMultiString(string path, string name) => pendingFileRename;
        public WindowsRegistryString ReadString(string path, string name) => path.EndsWith("ActiveComputerName", StringComparison.Ordinal) ? new(true, activeName) : path.EndsWith("ComputerName", StringComparison.Ordinal) ? new(true, pendingName) : new(true, joinDomain);
    }
}
