// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// The knowledge a task is bound to (KNOW-01B, ADR-0056): which tenant, under whose authority, and exactly which verified pack
/// content. It is plain data, created by the runtime from the host's admission result when the task is created and persisted with
/// the task, so a resumed attempt can ask for exactly this content again and be refused anything else. It is never built from a
/// model value, and it is never an authorisation: a resume and every read check the current authority again.
/// A task without a binding is a legacy task and gets no knowledge.
/// </summary>
/// <param name="SchemaVersion">The binding schema version.</param>
/// <param name="AuthorizedBy">The identity the host authorised the binding for when the task was created.</param>
/// <param name="TenantId">The knowledge-isolation tenant the host's authority assigned to the task. It cannot change.</param>
/// <param name="PackId">The pack id.</param>
/// <param name="Version">The exact pack version; there is no "latest".</param>
/// <param name="Kind">The kind the signed manifest declares: <c>Knowledge</c>, <c>Experience</c> or <c>Skill</c>.</param>
/// <param name="Publisher">The publisher whose trusted key verified the pack.</param>
/// <param name="KeyId">The key that verified the pack.</param>
/// <param name="ManifestHash">The SHA-256 of the signed manifest, which commits to every payload file.</param>
/// <param name="ResourceHashes">Every payload file path with its SHA-256, in path order.</param>
/// <param name="SnapshotId">The identifier of the verified, tenant-private, content-addressed snapshot that served the pack.</param>
public sealed record TaskKnowledgeBinding(
    int SchemaVersion,
    ActorIdentity AuthorizedBy,
    string TenantId,
    string PackId,
    string Version,
    string Kind,
    string Publisher,
    string KeyId,
    string ManifestHash,
    IReadOnlyDictionary<string, string> ResourceHashes,
    string SnapshotId);
