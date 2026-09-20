// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The rules of ADR-0030 section 5, in one place so the plan-level approval, the step-level approval and the checks on a
/// stored run cannot drift apart: approvals are human only, and no two roles of a run share an agent identity.
/// </summary>
internal static class SeparationOfDuties
{
    /// <summary>
    /// Whether <paramref name="approver"/> may decide an approval in a delegated run. It may not be an agent, the runtime, or
    /// an identity that claims to be one of the run's own agents; a model can reach the last by naming an id it read.
    /// </summary>
    /// <param name="approver">Who the approval provider says decided.</param>
    /// <param name="runAgents">The agents the run has created, including the one asking.</param>
    internal static bool IsHumanApprover(ActorIdentity approver, IEnumerable<AgentId> runAgents)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(runAgents);

        return !string.Equals(approver.Kind, "agent", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(approver.Kind, ActorIdentity.RuntimeSystem.Kind, StringComparison.OrdinalIgnoreCase)
            && !runAgents.Any(agent => string.Equals(agent.ToString(), approver.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Says which two roles share an agent identity, or <c>null</c> when every role has its own. A run in which the same
    /// identity remediated and verified, or diagnosed and remediated, has no independent check, so it is invalid (§5).
    /// </summary>
    /// <param name="agents">The agents of a run's roles, in the order the roles started.</param>
    internal static string? SharedIdentity(IEnumerable<AgentIdentity> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var seen = new Dictionary<AgentId, AgentIdentity>();
        foreach (var agent in agents)
        {
            if (seen.TryGetValue(agent.Id, out var earlier))
            {
                return $"The {earlier.Role} and {agent.Role} roles share one agent identity, which separation of duties forbids.";
            }

            seen.Add(agent.Id, agent);
        }

        return null;
    }
}
