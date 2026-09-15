// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// Creates a throwaway, uniquely named container against the real local Docker daemon for a test
/// to exercise, and always removes it afterwards — agentic/04-testing-rules.md: Docker is a
/// platform package, tested against a real daemon with real containers created by the test, never
/// mocked. Deliberately not created via xunit's <c>IAsyncLifetime</c>: that would run before a
/// <see cref="DockerAvailableFactAttribute"/> skip is known to the runner, so a skipped test must
/// never reach this class at all — every <c>[DockerAvailableFact]</c> test creates one explicitly,
/// inside its own body, and disposes it in a <c>finally</c>/<c>await using</c>.
/// </summary>
internal sealed class TestContainer : IAsyncDisposable
{
    private const string Image = "alpine";
    private const string Tag = "3.20";

    private readonly IDockerClientFactory _clientFactory;

    private TestContainer(IDockerClientFactory clientFactory, string name) => (_clientFactory, Name) = (clientFactory, name);

    public string Name { get; }

    public string Id { get; private set; } = string.Empty;

    public static async Task<TestContainer> CreateAsync(IDockerClientFactory clientFactory, CancellationToken ct = default)
    {
        var container = new TestContainer(clientFactory, $"bops-test-{Guid.NewGuid():N}");
        using var client = clientFactory.Create();

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = Image, Tag = Tag },
            authConfig: null,
            new Progress<JSONMessage>(),
            ct);

        var created = await client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = $"{Image}:{Tag}",
                Name = container.Name,
                Cmd = ["sleep", "3600"],
            },
            ct);

        container.Id = created.ID;
        return container;
    }

    public async ValueTask DisposeAsync()
    {
        if (string.IsNullOrEmpty(Id))
        {
            return;
        }

        using var client = _clientFactory.Create();
        try
        {
            await client.Containers.RemoveContainerAsync(Id, new ContainerRemoveParameters { Force = true });
        }
        catch (DockerApiException)
        {
            // Best-effort cleanup: the container may already be gone (a test that removed it itself).
        }
    }
}
