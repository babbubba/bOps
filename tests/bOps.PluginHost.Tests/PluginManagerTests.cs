// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.PluginHost.Tests;

/// <summary>
/// End-to-end against the REAL, compiled sample plugin (<c>samples/bops-sample-plugin</c>) —
/// never a fake of the loader, mirroring agentic/04-testing-rules.md's "never mock the operating
/// system." <see cref="PluginManagerTests"/> exercises install/enable/disable/remove exactly as
/// V0.10's Definition of Done requires.
/// </summary>
public sealed class PluginManagerTests : IDisposable
{
    private readonly DirectoryInfo _workDir = Directory.CreateTempSubdirectory("bops-plugin-manager-");
    private readonly ToolRegistry _toolRegistry = new(new AlwaysAvailableCapabilityProbe());
    private readonly ChatModelRegistry _chatModelRegistry = new();
    private readonly RSA _publisherKey = RSA.Create(2048);

    public PluginManagerTests()
    {
        var trust = new[]
        {
            new PluginPublisherTrust("Acme", "test-key", _publisherKey.ExportSubjectPublicKeyInfoPem(), PackageTrustLevel.Community),
        };
        File.WriteAllText(TrustStorePath, JsonSerializer.Serialize(trust));
    }

    public void Dispose()
    {
        _publisherKey.Dispose();
        // A test that enables a plugin but never disables/removes it leaves that plugin's
        // assembly loaded (correctly — an enabled plugin's files staying locked while loaded is
        // the same real constraint production Remove() has to work around, not a test bug), so
        // best-effort cleanup here, not a hard requirement: the OS temp directory reclaims it
        // eventually, and this test class's own coverage of the unload path (Disable/Remove
        // tests) is what actually proves collectible unloading works.
        try
        {
            _workDir.Delete(recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private string PluginsRoot => Path.Combine(_workDir.FullName, "plugins");
    private string StorePath => Path.Combine(_workDir.FullName, "plugins.json");
    private string TrustStorePath => Path.Combine(_workDir.FullName, "publisher-trust.json");

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plugins:TrustStorePath"] = TrustStorePath,
        })
        .Build();

    private PluginManager CreateManager() =>
        new(
            new PluginStore(StorePath),
            _toolRegistry,
            _chatModelRegistry,
            PluginsRoot,
            Configuration(),
            NullLoggerFactory.Instance,
            new FakeHttpClientFactory(),
            TimeProvider.System,
            new AlwaysAvailableCapabilityProbe());

    /// <summary>
    /// Copies only the sample plugin's own build output — not this test project's entire bin
    /// folder — into a fresh directory, so <see cref="PluginManager.Install"/> sees exactly what
    /// a real plugin distribution would contain.
    /// </summary>
    private string StageSamplePluginSource(bool sign = true)
    {
        var source = Directory.CreateTempSubdirectory("bops-sample-plugin-source-").FullName;
        foreach (var fileName in new[] { "Acme.SamplePlugin.dll", "bops-plugin.json" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, fileName), Path.Combine(source, fileName));
        }

        if (sign)
        {
            PluginPackageSignature.Sign(source, "Acme", "test-key", _publisherKey.ExportPkcs8PrivateKeyPem());
        }

        return source;
    }

    [Fact]
    public void Install_RecordsThePluginAsDisabled()
    {
        var manager = CreateManager();

        var record = manager.Install(StageSamplePluginSource());

        Assert.Equal("acme.sample-plugin", record.Id);
        Assert.False(record.Enabled);
        Assert.True(record.Provenance!.Verified);
        Assert.Equal(PackageTrustLevel.Community, record.Provenance.Trust);
        Assert.Single(manager.List());
        Assert.True(File.Exists(Path.Combine(record.InstallPath, "Acme.SamplePlugin.dll")));
    }

    [Fact]
    public void Enable_RejectsAnUnsignedPlugin()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource(sign: false));

        var exception = Assert.Throws<PluginOperationException>(() => manager.Enable("acme.sample-plugin"));

        Assert.Contains("trusted publisher", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.List().Single().Enabled);
    }

    [Fact]
    public void Enable_RejectsAPublisherWhoseTrustWasRevokedAfterInstall()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());
        var revoked = new[]
        {
            new PluginPublisherTrust("Acme", "test-key", _publisherKey.ExportSubjectPublicKeyInfoPem(), PackageTrustLevel.Unverified),
        };
        File.WriteAllText(TrustStorePath, JsonSerializer.Serialize(revoked));

        Assert.Throws<PluginOperationException>(() => manager.Enable("acme.sample-plugin"));

        Assert.Null(_toolRegistry.Resolve("sample.echo"));
        Assert.False(manager.List().Single().Enabled);
    }

    [Fact]
    public void Enable_RejectsAStoreIdentityThatDoesNotMatchTheSignedInstalledManifest()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());
        var store = JsonNode.Parse(File.ReadAllText(StorePath))!.AsArray();
        store[0]!["Id"] = "evil.relabelled-plugin";
        File.WriteAllText(StorePath, store.ToJsonString());

        var exception = Assert.Throws<PluginOperationException>(() => manager.Enable("evil.relabelled-plugin"));

        Assert.Contains("signed installed manifest", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_toolRegistry.Resolve("sample.echo"));
    }

    [Fact]
    public void Install_RejectsAPackageModifiedAfterSigning_AndLeavesNoInstalledDirectory()
    {
        var manager = CreateManager();
        var source = StageSamplePluginSource();
        File.AppendAllText(Path.Combine(source, "Acme.SamplePlugin.dll"), "tampered");

        Assert.Throws<PluginValidationException>(() => manager.Install(source));

        Assert.Empty(manager.List());
        Assert.False(Directory.Exists(PluginsRoot) && Directory.EnumerateFileSystemEntries(PluginsRoot).Any());
    }

    [Fact]
    public void Install_DoesNotRegisterAnyTool_UntilEnabled()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());

        Assert.Null(_toolRegistry.Resolve("sample.echo"));
    }

    [Fact]
    public async Task Enable_ActivatesTheRealPlugin_AndItsToolBecomesCallable()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());

        manager.Enable("acme.sample-plugin");

        var tool = _toolRegistry.Resolve("sample.echo");
        Assert.NotNull(tool);
        var result = await tool!.ExecuteAsync(ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["message"] = "hello" }));
        Assert.True(result.Succeeded);
        Assert.Equal("HELLO", result.Output);
        Assert.True(manager.List().Single().Enabled);
    }

    [Fact]
    public void Enable_Twice_Throws()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());
        manager.Enable("acme.sample-plugin");

        Assert.Throws<PluginOperationException>(() => manager.Enable("acme.sample-plugin"));
    }

    [Fact]
    public void Disable_HidesTheToolAgain()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());
        manager.Enable("acme.sample-plugin");

        manager.Disable("acme.sample-plugin");

        Assert.Null(_toolRegistry.Resolve("sample.echo"));
        Assert.False(manager.List().Single().Enabled);
    }

    [Fact]
    public void Remove_DisablesAndDeletesTheInstalledFiles()
    {
        var manager = CreateManager();
        var record = manager.Install(StageSamplePluginSource());
        manager.Enable("acme.sample-plugin");

        manager.Remove("acme.sample-plugin");

        Assert.Empty(manager.List());
        Assert.False(Directory.Exists(record.InstallPath));
        Assert.Null(_toolRegistry.Resolve("sample.echo"));
    }

    [Fact]
    public void LoadAllEnabled_ActivatesAnAlreadyEnabledPlugin_WithoutRewritingTheStore()
    {
        var installer = CreateManager();
        installer.Install(StageSamplePluginSource());
        installer.Enable("acme.sample-plugin");

        // Simulates a new host process: fresh registries, a PluginManager that has never called
        // Enable itself, reading the same on-disk store.
        var freshToolRegistry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        var nextProcessManager = new PluginManager(
            new PluginStore(StorePath), freshToolRegistry, new ChatModelRegistry(), PluginsRoot,
            Configuration(), NullLoggerFactory.Instance, new FakeHttpClientFactory(),
            TimeProvider.System, new AlwaysAvailableCapabilityProbe());

        nextProcessManager.LoadAllEnabled();

        Assert.NotNull(freshToolRegistry.Resolve("sample.echo"));
    }

    [Fact]
    public void Enable_WorksWithARelativePluginsRootDirectory()
    {
        // Regression: LoadFromAssemblyPath requires an absolute path. A relative Plugins:RootPath
        // (the CLI's actual default, "plugins") must still work regardless of the host process's
        // current directory.
        var originalDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _workDir.FullName;
        try
        {
            var manager = new PluginManager(
                new PluginStore(StorePath), _toolRegistry, _chatModelRegistry, "plugins",
                Configuration(), NullLoggerFactory.Instance, new FakeHttpClientFactory(),
                TimeProvider.System, new AlwaysAvailableCapabilityProbe());

            manager.Install(StageSamplePluginSource());
            manager.Enable("acme.sample-plugin");

            Assert.NotNull(_toolRegistry.Resolve("sample.echo"));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public void Install_RejectsAnIdAlreadyInstalled()
    {
        var manager = CreateManager();
        manager.Install(StageSamplePluginSource());

        Assert.Throws<PluginOperationException>(() => manager.Install(StageSamplePluginSource()));
    }

    [Fact]
    public void Install_OfAManifestThatFailsValidation_LeavesNothingBehind()
    {
        var manager = CreateManager();
        var source = StageSamplePluginSource();
        // Corrupt the staged manifest so validation fails partway through Install, after some
        // files would already have been copied to staging.
        File.WriteAllText(Path.Combine(source, "bops-plugin.json"), """{ "SchemaVersion": 999, "Id": "acme.sample-plugin", "Publisher": "Acme", "Version": "1.0.0", "MinHostAbstractionsVersion": "0.10.0", "EntryAssembly": "Acme.SamplePlugin.dll", "EntryType": "x", "DeclaredCapabilities": [], "Dependencies": [] }""");

        Assert.Throws<PluginValidationException>(() => manager.Install(source));

        Assert.Empty(manager.List());
        Assert.False(Directory.Exists(PluginsRoot) && Directory.EnumerateFileSystemEntries(PluginsRoot).Any());
    }

    private sealed class AlwaysAvailableCapabilityProbe : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
