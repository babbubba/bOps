// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>ADR-0049 composition: one host-owned registry, a distinct read-only probe for packages, the same registries everywhere.</summary>
public sealed class PrerequisiteCompositionTests
{
    private static readonly Dictionary<string, string?> NoDocker = new() { ["Docker:Endpoint"] = "tcp://127.0.0.1:1" };

    [Fact]
    public void TheProbePackagesReceive_IsNotTheMutableRegistry_AndCannotBeCastToTheRegistrar()
    {
        using var factory = new TestAppFactory { ExtraConfiguration = NoDocker };
        _ = factory.CreateAnonymousClient();
        var services = factory.Services;

        var packageProbe = services.GetRequiredService<ICapabilityProbe>();
        var registry = services.GetRequiredService<PrerequisiteRegistry>();

        Assert.IsNotType<PrerequisiteRegistry>(packageProbe);
        Assert.IsNotAssignableFrom<IPrerequisiteRegistrar>(packageProbe);
        Assert.Same(registry, services.GetRequiredService<IPrerequisiteRegistrar>());
        Assert.Same(registry, services.GetRequiredService<IPrerequisiteStateSource>());
        Assert.Same(services.GetRequiredService<IToolRegistry>(), services.GetRequiredService<ToolRegistry>());
        Assert.Same(services.GetRequiredService<ISkillRegistry>(), services.GetRequiredService<SkillRegistry>());
    }

    [Fact]
    public void TheFirstPartyPrerequisites_AreRegisteredAndAlreadyCheckedWhenTheHostAcceptsWork()
    {
        using var factory = new TestAppFactory { ExtraConfiguration = NoDocker };
        _ = factory.CreateAnonymousClient();
        var registry = factory.Services.GetRequiredService<PrerequisiteRegistry>();

        // The Windows debugger prerequisites are registered on a Windows host only.
        string[] expected = OperatingSystem.IsWindows()
            ? ["docker", "docker.build-contexts", "web.searxng", "windows.debugger.dumpchk", "windows.debugger.kd"]
            : ["docker", "docker.build-contexts", "web.searxng"];
        Assert.Equal(expected, registry.GetRegistrations().Select(r => r.Descriptor.Id));
        Assert.All(registry.GetRegistrations(), r => Assert.NotNull(registry.GetLastResult(r.Descriptor.Id)));
    }

    [Fact]
    public void TheShippedDefaults_AreTheDocumentedOnes()
    {
        var shipped = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false).Build();

        var prerequisites = shipped.GetSection(PrerequisiteOptions.SectionName).Get<PrerequisiteOptions>()!.Validate();
        var messages = shipped.GetSection(SystemMessageOptions.SectionName).Get<SystemMessageOptions>()!.Validate();

        Assert.Equal(4, prerequisites.MaxConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(30), prerequisites.RefreshInterval);
        Assert.Equal(TimeSpan.FromDays(90), messages.Retention);
    }
}
