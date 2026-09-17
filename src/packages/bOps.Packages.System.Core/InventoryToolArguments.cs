// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

internal static class InventoryToolArguments
{
    public static bool TryRead(
        ToolArguments arguments,
        out int limit,
        out int maxOutputBytes,
        out string? error)
    {
        limit = arguments.TryGet<int>("limit", out var requestedLimit)
            ? requestedLimit
            : InventoryToolLimits.DefaultItems;
        maxOutputBytes = arguments.TryGet<int>("maxOutputBytes", out var requestedBytes)
            ? requestedBytes
            : InventoryToolLimits.DefaultOutputBytes;

        if (limit is < 1 or > InventoryToolLimits.MaximumItems)
        {
            error = $"limit must be between 1 and {InventoryToolLimits.MaximumItems}.";
            return false;
        }

        if (maxOutputBytes is < InventoryToolLimits.MinimumOutputBytes or > InventoryToolLimits.MaximumOutputBytes)
        {
            error = $"maxOutputBytes must be between {InventoryToolLimits.MinimumOutputBytes} and {InventoryToolLimits.MaximumOutputBytes}.";
            return false;
        }

        error = null;
        return true;
    }
}
