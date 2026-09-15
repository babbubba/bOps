// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Network;

/// <summary>Contributes every <c>network.*</c> tool. All Read-risk; none needs configuration.</summary>
public sealed class NetworkToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() =>
    [
        new NetworkInterfacesTool(),
        new NetworkDnsTool(),
        new NetworkPingTool(),
        new NetworkConnectionsTool(),
    ];
}
