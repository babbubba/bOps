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
        new LinuxApplicationInventoryTool(),
        new LinuxDeviceInventoryTool(),
        new LinuxSystemEventsTool(),
        new LinuxCpuUsageTool(),
        new LinuxMemoryUsageTool(),
        new LinuxDiskUsageTool(),
        new LinuxProcessListTool(),
        new LinuxSwapUsageTool(),
        new LinuxIoUsageTool(),
        new LinuxProcessInspectTool(),
        new LinuxProcessStopTool(),
        new LinuxProcessKillTool(),
    ];
}
