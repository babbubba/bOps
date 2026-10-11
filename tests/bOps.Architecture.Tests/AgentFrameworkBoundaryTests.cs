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
