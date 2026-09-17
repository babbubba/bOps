// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>Non-secret metadata about a stored vault entry (ADR-0029). Never carries plaintext.</summary>
public sealed record VaultEntryInfo(
    string ProviderId,
    string MaskPrefix,
    string MaskSuffix,
    int PlaintextLength,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
