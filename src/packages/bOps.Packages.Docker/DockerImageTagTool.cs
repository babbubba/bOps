// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>
/// Creates one new reference (<c>image</c>) for one existing local image (<c>source</c>) (ADR-0033). <see cref="RiskLevel.Low"/>:
/// it changes no data and is undone by <c>docker.image.remove</c>. An existing tag that names a different image is never moved:
/// the call is refused, and the operator removes the old tag first. Verified via <c>docker.image.inspect</c>, which must list the
/// new reference among the image's tags. The argument is called <c>image</c> because the runtime carries verification arguments
/// by name.
/// </summary>
public sealed class DockerImageTagTool(IDockerClientFactory clientFactory) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.image.tag",
        Description = "Adds one new repository:tag reference to an existing local image. Refuses to move a tag that already names a different image.",
        Risk = RiskLevel.Low,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters =
        [
            new ToolParameter("source", ToolParameterType.String, "The existing local image: a reference or an image id."),
            new ToolParameter("image", ToolParameterType.String, "The new reference to create, repository:tag."),
        ],
        Verification = new VerificationSpec("docker.image.inspect", ["image"], "Confirms the new reference exists and is one of the image's tags."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var source = arguments.GetRequired<string>("source");
        var image = arguments.GetRequired<string>("image");

        if (!DockerImageReference.TryParse(source, out var sourceReference, out var sourceError))
        {
            return ToolCallResult.Failure($"source: {sourceError}");
        }

        if (!DockerImageReference.TryParseName(image, out var target, out var targetError))
        {
            return ToolCallResult.Failure($"image: {targetError}");
        }

        if (target!.Digest is not null)
        {
            return ToolCallResult.Failure("image: a new reference is repository:tag, not a digest.");
        }

        using var client = clientFactory.Create();
        try
        {
            var sourceInspect = await client.Images.InspectImageAsync(sourceReference!.Familiar, ct);

            var existing = await TryInspectAsync(client, target.Familiar, ct);
            if (existing is not null)
            {
                return string.Equals(existing.ID, sourceInspect.ID, StringComparison.Ordinal)
                    ? Result(sourceReference, target, sourceInspect.ID, alreadyPresent: true)
                    : ToolCallResult.Failure(
                        $"'{target.Familiar}' already names a different image ({DockerFailure.Bound(existing.ID)}); it is not moved. Remove that tag first if that is what you intend.");
            }

            await client.Images.TagImageAsync(
                sourceReference.Familiar,
                new ImageTagParameters { RepositoryName = target.Repository, Tag = target.Tag },
                ct);
            return Result(sourceReference, target, sourceInspect.ID, alreadyPresent: false);
        }
        catch (DockerApiException ex) when (DockerFailure.IsNotFound(ex))
        {
            return ToolCallResult.Failure($"No such image: '{DockerFailure.Bound(source)}'.");
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not tag '{DockerFailure.Bound(source)}' as '{DockerFailure.Bound(image)}': {DockerFailure.Describe(ex)}");
        }
        catch (Exception ex) when (DockerFailure.IsUnreachable(ex))
        {
            return ToolCallResult.Failure(DockerFailure.Unreachable(ex));
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originalArguments);
        var wanted = originalArguments.TryGet<string>("image", out var image)
            && DockerImageReference.TryParseName(image, out var reference, out _)
                ? reference!.Familiar
                : null;

        return Task.FromResult(DockerImageOutput.Verify(
            verificationToolResult,
            "tag",
            state =>
            {
                if (!state.Exists)
                {
                    return "docker.image.inspect reports the new reference does not exist.";
                }

                return wanted is null || state.Tags.Contains(wanted, StringComparer.Ordinal)
                    ? null
                    : $"docker.image.inspect does not list '{wanted}' among the image's tags.";
            }));
    }

    private static ToolCallResult Result(DockerImageReference source, DockerImageReference target, string id, bool alreadyPresent) =>
        ToolCallResult.Success(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["source"] = source.Familiar,
            ["image"] = target.Familiar,
            ["id"] = id,
            ["created"] = !alreadyPresent,
        }.ToJsonString());

    private static async Task<ImageInspectResponse?> TryInspectAsync(DockerClient client, string reference, CancellationToken ct)
    {
        try
        {
            return await client.Images.InspectImageAsync(reference, ct);
        }
        catch (DockerApiException ex) when (DockerFailure.IsNotFound(ex))
        {
            return null;
        }
    }
}
