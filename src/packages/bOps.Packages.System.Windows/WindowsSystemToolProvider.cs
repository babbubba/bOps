// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Sys.Windows;

/// <summary>Contributes the Windows implementations of every <c>system.*</c> and <c>process.*</c> tool.</summary>
public sealed class WindowsSystemToolProvider : IToolProvider
{
    /// <inheritdoc />
    public IEnumerable<ITool> GetTools() =>
    [
        new WindowsSystemInfoTool(),
        new WindowsSystemTimeTool(),
        new WindowsRebootPendingTool(),
        new WindowsUpdatesTool(),
        new WindowsUpdateHistoryTool(),
        new WindowsCrashEvidenceTool(),
        new WindowsApplicationInventoryTool(),
        new WindowsDeviceInventoryTool(),
        new WindowsSystemEventsTool(),
        new WindowsCpuUsageTool(),
        new WindowsMemoryUsageTool(),
        new WindowsDiskUsageTool(),
        new WindowsProcessListTool(),
        new WindowsSwapUsageTool(),
        new WindowsIoUsageTool(),
        new WindowsProcessInspectTool(),
        new WindowsProcessMetricsTool(),
        new WindowsProcessTreeTool(),
        new WindowsProcessModulesTool(),
        new WindowsProcessStopTool(),
        new WindowsProcessKillTool(),
    ];
}
