// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Architecture.Tests;

/// <summary>
/// KNOW-01A (ADR-0055), the mechanical half of G2-3, G2-4 and G2-5: the pack contract stays independent of the agent framework, holds no
/// signing key and no shared cache, the framework's file skills source is built in one place with no script capability, and nothing
/// composes any of it into a production host.
/// </summary>
public sealed class KnowledgePackBoundaryTests
{
    private const string KnowledgeProject = "src/core/bOps.Knowledge/";
    private const string SnapshotSkills = "src/core/bOps.AgentFramework/VerifiedSnapshotSkills.cs";
    private const string Bridge = "src/core/bOps.AgentFramework/KnowledgeReadBridge.cs";

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] FrameworkTypeNames = ["Microsoft.Agents", "Microsoft.Extensions.AI", "AIContextProvider", "ChatClientAgent", "IChatClient", "IChatModel"];

    [Fact]
    public void TheKnowledgeAssembly_DependsOnlyOnAbstractions_AndNamesNoAgentFrameworkType()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "core", "bOps.Knowledge", "bOps.Knowledge.csproj"));
        var references = Regex.Matches(project, "<ProjectReference Include=\"([^\"]+)\"").Select(match => match.Groups[1].Value.Replace('\\', '/')).ToArray();
        Assert.Equal(["../bOps.Abstractions/bOps.Abstractions.csproj"], references);
        Assert.DoesNotContain("<PackageReference", project, StringComparison.Ordinal);

        var offenders = Sources(KnowledgeProject)
            .SelectMany(file => FrameworkTypeNames.Where(name => file.Text.Contains(name, StringComparison.Ordinal)).Select(name => $"{file.Path}: {name}"))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void OnlyTheMafAdapter_ReferencesTheKnowledgeAssembly()
    {
        var referencing = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("bOps.Knowledge", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["src/core/bOps.AgentFramework/bOps.AgentFramework.csproj", "src/core/bOps.Knowledge/bOps.Knowledge.csproj"], referencing);
    }

    [Fact]
    public void NoRuntimeCode_HoldsASigningKeyOrAnySigningPath()
    {
        // Private keys exist in publisher tooling and in tests only; the runtime verifies with public keys.
        string[] forbidden = ["SignHash(", "SignData(", "ExportPkcs8PrivateKey", "ExportRSAPrivateKey", "ExportEncryptedPkcs8PrivateKey", "ImportPkcs8PrivateKey", "ImportRSAPrivateKey", "RSACng", "X509Certificate2"];
        var offenders = Sources("src/core/bOps.Knowledge/", "src/core/bOps.AgentFramework/")
            .SelectMany(file => forbidden.Where(name => file.Text.Contains(name, StringComparison.Ordinal)).Select(name => $"{file.Path}: {name}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheKnowledgeAssembly_HasNoProcessWideOrSharedMutableState()
    {
        // G2-5: no static cache and no fallback to one. Static members are immutable data or pure functions.
        string[] caches = ["ConcurrentDictionary", "MemoryCache", "ConditionalWeakTable", "AsyncLocal", "ThreadStatic", "CachingAgentSkillsSource"];
        var offenders = new List<string>();
        foreach (var file in Sources(KnowledgeProject, "src/core/bOps.AgentFramework/"))
        {
            offenders.AddRange(caches.Where(name => file.Text.Contains(name, StringComparison.Ordinal)).Select(name => $"{file.Path}: {name}"));
            var mutableStaticField = new Regex(@"^\s*(?:(?:private|internal|public|protected)\s+)?static\s+(?!readonly\b|class\b|partial\b|extern\b|async\b)[^()={}]*\s\w+\s*(?:=[^;]*)?;\s*$");
            offenders.AddRange(file.Text.Split('\n')
                .Where(line => !line.Contains("=>", StringComparison.Ordinal) && mutableStaticField.IsMatch(line))
                .Select(line => $"{file.Path}: {line.Trim()}"));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheFrameworkFileSkillsSource_IsBuiltInOnePlace_WithNoScriptRunnerAndNoScriptExtension()
    {
        var all = Sources("src/").ToList();
        var users = all.Where(file => file.Text.Contains("AgentFileSkillsSource", StringComparison.Ordinal)).Select(file => file.Path).ToArray();
        Assert.Equal([SnapshotSkills], users);

        var snapshot = all.Single(file => file.Path == SnapshotSkills).Text;
        Assert.Contains("scriptRunner: null", snapshot, StringComparison.Ordinal);
        Assert.Contains("AllowedScriptExtensions = []", snapshot, StringComparison.Ordinal);
        Assert.Contains("AllowedResourceExtensions = [.. KnowledgePackFormat.AllowedExtensions]", snapshot, StringComparison.Ordinal);

        // No script runner of any kind, and none of the framework's wrapping sources (caching would be a shared cache; G2-5).
        string[] forbidden = ["AgentFileSkillScriptRunner", "AgentFileSkillScript", "AgentInlineSkillScript", "AddScript(", "CachingAgentSkillsSource", "AggregatingAgentSkillsSource", "DeduplicatingAgentSkillsSource", "FilteringAgentSkillsSource", "DelegatingAgentSkillsSource"];
        var offenders = all.SelectMany(file => forbidden.Where(name => file.Text.Contains(name, StringComparison.Ordinal)).Select(name => $"{file.Path}: {name}")).ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void FrontmatterAllowedTools_IsOnlyEverReadToRefuseAPack_NeverToGrantAnything()
    {
        var readers = Sources("src/").Where(file => file.Text.Contains("Frontmatter.AllowedTools", StringComparison.Ordinal)).Select(file => file.Path).ToArray();
        Assert.Equal([Bridge], readers);

        var bridge = Sources("src/").Single(file => file.Path == Bridge).Text;
        Assert.Equal(1, Regex.Count(bridge, "Frontmatter\\.AllowedTools"));
        Assert.Contains("declares allowed-tools", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKnowledgeContracts_AreComposedByNoProductionHost_AndNoConfigurationCanEnableThem()
    {
        // Admission, the registry, the trust store and the snapshot store exist for KNOW-01B to wire; until then no host builds them.
        string[] names =
        [
            "KnowledgeAdmissionService", "KnowledgeProviderRegistry", "KnowledgeTrustStore", "KnowledgeSnapshotStore", "KnowledgeGrants",
            "KnowledgePackVerifier", "DirectoryKnowledgePackSource", "IKnowledgePackSource", "KnowledgeTaskScope", "KnowledgeBridgeAdmission",
            "VerifiedSnapshotSkills", "KnowledgePackAdmissionAuditEvent",
        ];
        var composed = Sources("src/")
            .Where(file => !file.Path.StartsWith(KnowledgeProject, StringComparison.Ordinal)
                && !file.Path.StartsWith("src/core/bOps.AgentFramework/", StringComparison.Ordinal)
                && file.Path is not "src/core/bOps.Abstractions/Audit.cs" and not "src/core/bOps.Audit/AuditJsonContext.cs")
            .SelectMany(file => names.Where(name => file.Text.Contains(name, StringComparison.Ordinal)).Select(name => $"{file.Path}: {name}"))
            .ToArray();
        Assert.Empty(composed);

        string[] configurationNames = ["bops-pack", "KnowledgePack", "KnowledgeTrustStore", "PublisherKey", "KnowledgeProvider", "ExperiencePack"];
        var configuration = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("packages.lock.json", StringComparison.Ordinal))
            .Where(path => configurationNames.Any(name => File.ReadAllText(path).Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .ToArray();
        Assert.Empty(configuration);
    }

    [Fact]
    public void NoNewToolReachesTheAuthorisedSurface_TheOnlyKnowledgeFunctionIsTheBridgesRuntimeControlCall()
    {
        var declarations = Sources("src/")
            .Where(file => Regex.IsMatch(file.Text, "\"(runtime\\.knowledge[a-z_]*|[a-z]+\\.(load|read|run)_skill[a-z_]*)\""))
            .Select(file => file.Path)
            .ToArray();

        Assert.Equal([Bridge], declarations);
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
