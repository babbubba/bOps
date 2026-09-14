using bOps.Abstractions;

namespace bOps.Packages.Sys.Windows;

/// <summary>Contributes the Windows implementations of every <c>system.*</c> and <c>process.*</c> tool.</summary>
public sealed class WindowsSystemToolProvider : IToolProvider
{
    /// <inheritdoc />
    public IEnumerable<ITool> GetTools() =>
    [
        new WindowsSystemInfoTool(),
        new WindowsCpuUsageTool(),
        new WindowsMemoryUsageTool(),
        new WindowsDiskUsageTool(),
        new WindowsProcessListTool(),
    ];
}
