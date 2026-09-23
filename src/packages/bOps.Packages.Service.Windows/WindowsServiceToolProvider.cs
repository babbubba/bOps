// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Service.Windows;

/// <summary>Contributes the Windows implementations of every <c>service.*</c> tool.</summary>
public sealed class WindowsServiceToolProvider : IToolProvider
{
    /// <inheritdoc />
    public IEnumerable<ITool> GetTools() =>
    [
        new WindowsServiceListTool(),
        new WindowsServiceStatusTool(),
        new WindowsServiceStartTool(),
        new WindowsServiceStopTool(),
        new WindowsServiceRestartTool(),
        new WindowsServiceConfigTool(),
        new WindowsServiceDependenciesTool(),
        new WindowsServiceEnableTool(),
        new WindowsServiceDisableTool(),
    ];
}
