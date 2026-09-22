// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Conformance;

namespace bOps.Packages.Network.Native.Windows.Tests;

public sealed class WindowsNetworkNativeConformanceTests
{
    [WindowsOnlyFact]
    public void SocketsManifest_IsWellFormed() => NetworkNativeConformance.AssertManifestIsWellFormed(new WindowsSocketsTool(), "windows");

    [WindowsOnlyFact]
    public void RoutesManifest_IsWellFormed() => NetworkNativeConformance.AssertManifestIsWellFormed(new WindowsRoutesTool(), "windows");

    [WindowsOnlyFact]
    public void NeighborsManifest_IsWellFormed() => NetworkNativeConformance.AssertManifestIsWellFormed(new WindowsNeighborsTool(), "windows");

    [WindowsOnlyFact]
    public void InterfaceStatsManifest_IsWellFormed() => NetworkNativeConformance.AssertManifestIsWellFormed(new WindowsInterfaceStatsTool(), "windows");

    [WindowsOnlyFact]
    public Task Sockets_MatchesSharedSchema() => NetworkNativeConformance.AssertSocketsShapeAsync(new WindowsSocketsTool());

    [WindowsOnlyFact]
    public Task Routes_MatchesSharedSchema() => NetworkNativeConformance.AssertRoutesShapeAsync(new WindowsRoutesTool());

    [WindowsOnlyFact]
    public Task Neighbors_MatchesSharedSchema() => NetworkNativeConformance.AssertNeighborsShapeAsync(new WindowsNeighborsTool());

    [WindowsOnlyFact]
    public Task InterfaceStats_MatchesSharedSchema() => NetworkNativeConformance.AssertInterfaceStatsShapeAsync(new WindowsInterfaceStatsTool());
}
