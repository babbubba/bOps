using bOps.Abstractions;

namespace bOps.Packages.Docker;

/// <summary>Contributes every <c>docker.*</c> tool, all sharing one <see cref="IDockerClientFactory"/>.</summary>
public sealed class DockerToolProvider(IDockerClientFactory clientFactory) : IToolProvider
{
    public IEnumerable<ITool> GetTools() =>
    [
        new DockerContainersTool(clientFactory),
        new DockerImagesTool(clientFactory),
        new DockerNetworksTool(clientFactory),
        new DockerInspectTool(clientFactory),
        new DockerLogsTool(clientFactory),
        new DockerStartTool(clientFactory),
        new DockerStopTool(clientFactory),
        new DockerRestartTool(clientFactory),
    ];
}
