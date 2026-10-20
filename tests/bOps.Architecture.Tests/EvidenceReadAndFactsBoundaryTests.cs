// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.Architecture.Tests;

/// <summary>
/// ADR-0052 AF-04 structural guards: EvidenceRead stays a runtime control function (never an operational tool), packages stay
/// below the runtime, and the Agent Framework integration never gains execution authority. Fact type/key opacity of the core is
/// enforced by <see cref="CoreNamesNoPackageTests"/>, which scans the compiled string literals.
/// </summary>
public sealed class EvidenceReadAndFactsBoundaryTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void EvidenceRead_IsNotAnOrdinaryTool()
    {
        foreach (var assembly in new[] { typeof(Runtime.AgentRunner).Assembly, typeof(Policy.PolicyEngine).Assembly })
        {
            var offenders = assembly.GetTypes()
                .Where(type => typeof(ITool).IsAssignableFrom(type) && !type.IsAbstract)
                .Where(type => type.Name.Contains("Evidence", StringComparison.Ordinal) || type.Name.Contains("Control", StringComparison.Ordinal))
                .Select(type => type.FullName)
                .ToArray();
            Assert.True(offenders.Length == 0, $"The runtime control function must not be an ITool: {string.Join(", ", offenders)}");
        }

        // The whole runtime. namespace is reserved by the registry, so no package can register a tool under it (EvidenceControlPlaneTests).
        var registrySource = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "core", "bOps.Runtime", "ToolRegistry.cs"));
        Assert.Contains("EvidenceRead.ControlNamespace", registrySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Packages_NeverReferenceTheRuntimeOrItsGovernanceProjects()
    {
        var forbidden = new[] { "bOps.Runtime", "bOps.AgentFramework", "bOps.Policy", "bOps.Audit", "bOps.Hosting", "bOps.Memory" };
        var offenders = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src", "packages"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => Regex.Matches(File.ReadAllText(project), @"<ProjectReference\s+Include=""([^""]+)""")
                .Select(match => (Project: Path.GetFileName(project), Reference: match.Groups[1].Value)))
            .Where(item => forbidden.Any(name => item.Reference.Contains(name + ".csproj", StringComparison.OrdinalIgnoreCase)))
            .Select(item => $"{item.Project} -> {item.Reference}")
            .ToArray();

        Assert.True(offenders.Length == 0, $"Packages must only depend on bOps.Abstractions: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void AgentFramework_HasNoExecutionAuthority()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src", "core", "bOps.AgentFramework"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(sources);

        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (var forbidden in new[]
                     {
                         ": ITool", "IToolInvoker", "IToolRegistry", "ToolRegistry", "IPolicyEngine", "IApprovalProvider",
                         "IMutationJournal", "EvidenceFact", "ToolCallResult", "runtime.evidence_read", "EvidenceRead",
                     })
            {
                Assert.True(!text.Contains(forbidden, StringComparison.Ordinal),
                    $"{Path.GetFileName(source)} must not know '{forbidden}': MAF holds no execution, policy or evidence authority.");
            }
        }
    }

    [Fact]
    public void FactProjections_AreStaticPackageClasses_NotToolsAndNotExecutionSurfaces()
    {
        foreach (var file in new[]
                 {
                     Path.Combine("bOps.Packages.System.Core", "StabilityFacts.cs"),
                     Path.Combine("bOps.Packages.System.Core", "SystemCrashFacts.cs"),
                     Path.Combine("bOps.Packages.System.Core", "DumpAnalysisFacts.cs"),
                     Path.Combine("bOps.Packages.Storage.Core", "StorageHealthFacts.cs"),
                 })
        {
            var text = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "packages", file));
            Assert.Contains("public static class", text, StringComparison.Ordinal);
            foreach (var forbidden in new[] { ": ITool", "System.Diagnostics.Process", "ProcessStartInfo", "ExecuteAsync(", "File.Open", "File.Read" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
