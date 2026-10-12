// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>What the trust store knows about a publisher and key id a signature named.</summary>
internal enum KeyStatus
{
    /// <summary>No such key is trusted.</summary>
    Unknown,

    /// <summary>The key is trusted and not revoked.</summary>
    Trusted,

    /// <summary>The key was revoked.</summary>
    KeyRevoked,

    /// <summary>The publisher was revoked.</summary>
    PublisherRevoked,
}

/// <summary>The trust store's answer: a status and, for a trusted key, the key.</summary>
internal sealed record KeyLookup(KeyStatus Status, PublisherKey? Key);
