// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Docker;
using Microsoft.Extensions.Configuration;

namespace bOps.Api.Tests;

/// <summary>
/// The Docker settings the hosts bind (ADR-0033): the shipped <c>appsettings.json</c> leaves <c>docker.build</c> off, and the keys
/// documented in <c>docs/docker-management.md</c> bind to the options the Docker package reads.
/// </summary>
public sealed class DockerConfigurationTests
{
    private static IConfigurationRoot Shipped() =>
        new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false).Build();

    [Fact]
    public void TheShippedSettings_AllowNoBuildContext_AndOnlyTheLocalVolumeDriver()
    {
        var configuration = Shipped();

        var build = configuration.GetSection("Docker:Build").Get<DockerBuildOptions>()!;
        var volumes = configuration.GetSection("Docker:Volumes").Get<DockerVolumeOptions>()!;

        Assert.Empty(build.Contexts);
        Assert.False(build.IsConfigured);
        Assert.Equal(DockerBuildOptions.DefaultMaximumContextFiles, build.MaximumContextFiles);
        Assert.Equal(DockerBuildOptions.DefaultMaximumContextBytes, build.MaximumContextBytes);
        Assert.Equal(["local"], volumes.AllowedDrivers);
    }

    [Fact]
    public void TheDocumentedKeys_BindToTheOptions_IncludingFromEnvironmentStyleNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Docker:Build:Contexts:0"] = "/srv/build-contexts",
                ["Docker:Build:Contexts:1"] = "C:\\build-contexts",
                ["Docker:Build:MaximumContextFiles"] = "250",
                ["Docker:Build:MaximumContextBytes"] = "1048576",
                ["Docker:Volumes:Drivers:0"] = "local",
                ["Docker:Volumes:Drivers:1"] = "custom",
            })
            .Build();

        var build = configuration.GetSection("Docker:Build").Get<DockerBuildOptions>()!;
        var volumes = configuration.GetSection("Docker:Volumes").Get<DockerVolumeOptions>()!;

        Assert.Equal(["/srv/build-contexts", "C:\\build-contexts"], build.Contexts);
        Assert.True(build.IsConfigured);
        Assert.Equal(250, build.MaximumContextFiles);
        Assert.Equal(1_048_576, build.MaximumContextBytes);
        Assert.Equal(["local", "custom"], volumes.AllowedDrivers);
    }

    [Fact]
    public void TheEnvironmentVariableForm_UsesDoubleUnderscoreAsTheKeyDelimiter()
    {
        var name = $"BOPS_TEST_DOCKER_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name + "__BUILD__CONTEXTS__0", "/from/env");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(name + "__").Build();

            var build = configuration.GetSection("BUILD").Get<DockerBuildOptions>()!;

            Assert.Equal(["/from/env"], build.Contexts);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name + "__BUILD__CONTEXTS__0", null);
        }
    }
}
