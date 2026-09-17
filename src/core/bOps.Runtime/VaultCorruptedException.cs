// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// Thrown when the encrypted local vault (ADR-0029) cannot be trusted: malformed JSON, an
/// unrecognized schema version, a truncated nonce/tag, or an AEAD tag that fails to verify
/// because the wrong master key was used or the file was tampered with. Never thrown with the
/// affected plaintext, key material, or file path in the message.
/// </summary>
public sealed class VaultCorruptedException : Exception
{
    /// <summary>Creates an empty exception for serializer/framework compatibility.</summary>
    public VaultCorruptedException()
    {
    }

    /// <summary>Creates an exception describing why the vault could not be trusted.</summary>
    /// <param name="message">A bounded, non-sensitive explanation.</param>
    public VaultCorruptedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception wrapping an underlying failure.</summary>
    public VaultCorruptedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
