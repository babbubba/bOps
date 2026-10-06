// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Api;

/// <summary>Resolves only the current credential of the exact pinned provider (ADR-0045 B-1).</summary>
internal sealed class ProviderCredentialResolver(IConfiguration configuration, ISecretProvider environmentSecrets, VaultStore? vault = null)
{
    internal static bool RequiresCredential(string providerId) =>
        providerId is not ("Ollama" or "LlamaCpp");

    internal string? Resolve(string providerId)
    {
        var configuredPrimary = configuration["ModelProvider:Provider"];
        if (providerId.Equals(configuredPrimary, StringComparison.OrdinalIgnoreCase))
        {
            var reference = configuration.GetSection("ModelProvider:ApiKeySecret").Get<SecretReference>();
            if (reference is not null)
            {
                var blockSecret = environmentSecrets.GetSecret(reference);
                if (!string.IsNullOrEmpty(blockSecret)) return blockSecret;
            }
        }
        return vault?.Resolve(providerId);
    }

    internal (bool Available, string Source) Availability(string providerId)
    {
        // The source is safe metadata; the value is never retained in a snapshot or response.
        var configuredPrimary = configuration["ModelProvider:Provider"];
        if (providerId.Equals(configuredPrimary, StringComparison.OrdinalIgnoreCase))
        {
            var reference = configuration.GetSection("ModelProvider:ApiKeySecret").Get<SecretReference>();
            if (reference is not null && !string.IsNullOrEmpty(environmentSecrets.GetSecret(reference)))
                return (true, "environment");
        }
        return vault?.Find(providerId) is not null ? (true, "vault") : (!RequiresCredential(providerId), "none");
    }
}
