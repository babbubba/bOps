// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>Local operator-owned mapping from publisher/key ids to RSA public keys and trust.</summary>
public sealed class PluginPublisherTrustStore(string filePath)
{
    public PluginPublisherTrust? Find(string publisher, string keyId) =>
        Load().FirstOrDefault(entry =>
            string.Equals(entry.Publisher, publisher, StringComparison.Ordinal) &&
            string.Equals(entry.KeyId, keyId, StringComparison.Ordinal));

    private List<PluginPublisherTrust> Load()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(filePath), PluginSecurityJsonContext.Default.ListPluginPublisherTrust) ?? [];
        }
        catch (JsonException ex)
        {
            throw new PluginOperationException($"Publisher trust store '{filePath}' is not valid JSON.", ex);
        }
    }
}

/// <summary>One trusted publisher key. The trust level is assigned locally, never by the package.</summary>
public sealed record PluginPublisherTrust(
    string Publisher,
    string KeyId,
    string PublicKeyPem,
    PackageTrustLevel Trust);
