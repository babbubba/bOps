using bOps.Abstractions;

namespace bOps.Packages.Scheduler.Linux;

public sealed class LinuxSchedulerToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() =>
    [new LinuxSchedulerListTool(), new LinuxSchedulerInspectTool(), new LinuxSchedulerHistoryTool(),
     new LinuxSchedulerEnableTool(), new LinuxSchedulerDisableTool()];
}
