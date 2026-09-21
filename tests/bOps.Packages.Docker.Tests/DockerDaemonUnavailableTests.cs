// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Docker.DotNet;

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// A daemon that cannot be reached (ADR-0033): every Docker tool turns that into a failed result the runtime can report, never an
/// uncaught exception. The endpoint points at nothing, so no daemon is needed and the tests run everywhere; the connection timeout
/// is short so a missing named pipe does not wait out its default.
/// </summary>
public sealed class DockerDaemonUnavailableTests
{
    private sealed class NothingListeningFactory : IDockerClientFactory
    {
        private static readonly Uri Endpoint = OperatingSystem.IsWindows()
            ? new Uri("npipe://./pipe/bops_test_no_such_pipe")
            : new Uri("unix:///nonexistent-bops-test/docker.sock");

        public DockerClient Create()
        {
            using var configuration = new DockerClientConfiguration(
                Endpoint, credentials: null, defaultTimeout: TimeSpan.FromSeconds(5), namedPipeConnectTimeout: TimeSpan.FromMilliseconds(500));
            return configuration.CreateClient();
        }
    }

    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = JsonValue.Create(value);
        }

        return ToolArguments.FromJson(json);
    }

    public static TheoryData<string> ToolNames => new(
        new DockerToolProvider(new NothingListeningFactory()).GetTools().Select(tool => tool.Manifest.Name).ToArray());

    private static ToolArguments ArgumentsFor(string tool) => tool switch
    {
        "docker.containers" or "docker.images" or "docker.networks" or "docker.volumes" => ToolArguments.Empty,
        "docker.inspect" or "docker.logs" or "docker.start" or "docker.stop" or "docker.restart" => Args(("container", "some-container")),
        "docker.image.inspect" or "docker.image.pull" or "docker.image.remove" => Args(("image", "alpine:3.20")),
        "docker.image.tag" => Args(("source", "alpine:3.20"), ("image", "bops-test/tag:1")),
        "docker.volume.inspect" or "docker.volume.create" or "docker.volume.remove" => Args(("volume", "bops-test-volume")),
        "docker.build" => Args(("context", Path.GetFullPath(Directory.GetCurrentDirectory())), ("image", "bops-test/build:1")),
        _ => throw new InvalidOperationException($"No arguments defined for {tool}."),
    };

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task EveryTool_ReportsAnUnreachableDaemonAsAFailedResult(string name)
    {
        var tools = new DockerToolProvider(
            new NothingListeningFactory(),
            new DockerBuildOptions { Contexts = [Path.GetFullPath(Directory.GetCurrentDirectory())] }).GetTools();
        var tool = tools.Single(candidate => candidate.Manifest.Name == name);

        var result = await tool.ExecuteAsync(ArgumentsFor(name));

        // A build needs a Dockerfile in the context; without one it is refused before the daemon is asked, which is also a failed result.
        Assert.False(result.Succeeded, name);
        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        if (name != "docker.build")
        {
            Assert.Contains("could not be reached", result.ErrorMessage, StringComparison.Ordinal);
        }

        Assert.True(result.ErrorMessage!.Length < 2_000);
    }

    [Fact]
    public async Task TheCapabilityProbe_SaysTheDaemonIsNotAvailable_NeverThrows()
    {
        Assert.False(await DockerCapability.IsAvailableAsync(new NothingListeningFactory(), CancellationToken.None));
    }
}
