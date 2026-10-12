// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Knowledge;
using Microsoft.Agents.AI;

namespace bOps.AgentFramework;

/// <summary>
/// The one place the framework's file skills source is used (KNOW-01A, ADR-0055). It reads a verified snapshot directory with the
/// framework's own SKILL.md parser, discovery and path checks; bOps adds no parser. It is built with no script runner and with no
/// script extension at all, with the same closed list of text extensions the pack verifier enforces, so the framework can neither
/// discover nor run a script even if one were somehow present. It is created per task from that task's tenant-private snapshot and
/// is never cached, shared or wrapped in a caching source.
/// </summary>
internal static class VerifiedSnapshotSkills
{
    /// <summary>A skill directory, then at most two levels of resource folders below it.</summary>
    private const int SearchDepth = 3;

    internal static AgentSkillsSource Open(KnowledgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new AgentFileSkillsSource(
            snapshot.ContentPath,
            scriptRunner: null,
            new AgentFileSkillsSourceOptions
            {
                AllowedResourceExtensions = [.. KnowledgePackFormat.AllowedExtensions],
                AllowedScriptExtensions = [],
                SearchDepth = SearchDepth,
            });
    }
}
