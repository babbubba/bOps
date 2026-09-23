// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Sys.Linux;

/// <summary>Contributes the Linux implementations of every <c>system.*</c> and <c>process.*</c> tool.</summary>
public sealed class LinuxSystemToolProvider : IToolProvider
{
    /// <inheritdoc />
    public IEnumerable<ITool> GetTools() =>
    [
        new LinuxSystemInfoTool(),
        new LinuxSystemTimeTool(),
        new LinuxRebootPendingTool(),
        new LinuxApplicationInventoryTool(),
        new LinuxDeviceInventoryTool(),
        new LinuxSystemEventsTool(),
        new LinuxUpdatesTool(),
        new LinuxUpdateHistoryTool(),
        new LinuxCrashEvidenceTool(),
        new LinuxDriverEvidenceTool(),
        new LinuxCpuUsageTool(),
        new LinuxMemoryUsageTool(),
        new LinuxDiskUsageTool(),
        new LinuxProcessListTool(),
        new LinuxSwapUsageTool(),
        new LinuxIoUsageTool(),
        new LinuxProcessInspectTool(),
        new LinuxProcessMetricsTool(),
        new LinuxProcessTreeTool(),
        new LinuxProcessModulesTool(),
        new LinuxProcessStopTool(),
        new LinuxProcessKillTool(),
    ];
}
