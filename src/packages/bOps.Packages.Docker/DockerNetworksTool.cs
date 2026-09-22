// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>Lists every Docker network: id, name, driver, and scope. Read-risk.</summary>
public sealed class DockerNetworksTool(IDockerClientFactory clientFactory) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.networks",
        Description = "Lists every Docker network: id, name, driver, and scope.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        using var client = clientFactory.Create();

        try
        {
            var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters(), ct);
            if (networks.Count == 0)
            {
                return ToolCallResult.Success("(no networks)");
            }

            var lines = networks.Select(n => $"{ShortId(n.ID)} {n.Name} {n.Driver} [{n.Scope}]");
            return ToolCallResult.Success(string.Join('\n', lines));
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not list networks: {ex.Message}");
        }
        catch (Exception ex) when (DockerFailure.IsUnreachable(ex))
        {
            return ToolCallResult.Failure(DockerFailure.Unreachable(ex));
        }
    }

    private static string ShortId(string id) => id.Length <= 12 ? id : id[..12];
}
