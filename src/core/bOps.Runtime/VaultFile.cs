// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// The vault's on-disk root (ADR-0029): a schema version, a monotonic <see cref="Version"/> used
/// as an optimistic-concurrency token, and every provider's encrypted entry keyed by provider id.
/// </summary>
public sealed record VaultFile(int SchemaVersion, int Version, Dictionary<string, VaultEntry> Entries);
