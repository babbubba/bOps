// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Docker.DotNet;

namespace bOps.Packages.Docker;

/// <summary>
/// Removes one named volume (ADR-0033). <see cref="RiskLevel.High"/> and always approved by a human: it permanently destroys
/// whatever the volume holds. Never forced and never pruned; a volume in use is refused by the daemon and reported, and a missing
/// volume is a failure, not a silent success. Verified via <c>docker.volume.inspect</c>, which must report it absent.
/// </summary>
public sealed class DockerVolumeRemoveTool(IDockerClientFactory clientFactory) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.volume.remove",
        Description = "Removes one named Docker volume and the data in it, permanently. Never forced, never pruned: a volume in use is refused.",
        Risk = RiskLevel.High,
        RequiresExplicitApproval = true,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [new ToolParameter("volume", ToolParameterType.String, "The volume name.")],
        Verification = new VerificationSpec("docker.volume.inspect", ["volume"], "Confirms the volume no longer exists."),
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
            await client.Volumes.RemoveAsync(volume, force: false, ct);
            return ToolCallResult.Success(new JsonObject { ["schemaVersion"] = 1, ["volume"] = volume, ["removed"] = true }.ToJsonString());
        }
        catch (DockerApiException ex) when (DockerFailure.IsNotFound(ex))
        {
            return ToolCallResult.Failure($"No such volume: '{volume}'.");
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not remove volume '{volume}': {DockerFailure.Describe(ex)}");
        }
        catch (Exception ex) when (DockerFailure.IsUnreachable(ex))
        {
            return ToolCallResult.Failure(DockerFailure.Unreachable(ex));
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(DockerVolumeOutput.Verify(
            verificationToolResult,
            "removal",
            state => state.Exists ? "docker.volume.inspect reports the volume still exists." : null));
}
