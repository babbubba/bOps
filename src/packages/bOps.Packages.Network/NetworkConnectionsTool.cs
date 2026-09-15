// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.NetworkInformation;
using bOps.Abstractions;

namespace bOps.Packages.Network;

/// <summary>Lists active TCP connections: local endpoint, remote endpoint, and state. Read-risk.</summary>
public sealed class NetworkConnectionsTool : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "network.connections",
        Description = "Lists active TCP connections: local endpoint, remote endpoint, and state.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        try
        {
            var connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            if (connections.Length == 0)
            {
                return Task.FromResult(ToolCallResult.Success("(no active TCP connections)"));
            }

            var lines = connections.Select(c => $"{c.LocalEndPoint} -> {c.RemoteEndPoint} [{c.State}]");
            return Task.FromResult(ToolCallResult.Success(string.Join('\n', lines)));
        }
        catch (NetworkInformationException ex)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not enumerate TCP connections: {ex.Message}"));
        }
    }
}
