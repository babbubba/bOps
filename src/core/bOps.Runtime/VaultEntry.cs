// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// One encrypted vault entry (ADR-0029) as persisted on disk: the AES-256-GCM sealed value plus
/// non-secret display metadata captured once, at write time. Never carries plaintext.
/// </summary>
public sealed record VaultEntry(
    string NonceBase64,
    string CiphertextBase64,
    string TagBase64,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string MaskPrefix,
    string MaskSuffix,
    int PlaintextLength);
