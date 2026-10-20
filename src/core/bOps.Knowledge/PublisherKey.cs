// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>One public key the operator trusts to sign packs. The trust is the operator's decision, recorded here; nothing a pack says adds to it.</summary>
/// <param name="Publisher">The publisher name signatures refer to.</param>
/// <param name="KeyId">The key id signatures refer to.</param>
/// <param name="PublicKeyPem">The RSA public key as PEM (SubjectPublicKeyInfo). A private key is refused.</param>
/// <param name="Revoked">Whether this key was revoked; a revoked key verifies nothing.</param>
public sealed record PublisherKey(string Publisher, string KeyId, string PublicKeyPem, bool Revoked = false);
