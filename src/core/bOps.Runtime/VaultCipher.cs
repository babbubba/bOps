// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;

namespace bOps.Runtime;

/// <summary>
/// AES-256-GCM encryption for the local vault (ADR-0029), keyed by an HKDF-SHA256-derived key.
/// Additional authenticated data binds every sealed value to the provider id and schema version
/// it was encrypted under, so an entry cannot be relabeled or swapped for another undetected.
/// </summary>
public static class VaultCipher
{
    /// <summary>The derived AES key size, in bytes (AES-256).</summary>
    public const int KeySizeBytes = 32;

    /// <summary>The GCM nonce size, in bytes (96 bits, the standard recommended size).</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>The GCM authentication tag size, in bytes (128 bits).</summary>
    public const int TagSizeBytes = 16;

    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("bOps.Vault.v1");
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("aes-256-gcm-key");

    /// <summary>Derives the 32-byte AES key from an externally supplied master secret via HKDF-SHA256.</summary>
    public static byte[] DeriveKey(string masterSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterSecret);

        var inputKeyMaterial = Encoding.UTF8.GetBytes(masterSecret);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, KeySizeBytes, Salt, Info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputKeyMaterial);
        }
    }

    /// <summary>Encrypts <paramref name="plaintext"/> with a fresh random nonce.</summary>
    public static VaultSealedValue Encrypt(byte[] key, string providerId, int schemaVersion, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        try
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, BuildAssociatedData(providerId, schemaVersion));

            return new VaultSealedValue(Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    /// <summary>
    /// Decrypts <paramref name="sealedValue"/>. Throws <see cref="VaultCorruptedException"/> for
    /// malformed base64, a wrong key, a tampered ciphertext/tag, or a provider id/schema version
    /// that does not match what the value was originally encrypted under.
    /// </summary>
    public static string Decrypt(byte[] key, string providerId, int schemaVersion, VaultSealedValue sealedValue)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(sealedValue);

        byte[] nonce, ciphertext, tag;
        try
        {
            nonce = Convert.FromBase64String(sealedValue.NonceBase64);
            ciphertext = Convert.FromBase64String(sealedValue.CiphertextBase64);
            tag = Convert.FromBase64String(sealedValue.TagBase64);
        }
        catch (FormatException ex)
        {
            throw new VaultCorruptedException("The vault entry is not valid base64.", ex);
        }

        if (nonce.Length != NonceSizeBytes)
        {
            throw new VaultCorruptedException("The vault entry's nonce has an unexpected length.");
        }

        if (tag.Length != TagSizeBytes)
        {
            throw new VaultCorruptedException("The vault entry's authentication tag has an unexpected length.");
        }

        var plaintextBytes = new byte[ciphertext.Length];
        try
        {
            try
            {
                using var aes = new AesGcm(key, TagSizeBytes);
                aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, BuildAssociatedData(providerId, schemaVersion));
            }
            catch (CryptographicException ex)
            {
                throw new VaultCorruptedException(
                    "The vault entry could not be decrypted; the master key is wrong or the vault was tampered with.", ex);
            }

            return Encoding.UTF8.GetString(plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    private static byte[] BuildAssociatedData(string providerId, int schemaVersion) =>
        Encoding.UTF8.GetBytes($"{schemaVersion}:{providerId}");
}
