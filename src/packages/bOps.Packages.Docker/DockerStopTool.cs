// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>
/// Stops a running container. <see cref="RiskLevel.Medium"/>. Verified via <c>docker.inspect</c>:
/// the mirror image of <see cref="DockerStartTool"/> — anything other than <c>"running"</c>
/// afterwards confirms it.
/// </summary>
public sealed class DockerStopTool(IDockerClientFactory clientFactory) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.stop",
        Description = "Stops a running container.",
        Risk = RiskLevel.Medium,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [new ToolParameter("container", ToolParameterType.String, "The container's name or id.")],
        Verification = new VerificationSpec(
            "docker.inspect", ["container"], "Confirms the container's status is no longer \"running\" afterwards."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var container = arguments.GetRequired<string>("container");

        using var client = clientFactory.Create();

        try
        {
            await client.Containers.StopContainerAsync(container, new ContainerStopParameters(), ct);
            return ToolCallResult.Success($"Requested stop of container '{container}'.");
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not stop container '{container}': {ex.Message}");
        }
        catch (Exception ex) when (DockerFailure.IsUnreachable(ex))
        {
            return ToolCallResult.Failure(DockerFailure.Unreachable(ex));
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verificationToolResult);

        if (!verificationToolResult.Succeeded)
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive, $"Could not confirm the stop: {verificationToolResult.ErrorMessage}"));
        }

        var status = DockerInspectOutput.TryReadStatus(verificationToolResult.Output);
        return Task.FromResult(status switch
        {
            null => new VerificationOutcome(VerificationStatus.Inconclusive, "docker.inspect's output could not be read."),
            "running" => new VerificationOutcome(VerificationStatus.Refuted, "docker.inspect reports the container is still running."),
            _ => new VerificationOutcome(VerificationStatus.Confirmed, null),
        });
    }
}
