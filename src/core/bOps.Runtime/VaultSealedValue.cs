// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// One AES-256-GCM sealed value (ADR-0029): a random nonce, the ciphertext, and the
/// authentication tag, each base64-encoded. Never carries plaintext.
/// </summary>
public sealed record VaultSealedValue(string NonceBase64, string CiphertextBase64, string TagBase64);
