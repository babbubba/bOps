// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Resolves exact environment-variable names through the <c>environment</c> provider.</summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    private const string ProviderId = "environment";

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

        return Environment.GetEnvironmentVariable(reference.Name);
    }
}
