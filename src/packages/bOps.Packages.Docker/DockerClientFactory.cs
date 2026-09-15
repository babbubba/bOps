// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using Docker.DotNet;

namespace bOps.Packages.Docker;

/// <summary>
/// Creates a connection to the local Docker daemon. Never a live <see cref="DockerClient"/>
/// crosses the tool boundary — a tool constructs one, uses it, and disposes it within
/// <c>ExecuteAsync</c>; only a serializable <see cref="bOps.Abstractions.ToolCallResult"/> ever
/// leaves (agentic/01-architecture-rules.md, rule A2).
/// </summary>
public interface IDockerClientFactory
{
    /// <summary>Creates a client connected to the local daemon. The caller owns disposal.</summary>
    DockerClient Create();
}

/// <summary>
/// The default <see cref="IDockerClientFactory"/>: connects over the platform's local Docker
/// endpoint — a named pipe on Windows, a Unix socket on Linux — unless overridden.
/// </summary>
public sealed class DockerClientFactory(string? endpoint = null) : IDockerClientFactory
{
    private readonly Uri _endpoint = new(endpoint ?? DefaultEndpoint);

    /// <inheritdoc />
    public DockerClient Create()
    {
        using var configuration = new DockerClientConfiguration(_endpoint);
        return configuration.CreateClient();
    }

    private static string DefaultEndpoint => OperatingSystem.IsWindows()
        ? "npipe://./pipe/docker_engine"
        : "unix:///var/run/docker.sock";
}
