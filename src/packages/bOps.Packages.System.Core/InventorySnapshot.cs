// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>A bounded operating-system inventory plus the status of every consulted source.</summary>
public sealed record InventorySnapshot<T>(
    IReadOnlyList<T> Items,
    IReadOnlyList<InventorySourceResult> Sources,
    bool CollectionTruncated = false);
