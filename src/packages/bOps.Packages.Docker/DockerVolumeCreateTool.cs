// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>
/// Creates one named, empty volume (ADR-0033). <see cref="RiskLevel.Low"/>: it holds no data yet and is undone by
/// <c>docker.volume.remove</c>. The driver must be on <see cref="DockerVolumeOptions.AllowedDrivers"/> (<c>local</c> unless the
/// operator configures more); there is no options bag and no labels. Creating a volume that already exists with the same
/// driver succeeds and says <c>created: false</c>; a different driver is refused. Verified via <c>docker.volume.inspect</c>.
/// </summary>
public sealed class DockerVolumeCreateTool(IDockerClientFactory clientFactory, DockerVolumeOptions? options = null) : IVerifiableTool
{
    private readonly DockerVolumeOptions _options = options ?? new DockerVolumeOptions();

    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.volume.create",
        Description = "Creates one named, empty Docker volume with an allowed driver (local unless the operator allows more). No options, no labels.",
        Risk = RiskLevel.Low,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters =
        [
            new ToolParameter("volume", ToolParameterType.String, "The volume name: 2 to 128 letters, digits, '_', '.' or '-', starting with a letter or digit."),
            new ToolParameter("driver", ToolParameterType.String, "The volume driver. Defaults to local; only drivers the operator has allowed are accepted.", Required: false),
        ],
        Verification = new VerificationSpec("docker.volume.inspect", ["volume"], "Confirms the volume exists afterwards."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var volume = arguments.GetRequired<string>("volume");
        if (!DockerNames.TryValidateVolumeName(volume, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        var allowed = _options.AllowedDrivers;
        var driver = allowed.Contains(DockerVolumeOptions.DefaultDriver, StringComparer.Ordinal) ? DockerVolumeOptions.DefaultDriver : allowed[0];
        if (arguments.TryGet<string>("driver", out var requested) && requested is not null)
        {
            if (!allowed.Contains(requested, StringComparer.Ordinal))
            {
                return ToolCallResult.Failure($"The volume driver '{DockerFailure.Bound(requested)}' is not allowed. Allowed: {string.Join(", ", allowed)}.");
            }

            driver = requested;
        }

        using var client = clientFactory.Create();
        try
        {
            VolumeResponse? existing = null;
            try
            {
                existing = await client.Volumes.InspectAsync(volume, ct);
            }
            catch (DockerApiException ex) when (DockerFailure.IsNotFound(ex))
            {
                // Not there yet: create it below.
            }

            if (existing is not null)
            {
                return string.Equals(existing.Driver, driver, StringComparison.Ordinal)
                    ? Result(volume, driver, created: false)
                    : ToolCallResult.Failure($"Volume '{volume}' already exists with driver '{DockerFailure.Bound(existing.Driver)}', not '{driver}'.");
            }

            await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = volume, Driver = driver }, ct);
            return Result(volume, driver, created: true);
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not create volume '{volume}': {DockerFailure.Describe(ex)}");
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
            "creation",
            state => state.Exists ? null : "docker.volume.inspect reports the volume does not exist."));

    private static ToolCallResult Result(string volume, string driver, bool created) =>
        ToolCallResult.Success(new JsonObject { ["schemaVersion"] = 1, ["volume"] = volume, ["driver"] = driver, ["created"] = created }.ToJsonString());
}
