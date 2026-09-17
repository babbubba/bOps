// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

public sealed class VaultSecretProviderTests : IDisposable
{
    private const string MasterSecret = "correct-horse-battery-staple-integration-test-secret";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bops-vault-secret-provider-");
    private readonly VaultStore _store;

    public VaultSecretProviderTests()
    {
        _store = new VaultStore(Path.Combine(_dir.FullName, "vault.dat"), VaultCipher.DeriveKey(MasterSecret), TimeProvider.System);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Delete(recursive: true);
    }

    [Fact]
    public void GetSecret_ResolvesAStoredProviderKey()
    {
        _store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);
        var provider = new VaultSecretProvider(_store);

        var value = provider.GetSecret(new SecretReference("vault", "openai"));

        Assert.Equal("sk-abcdefghijklmnop", value);
    }

    [Fact]
    public void GetSecret_ReturnsNull_WhenNothingIsStoredForThatProvider()
    {
        var provider = new VaultSecretProvider(_store);

        Assert.Null(provider.GetSecret(new SecretReference("vault", "nonexistent")));
    }

    [Fact]
    public void GetSecret_RejectsUnknownProviders_WithoutIncludingTheSecretName()
    {
        var provider = new VaultSecretProvider(_store);

        var exception = Assert.Throws<SecretProviderNotSupportedException>(() =>
            provider.GetSecret(new SecretReference("environment", "sensitive-name")));

        Assert.DoesNotContain("sensitive-name", exception.Message, StringComparison.Ordinal);
    }
}
