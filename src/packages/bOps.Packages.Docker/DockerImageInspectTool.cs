// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Docker.DotNet;

namespace bOps.Packages.Docker;

/// <summary>
/// Reports whether one image is present and what it is, as bounded JSON (ADR-0033). Read-risk. A missing image is
/// <c>exists: false</c> and a successful read; a daemon error is a failed one. Also the verification target of
/// <c>docker.image.pull</c>, <c>docker.image.tag</c>, <c>docker.image.remove</c> and <c>docker.build</c>.
/// </summary>
public sealed class DockerImageInspectTool(IDockerClientFactory clientFactory) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.image.inspect",
        Description = "Reports whether one image is present on this machine and, if so, its id, tags, digests, size, creation time and platform. " +
            "A missing image is reported as exists=false, not as an error.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [new ToolParameter("image", ToolParameterType.String, "The image reference (repository[:tag] or repository@sha256:digest) or the image id.")],
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
            var inspected = await client.Images.InspectImageAsync(reference!.Familiar, ct);
            return ToolCallResult.Success(DockerImageOutput.Present(image, inspected));
        }
        catch (DockerApiException ex) when (DockerFailure.IsNotFound(ex))
        {
            return ToolCallResult.Success(DockerImageOutput.Missing(image));
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not inspect image '{DockerFailure.Bound(image)}': {DockerFailure.Describe(ex)}");
        }
        catch (Exception ex) when (DockerFailure.IsUnreachable(ex))
        {
            return ToolCallResult.Failure(DockerFailure.Unreachable(ex));
        }
    }
}
