// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>One hardware or operating-system device observation.</summary>
public sealed record DeviceInventoryItem(
    string Identity,
    string Category,
    string Name,
    string? Vendor,
    string? Model,
    string? Status,
    string Source);
