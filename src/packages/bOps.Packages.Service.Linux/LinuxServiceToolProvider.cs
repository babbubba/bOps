// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Service.Linux;

/// <summary>Contributes the Linux implementations of every <c>service.*</c> tool.</summary>
public sealed class LinuxServiceToolProvider : IToolProvider
{
    /// <inheritdoc />
    public IEnumerable<ITool> GetTools() =>
    [
        new LinuxServiceListTool(),
        new LinuxServiceStatusTool(),
        new LinuxServiceStartTool(),
        new LinuxServiceStopTool(),
        new LinuxServiceRestartTool(),
    ];
}
