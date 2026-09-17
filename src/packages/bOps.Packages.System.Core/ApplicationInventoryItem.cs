// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>One installed application observed from an operating-system package database.</summary>
public sealed record ApplicationInventoryItem(
    string Identity,
    string Name,
    string? Version,
    string? Publisher,
    string Source);
