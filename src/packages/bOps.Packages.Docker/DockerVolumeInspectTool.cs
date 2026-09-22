// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Docker.DotNet;

namespace bOps.Packages.Docker;

/// <summary>
/// Reports whether one named volume exists and its bounded metadata (ADR-0033). Read-risk. A missing volume is
/// <c>exists: false</c> and a successful read; a daemon error is a failed one. Also the verification target of
/// <c>docker.volume.create</c> and <c>docker.volume.remove</c>.
/// </summary>
public sealed class DockerVolumeInspectTool(IDockerClientFactory clientFactory) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.volume.inspect",
        Description = "Reports whether one Docker volume exists and, if so, its driver, scope, creation time, labels and, when the daemon reports it, size and reference count. " +
            "A missing volume is reported as exists=false, not as an error.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [new ToolParameter("volume", ToolParameterType.String, "The volume name.")],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var volume = arguments.GetRequired<string>("volume");
        if (!DockerNames.TryValidateVolumeName(volume, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        using var client = clientFactory.Create();
        try
        {
            return ToolCallResult.Success(DockerVolumeOutput.Present(await client.Volumes.InspectAsync(volume, ct)));
        }
        catch (DockerApiException ex) when (DockerFailure.IsNotFound(ex))
        {
            return ToolCallResult.Success(DockerVolumeOutput.Missing(volume));
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not inspect volume '{volume}': {DockerFailure.Describe(ex)}");
        }
        catch (Exception ex) when (DockerFailure.IsUnreachable(ex))
        {
            return ToolCallResult.Failure(DockerFailure.Unreachable(ex));
        }
    }
}
