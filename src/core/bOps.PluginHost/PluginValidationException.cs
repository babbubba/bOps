// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.PluginHost;

/// <summary>
/// Thrown when a <c>bops-plugin.json</c> fails validation: an unsupported schema version, a
/// malformed field, a host-compatibility mismatch, or a manifest that is internally inconsistent
/// (ADR-0020). Never thrown to be caught and coerced into "probably fine" — a rejected manifest
/// stays rejected.
/// </summary>
public sealed class PluginValidationException : Exception
{
    /// <summary>Creates a plugin validation exception.</summary>
    public PluginValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a plugin validation exception with no message. CA1032 requires this constructor to exist, not that it be used.</summary>
    public PluginValidationException()
    {
    }

    /// <summary>Creates a plugin validation exception wrapping an underlying failure.</summary>
    public PluginValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
