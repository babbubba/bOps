// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Linux;

/// <summary>Collects <c>network.sockets</c> on Linux (V1.3-D, ADR-0035).</summary>
public sealed class LinuxSocketsTool() : SocketsToolBase("linux")
{
    protected override Task<SocketsSnapshot> CollectAsync(CancellationToken ct) => LinuxSocketCollector.CollectAsync(ct);
}
