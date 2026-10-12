// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace bOps.Knowledge;

/// <summary>
/// The operator-owned set of public keys allowed to sign packs, and the publishers and keys that were revoked. It is immutable: the
/// host replaces the whole store to change trust. It is the only source of trust; a pack's own claims about its publisher or key
/// select a lookup here and nothing more. It holds public keys only; a private key anywhere in it is a configuration error.
/// </summary>
public sealed partial class KnowledgeTrustStore
{
    private readonly Dictionary<(string Publisher, string KeyId), PublisherKey> keys;
    private readonly HashSet<string> revokedPublishers;

    /// <summary>Creates a store from public keys and revoked publishers.</summary>
    /// <exception cref="ArgumentException">An identifier is malformed, a key repeats, or a key is not a valid RSA public key of at least 2048 bits.</exception>
    public KnowledgeTrustStore(IEnumerable<PublisherKey> keys, IEnumerable<string>? revokedPublishers = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        this.keys = [];
        foreach (var key in keys)
        {
            ArgumentNullException.ThrowIfNull(key);
            Validate(key);
            if (!this.keys.TryAdd((key.Publisher, key.KeyId), key))
            {
                throw new ArgumentException($"The trust store lists key {key.KeyId} of {key.Publisher} twice.", nameof(keys));
            }
        }

        this.revokedPublishers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var publisher in revokedPublishers ?? [])
        {
            if (!Identifier().IsMatch(publisher ?? string.Empty))
            {
                throw new ArgumentException("A revoked publisher must be an identifier.", nameof(revokedPublishers));
            }

            this.revokedPublishers.Add(publisher!);
        }
    }

    /// <summary>A store that trusts nothing: every pack is refused as signed by an unknown key.</summary>
    public static KnowledgeTrustStore Empty { get; } = new([]);

    /// <summary>Reads a store from its JSON file.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid trust store.</exception>
    public static KnowledgeTrustStore FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromJson(File.ReadAllText(path));
    }

    /// <summary>Reads a store from JSON.</summary>
    /// <exception cref="InvalidDataException">The text is not a valid trust store.</exception>
    public static KnowledgeTrustStore FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var file = JsonSerializer.Deserialize(json, KnowledgeJsonContext.Default.KnowledgeTrustStoreFile)
                ?? throw new InvalidDataException("The trust store is empty.");
            if (file.SchemaVersion != KnowledgePackFormat.SchemaVersion)
            {
                throw new InvalidDataException("The trust store uses an unsupported schema version.");
            }

            return new KnowledgeTrustStore(file.Keys ?? [], file.RevokedPublishers);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The trust store is not valid.", ex);
        }
    }

    internal static bool IsIdentifier(string? value) => value is not null && Identifier().IsMatch(value);

    internal KeyLookup Find(string publisher, string keyId)
    {
        if (revokedPublishers.Contains(publisher))
        {
            return new KeyLookup(KeyStatus.PublisherRevoked, null);
        }

        if (!keys.TryGetValue((publisher, keyId), out var key))
        {
            return new KeyLookup(KeyStatus.Unknown, null);
        }

        return key.Revoked ? new KeyLookup(KeyStatus.KeyRevoked, null) : new KeyLookup(KeyStatus.Trusted, key);
    }

    private static void Validate(PublisherKey key)
    {
        if (!Identifier().IsMatch(key.Publisher ?? string.Empty) || !Identifier().IsMatch(key.KeyId ?? string.Empty))
        {
            throw new ArgumentException("A publisher and a key id must be identifiers.");
        }

        var pem = key.PublicKeyPem ?? string.Empty;
        if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The trust store holds public keys only; key {key.KeyId} carries private key material.");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < KnowledgePackFormat.MinimumKeyBits)
            {
                throw new ArgumentException($"Key {key.KeyId} is shorter than {KnowledgePackFormat.MinimumKeyBits} bits.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException && ex is not ArgumentNullException)
        {
            throw new ArgumentException($"Key {key.KeyId} is not a valid RSA public key.", ex);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
