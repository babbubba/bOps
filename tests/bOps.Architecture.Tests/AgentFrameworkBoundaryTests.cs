// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Architecture.Tests;

public sealed class AgentFrameworkBoundaryTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void AbstractionsProjectAndSource_DoNotReferenceAgentFrameworkTypes()
    {
        var project = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "core", "bOps.Abstractions", "bOps.Abstractions.csproj"));
        Assert.DoesNotContain("Microsoft.Agents.AI", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Extensions.AI", project, StringComparison.Ordinal);

        var sources = Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "src", "core", "bOps.Abstractions"), "*.cs", SearchOption.AllDirectories);
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain("Microsoft.Agents.AI", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.Extensions.AI", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AgentFrameworkProject_IsIncludedInTheSolution()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot, "bOps.slnx"));

        Assert.Contains(
            "src/core/bOps.AgentFramework/bOps.AgentFramework.csproj",
            solution.Replace('\\', '/'),
            StringComparison.Ordinal);

        foreach (var host in new[] { "bOps.Api", "bOps.Cli" })
        {
            var project = File.ReadAllText(Path.Combine(
                RepositoryRoot, "src", "core", host, $"{host}.csproj"));
            Assert.Contains(
                "bOps.AgentFramework\\bOps.AgentFramework.csproj",
                project,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Runtime_HasNoSecondModelFacingHistoryEngine()
    {
        // ADR-0052 §23 / AF-05: the HARDEN-8 custom history engine was removed; the MAF context layer is the only one. These
        // names must not come back as a parallel engine (a new one needs its own ADR, not a quiet re-introduction).
        string[] removed = ["BoundedHistory", "BOPS_HISTORY", "VerbatimHistorySteps", "LegacySafeRequest", "UseLegacySafeProjectionWhenUnknown"];
        var offenders = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (path, text: File.ReadAllText(path)))
            .SelectMany(file => removed.Where(name => file.text.Contains(name, StringComparison.Ordinal))
                .Select(name => $"{Path.GetRelativePath(RepositoryRoot, file.path).Replace('\\', '/')}: {name}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ProductionCode_CallsChatModelDirectlyOnlyAtGovernedOrAdapterBoundaries()
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/core/bOps.Runtime/ModelInvocationBroker.cs",
            "src/core/bOps.Hosting/FallbackChatModel.cs",
            "src/core/bOps.Api/ExecutionRunnerFactory.cs",
        };

        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(".CompleteAsync(request", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Where(path => !allowed.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"Production IChatModel.CompleteAsync calls must stay behind the governed broker/provider boundary: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Summarization_ReachesAModelOnlyThroughTheGovernedBroker_AndStaysInsideTheMafProject()
    {
        var client = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "core", "bOps.AgentFramework", "GovernedSummarizationChatClient.cs"));
        Assert.Contains("access.Broker.InvokeAsync(", client, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "IChatModel", ".CompleteAsync(", "HttpClient", "ApiKey", "ISecretProvider" })
        {
            Assert.DoesNotContain(forbidden, client, StringComparison.Ordinal);
        }

        var offenders = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("SummarizationCompactionStrategy", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("src/core/bOps.AgentFramework/", StringComparison.Ordinal))
            .ToArray();
        Assert.True(offenders.Length == 0, $"MAF summarization must stay inside bOps.AgentFramework: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheMafAgent_IsBuiltInOnePlace_StatelessAndWithoutAFrameworkToolLoop()
    {
        // AF-TURN-01A: the one construction point of a ChatClientAgent is MafGovernedTurn; behavioural guards exercise that very code.
        var production = ProductionSources().ToList();
        var constructors = production.Where(file => file.Text.Contains("ChatClientAgent", StringComparison.Ordinal))
            .Select(file => file.Path).ToArray();
        Assert.Equal(["src/core/bOps.AgentFramework/MafGovernedTurn.cs"], constructors);

        var turn = production.Single(file => file.Path == "src/core/bOps.AgentFramework/MafGovernedTurn.cs").Text;
        Assert.Contains("UseProvidedChatClientAsIs = true", turn, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            "FunctionInvokingChatClient", "UseFunctionInvocation", "AIFunctionFactory.Create(", "AsDeclarationOnly", "AIContextProvider",
            "ChatHistoryProvider", "AgentSession", "GetNewSessionAsync", "ConversationId",
        })
        {
            Assert.DoesNotContain(forbidden, turn.Replace("CreateSessionAsync", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        var loops = production.Where(file => file.Text.Contains("FunctionInvokingChatClient", StringComparison.Ordinal)
            || file.Text.Contains("UseFunctionInvocation", StringComparison.Ordinal)).Select(file => file.Path).ToArray();
        Assert.Empty(loops);
    }

    [Fact]
    public void InsideTheMafProject_OnlyTheGovernedLeavesReachTheBroker()
    {
        var offenders = ProductionSources()
            .Where(file => file.Path.StartsWith("src/core/bOps.AgentFramework/", StringComparison.Ordinal))
            .Where(file => file.Text.Contains(".InvokeAsync(", StringComparison.Ordinal))
            .Select(file => file.Path)
            .Where(path => path is not "src/core/bOps.AgentFramework/GovernedChatClient.cs"
                and not "src/core/bOps.AgentFramework/GovernedSummarizationChatClient.cs")
            .ToArray();
        Assert.True(offenders.Length == 0, $"A broker call that does not pass the context gate: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheMafTurnPath_IsNotComposedByAnyProductionHost_UntilItIsDeliberatelyActivated()
    {
        // AF-TURN-01A keeps the path off in production configurations until the equivalence suite is accepted. Activating it is a
        // deliberate change that updates this test together with its host wiring and decision record.
        var composed = ProductionSources()
            .Where(file => !file.Path.StartsWith("src/core/bOps.AgentFramework/", StringComparison.Ordinal))
            .Where(file => file.Text.Contains("MafGovernedTurnBroker", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();
        Assert.Empty(composed);
    }

    private static IEnumerable<(string Path, string Text)> ProductionSources() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'), File.ReadAllText(path)));

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
