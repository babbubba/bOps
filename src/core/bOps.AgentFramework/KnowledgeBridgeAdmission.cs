// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Knowledge;

namespace bOps.AgentFramework;

/// <summary>What opening a pack for a task produced: the bridge when it was admitted, and the single result that was audited.</summary>
/// <param name="Bridge">The bridge over the verified snapshot, or <c>null</c> when the pack was refused.</param>
/// <param name="Result">The audited outcome.</param>
internal sealed record KnowledgeBridgeOpening(KnowledgeReadBridge? Bridge, KnowledgeAdmissionResult Result);

/// <summary>
/// Opens a pack for one task end to end (KNOW-01A): host admission (registered provider, grant, signature, hashes, tenant snapshot),
/// then the framework's skills source over that snapshot with the pins and the skill checks of the bridge, and exactly one audit
/// event for the whole attempt, accepted or refused. A reason the content engine finds after admission (an invalid skill) turns the
/// attempt into a refusal before anything is audited or served. It composes nothing into a MAF context pipeline and is used by no
/// production host. With <c>authorizeEveryRead</c> the bridge asks <see cref="KnowledgeAdmissionService.Reauthorize"/> and the snapshot before
/// each read (KNOW-01B), so a revocation reaches a task that is already running.
/// </summary>
internal static class KnowledgeBridgeAdmission
{
    internal static async Task<KnowledgeBridgeOpening> OpenAsync(
        KnowledgeAdmissionService service,
        KnowledgeAdmissionRequest request,
        IAuditSink audit,
        TimeProvider? timeProvider = null,
        KnowledgeReadBudget? budget = null,
        bool authorizeEveryRead = false,
        Func<KnowledgeAdmissionCode?>? scopeAuthority = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(audit);

        var result = await service.AdmitAsync(request, ct);
        KnowledgeReadBridge? bridge = null;
        if (result.Pack is { } admitted)
        {
            try
            {
#pragma warning disable CA2000 // Ownership of the bridge passes to the caller in the returned opening.
                bridge = await KnowledgeReadBridge.CreateFromSnapshotAsync(
                    admitted, audit, timeProvider, budget,
                    authorizeEveryRead ? () => scopeAuthority?.Invoke() ?? CurrentAuthority(service, request, admitted) : null, ct);
#pragma warning restore CA2000
            }
            catch (KnowledgePackIntegrityException ex)
            {
                result = result.Refuse(ex.Code);
            }
        }

        await service.WriteAuditAsync(audit, request, result, ct);
        return new KnowledgeBridgeOpening(bridge, result);
    }

    /// <summary>
    /// The authority that applies <em>now</em> to an admitted pack (KNOW-01B, ADR-0056): the registration, grant and trust the host holds at
    /// this moment, then the integrity of the snapshot. <c>null</c> means nothing was withdrawn. The pin is never the answer.
    /// </summary>
    private static KnowledgeAdmissionCode? CurrentAuthority(
        KnowledgeAdmissionService service, KnowledgeAdmissionRequest request, AdmittedKnowledgePack admitted) =>
        service.Reauthorize(request.Scope, request.Source, admitted.Pin) switch
        {
            KnowledgeAdmissionCode.Admitted => admitted.Snapshot.VerifyIntegrity(),
            var withdrawn => withdrawn,
        };
}
