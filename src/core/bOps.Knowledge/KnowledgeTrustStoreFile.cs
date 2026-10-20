// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>The operator-owned trust store file: public keys and revoked publishers. It holds no private key.</summary>
/// <param name="SchemaVersion">The schema version.</param>
/// <param name="Keys">The trusted public keys.</param>
/// <param name="RevokedPublishers">Publishers whose every key is refused.</param>
internal sealed record KnowledgeTrustStoreFile(int SchemaVersion, IReadOnlyList<PublisherKey> Keys, IReadOnlyList<string> RevokedPublishers);
