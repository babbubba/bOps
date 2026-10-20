// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using bOps.Abstractions;
using bOps.Packages.Providers.LlamaCpp;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

public sealed class KnowledgeBenchmarkConfigurationTests
{
    [Fact]
    public void ExistingServicesResolveExternalProfileAndTestVaultWithoutModelCalls()
    {
        using var fixture = new LocalConfiguration();
        var options = KnowledgeHostCanaryLiveTests.LocalQwenConfiguration.Read(fixture.ConfigurationPath, fixture.Secrets);
        Assert.Equal("LlamaCpp", options.Provider);
        Assert.Equal(KnowledgeBenchmark.ModelId, options.Model);
        Assert.True(options.ResolvedApiKey == LocalConfiguration.ProviderKey);
        Assert.True(options.ResolvedApiKey != LocalConfiguration.MasterKey);
        Assert.Null(options.ApiKeySecret);
        KnowledgeBenchmark.RunnerOptions.Validate(options.EffectiveRequestTimeout);
        using var handler = new RefuseRequests();
        using var client = new HttpClient(handler, disposeHandler: false);
        var model = new LlamaCppProviderPackage(new ClientFactory(client)).Create(options);
        Assert.Equal(KnowledgeBenchmark.ModelId, model.Descriptor.ModelId);
    }

    [Fact]
    public void MissingConfigurationVaultOrCredentialRefusesWithoutFallbackOrSecretDiagnostics()
    {
        using var fixture = new LocalConfiguration();
        Reject(Path.Combine(fixture.DirectoryPath, "absent.json"), fixture.Secrets, "configuration absent");
        Reject(fixture.ConfigurationPath, new TestSecrets(null), "master key unavailable");
        Reject(fixture.ConfigurationPath, new TestSecrets("incorrect-test-master-key-for-offline-only"), "credential unavailable");
        File.WriteAllText(fixture.VaultPath, "invalid-test-vault-payload");
        Reject(fixture.ConfigurationPath, fixture.Secrets, "Vault unavailable");
        File.Delete(fixture.VaultPath);
        Reject(fixture.ConfigurationPath, fixture.Secrets, "Vault unavailable");
        using (var vault = new VaultStore(fixture.VaultPath, VaultCipher.DeriveKey(LocalConfiguration.MasterKey), TimeProvider.System))
            vault.Set("OtherProvider", LocalConfiguration.ProviderKey, 0);
        Reject(fixture.ConfigurationPath, fixture.Secrets, "credential unavailable");
        File.Delete(fixture.SettingsPath);
        Reject(fixture.ConfigurationPath, fixture.Secrets, "provider configuration absent");
    }

    private static void Reject(string path, ISecretProvider secrets, string cause)
    {
        var error = Assert.Throws<InvalidOperationException>(() => KnowledgeHostCanaryLiveTests.LocalQwenConfiguration.Read(path, secrets));
        Assert.Contains(cause, error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(LocalConfiguration.ProviderKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(LocalConfiguration.MasterKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-test-vault-payload", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class LocalConfiguration : IDisposable
    {
        internal const string MasterKey = "offline-only-master-key-not-a-real-credential";
        internal const string ProviderKey = "offline-only-provider-key";
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "bops-config-test-" + Guid.NewGuid().ToString("N"));
        internal string ConfigurationPath => Path.Combine(DirectoryPath, "host-test.json");
        internal string SettingsPath => Path.Combine(DirectoryPath, "profiles-test.json");
        internal string VaultPath => Path.Combine(DirectoryPath, "vault-test.dat");
        internal ISecretProvider Secrets { get; } = new TestSecrets(MasterKey);

        internal LocalConfiguration()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(new
            {
                Settings = new { FilePath = "profiles-test.json" },
                Vault = new { FilePath = "vault-test.dat", MasterKeySecret = new { Provider = "environment", Name = "offline-master" } },
                ModelProvider = new { Provider = "LlamaCpp", ApiKeySecret = new { Provider = "environment", Name = "offline-missing-provider" } },
            }));
            new SettingsStore(SettingsPath, TimeProvider.System).SetProviderProfile("LlamaCpp", "http://127.0.0.1:1/v1",
                KnowledgeBenchmark.ModelId, false, null);
            using var vault = new VaultStore(VaultPath, VaultCipher.DeriveKey(MasterKey), TimeProvider.System);
            vault.Set("LlamaCpp", ProviderKey, 0);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class TestSecrets(string? masterKey) : ISecretProvider
    {
        public string? GetSecret(SecretReference reference) => reference.Name == "offline-master" ? masterKey : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RefuseRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Offline configuration validation must never send HTTP requests.");
    }
}
