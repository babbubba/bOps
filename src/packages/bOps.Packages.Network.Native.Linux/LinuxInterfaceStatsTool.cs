// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>Collects <c>network.interface_stats</c> on Linux (V1.3-D, ADR-0035).</summary>
public sealed class LinuxInterfaceStatsTool() : InterfaceStatsToolBase("linux")
{
    protected override Task<IReadOnlyList<InterfaceCounterSample>> SampleAsync(string? interfaceName, CancellationToken ct) =>
        LinuxInterfaceStatsCollector.SampleAsync(interfaceName, ct);
}
