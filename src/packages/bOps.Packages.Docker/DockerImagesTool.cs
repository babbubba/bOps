using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>Lists every locally present image: id, repo:tag, and size. Read-risk.</summary>
public sealed class DockerImagesTool(IDockerClientFactory clientFactory) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.images",
        Description = "Lists every image present on this machine: id, repository:tag, and size in megabytes.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        using var client = clientFactory.Create();

        try
        {
            var images = await client.Images.ListImagesAsync(new ImagesListParameters(), ct);
            if (images.Count == 0)
            {
                return ToolCallResult.Success("(no images)");
            }

            var lines = images.Select(i =>
                $"{ShortId(i.ID)} {(i.RepoTags is { Count: > 0 } tags ? string.Join(',', tags) : "<none>")} {i.Size / (1024 * 1024)}MB");
            return ToolCallResult.Success(string.Join('\n', lines));
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not list images: {ex.Message}");
        }
    }

    private static string ShortId(string id)
    {
        var withoutPrefix = id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;
        return withoutPrefix.Length <= 12 ? withoutPrefix : withoutPrefix[..12];
    }
}
