// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Architecture.Tests;

/// <summary>
/// ADR-0058, the mechanical half: the Local Agent knowledge pilot is the one place a host composes knowledge, and it stays inside its
/// perimeter. Only the CLI composes it, only for a goal or a resume, only through the adapter's factory, for the Local Agent tenant, for
/// Knowledge packs, with no key in the runtime and no MAF turn broker, no new tool and no second executor behind it. The API, the
/// package hosts and every other composition stay without knowledge.
/// </summary>
public sealed class LocalKnowledgePilotBoundaryTests
{
    private const string Composition = "src/core/bOps.AgentFramework/LocalKnowledgeComposition.cs";
    private const string CliProgram = "src/core/bOps.Cli/Program.cs";
    private const string CliKnowledge = "src/core/bOps.Cli/CliKnowledge.cs";

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void TheAdaptersFactory_IsNamedOnlyByTheCliBinding_AndNoOtherHost()
    {
        var users = Sources("src/")
            .Where(file => Regex.IsMatch(file.Text, @"\bLocalKnowledge(Composition|Options|Grant|Binding|Budget|ConfigurationException)\b"))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([Composition, CliKnowledge, CliProgram], users);
        Assert.DoesNotContain(Sources("src/core/bOps.Api/"), file => Regex.IsMatch(file.Text, @"\bIKnowledgeTaskHost\b|\bknowledgeHost\b|LocalKnowledge|bOps\.Knowledge\b"));
        Assert.DoesNotContain(Sources("src/core/bOps.Hosting/", "src/core/bOps.PluginHost/", "src/packages/"),
            file => Regex.IsMatch(file.Text, @"\bIKnowledgeTaskHost\b|\bknowledgeHost\b|LocalKnowledge"));
    }

    [Fact]
    public void TheCliOpensKnowledgeForAGoalOrAResumeOnly_NeverForADelegationARecoveryOrAReconciliation()
    {
        var program = Sources("src/").Single(file => file.Path == CliProgram).Text;

        Assert.Equal(1, Regex.Count(program, @"CliKnowledge\.CreateHost\("));
        var guard = program.IndexOf("if (goal is not null || resumeTaskId is not null)", StringComparison.Ordinal);
        var call = program.IndexOf("CliKnowledge.CreateHost(", StringComparison.Ordinal);
        // The composition builds the host before the runner; the call is `Compose` since the diagnose command needs the governed broker too.
        var runnerBuild = program.IndexOf("CliRunnerComposition.Compose(", StringComparison.Ordinal);
        Assert.True(guard >= 0 && call > guard && runnerBuild > call, "The host is built inside the goal/resume guard, before the runner.");

        // A misconfigured section ends the command before it runs: exit code 2, never a continued run without the knowledge asked for.
        var catchBlock = program[call..runnerBuild];
        Assert.Contains("catch (LocalKnowledgeConfigurationException", catchBlock, StringComparison.Ordinal);
        Assert.Contains("return 2;", catchBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCliNamesTheMafTurnBrokerOnlyInItsOptInComposition_NoContextProvider_AndNamesNoKnowledgeEngineType()
    {
        // ADR-0058 section 3: the knowledge path rides on the broker and projector the CLI already used; the experimental MAF turn broker is not a prerequisite.
        // C01-R22 (BOPS-0122): the opt-in MAF turn host is the one exception, confined to CliRunnerComposition.cs (MafGovernedTurnBroker and the
        // assembly mode only; never the context provider, the leaf, the agent or any Knowledge type).
        string[] forbidden =
        [
            "MafGovernedTurnBroker", "MafContextAssembly", "BopsContextProvider", "AIContextProvider", "GovernedChatClient", "ChatClientAgent",
            "KnowledgeTrustStore", "KnowledgeGrants", "KnowledgeAdmissionService", "KnowledgeProviderRegistry", "KnowledgeTaskScope", "TenantId",
            "IKnowledgePackSource", "DirectoryKnowledgePackSource", "PackGrant",
        ];
        var offenders = Sources("src/core/bOps.Cli/")
            .SelectMany(file => forbidden
                .Where(name => file.Text.Contains(name, StringComparison.Ordinal))
                .Where(name => !(file.Path == "src/core/bOps.Cli/CliRunnerComposition.cs" && name is "MafGovernedTurnBroker" or "MafContextAssembly"))
                .Select(name => $"{file.Path}: {name}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheLocalComposition_ServesOnlyKnowledgePacksToTheLocalAgentTenant_FromOneDirectory_AndConstructsNoOtherTenant()
    {
        var text = Sources("src/").Single(file => file.Path == Composition).Text;

        Assert.Contains("[PackKind.Knowledge], [TenantId.Local]", text, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(text, @"registry\.Register\("));
        Assert.Equal(1, Regex.Count(text, @"new\s+DirectoryKnowledgePackSource\("));
        Assert.DoesNotContain("new TenantId(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("KnowledgeGrants.None", text, StringComparison.Ordinal);

        // The grants come only from the operator's list, always for the Local Agent tenant; a pack is never granted because it is installed.
        Assert.Equal(1, Regex.Count(text, @"new\s+PackGrant\("));
        Assert.Contains("new PackGrant(TenantId.Local, reference, kind)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumerateDirectories", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDirectories", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePilotAddsNoToolNoExecutorAndNoSigningCapability()
    {
        var text = Sources("src/").Single(file => file.Path == Composition).Text;

        Assert.Equal(1, Regex.Count(text, @"\.Register\(")); // the provider registry's one registration; no tool or skill is ever registered here
        string[] forbidden = ["IToolRegistry", "ITool ", "IChatModel", "AgentRunner", "SignHash", "SignData", "ExportPkcs8PrivateKey", "RSA.Create", "PrivateKey"];
        var offenders = forbidden.Where(name => text.Contains(name, StringComparison.Ordinal)).ToArray();
        Assert.Empty(offenders);
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
