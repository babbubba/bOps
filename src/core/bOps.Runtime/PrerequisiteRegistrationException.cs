// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>Thrown when a prerequisite check cannot be registered: an invalid descriptor, a duplicate id or a failing provider.</summary>
public sealed class PrerequisiteRegistrationException : Exception
{
    /// <summary>Creates a registration exception.</summary>
    public PrerequisiteRegistrationException(string message) : base(message)
    {
    }

    /// <summary>Creates a registration exception with no message.</summary>
    public PrerequisiteRegistrationException()
    {
    }

    /// <summary>Creates a registration exception wrapping an underlying provider failure.</summary>
    public PrerequisiteRegistrationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
