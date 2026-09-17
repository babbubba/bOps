// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Resolves provider API keys stored in the encrypted local vault (ADR-0029), provider id <c>vault</c>.</summary>
public sealed class VaultSecretProvider(VaultStore store) : ISecretProvider
{
    private const string ProviderId = "vault";

    /// <inheritdoc />
    public string? GetSecret(SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!string.Equals(reference.Provider, ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecretProviderNotSupportedException(reference.Provider);
        }

        if (string.IsNullOrWhiteSpace(reference.Name))
        {
            throw new ArgumentException("A secret reference name is required.", nameof(reference));
        }

        return store.Resolve(reference.Name);
    }
}
