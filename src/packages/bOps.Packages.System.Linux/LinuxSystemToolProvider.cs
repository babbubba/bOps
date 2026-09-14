using bOps.Abstractions;

namespace bOps.Packages.Sys.Linux;

/// <summary>Contributes the Linux implementations of every <c>system.*</c> and <c>process.*</c> tool.</summary>
public sealed class LinuxSystemToolProvider : IToolProvider
{
    /// <inheritdoc />
    public IEnumerable<ITool> GetTools() =>
    [
        new LinuxSystemInfoTool(),
        new LinuxCpuUsageTool(),
        new LinuxMemoryUsageTool(),
        new LinuxDiskUsageTool(),
        new LinuxProcessListTool(),
    ];
}
