// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>A read-only projection of persisted evidence coverage, independent of task status and operational authority.</summary>
/// <param name="Level">The evidence classification; even Checked does not certify diagnostic truth.</param>
/// <param name="Reasons">Positive reason counts ordered by code; contains only closed codes and counts.</param>
public sealed record DiagnosticAssuranceResult(DiagnosticAssuranceLevel Level, IReadOnlyList<AssuranceReason> Reasons);
