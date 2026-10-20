// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>The complete identity of a verified pack: what it is, who published it, and the hash of what it contains.</summary>
/// <param name="PackId">The pack id.</param>
/// <param name="Version">The pack version.</param>
/// <param name="Kind">The kind the signed manifest declares.</param>
/// <param name="Publisher">The publisher whose trusted key verified the signature.</param>
/// <param name="KeyId">The key that verified the signature.</param>
/// <param name="ContentHash">The SHA-256 of the manifest bytes. The manifest commits to every file, so this is the content identity.</param>
public sealed record PackIdentity(string PackId, string Version, PackKind Kind, string Publisher, string KeyId, string ContentHash);
