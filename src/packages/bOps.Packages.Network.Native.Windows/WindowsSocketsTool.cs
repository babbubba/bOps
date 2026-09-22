// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>Collects <c>network.sockets</c> on Windows (V1.3-D, ADR-0035).</summary>
public sealed class WindowsSocketsTool() : SocketsToolBase("windows")
{
    protected override Task<SocketsSnapshot> CollectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsSocketCollector.Collect());
    }
}
