// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace bOps.Knowledge.Tests;

/// <summary>
/// A publisher with a throw-away RSA key generated in memory for the test run. Nothing here is a real credential: the keys exist
/// only in the process, are never written anywhere and sign only test packs. Production code has no signing path at all.
/// </summary>
internal sealed class TestPublisher
{
    private readonly RSA key;

    private TestPublisher(string publisher, string keyId, int bits = 2048)
    {
        Publisher = publisher;
        KeyId = keyId;
#pragma warning disable CA2000 // The key lives as long as the test process.
        key = RSA.Create(bits);
#pragma warning restore CA2000
    }

    /// <summary>A publisher the trust store knows.</summary>
    internal static TestPublisher Acme { get; } = new("acme", "acme-2026");

    /// <summary>A second trusted publisher, for revocation of one of two.</summary>
    internal static TestPublisher Globex { get; } = new("globex", "globex-2026");

    /// <summary>A publisher that is never put in a trust store.</summary>
    internal static TestPublisher Stranger { get; } = new("stranger", "stranger-1");

    /// <summary>Holds a different key than <see cref="Acme"/> under the same publisher and key id: an impersonation of Acme's key id.</summary>
    internal static TestPublisher AcmeImpostor { get; } = new("acme", "acme-2026");

    internal string Publisher { get; }

    internal string KeyId { get; }

    // An RSA instance is not safe for concurrent use, and test classes run in parallel over the same publishers.
    internal PublisherKey Trusted(bool revoked = false)
    {
        lock (key)
        {
            return new PublisherKey(Publisher, KeyId, key.ExportSubjectPublicKeyInfoPem(), revoked);
        }
    }

    internal string PrivateKeyPem()
    {
        lock (key)
        {
            return key.ExportPkcs8PrivateKeyPem();
        }
    }

    internal static PublisherKey WeakKey(string publisher, string keyId)
    {
        using var weak = RSA.Create(1024);
        return new PublisherKey(publisher, keyId, weak.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>The base64 RSA-PSS signature over the SHA-256 of the manifest bytes.</summary>
    internal string Sign(byte[] manifestBytes)
    {
        lock (key)
        {
            return Convert.ToBase64String(key.SignHash(SHA256.HashData(manifestBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }
    }
}
