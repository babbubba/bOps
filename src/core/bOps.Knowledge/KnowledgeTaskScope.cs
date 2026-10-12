// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>
/// The host's decision about which tenant an actor acts for. It is the only way to obtain a <see cref="KnowledgeTaskScope"/> other
/// than the Local Agent's own. A Coordinator implements it over its authenticated principal; the answer never comes from the model.
/// </summary>
public interface ITenantAuthority
{
    /// <summary>The tenant the actor acts for, or <c>null</c> when the actor has none (the caller then gets no scope and no knowledge).</summary>
    TenantId? Resolve(ActorIdentity actor);
}

/// <summary>
/// The tenant and task a knowledge read is bound to. It has no public constructor and is a class, not a record, so it cannot be
/// copied with a changed tenant: the tenant is whatever the host's <see cref="ITenantAuthority"/> answered when the task started.
/// Everything that stores or serves knowledge (grants, provider registrations, snapshots, pins, audit) takes one.
/// </summary>
public sealed class KnowledgeTaskScope
{
    private KnowledgeTaskScope(TenantId tenant, Guid taskId)
    {
        Tenant = tenant;
        TaskId = taskId;
    }

    /// <summary>The tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>The task the scope was issued for. Scopes are per task and are not reused.</summary>
    public Guid TaskId { get; }

    /// <summary>
    /// The scope of a single-tenant Local Agent task: tenant <see cref="TenantId.Local"/>. It is not a wildcard; grants and
    /// provider registrations must name that tenant like any other.
    /// </summary>
    public static KnowledgeTaskScope ForLocalAgent(Guid taskId) => new(TenantId.Local, taskId);

    /// <summary>Issues the scope the host's authority gives an actor, or <c>null</c> when the authority does not know the actor.</summary>
    public static KnowledgeTaskScope? FromAuthority(ITenantAuthority authority, ActorIdentity actor, Guid taskId)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(actor);
        return authority.Resolve(actor) is { } tenant ? new KnowledgeTaskScope(tenant, taskId) : null;
    }
}
