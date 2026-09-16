// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>Thrown when a host is asked to resolve a secret-provider id it does not support.</summary>
public sealed class SecretProviderNotSupportedException : Exception
{
    /// <summary>Creates an empty exception for serializer/framework compatibility.</summary>
    public SecretProviderNotSupportedException()
    {
        Provider = string.Empty;
    }

    /// <summary>Creates an exception for <paramref name="provider"/> without disclosing a secret name or value.</summary>
    /// <param name="provider">The unsupported provider id.</param>
    public SecretProviderNotSupportedException(string provider)
        : base($"Secret provider '{provider}' is not supported by this host.")
    {
        Provider = provider;
    }

    /// <summary>Creates an exception wrapping an underlying provider failure.</summary>
    public SecretProviderNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Provider = string.Empty;
    }

    /// <summary>The unsupported provider id.</summary>
    public string Provider { get; }
}
