// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>
/// Starts a stopped container. <see cref="RiskLevel.Medium"/> (archived original plan §11). Verified via
/// <c>docker.inspect</c> on the same container: <c>status == "running"</c> afterwards confirms it,
/// anything else refutes it.
/// </summary>
public sealed class DockerStartTool(IDockerClientFactory clientFactory) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.start",
        Description = "Starts a stopped container.",
        Risk = RiskLevel.Medium,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [new ToolParameter("container", ToolParameterType.String, "The container's name or id.")],
        Verification = new VerificationSpec(
            "docker.inspect", ["container"], "Confirms the container's status is \"running\" afterwards."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var container = arguments.GetRequired<string>("container");

        using var client = clientFactory.Create();

        try
        {
            await client.Containers.StartContainerAsync(container, new ContainerStartParameters(), ct);
            return ToolCallResult.Success($"Requested start of container '{container}'.");
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not start container '{container}': {ex.Message}");
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verificationToolResult);

        if (!verificationToolResult.Succeeded)
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive, $"Could not confirm the start: {verificationToolResult.ErrorMessage}"));
        }

        var status = DockerInspectOutput.TryReadStatus(verificationToolResult.Output);
        return Task.FromResult(status switch
        {
            null => new VerificationOutcome(VerificationStatus.Inconclusive, "docker.inspect's output could not be read."),
            "running" => new VerificationOutcome(VerificationStatus.Confirmed, null),
            _ => new VerificationOutcome(VerificationStatus.Refuted, $"docker.inspect reports status \"{status}\", not \"running\"."),
        });
    }
}
