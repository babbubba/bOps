// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.NetworkInformation;
using System.Text;
using bOps.Abstractions;

namespace bOps.Packages.Network;

/// <summary>Lists every network interface: name, type, operational status, and its IP addresses. Read-risk.</summary>
public sealed class NetworkInterfacesTool : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "network.interfaces",
        Description = "Lists every network interface on this machine: name, type, operational status, and IP addresses.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            if (interfaces.Length == 0)
            {
                return Task.FromResult(ToolCallResult.Success("(no interfaces)"));
            }

            var lines = interfaces.Select(FormatInterface);
            return Task.FromResult(ToolCallResult.Success(string.Join('\n', lines)));
        }
        catch (NetworkInformationException ex)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not enumerate network interfaces: {ex.Message}"));
        }
    }

    private static string FormatInterface(NetworkInterface nic)
    {
        var addresses = nic.GetIPProperties().UnicastAddresses
            .Select(a => a.Address.ToString());

        var builder = new StringBuilder();
        builder.Append(nic.Name).Append(" (").Append(nic.NetworkInterfaceType).Append(", ")
            .Append(nic.OperationalStatus).Append("): ");
        builder.Append(string.Join(", ", addresses) is { Length: > 0 } joined ? joined : "(no addresses)");
        return builder.ToString();
    }
}
