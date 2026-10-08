// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.PluginHost.Tests;

/// <summary>
/// ADR-0049 plugin integration, against the REAL compiled sample plugin (never a fake loader): a plugin's prerequisite checks are
/// registered under the host-assigned package id, rolled back with a failed activation, removed — and never run again — on disable,
/// and never keep the collectible load context alive. Package code only ever sees the read-only probe.
/// </summary>
public sealed class PluginPrerequisiteTests : IDisposable
{
    private const string PluginId = "acme.sample-plugin";
    private const string PrerequisiteId = "acme.sample-marker-store";

    private static readonly string ControlDirectory = Path.Combine(Path.GetTempPath(), "bops-sample-prerequisite");

    private readonly DirectoryInfo _workDir = Directory.CreateTempSubdirectory("bops-plugin-prerequisite-");
    private readonly PrerequisiteRegistry _prerequisites = new(TimeProvider.System, TimeSpan.Zero);
    private readonly ToolRegistry _tools;
    private readonly SkillRegistry _skills;
    private readonly ChatModelRegistry _chatModels = new();
    private readonly RSA _publisherKey = RSA.Create(2048);

    public PluginPrerequisiteTests()
    {
        _tools = new ToolRegistry(_prerequisites);
        _skills = new SkillRegistry(_prerequisites);
        File.WriteAllText(TrustStorePath, JsonSerializer.Serialize(new[]
        {
            new PluginPublisherTrust("Acme", "test-key", _publisherKey.ExportSubjectPublicKeyInfoPem(), PackageTrustLevel.Community),
        }));
        if (Directory.Exists(ControlDirectory))
        {
            Directory.Delete(ControlDirectory, recursive: true);
        }

        Directory.CreateDirectory(ControlDirectory);
    }

    public void Dispose()
    {
        _publisherKey.Dispose();
        TryDelete(_workDir.FullName);
        TryDelete(ControlDirectory);
    }

    private string PluginsRoot => Path.Combine(_workDir.FullName, "plugins");

    private string TrustStorePath => Path.Combine(_workDir.FullName, "publisher-trust.json");

    private static int Runs()
    {
        var log = Path.Combine(ControlDirectory, "runs.log");
        return File.Exists(log) ? File.ReadAllLines(log).Length : 0;
    }

    [Fact]
    public void ThePluginServiceProvider_ResolvesTheReadOnlyProbe_AndNeverAMutationSurface()
    {
        var probe = _prerequisites.AsCapabilityProbe();
        var services = new RestrictedPackageServiceProvider(
            NullLoggerFactory.Instance, new FakeHttpClientFactory(), TimeProvider.System, new ConfigurationBuilder().Build().GetSection("x"), probe);

        var resolved = services.GetService(typeof(ICapabilityProbe));

        Assert.Same(probe, resolved);
        Assert.IsNotAssignableFrom<IPrerequisiteRegistrar>(resolved);
        Assert.IsNotType<PrerequisiteRegistry>(resolved);
        foreach (var hostOnly in new[]
                 {
                     typeof(IPrerequisiteRegistrar), typeof(PrerequisiteRegistry), typeof(IPrerequisiteStateSource),
                     typeof(IPrerequisiteStateStore), typeof(ISystemMessageStore), typeof(ToolRegistry), typeof(SkillRegistry),
                 })
        {
            Assert.Null(services.GetService(hostOnly));
        }
    }

    [Fact]
    public void AnEnabledPlugins_PrerequisiteIsRegisteredUnderTheHostAssignedPackageId()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());

        manager.Enable(PluginId);

        var registration = Assert.Single(_prerequisites.GetRegistrations());
        Assert.Equal(PrerequisiteId, registration.Descriptor.Id);
        Assert.Equal(PluginId, registration.Package.Value);
        Assert.Equal(PackageTrustLevel.Community, _tools.GetTrust(new PackageId(PluginId)));
    }

    [Fact]
    public async Task APluginPrerequisite_IsCheckedAndStampedByTheHost()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());
        manager.Enable(PluginId);

        var result = await _prerequisites.CheckAsync(PrerequisiteId);

        Assert.Equal(PrerequisiteId, result.Id);
        Assert.Equal(PrerequisiteState.Available, result.State);
        Assert.Equal(1, Runs());

        await File.WriteAllTextAsync(Path.Combine(ControlDirectory, "unavailable"), "x");
        Assert.Equal(PrerequisiteState.Unavailable, (await _prerequisites.CheckAsync(PrerequisiteId)).State);
    }

    [Fact]
    public void AnInstalledButNotEnabledPlugin_RegistersNothing()
    {
        CreateManager().Install(StageSamplePluginSource());

        Assert.Empty(_prerequisites.GetRegistrations());
    }

    [Fact]
    public void LoadAllEnabled_RegistersThePluginsPrerequisites_AtStartup()
    {
        var first = CreateManager();
        first.Install(StageSamplePluginSource());
        first.Enable(PluginId);
        first.Disable(PluginId);
        first.Enable(PluginId);
        first.Disable(PluginId);
        _prerequisites.Unregister(new PackageId(PluginId));
        // Mark it enabled in the store without activating, then start a fresh host.
        new PluginStore(Path.Combine(_workDir.FullName, "plugins.json")).SetEnabled(PluginId, true);

        var restarted = CreateManager();
        Assert.Empty(restarted.LoadAllEnabled());

        Assert.Single(_prerequisites.GetRegistrations());
        restarted.Disable(PluginId);
    }

    [Fact]
    public void ADuplicateOfAnotherPackagesPrerequisite_RefusesTheActivation_AndLeavesTheOtherPackageIntact()
    {
        var owner = new PackageId("bops.packages.first-party");
        _prerequisites.Register(owner, new Squatter());
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());

        var failure = Assert.Throws<PluginOperationException>(() => manager.Enable(PluginId));

        Assert.Contains("refused", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.IsActivated(PluginId));
        Assert.Null(_tools.Resolve("sample.echo"));
        Assert.Empty(_skills.GetAvailableSkills());
        var remaining = Assert.Single(_prerequisites.GetRegistrations());
        Assert.Equal(owner, remaining.Package);
        Assert.False(manager.List().Single().Enabled);
    }

    [Fact]
    public void AToolRegistrationFailureAfterwards_RollsTheNewPrerequisiteBack()
    {
        // A first-party tool already owns the name the plugin's first tool wants: Tool registration fails after the prerequisites went in.
        _tools.Register(new PackageId("bops.packages.first-party"), new NamedTool("sample.echo"));
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());

        Assert.Throws<ToolRegistrationException>(() => manager.Enable(PluginId));

        Assert.Empty(_prerequisites.GetRegistrations());
        Assert.False(manager.IsActivated(PluginId));
        Assert.Empty(_skills.GetAvailableSkills());
    }

    [Fact]
    public async Task Disable_RemovesThePrerequisite_AndItsCheckNeverRunsAgain()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());
        manager.Enable(PluginId);
        await _prerequisites.RefreshAsync();
        Assert.Equal(1, Runs());

        manager.Disable(PluginId);

        Assert.Empty(_prerequisites.GetRegistrations());
        Assert.Null(_prerequisites.GetLastResult(PrerequisiteId));
        Assert.Empty(await _prerequisites.RefreshAsync());
        Assert.Equal(PrerequisiteState.Unknown, (await _prerequisites.CheckAsync(PrerequisiteId)).State);
        Assert.False(await _prerequisites.AsCapabilityProbe().IsAvailableAsync(PrerequisiteId));
        Assert.Equal(1, Runs());
    }

    [Fact]
    public async Task Disable_LetsThePluginsCollectibleLoadContextBeCollected_NothingInThePrerequisiteRegistryRetainsIt()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());
        var weak = EnableCheckAndTrack(manager);

        manager.Disable(PluginId);
        for (var attempt = 0; attempt < 20 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(10);
        }

        Assert.False(weak.IsAlive, "The plugin's AssemblyLoadContext is still referenced after disable.");
        Assert.Empty(_prerequisites.GetRegistrations());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference EnableCheckAndTrack(PluginManager manager)
    {
        manager.Enable(PluginId);
        _prerequisites.CheckAsync(PrerequisiteId).GetAwaiter().GetResult();
        _prerequisites.RefreshAsync().GetAwaiter().GetResult();

        // This test's own plugin directory identifies the context among any others the process has loaded.
        var context = AssemblyLoadContext.All.Single(
            candidate => candidate.IsCollectible
                && candidate.Assemblies.Any(assembly => !assembly.IsDynamic && assembly.Location.StartsWith(PluginsRoot, StringComparison.Ordinal)));
        return new WeakReference(context);
    }

    private PluginManager CreateManager() => new(
        new PluginStore(Path.Combine(_workDir.FullName, "plugins.json")),
        _tools,
        _skills,
        _chatModels,
        PluginsRoot,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:TrustStorePath"] = TrustStorePath }).Build(),
        NullLoggerFactory.Instance,
        new FakeHttpClientFactory(),
        TimeProvider.System,
        _prerequisites.AsCapabilityProbe(),
        _prerequisites);

    private string StageSamplePluginSource()
    {
        var source = Directory.CreateTempSubdirectory("bops-sample-plugin-source-").FullName;
        foreach (var fileName in new[] { "Acme.SamplePlugin.dll", "bops-plugin.json" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, fileName), Path.Combine(source, fileName));
        }

        PluginPackageSignature.Sign(source, "Acme", "test-key", _publisherKey.ExportPkcs8PrivateKeyPem());
        return source;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // An enabled plugin keeps its files locked; the OS temp directory reclaims them.
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class Squatter : IPrerequisiteProvider
    {
        public IReadOnlyList<IPrerequisiteCheck> GetPrerequisiteChecks() => [new SquatterCheck()];
    }

    private sealed class SquatterCheck : IPrerequisiteCheck
    {
        public PrerequisiteDescriptor Descriptor { get; } = new(PrerequisiteId, "First-party", "A first-party check.");

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(new PrerequisiteCheckOutcome(PrerequisiteState.Available, "available", "Fine."));
    }

    private sealed class NamedTool(string name) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "A first-party tool.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(null));
    }
}
