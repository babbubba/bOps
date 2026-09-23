using bOps.Abstractions;

namespace bOps.Packages.Scheduler.Windows;

public sealed class WindowsSchedulerToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() =>
    [
        new WindowsSchedulerListTool(),
        new WindowsSchedulerInspectTool(),
        new WindowsSchedulerHistoryTool(),
        new WindowsSchedulerEnableTool(),
        new WindowsSchedulerDisableTool(),
    ];
}
