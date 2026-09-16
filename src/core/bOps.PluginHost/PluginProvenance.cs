// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>Host-verified provenance for the exact bytes installed for a plugin.</summary>
public sealed record PluginProvenance(
    bool SignaturePresent,
    bool Verified,
    string? Publisher,
    string? KeyId,
    string PackageDigestSha256,
    PackageTrustLevel Trust,
    string? FailureReason);
