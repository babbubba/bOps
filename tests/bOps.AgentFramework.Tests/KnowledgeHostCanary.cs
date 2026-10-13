// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;
using Microsoft.Extensions.Logging;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// Opt-in, test-only composition for a local-model knowledge canary. Its pack is generated and signed with the test key in a
/// temporary source; the selected tenant alone is registered, granted and bound. This is deliberately outside product projects and
/// has no configuration binding, so it cannot activate knowledge in a production host.
/// </summary>
internal sealed class KnowledgeHostCanary : IDisposable
{
    private readonly KnowledgeRunnerWorld world;
    private readonly KnowledgeProcess process;

    private KnowledgeHostCanary(KnowledgeRunnerWorld world, KnowledgeProcess process)
    {
        this.world = world;
        this.process = process;
    }

    internal IReadOnlyList<AuditEvent> AuditEvents => world.Events;

    internal int SourceOpens => world.Source.Opens;

    /// <summary>Composes only when a caller explicitly opts in. A caller may withhold the one grant to exercise refusal closed.</summary>
    internal static KnowledgeHostCanary Create(KnowledgeCanaryActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (!activation.Enabled)
        {
            throw new InvalidOperationException("The knowledge host canary is disabled. Set Enabled explicitly for the isolated test host.");
        }

        var world = new KnowledgeRunnerWorld();
        try
        {
            var pack = new TestPack();
            var reference = new PackReference(pack.PackId, pack.Version);
            world.Source.Add(activation.Tenant, pack.PackId, pack.Version, () => pack.Build());
            IEnumerable<PackGrant> grants = activation.GrantPack
                ? [new PackGrant(activation.Tenant, reference, pack.Kind)]
                : [];
            var process = world.Boot(
                grants,
                providerTenants: [activation.Tenant],
                bindings: new Dictionary<TenantId, PackReference> { [activation.Tenant] = reference });
            return new KnowledgeHostCanary(world, process);
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }

    /// <summary>Uses the existing AgentRunner path with the existing KnowledgeTaskHost and MAF broker.</summary>
    internal CanaryRig Compose(
        IChatModel model, ILogger<AgentRunner>? diagnosticLogger = null, bool withWindowsSystemEvents = false,
        CanaryEventsActivation? eventsCorpus = null) =>
        process.ComposeCanary(model, diagnosticLogger: diagnosticLogger, withWindowsSystemEvents: withWindowsSystemEvents,
            eventsCorpus: eventsCorpus);

    public void Dispose() => world.Dispose();
}

/// <summary>Explicit switch and exact tenant/grant inputs for the test host; this is not product configuration or a general policy.</summary>
internal sealed record KnowledgeCanaryActivation(bool Enabled, TenantId Tenant, bool GrantPack)
{
    internal static KnowledgeCanaryActivation Authorized(TenantId tenant) => new(true, tenant, true);

    internal static KnowledgeCanaryActivation Unauthorized(TenantId tenant) => new(true, tenant, false);
}
