// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>Collects <c>network.routes</c> on Linux via <c>ip -j route show</c> (V1.3-D, ADR-0035).</summary>
public sealed class LinuxRoutesTool() : RoutesToolBase("linux")
{
    protected override Task<RoutesSnapshot> CollectAsync(CancellationToken ct) => LinuxRouteCollector.CollectAsync(ct);
}
