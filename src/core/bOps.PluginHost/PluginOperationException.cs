// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.PluginHost;

/// <summary>
/// Thrown when an install/enable/disable/remove operation cannot proceed: an unknown id, an id
/// already installed, a corrupted <c>plugins.json</c>, or an entry type that does not implement
/// a recognized package contract (ADR-0020).
/// </summary>
public sealed class PluginOperationException : Exception
{
    /// <summary>Creates a plugin operation exception.</summary>
    public PluginOperationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a plugin operation exception with no message. CA1032 requires this constructor to exist, not that it be used.</summary>
    public PluginOperationException()
    {
    }

    /// <summary>Creates a plugin operation exception wrapping an underlying failure.</summary>
    public PluginOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
