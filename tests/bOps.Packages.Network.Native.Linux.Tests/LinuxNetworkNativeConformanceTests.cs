// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Conformance;

namespace bOps.Packages.Network.Native.Linux.Tests;

public sealed class LinuxNetworkNativeConformanceTests
{
    [LinuxOnlyFact]
    public void SocketsManifest_IsWellFormed() => NetworkNativeConformance.AssertManifestIsWellFormed(new LinuxSocketsTool(), "linux");

    [LinuxOnlyFact]
    public void RoutesManifest_IsWellFormed() => NetworkNativeConformance.AssertManifestIsWellFormed(new LinuxRoutesTool(), "linux");

    [LinuxOnlyFact]
    public void NeighborsManifest_IsWellFormed() => NetworkNativeConformance.AssertManifestIsWellFormed(new LinuxNeighborsTool(), "linux");

    [LinuxOnlyFact]
    public void InterfaceStatsManifest_IsWellFormed() => NetworkNativeConformance.AssertManifestIsWellFormed(new LinuxInterfaceStatsTool(), "linux");

    [LinuxOnlyFact]
    public Task Sockets_MatchesSharedSchema() => NetworkNativeConformance.AssertSocketsShapeAsync(new LinuxSocketsTool());

    [LinuxOnlyFact]
    public Task Routes_MatchesSharedSchema() => NetworkNativeConformance.AssertRoutesShapeAsync(new LinuxRoutesTool());

    [LinuxOnlyFact]
    public Task Neighbors_MatchesSharedSchema() => NetworkNativeConformance.AssertNeighborsShapeAsync(new LinuxNeighborsTool());

    [LinuxOnlyFact]
    public Task InterfaceStats_MatchesSharedSchema() => NetworkNativeConformance.AssertInterfaceStatsShapeAsync(new LinuxInterfaceStatsTool());
}
