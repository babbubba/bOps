// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>Collects <c>network.routes</c> on Windows (V1.3-D, ADR-0035).</summary>
public sealed class WindowsRoutesTool() : RoutesToolBase("windows")
{
    protected override Task<RoutesSnapshot> CollectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsRouteCollector.Collect());
    }
}
