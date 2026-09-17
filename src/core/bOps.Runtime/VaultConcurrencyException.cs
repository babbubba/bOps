// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// Thrown when a vault write's <c>expectedVersion</c> (ADR-0029) does not match the version
/// currently on disk — the state changed since it was last read. Never a silent overwrite.
/// </summary>
public sealed class VaultConcurrencyException : Exception
{
    /// <summary>Creates an empty exception for serializer/framework compatibility.</summary>
    public VaultConcurrencyException()
    {
        ExpectedVersion = 0;
        ActualVersion = 0;
    }

    /// <summary>Creates an exception describing the version mismatch.</summary>
    /// <param name="expectedVersion">The version the caller expected to still be current.</param>
    /// <param name="actualVersion">The version actually on disk.</param>
    public VaultConcurrencyException(int expectedVersion, int actualVersion)
        : base($"The vault changed since it was last read (expected version {expectedVersion}, found {actualVersion}).")
    {
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>Creates an exception describing the version mismatch.</summary>
    /// <param name="message">A bounded, non-sensitive explanation.</param>
    public VaultConcurrencyException(string message)
        : base(message)
    {
        ExpectedVersion = 0;
        ActualVersion = 0;
    }

    /// <summary>Creates an exception wrapping an underlying failure.</summary>
    public VaultConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
        ExpectedVersion = 0;
        ActualVersion = 0;
    }

    /// <summary>The version the caller expected to still be current.</summary>
    public int ExpectedVersion { get; }

    /// <summary>The version actually on disk.</summary>
    public int ActualVersion { get; }
}
