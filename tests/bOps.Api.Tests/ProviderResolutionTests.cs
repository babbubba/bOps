// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0029's precedence rule, tested directly against <see cref="ProviderResolution"/> rather
/// than through HTTP: an explicit <c>ModelProvider__Provider</c> environment variable always wins
/// outright, Settings is the fallback, and the <c>appsettings.json</c>-style default is the
/// fallback of the fallback. Also covers the "CLI compatibility" requirement: with no vault
/// configured at all (<see cref="VaultSecretProvider"/> absent), the original environment-only
/// resolution is completely unaffected.
/// </summary>
public sealed class ProviderResolutionTests : IDisposable
{
    private const string EnvironmentOverrideVariable = "ModelProvider__Provider";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bops-provider-resolution-");
    private readonly EnvironmentSecretProvider _environmentSecretProvider = new();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvironmentOverrideVariable, null);
        _dir.Delete(recursive: true);
    }

    private SettingsStore CreateSettingsStore() => new(Path.Combine(_dir.FullName, "settings.json"), TimeProvider.System);

    private static IConfiguration BuildConfiguration(string? apiKeySecretVariable = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ModelProvider:Provider"] = "OpenRouter",
            ["ModelProvider:BaseUrl"] = "https://openrouter.ai/api/v1",
            ["ModelProvider:Model"] = "openrouter/free",
            ["ModelProvider:ApiKeySecret:Provider"] = "environment",
            ["ModelProvider:ApiKeySecret:Name"] = apiKeySecretVariable ?? "BOPS_TEST_UNSET_MODEL_KEY",
        }).Build();

    [Fact]
    public void ResolveActiveProvider_ReturnsTheConfiguredDefault_WhenNothingElseIsSet()
    {
        var (providerId, source) = ProviderResolution.ResolveActiveProvider(BuildConfiguration(), CreateSettingsStore());

        Assert.Equal("OpenRouter", providerId);
        Assert.Equal(ProviderResolution.Source.Default, source);
    }

    [Fact]
    public void ResolveActiveProvider_PrefersTheSettingsSelection_OverTheConfiguredDefault()
    {
        var settingsStore = CreateSettingsStore();
        settingsStore.SetActiveProviderId("Anthropic");

        var (providerId, source) = ProviderResolution.ResolveActiveProvider(BuildConfiguration(), settingsStore);

        Assert.Equal("Anthropic", providerId);
        Assert.Equal(ProviderResolution.Source.Settings, source);
    }

    [Fact]
    public void ResolveActiveProvider_PrefersTheEnvironmentOverride_OverSettings()
    {
        var settingsStore = CreateSettingsStore();
        settingsStore.SetActiveProviderId("Anthropic");
        Environment.SetEnvironmentVariable(EnvironmentOverrideVariable, "OpenAI");

        var (providerId, source) = ProviderResolution.ResolveActiveProvider(BuildConfiguration(), settingsStore);

        Assert.Equal("OpenRouter", providerId);
        Assert.Equal(ProviderResolution.Source.EnvironmentOverride, source);
    }

    [Fact]
    public void ResolveEffectiveModelOptions_WithNoVaultConfigured_BehavesExactlyLikeTheOriginalEnvironmentOnlyResolution()
    {
        var variableName = $"BOPS_TEST_MODEL_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "sk-from-environment");
        try
        {
            var options = ProviderResolution.ResolveEffectiveModelOptions(
                BuildConfiguration(variableName), CreateSettingsStore(), _environmentSecretProvider, vaultSecretProvider: null);

            Assert.Equal("OpenRouter", options.Provider);
            Assert.Equal("https://openrouter.ai/api/v1", options.BaseUrl);
            Assert.Equal("openrouter/free", options.Model);
            Assert.Equal("sk-from-environment", options.ResolvedApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void ResolveEffectiveModelOptions_FallsBackToTheVault_WhenNoEnvironmentSecretIsConfigured()
    {
        using var vaultStore = new VaultStore(
            Path.Combine(_dir.FullName, "vault.dat"), VaultCipher.DeriveKey("correct-horse-battery-staple-test-key"), TimeProvider.System);
        vaultStore.Set("OpenRouter", "sk-from-vault", expectedVersion: 0);
        var vaultSecretProvider = new VaultSecretProvider(vaultStore);

        var options = ProviderResolution.ResolveEffectiveModelOptions(
            BuildConfiguration(), CreateSettingsStore(), _environmentSecretProvider, vaultSecretProvider);

        Assert.Equal("sk-from-vault", options.ResolvedApiKey);
    }

    [Fact]
    public void ResolveEffectiveModelOptions_PrefersTheEnvironmentSecret_OverTheVault()
    {
        var variableName = $"BOPS_TEST_MODEL_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "sk-from-environment");
        using var vaultStore = new VaultStore(
            Path.Combine(_dir.FullName, "vault.dat"), VaultCipher.DeriveKey("correct-horse-battery-staple-test-key"), TimeProvider.System);
        vaultStore.Set("OpenRouter", "sk-from-vault", expectedVersion: 0);
        var vaultSecretProvider = new VaultSecretProvider(vaultStore);

        try
        {
            var options = ProviderResolution.ResolveEffectiveModelOptions(
                BuildConfiguration(variableName), CreateSettingsStore(), _environmentSecretProvider, vaultSecretProvider);

            Assert.Equal("sk-from-environment", options.ResolvedApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void ResolveEffectiveModelOptions_UsesTheSelectedProvidersStoredProfile()
    {
        var settingsStore = CreateSettingsStore();
        settingsStore.SetActiveProviderId("Anthropic");
        settingsStore.SetProviderProfile("Anthropic", "https://api.anthropic.com", "claude-sonnet-4-5", true, null);

        var options = ProviderResolution.ResolveEffectiveModelOptions(
            BuildConfiguration(), settingsStore, _environmentSecretProvider, vaultSecretProvider: null);

        Assert.Equal("Anthropic", options.Provider);
        Assert.Equal("https://api.anthropic.com", options.BaseUrl);
        Assert.Equal("claude-sonnet-4-5", options.Model);
    }
}
