// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// Integration tests against the real Windows OS (never mocked — agentic/04-testing-rules.md,
/// "Never mock the operating system"), run through the shared conformance suite so a difference
/// in output shape from the Linux package would be caught here.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WindowsSystemToolsTests
{
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

    [Fact]
    public void ToolProvider_ContributesExactlyTheTwelveSystemAndProcessTools()
    {
        var names = new WindowsSystemToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(
            [
                "system.info", "system.apps", "system.devices", "system.cpu", "system.memory", "system.disk", "process.list",
                "system.swap", "system.io", "process.inspect", "process.stop", "process.kill",
            ],
            names);
    }
}
