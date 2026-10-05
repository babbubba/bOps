// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Abstractions;

namespace bOps.Cli;

/// <summary>
/// The console rendering of a model role's typed evidence limitations (ADR-0044 section 16), from the typed fields only. Each
/// limitation is named by role and source (the role's model-loop evidence), "not recorded" is never shown as "none", and nothing
/// a tool or a model wrote is printed.
/// </summary>
internal static class LimitationText
{
    /// <summary>The lines for one Discovery or Diagnostic role; nothing for the other roles.</summary>
    public static IEnumerable<string> Lines(DelegationRoleRun role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (role.Agent.Role is not (AgentRoleKind.Discovery or AgentRoleKind.Diagnostic))
        {
            yield break;
        }

        if (role.EvidenceLimitations is not { } limitations)
        {
            yield return $"{role.Agent.Role} evidence limitations: not recorded.";
        }
        else if (limitations.Count == 0)
        {
            yield return $"{role.Agent.Role} evidence limitations: none recorded for this role's model-loop evidence.";
        }
        else
        {
            yield return $"{role.Agent.Role} evidence limitations (this role's model-loop evidence):";
            if (role.EvidenceLimitationsOmitted > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"  {role.EvidenceLimitationsOmitted} earlier limitation(s) are not listed.");
            }

            foreach (var limitation in limitations)
            {
                yield return "  " + Line(limitation);
            }
        }

        if (role.FindingsReply is { } reply)
        {
            var problem = reply.Problem == DiagnosticReplyProblem.None ? string.Empty : $" ({reply.Problem})";
            var discarded = reply.DiscardedFindings > 0 ? string.Create(CultureInfo.InvariantCulture, $", {reply.DiscardedFindings} finding(s) discarded") : string.Empty;
            yield return $"Diagnostic reply: {reply.Status}{problem}{discarded}.";
        }
    }

    /// <summary>The ids of Evidence typed as limited in the run, or <c>null</c> when a completed model role recorded none (not recorded).</summary>
    public static HashSet<string>? LimitedEvidenceIds(DelegationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var modelRoles = run.Roles.Where(r => r.Agent.Role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic && r.Status == DelegationRoleStatus.Completed).ToList();
        return modelRoles.Count == 0 || modelRoles.Any(r => r.EvidenceLimitations is null)
            ? null
            : modelRoles.SelectMany(r => r.EvidenceLimitations!).Select(l => l.EvidenceId).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    private static string Line(EvidenceLimitation limitation)
    {
        var tool = limitation.UnknownTool ? "(unknown tool)" : limitation.ToolName ?? "(tool name omitted)";
        var facts = new List<string>(3);
        if (limitation.Completeness is ToolResultCompleteness.Partial or ToolResultCompleteness.Unavailable)
        {
            facts.Add($"completeness {limitation.Completeness}");
        }

        if (limitation.Outcome != ToolOutcome.Success)
        {
            facts.Add($"outcome {limitation.Outcome}, failure {limitation.FailureKind}");
        }

        if (limitation.ShortenedFromCharacters is { } length)
        {
            facts.Add(string.Create(CultureInfo.InvariantCulture, $"observation shortened from {length} characters"));
        }

        var evidence = limitation.EvidenceId is { } id ? $"evidence {id}" : "no evidence produced";
        return string.Create(CultureInfo.InvariantCulture, $"step {limitation.StepIndex}: {tool} — {string.Join("; ", facts)} ({evidence})");
    }
}
