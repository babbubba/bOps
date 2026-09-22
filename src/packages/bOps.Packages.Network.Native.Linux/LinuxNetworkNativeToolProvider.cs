// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>Contributes every native network.* tool on Linux (V1.3-D, ADR-0035).</summary>
public sealed class LinuxNetworkNativeToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() =>
    [
        new LinuxSocketsTool(),
        new LinuxRoutesTool(),
        new LinuxNeighborsTool(),
        new LinuxInterfaceStatsTool(),
        new DnsQueryTool("linux"),
        new TracerouteTool("linux"),
        new NtpProbeTool("linux"),
    ];
}
