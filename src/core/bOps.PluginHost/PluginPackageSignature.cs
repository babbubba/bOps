// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>Creates and verifies detached RSA-PSS/SHA-256 signatures over complete plugin directories.</summary>
public static class PluginPackageSignature
{
    /// <summary>The detached signature file stored beside <c>bops-plugin.json</c>.</summary>
    public const string SignatureFileName = "bops-plugin.sig.json";

    private const string Algorithm = "RSA-PSS-SHA256";

    /// <summary>Signs the current package bytes and writes the detached signature file.</summary>
    public static void Sign(string packageDirectory, string publisher, string keyId, string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        var payload = BuildPayload(packageDirectory);
        var digest = SHA256.HashData(payload);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelope = new PluginSignatureEnvelope(
            1, publisher, keyId, Algorithm, Convert.ToHexStringLower(digest), Convert.ToBase64String(signature));
        File.WriteAllText(
            Path.Combine(packageDirectory, SignatureFileName),
            JsonSerializer.Serialize(envelope, PluginSecurityJsonContext.Default.PluginSignatureEnvelope));
    }

    /// <summary>Verifies package bytes against the detached signature and local trust store.</summary>
    public static PluginProvenance Verify(string packageDirectory, PluginManifest manifest, PluginPublisherTrustStore trustStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(trustStore);

        var payload = BuildPayload(packageDirectory);
        var digest = SHA256.HashData(payload);
        var digestText = Convert.ToHexStringLower(digest);
        var signaturePath = Path.Combine(packageDirectory, SignatureFileName);
        if (!File.Exists(signaturePath))
        {
            return new PluginProvenance(false, false, manifest.Publisher, null, digestText,
                PackageTrustLevel.Unverified, "The package has no detached signature.");
        }

        PluginSignatureEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                File.ReadAllText(signaturePath), PluginSecurityJsonContext.Default.PluginSignatureEnvelope)
                ?? throw new PluginValidationException("The detached signature file is empty.");
        }
        catch (JsonException ex)
        {
            throw new PluginValidationException("The detached signature file is not valid JSON.", ex);
        }

        if (envelope.SchemaVersion != 1 || !string.Equals(envelope.Algorithm, Algorithm, StringComparison.Ordinal))
        {
            throw new PluginValidationException("The detached signature uses an unsupported schema or algorithm.");
        }

        if (!string.Equals(envelope.Publisher, manifest.Publisher, StringComparison.Ordinal))
        {
            throw new PluginValidationException("The signature publisher does not match the plugin manifest publisher.");
        }

        if (!string.Equals(envelope.PackageDigestSha256, digestText, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginValidationException("The plugin package digest does not match its detached signature; package bytes were modified.");
        }

        var trust = trustStore.Find(envelope.Publisher, envelope.KeyId);
        if (trust is null)
        {
            return new PluginProvenance(true, false, envelope.Publisher, envelope.KeyId, digestText,
                PackageTrustLevel.Unverified, "The signing key is not present in the local publisher trust store.");
        }

        bool verified;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(trust.PublicKeyPem);
            verified = rsa.VerifyHash(digest, Convert.FromBase64String(envelope.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (FormatException ex)
        {
            throw new PluginValidationException("The detached signature is not valid base64.", ex);
        }
        catch (CryptographicException ex)
        {
            throw new PluginValidationException("The publisher trust entry or detached signature is cryptographically invalid.", ex);
        }

        if (!verified)
        {
            throw new PluginValidationException("The detached plugin signature is invalid; package bytes or signature were modified.");
        }

        return new PluginProvenance(true, true, envelope.Publisher, envelope.KeyId, digestText, trust.Trust, null);
    }

    private static byte[] BuildPayload(string packageDirectory)
    {
        if (!Directory.Exists(packageDirectory))
        {
            throw new PluginOperationException($"Plugin package directory '{packageDirectory}' does not exist.");
        }

        var root = Path.GetFullPath(packageDirectory);
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(Path.GetFileName(path), SignatureFileName, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var fileDigest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            builder.Append(relative).Append('\n').Append(fileDigest).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}

internal sealed record PluginSignatureEnvelope(
    int SchemaVersion,
    string Publisher,
    string KeyId,
    string Algorithm,
    string PackageDigestSha256,
    string Signature);
