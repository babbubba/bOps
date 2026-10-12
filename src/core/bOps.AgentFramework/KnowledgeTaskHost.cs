// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Runtime;

namespace bOps.AgentFramework;

/// <summary>The pack the host binds a new task to, and the registered source that supplies it.</summary>
/// <param name="Source">The source instance. It must be one the host registered; the admission refuses any other.</param>
/// <param name="Reference">The exact pack and version. There is no "latest".</param>
internal sealed record KnowledgeBindingChoice(IKnowledgePackSource Source, PackReference Reference);

/// <summary>
/// The host's decision about which knowledge a task gets (KNOW-01B, ADR-0056). It is host configuration or policy, never data from a
/// model, a pack or a task argument. The runtime persists its result and asks it again, by pack reference, when a task is resumed.
/// </summary>
internal interface IKnowledgeTaskPolicy
{
    /// <summary>The pack a new task of <paramref name="actor"/> in <paramref name="scope"/> is bound to, or <c>null</c> for no knowledge.</summary>
    KnowledgeBindingChoice? ChooseFor(ActorIdentity actor, KnowledgeTaskScope scope);

    /// <summary>The registered source that supplies an already pinned pack in <paramref name="scope"/>, or <c>null</c> when there is none now.</summary>
    IKnowledgePackSource? SourceFor(KnowledgeTaskScope scope, PackReference reference);
}

/// <summary>
/// Opens the knowledge of a new or resumed task for <c>AgentRunner</c> (KNOW-01B, ADR-0056) over the KNOW-01A admission and the AF-TURN-01B
/// bridge, and nothing else: no model, no loop, no second executor. The tenant comes from the host's authority for the acting identity,
/// the pack from the host policy; admission checks provider, grant, signature, hashes and snapshot, and writes exactly one audit event.
/// A resumed task is admitted again with its pin as the expected content, for the tenant its resuming actor has now, so a republished
/// pack, another tenant, a withdrawn grant, a revoked key or publisher, and a source or snapshot that is gone, all end as a refusal with
/// a cause. Every later read asks the same current authority again. Internal and composed by no production host.
/// </summary>
internal sealed class KnowledgeTaskHost(
    KnowledgeAdmissionService service,
    ITenantAuthority tenants,
    IKnowledgeTaskPolicy policy,
    IAuditSink audit,
    TimeProvider? timeProvider = null,
    KnowledgeReadBudget? budget = null) : IKnowledgeTaskHost
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<KnowledgeTaskOpening> OpenForNewTaskAsync(Guid taskId, ActorIdentity actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // An actor the authority does not know has no tenant and therefore no knowledge: a legacy task, not a refusal.
        if (KnowledgeTaskScope.FromAuthority(tenants, actor, taskId) is not { } scope
            || policy.ChooseFor(actor, scope) is not { } choice)
        {
            return KnowledgeTaskOpening.NotBound;
        }

        var request = new KnowledgeAdmissionRequest { Scope = scope, Actor = actor, Source = choice.Source, Reference = choice.Reference };
        return await OpenAsync(request, ct);
    }

    /// <inheritdoc />
    public async Task<KnowledgeTaskOpening> OpenForResumeAsync(
        Guid taskId, ActorIdentity actor, TaskKnowledgeBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(binding);

        // The stored binding is untrusted data until it is shape-checked again; the tenant is the one the authority gives the resuming
        // actor now, and it must be the one the task was pinned under.
        if (!KnowledgePin.TryFromBinding(binding, out var pin))
        {
            return await RefuseAsync(taskId, actor, "unknown", null, null, KnowledgeAdmissionCode.PinMismatch, ct);
        }

        if (KnowledgeTaskScope.FromAuthority(tenants, actor, taskId) is not { } scope)
        {
            return await RefuseAsync(taskId, actor, pin.TenantId, pin, null, KnowledgeAdmissionCode.TenantNotAllowed, ct);
        }

        if (!string.Equals(scope.Tenant.Value, pin.TenantId, StringComparison.Ordinal))
        {
            return await RefuseAsync(taskId, actor, scope.Tenant.Value, pin, null, KnowledgeAdmissionCode.TenantNotAllowed, ct);
        }

        var reference = new PackReference(pin.PackId, pin.Version);
        if (policy.SourceFor(scope, reference) is not { } source)
        {
            return await RefuseAsync(taskId, actor, scope.Tenant.Value, pin, null, KnowledgeAdmissionCode.ProviderNotAdmitted, ct);
        }

        var request = new KnowledgeAdmissionRequest
        {
            Scope = scope, Actor = actor, Source = source, Reference = reference, ExpectedPin = pin,
        };
        return await OpenAsync(request, ct);
    }

    private async Task<KnowledgeTaskOpening> OpenAsync(KnowledgeAdmissionRequest request, CancellationToken ct)
    {
        var opening = await KnowledgeBridgeAdmission.OpenAsync(service, request, audit, clock, budget, authorizeEveryRead: true, ct);
        if (opening.Bridge is not { } bridge || opening.Result.Pack is not { } admitted)
        {
            opening.Bridge?.Dispose();
            return KnowledgeTaskOpening.Refused(opening.Result.Code);
        }

        return KnowledgeTaskOpening.Bound(bridge, admitted.Pin.ToBinding(request.Actor, admitted.Snapshot.Id));
    }

    /// <summary>A refusal found before the admission service could be asked: audited like any other attempt, once, with identifiers that passed their shape check.</summary>
    private async Task<KnowledgeTaskOpening> RefuseAsync(
        Guid taskId, ActorIdentity actor, string tenant, KnowledgePin? pin, string? providerId, KnowledgeAdmissionCode code, CancellationToken ct)
    {
        await audit.WriteAsync(new KnowledgePackAdmissionAuditEvent
        {
            TimestampUtc = clock.GetUtcNow(),
            Node = NodeId.Local,
            TaskId = taskId,
            Actor = actor,
            TenantId = tenant,
            ProviderId = providerId,
            PackId = pin?.PackId,
            PackVersion = pin?.Version,
            PackKind = pin?.Kind.ToString(),
            Publisher = pin?.Publisher,
            KeyId = pin?.KeyId,
            ContentHash = pin?.ContentHash,
            Code = code,
        }, ct);
        return KnowledgeTaskOpening.Refused(code);
    }
}
