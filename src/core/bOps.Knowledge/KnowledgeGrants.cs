// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// The entitlement table: which tenant may use which pack version. It is immutable and per tenant, with no wildcard, so a pack
/// reaches a tenant only because the host listed it for that tenant. A Coordinator fills it from its own entitlement; the Local
/// Agent lists packs for <see cref="TenantId.Local"/> like any other tenant.
/// </summary>
public sealed class KnowledgeGrants
{
    private readonly Dictionary<(TenantId Tenant, string PackId, string Version), PackKind> grants;

    /// <summary>Creates the table.</summary>
    /// <exception cref="ArgumentException">The same tenant, pack and version are granted with two kinds.</exception>
    public KnowledgeGrants(IEnumerable<PackGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        this.grants = [];
        foreach (var grant in grants)
        {
            ArgumentNullException.ThrowIfNull(grant);
            if (!this.grants.TryAdd((grant.Tenant, grant.Reference.PackId, grant.Reference.Version), grant.Kind))
            {
                throw new ArgumentException("A pack version is granted to a tenant once.", nameof(grants));
            }
        }
    }

    /// <summary>A table that grants nothing.</summary>
    public static KnowledgeGrants None { get; } = new([]);

    /// <summary>The kind granted to the tenant for the exact pack version, if any.</summary>
    public bool TryGetGrantedKind(TenantId tenant, PackReference reference, out PackKind kind)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(reference);
        return grants.TryGetValue((tenant, reference.PackId, reference.Version), out kind);
    }
}
