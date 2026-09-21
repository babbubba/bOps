// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>
/// Removes exactly the named image reference or id (ADR-0033). <see cref="RiskLevel.High"/> and always approved by a human: an
/// image built locally cannot be pulled back. Never forced and never pruned: an image a container uses, or an image with other
/// tags when it is named by id, is refused by the daemon and reported, and untagged parent images are left alone. A missing
/// image is a failure, not a silent success. Verified via <c>docker.image.inspect</c>, which must report it absent.
/// </summary>
public sealed class DockerImageRemoveTool(IDockerClientFactory clientFactory) : IVerifiableTool
{
    private const int MaximumListed = 20;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.image.remove",
        Description = "Removes one image reference or image id. Never forced, never pruned: an image in use is refused.",
        Risk = RiskLevel.High,
        RequiresExplicitApproval = true,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [new ToolParameter("image", ToolParameterType.String, "The image reference or image id to remove.")],
        Verification = new VerificationSpec("docker.image.inspect", ["image"], "Confirms the image reference no longer exists."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var image = arguments.GetRequired<string>("image");
        if (!DockerImageReference.TryParse(image, out var reference, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        using var client = clientFactory.Create();
        try
        {
            var removed = await client.Images.DeleteImageAsync(
                reference!.Familiar,
                new ImageDeleteParameters { Force = false, NoPrune = true },
                ct);

            var untagged = new List<string>();
            var deleted = new List<string>();
            foreach (var item in removed ?? [])
            {
                if (item.TryGetValue("Untagged", out var tag))
                {
                    untagged.Add(tag);
                }

                if (item.TryGetValue("Deleted", out var layer))
                {
                    deleted.Add(layer);
                }
            }

            return ToolCallResult.Success(new JsonObject
            {
                ["schemaVersion"] = 1,
                ["image"] = reference.Familiar,
                ["untagged"] = DockerImageOutput.ToArray(untagged, MaximumListed),
                ["deleted"] = DockerImageOutput.ToArray(deleted, MaximumListed),
                ["truncated"] = untagged.Count > MaximumListed || deleted.Count > MaximumListed,
            }.ToJsonString());
        }
        catch (DockerApiException ex) when (DockerFailure.IsNotFound(ex))
        {
            return ToolCallResult.Failure($"No such image: '{DockerFailure.Bound(image)}'.");
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not remove image '{DockerFailure.Bound(image)}': {DockerFailure.Describe(ex)}");
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
            "removal",
            state => state.Exists ? "docker.image.inspect reports the image still exists." : null));
}
