// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>The explicit outcome of reading one inventory source.</summary>
public sealed record InventorySourceResult(
    string Name,
    InventorySourceStatus Status,
    string? Detail);
