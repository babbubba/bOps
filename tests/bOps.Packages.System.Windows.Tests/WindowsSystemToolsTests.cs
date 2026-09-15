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
    [Fact]
    public Task SystemInfo_Conforms() =>
        SystemToolConformance.AssertSystemInfoConformsAsync(new WindowsSystemInfoTool(), "windows");

    [Fact]
    public Task Cpu_Conforms() =>
        SystemToolConformance.AssertCpuUsageConformsAsync(new WindowsCpuUsageTool(), "windows");

    [Fact]
    public Task Memory_Conforms() =>
        SystemToolConformance.AssertMemoryUsageConformsAsync(new WindowsMemoryUsageTool(), "windows");

    [Fact]
    public Task Disk_Conforms() =>
        SystemToolConformance.AssertDiskUsageConformsAsync(new WindowsDiskUsageTool(), "windows");

    [Fact]
    public Task ProcessList_Conforms() =>
        SystemToolConformance.AssertProcessListConformsAsync(new WindowsProcessListTool(), "windows");

    [Fact]
    public Task ProcessList_RespectsLimit() =>
        SystemToolConformance.AssertProcessListRespectsLimitAsync(new WindowsProcessListTool());

    [Fact]
    public void ToolProvider_ContributesExactlyTheFiveSystemAndProcessTools()
    {
        var names = new WindowsSystemToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(
            ["system.info", "system.cpu", "system.memory", "system.disk", "process.list"],
            names);
    }
}
