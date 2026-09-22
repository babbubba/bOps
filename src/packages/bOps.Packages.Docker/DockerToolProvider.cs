// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Docker;

/// <summary>
/// Contributes every <c>docker.*</c> tool, all sharing one <see cref="IDockerClientFactory"/>. The build and volume options are
/// optional so existing callers keep working; without build contexts <c>docker.build</c> is hidden by its capability
/// (ADR-0033).
/// </summary>
public sealed class DockerToolProvider(
    IDockerClientFactory clientFactory,
    DockerBuildOptions? buildOptions = null,
    DockerVolumeOptions? volumeOptions = null) : IToolProvider
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
        new DockerImageInspectTool(clientFactory),
        new DockerImagePullTool(clientFactory),
        new DockerImageTagTool(clientFactory),
        new DockerImageRemoveTool(clientFactory),
        new DockerBuildTool(clientFactory, buildOptions),
        new DockerVolumesTool(clientFactory),
        new DockerVolumeInspectTool(clientFactory),
        new DockerVolumeCreateTool(clientFactory, volumeOptions),
        new DockerVolumeRemoveTool(clientFactory),
    ];
}
