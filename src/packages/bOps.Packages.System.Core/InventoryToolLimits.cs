// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>Shared finite limits for <c>system.apps</c> and <c>system.devices</c>.</summary>
public static class InventoryToolLimits
{
    /// <summary>Default number of items returned to the caller.</summary>
    public const int DefaultItems = 100;

    /// <summary>Maximum number of items a caller may request.</summary>
    public const int MaximumItems = 500;

    /// <summary>Maximum records an OS collector may observe before stopping.</summary>
    public const int CollectionItems = 2_000;

    /// <summary>Default maximum UTF-8 output size.</summary>
    public const int DefaultOutputBytes = 32_768;

    /// <summary>Smallest supported UTF-8 output budget.</summary>
    public const int MinimumOutputBytes = 4_096;

    /// <summary>Largest supported UTF-8 output budget.</summary>
    public const int MaximumOutputBytes = 65_536;
}
