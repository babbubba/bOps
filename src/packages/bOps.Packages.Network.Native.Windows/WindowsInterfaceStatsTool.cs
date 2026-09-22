// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>Collects <c>network.interface_stats</c> on Windows (V1.3-D, ADR-0035).</summary>
public sealed class WindowsInterfaceStatsTool() : InterfaceStatsToolBase("windows")
{
    protected override Task<IReadOnlyList<InterfaceCounterSample>> SampleAsync(string? interfaceName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsInterfaceStatsCollector.Sample(interfaceName));
    }
}
