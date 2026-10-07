// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.AppHost.Tests;

public sealed class AppHostCompositionTests
{
    [Fact]
    public async Task DefaultComposition_ProvidesJsonEnabledSearxngToTheApi()
    {
        using IDistributedApplicationTestingBuilder builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.bOps_AppHost>();

        var searxng = Assert.IsType<ContainerResource>(
            Assert.Single(builder.Resources, resource => resource.Name == "searxng"));

        var image = Assert.Single(searxng.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("docker.io", image.Registry);
        Assert.Equal("searxng/searxng", image.Image);
        Assert.Equal("2026.10.4-d48c4b555", image.Tag);

        var endpoint = Assert.Single(searxng.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal(8081, endpoint.Port);
        Assert.Equal(8080, endpoint.TargetPort);

        var settingsMount = Assert.Single(
            searxng.Annotations.OfType<ContainerMountAnnotation>(),
            mount => mount.Target == "/etc/searxng/settings.yml");
        Assert.True(settingsMount.IsReadOnly);
        var settings = await File.ReadAllTextAsync(Assert.IsType<string>(settingsMount.Source));
        Assert.Contains("- json", settings, StringComparison.Ordinal);
        Assert.Contains("limiter: false", settings, StringComparison.Ordinal);

        var api = builder.CreateResourceBuilder<ProjectResource>("bops-api");
        var executionConfiguration = await ExecutionConfigurationBuilder.Create(api.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(
                new(DistributedApplicationOperation.Publish),
                NullLogger.Instance,
                CancellationToken.None);
        var environment = executionConfiguration.EnvironmentVariables.ToDictionary();

        Assert.Equal("{searxng.bindings.http.url}", environment["Web__Search__BaseUrl"]);
        Assert.Contains(
            api.Resource.Annotations.OfType<WaitAnnotation>(),
            wait => wait.Resource == searxng && wait.WaitType == WaitType.WaitUntilHealthy);
    }

    [Fact]
    public async Task Composition_CanDisableSearxngWithoutChangingTheApiRuntime()
    {
        using IDistributedApplicationTestingBuilder builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.bOps_AppHost>(["Searxng:Enabled=false"]);

        Assert.DoesNotContain(builder.Resources, resource => resource.Name == "searxng");

        var api = builder.CreateResourceBuilder<ProjectResource>("bops-api");
        var executionConfiguration = await ExecutionConfigurationBuilder.Create(api.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(
                new(DistributedApplicationOperation.Publish),
                NullLogger.Instance,
                CancellationToken.None);

        Assert.DoesNotContain(
            executionConfiguration.EnvironmentVariables,
            pair => pair.Key == "Web__Search__BaseUrl");
    }
}
