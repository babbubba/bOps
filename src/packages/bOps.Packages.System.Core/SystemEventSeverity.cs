// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The normalized severity of an operating-system event, most severe first. <see cref="Unknown"/> is an explicit
/// value for a native level bOps cannot place; it is never guessed and never passes a minimum-severity filter.
/// </summary>
public enum SystemEventSeverity
{
    /// <summary>Critical, alert or emergency.</summary>
    Critical,

    /// <summary>An error.</summary>
    Error,

    /// <summary>A warning.</summary>
    Warning,

    /// <summary>Informational, including notices.</summary>
    Information,

    /// <summary>Verbose or debug detail.</summary>
    Verbose,

    /// <summary>The native level is not one bOps recognizes.</summary>
    Unknown,
}
