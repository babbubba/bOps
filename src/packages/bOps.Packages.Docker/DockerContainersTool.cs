using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>Lists every container, running or not: id, names, image, and status. Read-risk.</summary>
public sealed class DockerContainersTool(IDockerClientFactory clientFactory) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.containers",
        Description = "Lists every container (running or not): id, name, image, and status.",
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
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
            if (containers.Count == 0)
            {
                return ToolCallResult.Success("(no containers)");
            }

            var lines = containers.Select(c =>
                $"{ShortId(c.ID)} {string.Join(',', c.Names.Select(n => n.TrimStart('/')))} {c.Image} [{c.Status}]");
            return ToolCallResult.Success(string.Join('\n', lines));
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not list containers: {ex.Message}");
        }
    }

    private static string ShortId(string id) => id.Length <= 12 ? id : id[..12];
}
