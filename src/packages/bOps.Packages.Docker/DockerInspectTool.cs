// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Docker.DotNet;

namespace bOps.Packages.Docker;

/// <summary>
/// Reports one container's id, image, and current state as a single-line JSON observation
/// (<see cref="DockerInspectOutput"/>). Read-risk. Also the verification target
/// <see cref="DockerStartTool"/>, <see cref="DockerStopTool"/> and <see cref="DockerRestartTool"/>
/// declare.
/// </summary>
public sealed class DockerInspectTool(IDockerClientFactory clientFactory) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.inspect",
        Description = "Reports one container's id, image, and current state (running, exited, created, ...).",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [new ToolParameter("container", ToolParameterType.String, "The container's name or id.")],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var container = arguments.GetRequired<string>("container");

        using var client = clientFactory.Create();

        try
        {
            var response = await client.Containers.InspectContainerAsync(container, ct);
            return ToolCallResult.Success(DockerInspectOutput.Build(response));
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not inspect container '{container}': {ex.Message}");
        }
    }
}
