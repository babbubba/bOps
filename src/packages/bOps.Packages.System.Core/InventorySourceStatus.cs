// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>Whether one operating-system inventory source could provide observations.</summary>
public enum InventorySourceStatus
{
    /// <summary>The source was read successfully, including when it contained zero items.</summary>
    Available,

    /// <summary>The source was readable, but one or more entries could not be inspected.</summary>
    Partial,

    /// <summary>The source exists but could not be read because of permissions or I/O failure.</summary>
    Unavailable,

    /// <summary>The source was detected but this bOps version does not parse its format.</summary>
    Unsupported,

    /// <summary>The source does not apply to this machine or operating-system configuration.</summary>
    NotApplicable,
}
