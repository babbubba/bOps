// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0029: every write is atomic (temp file + rename), guarded by an optimistic-concurrency
/// version token, and a corrupted or tampered file fails closed rather than silently losing or
/// misreporting a stored secret.
/// </summary>
public sealed class VaultStoreTests : IDisposable
{
    private const string MasterSecret = "correct-horse-battery-staple-integration-test-secret";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bops-vault-store-");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
    private string VaultPath => Path.Combine(_dir.FullName, "vault.dat");

    public void Dispose() => _dir.Delete(recursive: true);

    private VaultStore CreateStore() => new(VaultPath, VaultCipher.DeriveKey(MasterSecret), _time);

    [Fact]
    public void Version_IsZero_WhenNoVaultFileExistsYet()
    {
        using var store = CreateStore();

        Assert.Equal(0, store.Version);
    }

    [Fact]
    public void Set_ThenANewStoreInstance_ResolvesTheSamePlaintext()
    {
        using (var store = CreateStore())
        {
            store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);
        }

        using var reopened = CreateStore();
        Assert.Equal("sk-abcdefghijklmnop", reopened.Resolve("openai"));
    }

    [Fact]
    public void Set_ReturnsTheIncrementedVersion()
    {
        using var store = CreateStore();

        var version = store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);

        Assert.Equal(1, version);
        Assert.Equal(1, store.Version);
    }

    [Fact]
    public void Set_CapturesTheMask_WithoutStoringPlaintextInDisplayMetadata()
    {
        using var store = CreateStore();

        store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);

        var info = store.Find("openai");
        Assert.NotNull(info);
        Assert.Equal("sk-abc", info!.MaskPrefix);
        Assert.Equal("mnop", info.MaskSuffix);
        Assert.DoesNotContain("sk-abcdefghijklmnop", File.ReadAllText(VaultPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_WithAStaleExpectedVersion_ThrowsVaultConcurrency_AndDoesNotOverwrite()
    {
        using var store = CreateStore();
        store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);

        var ex = Assert.Throws<VaultConcurrencyException>(() => store.Set("openai", "sk-replacement-value", expectedVersion: 0));

        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
        Assert.Equal("sk-abcdefghijklmnop", store.Resolve("openai"));
    }

    [Fact]
    public void Set_Replacing_UpdatesUpdatedUtc_ButKeepsCreatedUtc()
    {
        using var store = CreateStore();
        store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);
        var createdAt = store.Find("openai")!.CreatedUtc;

        _time.Advance(TimeSpan.FromMinutes(5));
        store.Set("openai", "sk-replacement-value!", expectedVersion: 1);

        var info = store.Find("openai");
        Assert.Equal(createdAt, info!.CreatedUtc);
        Assert.Equal(_time.GetUtcNow(), info.UpdatedUtc);
    }

    [Fact]
    public void Clear_RemovesTheEntry()
    {
        using var store = CreateStore();
        store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);

        store.Clear("openai", expectedVersion: 1);

        Assert.Null(store.Find("openai"));
        Assert.Null(store.Resolve("openai"));
    }

    [Fact]
    public void Clear_WithAStaleExpectedVersion_ThrowsVaultConcurrency()
    {
        using var store = CreateStore();
        store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);

        Assert.Throws<VaultConcurrencyException>(() => store.Clear("openai", expectedVersion: 0));
    }

    [Fact]
    public void Resolve_ReturnsNull_ForAnUnknownProvider()
    {
        using var store = CreateStore();

        Assert.Null(store.Resolve("nonexistent"));
    }

    [Fact]
    public void List_OrdersEntriesByProviderId()
    {
        using var store = CreateStore();
        store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);
        store.Set("anthropic", "sk-qrstuvwxyzabcdef", expectedVersion: 1);

        var ids = store.List().Select(entry => entry.ProviderId).ToArray();

        Assert.Equal(["anthropic", "openai"], ids);
    }

    [Fact]
    public void Load_OfACorruptedVaultFile_ThrowsVaultCorrupted_RatherThanSilentlyLosingStoredSecrets()
    {
        File.WriteAllText(VaultPath, "{ this is not valid json");
        using var store = CreateStore();

        Assert.Throws<VaultCorruptedException>(() => store.Version);
    }

    [Fact]
    public void Resolve_WithTheWrongMasterKey_ThrowsVaultCorrupted_RatherThanReturningGarbage()
    {
        using (var store = CreateStore())
        {
            store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);
        }

        using var wrongKeyStore = new VaultStore(VaultPath, VaultCipher.DeriveKey($"{MasterSecret}-different"), _time);
        Assert.Throws<VaultCorruptedException>(() => wrongKeyStore.Resolve("openai"));
    }

    [Fact]
    public void Set_RestrictsVaultFilePermissions_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var store = CreateStore();
        store.Set("openai", "sk-abcdefghijklmnop", expectedVersion: 0);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(VaultPath));
    }

    [Fact]
    public async Task ConcurrentSets_ForDifferentProviders_BothSucceed_WithoutLosingAnEntry()
    {
        using var store = CreateStore();
        store.Set("seed", "sk-seed-value-0123456", expectedVersion: 0);

        await Task.WhenAll(
            Task.Run(() => RetryOnConflict(store, "openai", "sk-openai-value-01234")),
            Task.Run(() => RetryOnConflict(store, "anthropic", "sk-anthropic-value-01")));

        Assert.Equal("sk-openai-value-01234", store.Resolve("openai"));
        Assert.Equal("sk-anthropic-value-01", store.Resolve("anthropic"));
        Assert.Equal("sk-seed-value-0123456", store.Resolve("seed"));
    }

    private static void RetryOnConflict(VaultStore store, string providerId, string plaintext)
    {
        while (true)
        {
            try
            {
                store.Set(providerId, plaintext, store.Version);
                return;
            }
            catch (VaultConcurrencyException)
            {
            }
        }
    }
}
