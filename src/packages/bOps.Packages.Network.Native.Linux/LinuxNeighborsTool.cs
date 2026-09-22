// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>Collects <c>network.neighbors</c> on Linux via <c>ip -j neighbor show</c> (V1.3-D, ADR-0035).</summary>
public sealed class LinuxNeighborsTool() : NeighborsToolBase("linux")
{
    protected override Task<NeighborsSnapshot> CollectAsync(CancellationToken ct) => LinuxNeighborCollector.CollectAsync(ct);
}
