// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>Contributes every native network.* tool on Windows (V1.3-D, ADR-0035).</summary>
public sealed class WindowsNetworkNativeToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() =>
    [
        new WindowsSocketsTool(),
        new WindowsRoutesTool(),
        new WindowsNeighborsTool(),
        new WindowsInterfaceStatsTool(),
        new DnsQueryTool("windows"),
        new TracerouteTool("windows"),
        new NtpProbeTool("windows"),
    ];
}
