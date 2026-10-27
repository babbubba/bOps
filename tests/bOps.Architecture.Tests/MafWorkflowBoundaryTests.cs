// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Architecture.Tests;

/// <summary>
/// C01-R23: the MAF troubleshooting workflow reaches tools only through the runner's gateway session and models only through the governed
/// broker, it is composed by the CLI alone, and no shipped configuration enables it. These are source-level guards: they fail if a later
/// change gives the workflow a way around the gateway, however that is spelled.
/// </summary>
public sealed class MafWorkflowBoundaryTests
{
    private const string Workflow = "src/core/bOps.AgentFramework/Troubleshooting/";

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void TheWorkflowCode_HasNoWayToResolveExecuteOrAuthorizeATool()
    {
        // Nothing in the workflow can hold a tool, the registry, policy or approval: the only things it may do with tools are read the manifests
        // the runner reports (to offer them) and call the gateway handle.
        string[] forbidden =
        [
            "IToolRegistry", "ToolRegistry", "ResolveForExecution", "IToolInvoker", "IPolicyEngine", "IApprovalProvider",
            "ApprovalRequiredAIFunction", "IVerifiableTool", "IContextualTool", "ExecuteStepAsync", "ExecuteWithTimeoutAsync", "ITaskStore",
            "IEntitlementService",
        ];
        var offenders = Sources(Workflow)
            .SelectMany(file => forbidden.Where(name => file.Text.Contains(name, StringComparison.Ordinal)).Select(name => $"{file.Path}: {name}"))
            .ToArray();
        Assert.Empty(offenders);

        // The catalog resolves specialists from manifests the runner reports; nothing resolves a tool from a registry.
        Assert.DoesNotContain(Sources(Workflow), file => Regex.IsMatch(file.Text, @"[rR]egistryw*.Resolve"));

        // No type of the workflow implements ITool: it contributes no tool of its own.
        Assert.DoesNotContain(Sources(Workflow), file => Regex.IsMatch(file.Text, @":\s*(I[A-Za-z]*)?ITool\b"));
    }

    [Fact]
    public void TheFunctionMafInvokes_DoesNothingButCallTheGatewayHandle()
    {
        var function = Sources(Workflow).Single(file => file.Path == Workflow + "GovernedToolFunction.cs").Text;
        Assert.Contains("handle.ExecuteAsync(", function, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(function, @"\.ExecuteAsync\("));
        foreach (var forbidden in new[] { "Resolve", "AgentRunner", "IModelInvocationBroker", "Process", "File." })
        {
            Assert.DoesNotContain(forbidden, function, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryModelCallOfTheWorkflow_GoesThroughTheGovernedBroker_FromOneLeaf()
    {
        var leaves = Sources(Workflow).Where(file => Regex.IsMatch(file.Text, @":\s*IChatClient\b")).Select(file => file.Path).ToArray();
        Assert.Equal([Workflow + "WorkflowGovernedChatClient.cs"], leaves);

        var brokerCalls = Sources(Workflow).Where(file => file.Text.Contains(".InvokeAsync(", StringComparison.Ordinal)).Select(file => file.Path).ToArray();
        Assert.Equal([Workflow + "WorkflowGovernedChatClient.cs"], brokerCalls);

        // No second channel to a model: no HTTP, no provider SDK, no IChatModel.
        string[] channels = ["HttpClient", "OpenAI", "Ollama", "Anthropic", "OpenRouter", "IChatModel", "CompleteAsync("];
        var offenders = Sources(Workflow)
            .SelectMany(file => channels.Where(name => file.Text.Contains(name, StringComparison.Ordinal)).Select(name => $"{file.Path}: {name}"))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheGatewaySession_IsOpenedByTheWorkflowAlone_AndTheWorkflowIsComposedByTheCliAlone()
    {
        var openers = ProductionSources()
            .Where(file => file.Text.Contains("OpenWorkflowToolSession(", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();
        Assert.Equal(
            ["src/core/bOps.AgentFramework/Troubleshooting/MafTroubleshootingWorkflow.cs", "src/core/bOps.Runtime/AgentRunner.WorkflowTools.cs"],
            openers.Order(StringComparer.Ordinal));

        var composers = ProductionSources()
            .Where(file => !file.Path.StartsWith(Workflow, StringComparison.Ordinal))
            .Where(file => Regex.IsMatch(file.Text, @"new\s+MafTroubleshootingWorkflow\("))
            .Select(file => file.Path)
            .ToArray();
        Assert.Equal(["src/core/bOps.Cli/MafDiagnoseCommand.cs"], composers);
    }

    [Fact]
    public void TheRuntimeGateway_AuthorizesNothingItself_ItReachesTheOneStepPipeline()
    {
        var gateway = ProductionSources().Single(file => file.Path == "src/core/bOps.Runtime/AgentRunner.WorkflowTools.cs").Text;
        Assert.Single(Regex.Matches(gateway, @"await ExecuteStepAsync\("));
        Assert.DoesNotContain("policyEngine.Evaluate", gateway, StringComparison.Ordinal);
        Assert.DoesNotContain("approvalProvider", gateway, StringComparison.Ordinal);
        Assert.DoesNotContain(".ExecuteAsync(", gateway, StringComparison.Ordinal);
        // The read-only ceiling is checked in the gateway before the step pipeline, and non-Read tools are refused there.
        Assert.Contains("RiskLevel.Read", gateway, StringComparison.Ordinal);
    }

    [Fact]
    public void NoShippedConfiguration_EnablesTheWorkflow()
    {
        var configuration = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("packages.lock.json", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("MafWorkflow", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .ToArray();
        Assert.Empty(configuration);
    }

    [Fact]
    public void TheWorkflowAndTheRuntimeGateway_NameNoPackageNoToolAndNoSpecialist()
    {
        // Specialists, packages and tools are host data. The workflow and the runtime gateway name none of them in a string literal.
        string[] names = ["system.", "docker.", "service.", "network.", "fs.", "postgres", "bops.packages", "shell.run", "system.exec"];
        var offenders = Sources(Workflow)
            .Concat(ProductionSources().Where(file => file.Path is "src/core/bOps.Runtime/AgentRunner.WorkflowTools.cs" or "src/core/bOps.Runtime/WorkflowToolSession.cs" or "src/core/bOps.Runtime/WorkflowAgentHandle.cs"))
            .SelectMany(file => names.Where(name => Regex.IsMatch(file.Text, $@"""[^""\n]*{Regex.Escape(name)}")).Select(name => $"{file.Path}: {name}"))
            .ToArray();
        Assert.Empty(offenders);
    }

    private static IEnumerable<(string Path, string Text)> Sources(string prefix) =>
        ProductionSources().Where(file => file.Path.StartsWith(prefix, StringComparison.Ordinal));

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

        throw new InvalidOperationException("The repository root was not found.");
    }
}
