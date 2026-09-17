// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace bOps.Runtime;

/// <summary>
/// Tracks provider API keys in one AES-256-GCM-encrypted JSON file (ADR-0029). Every write goes
/// to a temp file first and is atomically renamed into place, so a process killed mid-write
/// leaves the previous, valid vault rather than a half-written file the next read would fail to
/// parse. Concurrent in-process writers are serialized; concurrent/stale writers across processes
/// are caught by the optimistic-concurrency <c>expectedVersion</c> token every write requires.
/// </summary>
public sealed class VaultStore(string vaultFilePath, byte[] masterKey, TimeProvider timeProvider) : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The vault's current optimistic-concurrency version token.</summary>
    public int Version => LoadAll().Version;

    /// <summary>Every stored provider's non-secret display metadata, ordered by provider id.</summary>
    public IReadOnlyList<VaultEntryInfo> List() =>
        LoadAll().Entries
            .Select(pair => ToInfo(pair.Key, pair.Value))
            .OrderBy(entry => entry.ProviderId, StringComparer.Ordinal)
            .ToList();

    /// <summary>The stored provider's non-secret display metadata, or <c>null</c> if none is stored.</summary>
    public VaultEntryInfo? Find(string providerId) =>
        LoadAll().Entries.TryGetValue(providerId, out var entry) ? ToInfo(providerId, entry) : null;

    /// <summary>Decrypts and returns the stored plaintext for a provider, or <c>null</c> if none is stored.</summary>
    public string? Resolve(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var file = LoadAll();
        if (!file.Entries.TryGetValue(providerId, out var entry))
        {
            return null;
        }

        var sealedValue = new VaultSealedValue(entry.NonceBase64, entry.CiphertextBase64, entry.TagBase64);
        return VaultCipher.Decrypt(masterKey, providerId, file.SchemaVersion, sealedValue);
    }

    /// <summary>
    /// Stores (or replaces) a provider's secret. Throws <see cref="VaultConcurrencyException"/> if
    /// <paramref name="expectedVersion"/> no longer matches the vault's current version. Returns
    /// the new version.
    /// </summary>
    public int Set(string providerId, string plaintext, int expectedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        _gate.Wait();
        try
        {
            var file = LoadAll();
            if (file.Version != expectedVersion)
            {
                throw new VaultConcurrencyException(expectedVersion, file.Version);
            }

            var now = timeProvider.GetUtcNow();
            var createdUtc = file.Entries.TryGetValue(providerId, out var existing) ? existing.CreatedUtc : now;
            var sealedValue = VaultCipher.Encrypt(masterKey, providerId, file.SchemaVersion, plaintext);
            var mask = SecretMask.Compute(plaintext);

            file.Entries[providerId] = new VaultEntry(
                sealedValue.NonceBase64, sealedValue.CiphertextBase64, sealedValue.TagBase64,
                createdUtc, now, mask.Prefix, mask.Suffix, mask.PlaintextLength);

            var newVersion = file.Version + 1;
            SaveAll(file with { Version = newVersion });
            return newVersion;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes a provider's stored secret, if any. Throws <see cref="VaultConcurrencyException"/>
    /// if <paramref name="expectedVersion"/> no longer matches the vault's current version.
    /// Returns the new version (unchanged if nothing was stored for this provider).
    /// </summary>
    public int Clear(string providerId, int expectedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        _gate.Wait();
        try
        {
            var file = LoadAll();
            if (file.Version != expectedVersion)
            {
                throw new VaultConcurrencyException(expectedVersion, file.Version);
            }

            if (!file.Entries.Remove(providerId))
            {
                return file.Version;
            }

            var newVersion = file.Version + 1;
            SaveAll(file with { Version = newVersion });
            return newVersion;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static VaultEntryInfo ToInfo(string providerId, VaultEntry entry) =>
        new(providerId, entry.MaskPrefix, entry.MaskSuffix, entry.PlaintextLength, entry.CreatedUtc, entry.UpdatedUtc);

    private VaultFile LoadAll()
    {
        if (!File.Exists(vaultFilePath))
        {
            return new VaultFile(CurrentSchemaVersion, 0, []);
        }

        VaultFile? file;
        try
        {
            var json = File.ReadAllText(vaultFilePath);
            file = JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultFile);
        }
        catch (JsonException ex)
        {
            throw new VaultCorruptedException($"'{vaultFilePath}' (vault.dat) is corrupted and could not be parsed.", ex);
        }

        if (file is null)
        {
            throw new VaultCorruptedException($"'{vaultFilePath}' (vault.dat) is empty.");
        }

        if (file.SchemaVersion != CurrentSchemaVersion)
        {
            throw new VaultCorruptedException(
                $"'{vaultFilePath}' (vault.dat) uses schema version {file.SchemaVersion}, which this host does not support.");
        }

        return file;
    }

    private void SaveAll(VaultFile file)
    {
        var directory = Path.GetDirectoryName(vaultFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(file, VaultJsonContext.Default.VaultFile);
        var tempPath = $"{vaultFilePath}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, vaultFilePath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(vaultFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
