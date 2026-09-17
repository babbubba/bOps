// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bOps.Abstractions;
using bOps.PluginHost;

namespace bOps.Api.Tests;

/// <summary>
/// Drives <c>GET /api/plugins</c> and <c>GET /api/plugins/{id}</c> (V1.1-F) against the real
/// composition root. Seeds <c>plugins.json</c> directly with plain <see cref="PluginRecord"/>
/// objects rather than a real signed plugin fixture — the catalog only ever reads
/// <see cref="PluginManager.List()"/> and re-validates the manifest, neither of which needs a real
/// loadable assembly for a plugin that stays disabled throughout a test.
/// </summary>
public sealed class PluginCatalogEndpointsTests
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SampleReadCapability = ["sample.read"];

    [Fact]
    public async Task ListPlugins_ReturnsAnEmptyPage_WhenNoneAreInstalled()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<PluginCatalogPage>("/api/plugins", ResponseJsonOptions);

        Assert.NotNull(page);
        Assert.Empty(page!.Entries);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task ListPlugins_ProjectsADisabledPlugin_WithoutLeakingItsInstallPath()
    {
        using var factory = new TestAppFactory();
        var installPath = SeedPlugin(factory, "acme.sample-plugin", enabled: false);
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync(new Uri("/api/plugins", UriKind.Relative));
        Assert.DoesNotContain(installPath, raw, StringComparison.Ordinal);

        var page = JsonSerializer.Deserialize<PluginCatalogPage>(raw, ResponseJsonOptions);
        var entry = Assert.Single(page!.Entries);
        Assert.Equal("acme.sample-plugin", entry.Id);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal("Acme", entry.Publisher);
        Assert.False(entry.Enabled);
        Assert.False(entry.Loaded);
        Assert.True(entry.Compatible);
        Assert.True(entry.SignaturePresent);
        Assert.True(entry.Verified);
        Assert.Equal(PackageTrustLevel.Verified, entry.Trust);
        Assert.Equal(SampleReadCapability, entry.DeclaredCapabilities);
        Assert.Equal("acme-dep", Assert.Single(entry.Dependencies).Name);
        Assert.Equal(RiskLevel.Read, entry.DeclaredMaxRisk);
        Assert.Null(entry.EffectiveMaxRisk);
        Assert.Null(entry.LoadError);
    }

    [Fact]
    public async Task ListPlugins_FiltersByEnabledAndTrust()
    {
        using var factory = new TestAppFactory();
        SeedPlugin(factory, "acme.enabled-plugin", enabled: true, trust: PackageTrustLevel.Official);
        SeedPlugin(factory, "acme.disabled-plugin", enabled: false, trust: PackageTrustLevel.Community, append: true);
        using var client = factory.CreateClient();

        var enabledOnly = await client.GetFromJsonAsync<PluginCatalogPage>("/api/plugins?enabled=true", ResponseJsonOptions);
        var officialOnly = await client.GetFromJsonAsync<PluginCatalogPage>("/api/plugins?trust=3", ResponseJsonOptions);

        Assert.Equal("acme.enabled-plugin", Assert.Single(enabledOnly!.Entries).Id);
        Assert.Equal("acme.enabled-plugin", Assert.Single(officialOnly!.Entries).Id);
    }

    [Fact]
    public async Task GetPlugin_ReturnsNotFound_ForAnUnknownId()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/plugins/does-not-exist", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPlugin_ReturnsTheProjectedEntry()
    {
        using var factory = new TestAppFactory();
        SeedPlugin(factory, "acme.sample-plugin", enabled: false);
        using var client = factory.CreateClient();

        var entry = await client.GetFromJsonAsync<PluginCatalogEntry>("/api/plugins/acme.sample-plugin", ResponseJsonOptions);

        Assert.NotNull(entry);
        Assert.Equal("acme.sample-plugin", entry!.Id);
    }

    [Fact]
    public async Task ListPlugins_RequiresAuthentication()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(new Uri("/api/plugins", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Writes a plain <see cref="PluginRecord"/> straight to the test host's configured
    /// <c>Plugins:StorePath</c>, bypassing <see cref="PluginManager.Install"/> entirely — no
    /// signing key or real assembly is needed for a plugin this test never enables/activates.
    /// </summary>
    /// <param name="append">When true, reads back and appends to any records already written by an earlier call in the same test, instead of overwriting.</param>
    /// <returns>The install path recorded for the plugin, for the "never leaked to the wire" assertion.</returns>
    private static string SeedPlugin(
        TestAppFactory factory,
        string id,
        bool enabled,
        PackageTrustLevel trust = PackageTrustLevel.Verified,
        bool append = false)
    {
        var storePath = Path.Combine(factory.TempDirectory, "plugins.json");
        var installPath = Path.Combine(factory.TempDirectory, "plugins", id);
        Directory.CreateDirectory(installPath);

        var manifest = new PluginManifest(
            SchemaVersion: PluginManifestValidator.SupportedSchemaVersion,
            Id: id,
            Publisher: "Acme",
            Version: "1.0.0",
            MinHostAbstractionsVersion: "0.10.0",
            EntryAssembly: "Fake.dll",
            EntryType: "Acme.Fake",
            DeclaredCapabilities: ["sample.read"],
            Dependencies: [new PluginDependency("acme-dep", "2.0.0")],
            MaxDeclaredRisk: RiskLevel.Read);
        File.WriteAllText(Path.Combine(installPath, manifest.EntryAssembly), string.Empty);

        var record = new PluginRecord(
            id,
            installPath,
            manifest,
            enabled,
            DateTimeOffset.UtcNow,
            new PluginProvenance(SignaturePresent: true, Verified: true, "Acme", "test-key", "deadbeef", trust, FailureReason: null));

        var existing = append && File.Exists(storePath)
            ? JsonSerializer.Deserialize<List<PluginRecord>>(File.ReadAllText(storePath)) ?? []
            : [];
        existing.Add(record);
        File.WriteAllText(storePath, JsonSerializer.Serialize(existing));

        return installPath;
    }
}
