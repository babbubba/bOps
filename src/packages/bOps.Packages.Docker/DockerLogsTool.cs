// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>Reads recent stdout/stderr log lines from a container. Read-risk.</summary>
public sealed class DockerLogsTool(IDockerClientFactory clientFactory) : ITool
{
    private const int DefaultTail = 200;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.logs",
        Description = "Reads recent stdout/stderr log lines from a container.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters =
        [
            new ToolParameter("container", ToolParameterType.String, "The container's name or id."),
            new ToolParameter("tail", ToolParameterType.Integer, "Number of most recent lines to return. Defaults to 200.", Required: false),
        ],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var container = arguments.GetRequired<string>("container");
        var tail = arguments.TryGet<int>("tail", out var requested) && requested > 0 ? requested : DefaultTail;

        using var client = clientFactory.Create();

        try
        {
            var parameters = new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Tail = tail.ToString(CultureInfo.InvariantCulture),
            };

            using var stream = await client.Containers.GetContainerLogsAsync(container, tty: false, parameters, ct);
            var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);
            var combined = string.Concat(stdout, stderr);
            return ToolCallResult.Success(string.IsNullOrEmpty(combined) ? "(no output)" : combined);
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not read logs for '{container}': {ex.Message}");
        }
    }
}
