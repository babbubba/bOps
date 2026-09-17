// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>Thrown when the non-secret Settings file (ADR-0029, active provider selection) cannot be parsed.</summary>
public sealed class SettingsCorruptedException : Exception
{
    /// <summary>Creates an empty exception for serializer/framework compatibility.</summary>
    public SettingsCorruptedException()
    {
    }

    /// <summary>Creates an exception describing why the Settings file could not be parsed.</summary>
    /// <param name="message">A bounded explanation.</param>
    public SettingsCorruptedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception wrapping an underlying failure.</summary>
    public SettingsCorruptedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
