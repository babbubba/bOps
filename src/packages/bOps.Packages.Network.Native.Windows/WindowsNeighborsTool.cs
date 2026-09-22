// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>Collects <c>network.neighbors</c> on Windows (V1.3-D, ADR-0035).</summary>
public sealed class WindowsNeighborsTool() : NeighborsToolBase("windows")
{
    protected override Task<NeighborsSnapshot> CollectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsNeighborCollector.Collect());
    }
}
