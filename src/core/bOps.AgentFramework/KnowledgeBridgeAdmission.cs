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
/// production host.
/// </summary>
internal static class KnowledgeBridgeAdmission
{
    internal static async Task<KnowledgeBridgeOpening> OpenAsync(
        KnowledgeAdmissionService service,
        KnowledgeAdmissionRequest request,
        IAuditSink audit,
        TimeProvider? timeProvider = null,
        KnowledgeReadBudget? budget = null,
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
                bridge = await KnowledgeReadBridge.CreateFromSnapshotAsync(admitted, audit, timeProvider, budget, ct);
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
}
