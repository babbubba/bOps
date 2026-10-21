// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>A closed reason and its positive affected-item count; it carries no diagnostic text.</summary>
/// <param name="Code">The classification.</param>
/// <param name="Count">The number of affected items, using the unit documented by <paramref name="Code"/>.</param>
public sealed record AssuranceReason(AssuranceReasonCode Code, int Count);
