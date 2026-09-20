// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The delegated identity and authority one step executes under (ADR-0030 sections 1 and 3): which
/// delegated run and agent it belongs to, and the envelope that bounds it. It is what makes a step
/// "delegated": every entry point of the runner that takes one runs each of its steps through
/// <see cref="EnvelopeEnforcer"/> before policy is evaluated, and stamps <see cref="Correlation"/> on every
/// audit event it writes.
/// </summary>
/// <remarks>
/// <para>
/// The correlation and the envelope are separate on purpose, and the envelope is nullable on purpose. The
/// correlation names the agent and the hash of the envelope the orchestrator believes is in force; the
/// envelope is the authority itself. A scope that names an agent but carries no envelope, or one whose
/// envelope hashes to something else, is a bug in whoever built it: a resumed run whose envelope was not
/// reloaded, say. The step it would run is refused, not run unrestricted (ADR-0030 section 3), and the two
/// halves being independent is what lets the enforcer notice.
/// </para>
/// <para>
/// Internal: only the runtime's orchestrator builds one, so nothing outside it can attach an envelope of its
/// own choosing to a step.
/// </para>
/// </remarks>
internal sealed record DelegatedExecutionScope
{
    /// <summary>Creates a scope from its two halves.</summary>
    /// <param name="correlation">The block stamped on every audit event of the run's steps. Its agent is the one executing them.</param>
    /// <param name="envelope">The authority the agent was granted, or <c>null</c> when the caller has none to give, which makes every step refused.</param>
    /// <exception cref="ArgumentNullException"><paramref name="correlation"/> is <c>null</c>.</exception>
    /// <param name="consumed">What the agent already spent under this envelope, when it is a role being restarted; nothing otherwise.</param>
    internal DelegatedExecutionScope(DelegationCorrelation correlation, AuthorityEnvelope? envelope, BudgetConsumption? consumed = null)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        Correlation = correlation;
        Envelope = envelope;
        Meter = envelope is null ? null : new RoleMeter(envelope.Budget, consumed);
    }

    /// <summary>The delegated run, the acting agent and the hash of the envelope in force.</summary>
    internal DelegationCorrelation Correlation { get; }

    /// <summary>The authority the agent was granted. <c>null</c> refuses every step.</summary>
    internal AuthorityEnvelope? Envelope { get; }

    /// <summary>Where the agent's side-effecting steps are journaled, when the run is durable (ADR-0030 section 7). <c>null</c> for a run that is not stored.</summary>
    internal IStepJournal? Journal { get; init; }

    /// <summary>The agents the run had created before this one, so an approval decided in the name of any of them can be recognised as not human (ADR-0030 section 5).</summary>
    internal IReadOnlyCollection<AgentId> PeerAgents { get; init; } = [];

    /// <summary>What the agent has spent of the budget its envelope grants (ADR-0030 section 6). <c>null</c> when there is no envelope, which refuses every step anyway.</summary>
    internal RoleMeter? Meter { get; }

    /// <summary>
    /// The normal way to build a scope: from the run, the agent and the envelope the reduction granted it, with
    /// the correlation's hash computed from that envelope so the two agree.
    /// </summary>
    /// <param name="delegationId">The delegated run (<see cref="DelegationRun.Id"/>).</param>
    /// <param name="agent">The agent that acts.</param>
    /// <param name="envelope">The envelope granted to that agent.</param>
    /// <param name="consumed">What the agent already spent, for a role that is restarted: its budget is never reset.</param>
    internal static DelegatedExecutionScope For(Guid delegationId, AgentIdentity agent, AuthorityEnvelope envelope, BudgetConsumption? consumed = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(envelope);

        return new DelegatedExecutionScope(
            new DelegationCorrelation(delegationId, DelegationHasher.ComputeEnvelopeHash(envelope), agent),
            envelope,
            consumed);
    }
}
