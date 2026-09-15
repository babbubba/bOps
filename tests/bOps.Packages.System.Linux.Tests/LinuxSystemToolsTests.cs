using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>
/// Integration tests against the real Linux OS (never mocked — agentic/04-testing-rules.md,
/// "Never mock the operating system"), run through the shared conformance suite so a difference
/// in output shape from the Windows package would be caught here. Marked
/// <c>[Trait("Platform","Linux")]</c> so CI can select or skip this project explicitly; there is
/// no Linux host in this dev environment, so this project is scaffolded but not exercised here
/// (HANDOFF.md) — it runs wherever a real Linux target exists (a container, or the Aspire
/// AppHost from V0.5).
/// </summary>
[Trait("Platform", "Linux")]
public sealed class LinuxSystemToolsTests
{
    [LinuxOnlyFact]
    public Task SystemInfo_Conforms() =>
        SystemToolConformance.AssertSystemInfoConformsAsync(new LinuxSystemInfoTool(), "linux");

    [LinuxOnlyFact]
    public Task Cpu_Conforms() =>
        SystemToolConformance.AssertCpuUsageConformsAsync(new LinuxCpuUsageTool(), "linux");

    [LinuxOnlyFact]
    public Task Memory_Conforms() =>
        SystemToolConformance.AssertMemoryUsageConformsAsync(new LinuxMemoryUsageTool(), "linux");

    [LinuxOnlyFact]
    public Task Disk_Conforms() =>
        SystemToolConformance.AssertDiskUsageConformsAsync(new LinuxDiskUsageTool(), "linux");

    [LinuxOnlyFact]
    public Task ProcessList_Conforms() =>
        SystemToolConformance.AssertProcessListConformsAsync(new LinuxProcessListTool(), "linux");

    [LinuxOnlyFact]
    public Task ProcessList_RespectsLimit() =>
        SystemToolConformance.AssertProcessListRespectsLimitAsync(new LinuxProcessListTool());

    [Fact]
    public void ToolProvider_ContributesExactlyTheFiveSystemAndProcessTools()
    {
        var names = new LinuxSystemToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(
            ["system.info", "system.cpu", "system.memory", "system.disk", "process.list"],
            names);
    }
}
