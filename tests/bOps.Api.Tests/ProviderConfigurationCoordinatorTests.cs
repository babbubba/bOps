// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;

namespace bOps.Api.Tests;

public sealed class ProviderConfigurationCoordinatorTests
{
    private static readonly ModelRequest Request = new("test", [], []);

    [Fact]
    public async Task ProviderAndModelSwitch_PinOldExecution_AndChangeTheNextOne()
    {
        using var rig = new Rig();
        var oldPin = rig.Coordinator.Current.Pin;
        var oldModel = rig.Models.Create(oldPin);
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", false, null));
        rig.Coordinator.SetKey("Anthropic", "ANTHROPIC_KEY", 0);
        Assert.True(rig.Coordinator.SelectProvider("anthropic"));

        var newPin = rig.Coordinator.Current.Pin;
        Assert.Equal("OpenRouter", oldPin.ProviderId);
        Assert.Equal("openrouter/free", oldPin.Model);
        Assert.Equal("Anthropic", newPin.ProviderId);
        Assert.Equal("sonnet-x", newPin.Model);
        Assert.Equal("https://api.anthropic.test", newPin.BaseUrl);
        Assert.False(newPin.SupportsNativeToolCalling);
        await oldModel.CompleteAsync(Request);
        await rig.Models.Create(newPin).CompleteAsync(Request);
        Assert.Equal(("OpenRouter", "openrouter/free", "OPENROUTER_KEY"), rig.Calls[0].Identity);
        Assert.Equal(("Anthropic", "sonnet-x", "ANTHROPIC_KEY"), rig.Calls[1].Identity);

        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.new", "sonnet-y", true, null));
        var later = rig.Coordinator.Current.Pin;
        Assert.Equal("sonnet-x", newPin.Model);
        Assert.Equal("https://api.anthropic.test", newPin.BaseUrl);
        Assert.False(newPin.SupportsNativeToolCalling);
        Assert.Equal("sonnet-y", later.Model);
        Assert.Equal("https://api.anthropic.new", later.BaseUrl);
        Assert.True(later.SupportsNativeToolCalling);
        Assert.True(later.Generation > newPin.Generation);
    }

    [Fact]
    public async Task KeyRotationAndRemoval_AffectNextAttemptWithoutChangingPinOrGeneration()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        rig.Coordinator.SetKey("Anthropic", "K1", 0);
        rig.Coordinator.SelectProvider("Anthropic");
        var pin = rig.Coordinator.Current.Pin;
        var model = rig.Models.Create(pin);

        await model.CompleteAsync(Request);
        rig.Coordinator.SetKey("Anthropic", "K2", 1);
        Assert.Equal(pin.Generation, rig.Coordinator.Current.Pin.Generation);
        Assert.Equal(pin.SnapshotHash, rig.Coordinator.Current.Pin.SnapshotHash);
        await model.CompleteAsync(Request);
        rig.Coordinator.RemoveKey("Anthropic", 2);
        Assert.False(rig.Coordinator.Current.PrimaryCredentialAvailable);
        Assert.Equal(pin.Generation, rig.Coordinator.Current.Pin.Generation);
        var error = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));
        Assert.Equal(ModelFailureKind.Authentication, error.FailureKind);
        Assert.Equal(["K1", "K2"], rig.Calls.Select(call => call.Key));
        Assert.DoesNotContain("K1", JsonSerializer.Serialize(pin), StringComparison.Ordinal);
        Assert.DoesNotContain("K2", JsonSerializer.Serialize(pin), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredPrimaryBlockSecret_IsNeverUsedForSettingsSelectedProvider()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        rig.Coordinator.SetKey("Anthropic", "ANTHROPIC_KEY", 0);
        rig.Coordinator.SelectProvider("Anthropic");
        await rig.Models.Create(rig.Coordinator.Current.Pin).CompleteAsync(Request);
        Assert.Equal("ANTHROPIC_KEY", Assert.Single(rig.Calls).Key);
        Assert.Equal("vault", rig.Credentials.Availability("Anthropic").Source);
        Assert.Equal("environment", rig.Credentials.Availability("OpenRouter").Source);
    }

    [Fact]
    public void InvalidAndStaleWrites_DoNotPublish()
    {
        using var rig = new Rig();
        var before = rig.Coordinator.Current.Pin;
        Assert.Throws<ArgumentException>(() => rig.Coordinator.SetProfile("Anthropic",
            new("file:///invalid", "sonnet", true, null)));
        Assert.Throws<SettingsConcurrencyException>(() => rig.Coordinator.SetProfile("Anthropic",
            new("https://api.anthropic.test", "sonnet", true, null, ExpectedRevision: 99)));
        Assert.Equal(before, rig.Coordinator.Current.Pin);
        Assert.Equal(0, rig.Settings.Revision);
    }

    [Fact]
    public void ConcurrentAdmission_ObservesOnlyCompleteGenerations()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", false, null));
        rig.Coordinator.SetKey("Anthropic", "ANTHROPIC_KEY", 0);
        var observed = new ConcurrentBag<PinnedProviderConfiguration>();
        Parallel.Invoke(
            () => rig.Coordinator.SelectProvider("Anthropic"),
            () => Parallel.For(0, 1000, _ => observed.Add(rig.Coordinator.Current.Pin)));
        Assert.All(observed, pin =>
        {
            Assert.True(pin.ProviderId is "OpenRouter" or "Anthropic");
            if (pin.ProviderId == "OpenRouter")
                Assert.Equal(("openrouter/free", "https://openrouter.test", true),
                    (pin.Model, pin.BaseUrl, pin.SupportsNativeToolCalling));
            else
                Assert.Equal(("sonnet-x", "https://api.anthropic.test", false),
                    (pin.Model, pin.BaseUrl, pin.SupportsNativeToolCalling));
        });
    }

    [Fact]
    public async Task PersistedPin_ReconstructsAfterSettingsChangeAndRestart_UsingCurrentKey()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        rig.Coordinator.SetKey("Anthropic", "K1", 0);
        rig.Coordinator.SelectProvider("Anthropic");
        var storedPin = JsonSerializer.Deserialize<PinnedProviderConfiguration>(JsonSerializer.Serialize(rig.Coordinator.Current.Pin))!;
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.new", "sonnet-y", false, null));
        rig.Coordinator.SetKey("Anthropic", "K2", 1);
        rig.Coordinator.SelectProvider("OpenRouter");
        rig.Settings.RemoveProviderProfile("Anthropic");

        var restarted = new ProviderConfigurationCoordinator(rig.Configuration, rig.Settings, rig.Registry, rig.Secrets, rig.Vault);
        var factory = new ExecutionChatModelFactory(rig.Registry, rig.Credentials, restarted);
        await factory.Create(storedPin).CompleteAsync(Request);
        var call = Assert.Single(rig.Calls);
        Assert.Equal(("Anthropic", "sonnet-x", "K2"), call.Identity);
        Assert.Equal("https://api.anthropic.test", call.BaseUrl);
        Assert.Throws<ProviderNotSupportedException>(() =>
            new ProviderConfigurationCoordinator(rig.Configuration, rig.Settings, rig.OpenRouterOnlyRegistry,
                rig.Secrets, rig.Vault).ValidatePin(storedPin));
    }

    private static string LegacyHash(PinnedProviderConfiguration p) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('\u001f', p.SchemaVersion, p.ProviderId, p.BaseUrl, p.Model, p.SupportsNativeToolCalling,
                p.RequestTimeout?.Ticks, p.ProviderSource, p.BaseUrlSource, p.ModelSource,
                p.SupportsNativeToolCallingSource, p.RequestTimeoutSource))));

    [Fact]
    public void LegacyPin_WithRealLegacyHash_ValidatesAndMatchesEmptyChainHash()
    {
        using var rig = new Rig();
        var template = new PinnedProviderConfiguration(1, 3, "OpenRouter", "https://openrouter.test", "model-x",
            false, null, "settings", "settings", "settings", "settings", "default", "");
        var stored = JsonSerializer.Deserialize<PinnedProviderConfiguration>(
            $$"""
            {"SchemaVersion":1,"Generation":3,"ProviderId":"OpenRouter","BaseUrl":"https://openrouter.test","Model":"model-x",
            "SupportsNativeToolCalling":false,"RequestTimeout":null,"ProviderSource":"settings","BaseUrlSource":"settings",
            "ModelSource":"settings","SupportsNativeToolCallingSource":"settings","RequestTimeoutSource":"default",
            "SnapshotHash":"{{LegacyHash(template)}}"}
            """)!;
        Assert.Empty(stored.Fallbacks);
        Assert.Equal(0, stored.FallbackOrdinal);
        rig.Coordinator.ValidatePin(stored);

        var current = rig.Coordinator.Current.Pin;
        Assert.Empty(current.Fallbacks);
        Assert.Equal(LegacyHash(current), current.SnapshotHash);
    }

    [Fact]
    public void FallbackChain_ChangesSnapshotHash_ButOrdinalDoesNot()
    {
        using var rig = new Rig();
        var baseline = rig.Coordinator.Current.Pin;
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ModelProvider:Provider"] = "OpenRouter",
            ["ModelProvider:BaseUrl"] = "https://openrouter.test",
            ["ModelProvider:Model"] = "openrouter/free",
            ["ModelProvider:SupportsNativeToolCalling"] = "true",
            ["ModelProvider:ApiKeySecret:Provider"] = "fake",
            ["ModelProvider:ApiKeySecret:Name"] = "OPENROUTER",
            ["ModelProvider:Fallbacks:0:Provider"] = "Anthropic",
            ["ModelProvider:Fallbacks:0:Model"] = "sonnet-x",
        }).Build();
        var chained = new ProviderConfigurationCoordinator(configuration, rig.Settings, rig.Registry, rig.Secrets, rig.Vault)
            .Current.Pin;
        Assert.Single(chained.Fallbacks);
        Assert.NotEqual(baseline.SnapshotHash, chained.SnapshotHash);

        var advanced = chained with { FallbackOrdinal = 1 };
        Assert.Equal(chained.SnapshotHash, advanced.SnapshotHash);
    }

    [Fact]
    public void SettingsFallbacks_ArePinnedForNewExecutionsOnly_AndSurviveProcessRecreation()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        Assert.True(rig.Coordinator.SetFallbacks([new("anthropic", "model-b")]));
        var first = rig.Coordinator.Current.Pin;
        Assert.Equal([("Anthropic", "model-b")], first.Fallbacks.Select(f => (f.ProviderId, f.Model)));
        Assert.Equal("settings", rig.Coordinator.Current.FallbackSource);

        Assert.True(rig.Coordinator.SetFallbacks([new("Anthropic", "model-c"), new("Anthropic", "model-d")]));
        var second = rig.Coordinator.Current.Pin;

        Assert.Equal(["model-b"], first.Fallbacks.Select(f => f.Model));
        Assert.Equal(["model-c", "model-d"], second.Fallbacks.Select(f => f.Model));
        Assert.NotEqual(first.SnapshotHash, second.SnapshotHash);
        Assert.True(second.Generation > first.Generation);
        // A recreated process reads the same persisted list; the stored pin is untouched.
        var restarted = new ProviderConfigurationCoordinator(rig.Configuration, rig.Settings, rig.Registry, rig.Secrets, rig.Vault);
        Assert.Equal(second.Fallbacks, restarted.Current.Pin.Fallbacks);
        Assert.Equal(second.SnapshotHash, restarted.Current.Pin.SnapshotHash);
        restarted.ValidatePin(first);
    }

    [Fact]
    public void FallbackSnapshotHash_IgnoresCredentialRotationAndOrdinal_ButNotTheChain()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        rig.Coordinator.SetKey("Anthropic", "K1", 0);
        rig.Coordinator.SetFallbacks([new("Anthropic", "model-b")]);
        var before = rig.Coordinator.Current.Pin;

        rig.Coordinator.SetKey("Anthropic", "K2", 1);
        Assert.Equal(before.SnapshotHash, rig.Coordinator.Current.Pin.SnapshotHash);
        Assert.Equal(before.Generation, rig.Coordinator.Current.Pin.Generation);
        var advanced = before with { FallbackOrdinal = 1 };
        rig.Coordinator.ValidatePin(advanced);
        Assert.Equal(before.SnapshotHash, advanced.SnapshotHash);
        Assert.Equal(0, rig.Coordinator.Current.Pin.FallbackOrdinal);

        rig.Coordinator.SetFallbacks([new("Anthropic", "model-c")]);
        Assert.NotEqual(before.SnapshotHash, rig.Coordinator.Current.Pin.SnapshotHash);
    }

    [Fact]
    public void OldPin_WithoutFallbacks_NeverReceivesCurrentSettingsFallbacks()
    {
        using var rig = new Rig();
        var legacy = JsonSerializer.Deserialize<PinnedProviderConfiguration>(JsonSerializer.Serialize(rig.Coordinator.Current.Pin))!;
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        rig.Coordinator.SetFallbacks([new("Anthropic", "model-b")]);

        Assert.Empty(legacy.Fallbacks);
        Assert.Equal(0, legacy.FallbackOrdinal);
        var model = rig.Models.Create(legacy);
        Assert.False(Assert.IsAssignableFrom<IFallbackChatModelControl>(model).HasNextCandidate);
        Assert.Single(rig.Coordinator.Current.Pin.Fallbacks);
    }

    [Fact]
    public void InvalidFallbackProposals_NeverReplaceThePublishedGeneration_OrTheStoredList()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        rig.Coordinator.SetFallbacks([new("Anthropic", "model-b")]);
        var published = rig.Coordinator.Current;
        var revision = rig.Settings.Revision;

        Assert.Throws<ArgumentException>(() => rig.Coordinator.SetFallbacks([new("Anthropic", "model-b"), new("anthropic", "MODEL-B")]));
        Assert.Throws<ArgumentException>(() => rig.Coordinator.SetFallbacks([new("OpenRouter", "openrouter/free")]));
        Assert.Throws<ArgumentException>(() => rig.Coordinator.SetFallbacks([new("Anthropic", " ")]));
        Assert.Throws<ProviderNotSupportedException>(() => rig.Coordinator.SetFallbacks([new("Nope", "m")]));
        Assert.Throws<ArgumentException>(() => rig.Coordinator.SetFallbacks(
            [new("Anthropic", "a"), new("Anthropic", "b"), new("Anthropic", "c"), new("Anthropic", "d")]));
        Assert.Throws<SettingsConcurrencyException>(() => rig.Coordinator.SetFallbacks([new("Anthropic", "z")], revision - 1));

        Assert.Same(published, rig.Coordinator.Current);
        Assert.Equal(revision, rig.Settings.Revision);
        Assert.Equal(["model-b"], rig.Settings.Fallbacks.Select(f => f.Model));
    }

    [Fact]
    public void FallbackReadiness_IsAdvisory_NeverGatesPublication_AndExposesNoSecret()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        rig.Coordinator.SetFallbacks([new("Anthropic", "model-b")]);
        Assert.Equal([false], rig.Coordinator.Current.FallbackCredentialAvailable);

        rig.Coordinator.SetKey("Anthropic", "SECRET-FALLBACK-KEY", 0);
        Assert.Equal([true], rig.Coordinator.Current.FallbackCredentialAvailable);
        Assert.DoesNotContain("SECRET-FALLBACK-KEY",
            JsonSerializer.Serialize(rig.Coordinator.Current.Pin) + JsonSerializer.Serialize(rig.Coordinator.Current.FallbackCredentialAvailable),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationOwnedFallbacks_ShadowSettings_AndTheStoredListIsPreserved()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        rig.Coordinator.SetFallbacks([new("Anthropic", "model-b")]);
        rig.Configuration["ModelProvider:Fallbacks:0:Provider"] = "Anthropic";
        rig.Configuration["ModelProvider:Fallbacks:0:Model"] = "host-owned";

        rig.Coordinator.SetFallbacks([new("Anthropic", "model-c")]);

        Assert.Equal("configuration", rig.Coordinator.Current.FallbackSource);
        Assert.Equal(["host-owned"], rig.Coordinator.Current.Pin.Fallbacks.Select(f => f.Model));
        Assert.Equal(["model-c"], rig.Settings.Fallbacks.Select(f => f.Model));
    }

    [Fact]
    public void ShadowedSettingsFallbacks_AreValidatedBeforePersistence_AndHostChainStaysEffective()
    {
        using var rig = new Rig();
        rig.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        rig.Coordinator.SetFallbacks([new("Anthropic", "model-b")]);
        rig.Configuration["ModelProvider:Fallbacks:0:Provider"] = "Anthropic";
        rig.Configuration["ModelProvider:Fallbacks:0:Model"] = "host-model";
        rig.Coordinator.SetFallbacks([new("Anthropic", "model-b")]);
        var published = rig.Coordinator.Current;
        var revision = rig.Settings.Revision;
        Assert.Equal("configuration", published.FallbackSource);

        Assert.Throws<ProviderNotSupportedException>(() => rig.Coordinator.SetFallbacks([new("Nope", "m")]));
        Assert.Throws<ArgumentException>(() => rig.Coordinator.SetFallbacks([new("Anthropic", "x"), new("anthropic", "X")]));
        Assert.Throws<ArgumentException>(() => rig.Coordinator.SetFallbacks([new("OpenRouter", "openrouter/free")]));
        Assert.Throws<ArgumentException>(() => rig.Coordinator.SetFallbacks([new("Anthropic", " ")]));
        Assert.Throws<ArgumentException>(() => rig.Coordinator.SetFallbacks(
            [new("Anthropic", "a"), new("Anthropic", "b"), new("Anthropic", "c"), new("Anthropic", "d")]));

        Assert.Equal(revision, rig.Settings.Revision);
        Assert.Equal(["model-b"], rig.Settings.Fallbacks.Select(f => f.Model));
        Assert.Same(published, rig.Coordinator.Current);
        Assert.Equal(["host-model"], rig.Coordinator.Current.Pin.Fallbacks.Select(f => f.Model));

        Assert.False(rig.Coordinator.SetFallbacks([new("Anthropic", "model-valid")]));
        Assert.Equal(["model-valid"], rig.Settings.Fallbacks.Select(f => f.Model));
        Assert.Equal("configuration", rig.Coordinator.Current.FallbackSource);
        Assert.Equal(published.Pin.SnapshotHash, rig.Coordinator.Current.Pin.SnapshotHash);
        Assert.Equal(published.Pin.Generation, rig.Coordinator.Current.Pin.Generation);

        var withoutHostFallbacks = new ConfigurationBuilder().AddInMemoryCollection(rig.Configuration.AsEnumerable()
            .Where(pair => !pair.Key.StartsWith("ModelProvider:Fallbacks", StringComparison.Ordinal))).Build();
        var recreated = new ProviderConfigurationCoordinator(withoutHostFallbacks, rig.Settings, rig.Registry, rig.Secrets, rig.Vault);
        Assert.Equal("settings", recreated.Current.FallbackSource);
        Assert.Equal(["model-valid"], recreated.Current.Pin.Fallbacks.Select(f => f.Model));
    }

    private sealed class Rig : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bops-harden13-");
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ModelProvider:Provider"] = "OpenRouter",
                ["ModelProvider:BaseUrl"] = "https://openrouter.test",
                ["ModelProvider:Model"] = "openrouter/free",
                ["ModelProvider:SupportsNativeToolCalling"] = "true",
                ["ModelProvider:ApiKeySecret:Provider"] = "fake",
                ["ModelProvider:ApiKeySecret:Name"] = "OPENROUTER",
            }).Build();
        public SettingsStore Settings { get; }
        public VaultStore Vault { get; }
        public FakeSecrets Secrets { get; } = new();
        public ChatModelRegistry Registry { get; } = new();
        public ChatModelRegistry OpenRouterOnlyRegistry { get; } = new();
        public ProviderCredentialResolver Credentials { get; }
        public ProviderConfigurationCoordinator Coordinator { get; }
        public ExecutionChatModelFactory Models { get; }
        public List<RecordedCall> Calls { get; } = [];

        public Rig()
        {
            Settings = new SettingsStore(Path.Combine(_dir.FullName, "settings.json"), TimeProvider.System);
            Vault = new VaultStore(Path.Combine(_dir.FullName, "vault.dat"),
                VaultCipher.DeriveKey("harden-13-test-master-key"), TimeProvider.System);
            Registry.Register(new PackageId("test.provider"), new FakePackage(["OpenRouter", "Anthropic"], Calls));
            OpenRouterOnlyRegistry.Register(new PackageId("test.provider.openrouter"), new FakePackage(["OpenRouter"], Calls));
            Credentials = new ProviderCredentialResolver(Configuration, Secrets, Vault);
            Coordinator = new ProviderConfigurationCoordinator(Configuration, Settings, Registry, Secrets, Vault);
            Models = new ExecutionChatModelFactory(Registry, Credentials, Coordinator);
        }

        public void Dispose()
        {
            Vault.Dispose();
            _dir.Delete(recursive: true);
        }
    }

    private sealed class FakeSecrets : ISecretProvider
    {
        public string? GetSecret(SecretReference reference) =>
            reference.Name == "OPENROUTER" ? "OPENROUTER_KEY" : null;
    }

    private sealed class FakePackage(IReadOnlyList<string> ids, List<RecordedCall> calls) : IModelProviderPackage
    {
        public IReadOnlyList<string> SupportedProviderIds => ids;
        public IChatModel Create(ChatModelOptions options) => new FakeModel(options, calls);
    }

    private sealed class FakeModel(ChatModelOptions options, List<RecordedCall> calls) : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new(options.Provider, options.Model);
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            lock (calls) calls.Add(new RecordedCall(options.Provider, options.Model, options.BaseUrl, options.ResolvedApiKey));
            return Task.FromResult(new ModelResponse("ok", [], true, null));
        }
    }

    private sealed record RecordedCall(string Provider, string Model, string BaseUrl, string? Key)
    {
        public (string Provider, string Model, string? Key) Identity => (Provider, Model, Key);
    }
}
