// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>
/// Pulls one image by a typed reference (ADR-0033). <see cref="RiskLevel.Medium"/>: it downloads and stores data, and is undone
/// by <c>docker.image.remove</c>. Anonymous registry access only: registry credentials are not supported in this version and
/// never appear in arguments, output, audit or telemetry. The daemon's progress stream is consumed here and reduced to its last
/// error, so nothing unbounded is returned. Verified via <c>docker.image.inspect</c>.
/// </summary>
public sealed class DockerImagePullTool(IDockerClientFactory clientFactory) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.image.pull",
        Description = "Pulls one image from its registry (anonymous access only) and returns the local id and digests.",
        Risk = RiskLevel.Medium,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters =
        [
            new ToolParameter("image", ToolParameterType.String, "The image name to pull: repository[:tag] or repository@sha256:digest, with an optional registry host."),
            new ToolParameter("platform", ToolParameterType.String, "The platform to pull, os/architecture[/variant], for example linux/amd64. Defaults to the daemon's.", Required: false),
        ],
        Verification = new VerificationSpec("docker.image.inspect", ["image"], "Confirms the image is present locally afterwards."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var image = arguments.GetRequired<string>("image");
        if (!DockerImageReference.TryParseName(image, out var reference, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        string? platform = null;
        if (arguments.TryGet<string>("platform", out var requestedPlatform) && requestedPlatform is not null)
        {
            if (!DockerNames.TryValidatePlatform(requestedPlatform, out var platformError))
            {
                return ToolCallResult.Failure(platformError!);
            }

            platform = requestedPlatform;
        }

        using var client = clientFactory.Create();
        try
        {
            string? streamedError = null;
            var progress = new InlineProgress<JSONMessage>(message =>
            {
                if (message.Error is not null || !string.IsNullOrEmpty(message.ErrorMessage))
                {
                    streamedError = message.Error?.Message ?? message.ErrorMessage;
                }
            });

            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = reference!.Repository, Tag = reference.Digest ?? reference.Tag, Platform = platform },
                authConfig: null,
                progress,
                ct);

            if (streamedError is not null)
            {
                return ToolCallResult.Failure($"Could not pull '{DockerFailure.Bound(image)}': {DockerFailure.Bound(streamedError)}");
            }

            var pulled = await client.Images.InspectImageAsync(reference.Familiar, ct);
            return ToolCallResult.Success(new JsonObject
            {
                ["schemaVersion"] = 1,
                ["image"] = reference.Familiar,
                ["id"] = pulled.ID,
                ["digests"] = DockerImageOutput.ToArray(pulled.RepoDigests, DockerImageOutput.MaximumDigests),
                ["platform"] = platform,
            }.ToJsonString());
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure(PullFailure(image, ex));
        }
        catch (Exception ex) when (DockerFailure.IsUnreachable(ex))
        {
            return ToolCallResult.Failure(DockerFailure.Unreachable(ex));
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(DockerImageOutput.Verify(
            verificationToolResult,
            "pull",
            state => state.Exists ? null : "docker.image.inspect reports the image is not present."));

    private static string PullFailure(string image, DockerApiException exception)
    {
        var text = DockerFailure.Describe(exception);
        var hint = text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || text.Contains("denied", StringComparison.OrdinalIgnoreCase)
            ? " bOps pulls anonymously; registry credentials are not supported in this version."
            : string.Empty;
        return $"Could not pull '{DockerFailure.Bound(image)}': {text}{hint}";
    }
}
