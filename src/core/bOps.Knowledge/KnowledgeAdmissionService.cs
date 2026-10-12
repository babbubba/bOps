// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>
/// Admits a pack for a task (ADR-0055). In order, and failing closed at the first refusal: the source must be a registered instance
/// for this tenant; the host must have granted this exact pack to this tenant; only then are bytes read; the signature, the manifest
/// and every byte are verified; the signed kind must be the granted kind and one the provider may serve; a resumed task's pin must
/// match; and the verified bytes are written to the tenant's snapshot. Nothing is read from the source before the grant exists, so a
/// tenant cannot learn what it is not entitled to by asking for it.
/// The service keeps no state of its own: no cache, no shared mutable data. Auditing is separate so that a consumer that finds a
/// further reason to refuse (a skill the content engine rejects) can still record exactly one outcome.
/// </summary>
public sealed class KnowledgeAdmissionService
{
    private readonly KnowledgeProviderRegistry registry;
    private readonly KnowledgeGrants grants;
    private readonly KnowledgePackVerifier verifier;
    private readonly KnowledgeSnapshotStore snapshots;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the service from the host's registry, grants, verifier and snapshot store.</summary>
    public KnowledgeAdmissionService(
        KnowledgeProviderRegistry registry,
        KnowledgeGrants grants,
        KnowledgePackVerifier verifier,
        KnowledgeSnapshotStore snapshots,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(snapshots);
        this.registry = registry;
        this.grants = grants;
        this.verifier = verifier;
        this.snapshots = snapshots;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Admits the requested pack, or returns why not. Cancellation propagates; everything else that can go wrong with a pack is a result.</summary>
    public async Task<KnowledgeAdmissionResult> AdmitAsync(KnowledgeAdmissionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = request.Scope;

        var provider = registry.Admit(request.Source, scope);
        if (provider.Code != KnowledgeAdmissionCode.Admitted)
        {
            return Refused(provider.Code, provider.ProviderId);
        }

        if (!grants.TryGetGrantedKind(scope.Tenant, request.Reference, out var grantedKind))
        {
            return Refused(KnowledgeAdmissionCode.NotEntitled, provider.ProviderId);
        }

        if (!provider.MayServe(grantedKind))
        {
            return Refused(KnowledgeAdmissionCode.KindNotAllowed, provider.ProviderId, grantedKind);
        }

        RawKnowledgePack? raw;
        try
        {
            raw = await provider.Source!.OpenAsync(request.Reference, scope.Tenant, ct);
        }
#pragma warning disable CA1031 // A source is outside code: whatever it throws, the admission is refused, never propagated into the task.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return Refused(KnowledgeAdmissionCode.SourceUnavailable, provider.ProviderId, grantedKind);
        }

        if (raw is null)
        {
            return Refused(KnowledgeAdmissionCode.SourceUnavailable, provider.ProviderId, grantedKind);
        }

        var verification = verifier.Verify(raw);
        if (verification.Pack is not { } pack)
        {
            return new KnowledgeAdmissionResult(
                verification.Code, null, provider.ProviderId, verification.Kind, verification.Publisher, verification.KeyId, verification.ContentHash);
        }

        KnowledgeAdmissionResult Fail(KnowledgeAdmissionCode code) =>
            new(code, null, provider.ProviderId, pack.Identity.Kind, pack.Identity.Publisher, pack.Identity.KeyId, pack.Identity.ContentHash);

        if (!string.Equals(pack.Identity.PackId, request.Reference.PackId, StringComparison.Ordinal)
            || !string.Equals(pack.Identity.Version, request.Reference.Version, StringComparison.Ordinal))
        {
            return Fail(KnowledgeAdmissionCode.IdentityMismatch);
        }

        if (pack.Identity.Kind != grantedKind)
        {
            return Fail(KnowledgeAdmissionCode.NotEntitled);
        }

        if (request.ExpectedPin is { } expected && !Matches(expected, scope.Tenant, pack.Identity))
        {
            return Fail(KnowledgeAdmissionCode.PinMismatch);
        }

        var snapshot = snapshots.Acquire(scope.Tenant, pack);
        if (snapshot is null)
        {
            return Fail(KnowledgeAdmissionCode.SnapshotTampered);
        }

        return new KnowledgeAdmissionResult(
            KnowledgeAdmissionCode.Admitted,
            new AdmittedKnowledgePack(scope, provider.ProviderId!, provider.Trust, snapshot),
            provider.ProviderId, pack.Identity.Kind, pack.Identity.Publisher, pack.Identity.KeyId, pack.Identity.ContentHash);
    }

    /// <summary>Writes the audit event for an attempt. Every outcome is written, refusals included.</summary>
    public Task WriteAuditAsync(IAuditSink audit, KnowledgeAdmissionRequest request, KnowledgeAdmissionResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        return audit.WriteAsync(new KnowledgePackAdmissionAuditEvent
        {
            TimestampUtc = timeProvider.GetUtcNow(),
            Node = NodeId.Local,
            TaskId = request.Scope.TaskId,
            Actor = request.Actor,
            TenantId = request.Scope.Tenant.Value,
            ProviderId = result.ProviderId,
            PackId = request.Reference.PackId,
            PackVersion = request.Reference.Version,
            PackKind = result.Kind?.ToString(),
            Publisher = result.Publisher,
            KeyId = result.KeyId,
            ContentHash = result.ContentHash,
            Code = result.Code,
        }, ct);
    }

    private static bool Matches(KnowledgePin expected, TenantId tenant, PackIdentity actual) =>
        expected.SchemaVersion == KnowledgePackFormat.SchemaVersion
        && string.Equals(expected.TenantId, tenant.Value, StringComparison.Ordinal)
        && string.Equals(expected.PackId, actual.PackId, StringComparison.Ordinal)
        && string.Equals(expected.Version, actual.Version, StringComparison.Ordinal)
        && expected.Kind == actual.Kind
        && string.Equals(expected.Publisher, actual.Publisher, StringComparison.Ordinal)
        && string.Equals(expected.ContentHash, actual.ContentHash, StringComparison.Ordinal);

    private static KnowledgeAdmissionResult Refused(KnowledgeAdmissionCode code, string? providerId, PackKind? kind = null) =>
        new(code, null, providerId, kind, null, null, null);
}
