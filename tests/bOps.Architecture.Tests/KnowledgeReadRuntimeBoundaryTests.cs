// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Architecture.Tests;

/// <summary>
/// KNOW-01B (ADR-0056), the mechanical half: <c>bOps.Runtime</c> learns about knowledge through two small contracts and names neither the
/// Knowledge engine, nor a pack, nor any skills function; only the runner and the MAF adapter touch the host contract; the concrete
/// host and policy are internal to the adapter; and nothing in the product builds one, so no production host can have knowledge.
/// </summary>
public sealed class KnowledgeReadRuntimeBoundaryTests
{
    private const string Runner = "src/core/bOps.Runtime/AgentRunner.cs";
    private const string Contracts = "src/core/bOps.Runtime/KnowledgeRead.cs";
    private const string HostClass = "src/core/bOps.AgentFramework/KnowledgeTaskHost.cs";

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void TheRuntime_NamesNeitherTheKnowledgeEngineNorAPackNorAnySkillsFunction()
    {
        string[] forbidden =
        [
            "knowledge_read", "load_skill", "read_skill_resource", "run_skill_script", "SKILL.md", "AgentSkills", "bOps.Knowledge",
            "bOps.AgentFramework", "KnowledgeTaskScope", "KnowledgePin", "KnowledgeAdmissionService", "KnowledgeSnapshot", "KnowledgeReadBridge",
            "KnowledgeReadAuditEvent", "KnowledgePackAdmissionAuditEvent", "PackKind", "TenantId.Local",
        ];
        var offenders = Sources("src/core/bOps.Runtime/")
            .SelectMany(file => forbidden.Where(name => file.Text.Contains(name, StringComparison.Ordinal)).Select(name => $"{file.Path}: {name}"))
            .ToArray();

        Assert.Empty(offenders);

        var project = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "core", "bOps.Runtime", "bOps.Runtime.csproj"));
        var references = Regex.Matches(project, "<ProjectReference Include=\"([^\"]+)\"").Select(match => match.Groups[1].Value.Replace('\\', '/')).ToArray();
        Assert.Equal(["../bOps.Abstractions/bOps.Abstractions.csproj"], references);
    }

    [Fact]
    public void TheHostContract_IsUsedOnlyByTheRunnerItsOwnFileAndTheMafAdapter_AndNoProductionHostPassesOne()
    {
        var users = Sources("src/")
            .Where(file => Regex.IsMatch(file.Text, @"\bIKnowledgeTaskHost\b|\bknowledgeHost\b"))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([HostClass, Runner, Contracts], users);
    }

    [Fact]
    public void TheConcreteHostAndItsPolicy_AreInternal_AndNothingInTheProductBuildsOne()
    {
        var host = Sources("src/").Single(file => file.Path == HostClass).Text;
        Assert.Matches(@"internal sealed class KnowledgeTaskHost\b", host);
        Assert.Matches(@"internal interface IKnowledgeTaskPolicy\b", host);
        Assert.Matches(@"internal sealed record KnowledgeBindingChoice\b", host);

        var builders = Sources("src/")
            .Where(file => Regex.IsMatch(file.Text, @"new\s+KnowledgeTaskHost\s*\(|:\s*IKnowledgeTaskPolicy\b"))
            .Select(file => file.Path)
            .ToArray();
        Assert.Empty(builders);
    }

    [Fact]
    public void TheRunner_NeverRegistersTheKnowledgeFunction_AndOffersOnlyTheManifestTheSessionDeclaresAfterValidatingIt()
    {
        var runner = Sources("src/").Single(file => file.Path == Runner).Text;

        // One place offers it, one place accepts it, and the acceptance is the read-only runtime-namespace rule.
        Assert.Equal(1, Regex.Count(runner, @"knowledgeSession\.Manifest"));
        Assert.Equal(1, Regex.Count(runner, @"session\.Manifest"));
        Assert.Contains("manifest.Risk == RiskLevel.Read", runner, StringComparison.Ordinal);
        Assert.Contains("EvidenceRead.ControlNamespace", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("registry.Register(", runner, StringComparison.Ordinal);

        // A delegated role never gets a session: both the open and the resume path are guarded by the absence of a delegation.
        Assert.Contains("if (delegation is null && knowledgeHost is not null)", runner, StringComparison.Ordinal);
        Assert.Contains("acquired.KnowledgeBinding is { } pinnedKnowledge", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoundTaskThatCannotBeAdmitted_HasNoPathThatRunsItWithoutItsKnowledge()
    {
        var runner = Sources("src/").Single(file => file.Path == Runner).Text;

        // Every refusal ends in the one terminal that records the cause, and the refusal is checked before planning or any step.
        Assert.Equal(2, Regex.Count(runner, @"FailKnowledgeUnavailableAsync\(run, (refusal|knowledgeRefusal|refusal)"));
        Assert.Contains("TaskTerminalKind.KnowledgeUnavailable", runner, StringComparison.Ordinal);
        var failure = runner.IndexOf("knowledgeRefusal is { } refusal ? FailKnowledgeUnavailableAsync", StringComparison.Ordinal);
        var planning = runner.IndexOf(": PlanAndContinueAsync(run, taskActivity, ct)", failure, StringComparison.Ordinal);
        Assert.True(failure >= 0 && planning > failure);
    }

    private static IEnumerable<(string Path, string Text)> Sources(params string[] prefixes) =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path: Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'), Text: File.ReadAllText(path)))
            .Where(file => prefixes.Any(prefix => file.Path.StartsWith(prefix, StringComparison.Ordinal)));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (bOps.slnx) was not found.");
    }
}
