// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

public sealed class VaultCipherTests
{
    private const string MasterSecret = "correct-horse-battery-staple-integration-test-secret";
    private const string ProviderId = "openai";
    private const int SchemaVersion = 1;

    [Fact]
    public void DeriveKey_ProducesA32ByteKey()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);

        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void DeriveKey_IsDeterministic_ForTheSameMasterSecret()
    {
        var first = VaultCipher.DeriveKey(MasterSecret);
        var second = VaultCipher.DeriveKey(MasterSecret);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DeriveKey_ProducesDifferentKeys_ForDifferentMasterSecrets()
    {
        var first = VaultCipher.DeriveKey(MasterSecret);
        var second = VaultCipher.DeriveKey($"{MasterSecret}-different");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Encrypt_ThenDecrypt_RoundTripsThePlaintext()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);

        var sealedEntry = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "sk-super-secret-key");
        var plaintext = VaultCipher.Decrypt(key, ProviderId, SchemaVersion, sealedEntry);

        Assert.Equal("sk-super-secret-key", plaintext);
    }

    [Fact]
    public void Encrypt_NeverReusesANonce_AcrossCalls()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);

        var first = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "value-one");
        var second = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "value-one");

        Assert.NotEqual(first.NonceBase64, second.NonceBase64);
        Assert.NotEqual(first.CiphertextBase64, second.CiphertextBase64);
    }

    [Fact]
    public void Decrypt_ThrowsVaultCorrupted_WhenTheCiphertextIsTampered()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);
        var sealedEntry = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "sk-super-secret-key");
        var tampered = sealedEntry with { CiphertextBase64 = FlipFirstByte(sealedEntry.CiphertextBase64) };

        Assert.Throws<VaultCorruptedException>(() => VaultCipher.Decrypt(key, ProviderId, SchemaVersion, tampered));
    }

    [Fact]
    public void Decrypt_ThrowsVaultCorrupted_WhenTheTagIsTampered()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);
        var sealedEntry = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "sk-super-secret-key");
        var tampered = sealedEntry with { TagBase64 = FlipFirstByte(sealedEntry.TagBase64) };

        Assert.Throws<VaultCorruptedException>(() => VaultCipher.Decrypt(key, ProviderId, SchemaVersion, tampered));
    }

    [Fact]
    public void Decrypt_ThrowsVaultCorrupted_WhenTheWrongKeyIsUsed()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);
        var wrongKey = VaultCipher.DeriveKey($"{MasterSecret}-different");
        var sealedEntry = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "sk-super-secret-key");

        Assert.Throws<VaultCorruptedException>(() => VaultCipher.Decrypt(wrongKey, ProviderId, SchemaVersion, sealedEntry));
    }

    [Fact]
    public void Decrypt_ThrowsVaultCorrupted_WhenTheProviderIdDoesNotMatchWhatWasEncrypted()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);
        var sealedEntry = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "sk-super-secret-key");

        Assert.Throws<VaultCorruptedException>(() => VaultCipher.Decrypt(key, "anthropic", SchemaVersion, sealedEntry));
    }

    [Fact]
    public void Decrypt_ThrowsVaultCorrupted_WhenTheSchemaVersionDoesNotMatchWhatWasEncrypted()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);
        var sealedEntry = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "sk-super-secret-key");

        Assert.Throws<VaultCorruptedException>(() => VaultCipher.Decrypt(key, ProviderId, SchemaVersion + 1, sealedEntry));
    }

    [Fact]
    public void Decrypt_ThrowsVaultCorrupted_WhenTheNonceIsTruncated()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);
        var sealedEntry = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "sk-super-secret-key");
        var truncated = sealedEntry with { NonceBase64 = TruncateLastByte(sealedEntry.NonceBase64) };

        Assert.Throws<VaultCorruptedException>(() => VaultCipher.Decrypt(key, ProviderId, SchemaVersion, truncated));
    }

    [Fact]
    public void Decrypt_ThrowsVaultCorrupted_WhenTheTagIsTruncated()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);
        var sealedEntry = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "sk-super-secret-key");
        var truncated = sealedEntry with { TagBase64 = TruncateLastByte(sealedEntry.TagBase64) };

        Assert.Throws<VaultCorruptedException>(() => VaultCipher.Decrypt(key, ProviderId, SchemaVersion, truncated));
    }

    [Fact]
    public void Decrypt_ThrowsVaultCorrupted_WhenTheValueIsNotValidBase64()
    {
        var key = VaultCipher.DeriveKey(MasterSecret);
        var sealedEntry = VaultCipher.Encrypt(key, ProviderId, SchemaVersion, "sk-super-secret-key");
        var malformed = sealedEntry with { CiphertextBase64 = "not-valid-base64!!" };

        Assert.Throws<VaultCorruptedException>(() => VaultCipher.Decrypt(key, ProviderId, SchemaVersion, malformed));
    }

    private static string FlipFirstByte(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        bytes[0] ^= 0xFF;
        return Convert.ToBase64String(bytes);
    }

    private static string TruncateLastByte(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        return Convert.ToBase64String(bytes[..^1]);
    }
}
