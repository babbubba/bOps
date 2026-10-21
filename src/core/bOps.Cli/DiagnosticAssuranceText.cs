// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Abstractions;

namespace bOps.Cli;

/// <summary>Human-readable evidence coverage; never a diagnostic correctness claim.</summary>
internal static class DiagnosticAssuranceText
{
    internal static string Format(DiagnosticAssuranceResult assurance)
    {
        ArgumentNullException.ThrowIfNull(assurance);
        var reasons = string.Join(", ", assurance.Reasons.Select(reason =>
            string.Create(CultureInfo.InvariantCulture, $"{reason.Code}={reason.Count}")));
        return $"Evidence assurance: {assurance.Level} (coverage and check status; does not certify diagnostic truth)"
            + (reasons.Length > 0 ? $" — {reasons}" : string.Empty);
    }
}
