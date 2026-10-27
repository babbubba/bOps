// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using bOps.AgentFramework.Troubleshooting;
using bOps.Memory;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace bOps.Cli.Tests;

/// <summary>
/// C01-R23 in the CLI: <c>bops diagnose "&lt;goal&gt;" --workflow maf</c>. The arguments are strict; the workflow is off unless the host enables
/// it; and with it enabled the command runs the real workflow over the real runner and prints the stages, the agents, the tool calls, the
/// review, the diagnosis and the consumption. Offline: a scripted model and one fixture tool, no provider and no machine tool.
/// </summary>
public sealed class MafDiagnoseCommandTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bops-diagnose-{Guid.NewGuid():N}");


    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed is not a test failure.
        }
    }

    [Fact]
    public void TheArguments_NameTheWorkflow_TheGoalAndTheLimits()
    {
        var (parsed, error) = DiagnoseArguments.Parse(["Analizza", "i riavvii", "--workflow", "maf", "--max-rounds", "3", "--timeout-minutes", "10"]);

        Assert.Null(error);
        Assert.Equal("Analizza i riavvii", parsed!.Goal);
        Assert.Equal(3, parsed.MaxRounds);
        Assert.Equal(10, parsed.TimeoutMinutes);
        Assert.Null(parsed.MaxToolCalls);
    }

    [Theory]
    [InlineData("--workflow", "maf")]
    [InlineData("goal", "--workflow", "other")]
    [InlineData("goal")]
    [InlineData("goal", "--workflow", "maf", "--max-rounds", "0")]
    [InlineData("goal", "--workflow", "maf", "--max-tokens", "-5")]
    [InlineData("goal", "--workflow", "maf", "--nope", "1")]
    [InlineData("goal", "--workflow")]
    public void ABadInvocation_IsRefusedWithAMessage(params string[] args)
    {
        var (parsed, error) = DiagnoseArguments.Parse(args);

        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TheWorkflowIsOffByDefault_AndTheOptInIsStrict()
    {
        Assert.False(MafDiagnoseCommand.ReadEnabled(Config(null)));
        Assert.False(MafDiagnoseCommand.ReadEnabled(Config("")));
        Assert.False(MafDiagnoseCommand.ReadEnabled(Config("false")));
        Assert.True(MafDiagnoseCommand.ReadEnabled(Config("true")));
        var refused = Assert.Throws<InvalidOperationException>(() => MafDiagnoseCommand.ReadEnabled(Config("yes")));
        Assert.Contains(MafDiagnoseCommand.EnabledKey, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedConfiguration_NeverEnablesTheWorkflow()
    {
        var shipped = Directory.GetFiles(FindRepositoryRoot(), "appsettings*.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains("node_modules", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(shipped);
        Assert.All(shipped, path => Assert.DoesNotContain("MafWorkflow", File.ReadAllText(path), StringComparison.Ordinal));
    }

    [Fact]
    public void TheLimitsAGivenInvocationAsks_CannotExceedTheHardMaxima()
    {
        var tooMany = new DiagnoseInvocation("goal", MafWorkflowLimits.HardMaxInvestigationRounds + 1, null, null, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => MafDiagnoseCommand.LimitsFor(tooMany));
        Assert.Equal(2, MafDiagnoseCommand.LimitsFor(new DiagnoseInvocation("goal", null, null, null, null)).MaxInvestigationRounds);
    }

    [Fact]
    public async Task TheCommand_RunsTheRealWorkflow_AndShowsStagesAgentsToolsReviewDiagnosisAndConsumption()
    {
        var (command, output, error, audit) = Compose("tasks.db");

        var code = await command.RunAsync(
            new DiagnoseInvocation("Analizza il motivo dei riavvii Windows", null, null, null, null), ActorIdentity.FromOperatingSystemUser("cli-test"), default);

        var text = output.ToString();
        log.WriteLine(text);
        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Workflow started", text, StringComparison.Ordinal);
        Assert.Contains("Triage: selected: system", text, StringComparison.Ordinal);
        Assert.Contains("postgresql: no tool is available on this node", text, StringComparison.Ordinal);
        Assert.Contains("specialist-system [round 1] requests system.events", text, StringComparison.Ordinal);
        Assert.Contains("specialist-system [round 1] ← system.events → Executed", text, StringComparison.Ordinal);
        Assert.Contains("Diagnostic review [round 1]: evidence sufficient", text, StringComparison.Ordinal);
        Assert.Contains("=== Diagnosis", text, StringComparison.Ordinal);
        Assert.Contains("Intervention plan (advisory: nothing here is executed)", text, StringComparison.Ordinal);
        Assert.Contains("1 tool call(s)", text, StringComparison.Ordinal);
        Assert.Contains("tokens", text, StringComparison.Ordinal);
        Assert.Equal(1, audit.Events.OfType<ToolCallAuditEvent>().Count(evt => evt.Workflow is not null));
    }

    [Fact]
    public async Task ARunThatEndsOnALimit_ExitsWithTheLimitCode_AndStillPrintsTheReport()
    {
        var (command, output, _, _) = Compose("tasks2.db");

        var code = await command.RunAsync(
            new DiagnoseInvocation("Analizza i riavvii", null, null, 100, null), ActorIdentity.FromOperatingSystemUser("cli-test"), default);

        Assert.Equal(MafDiagnoseCommand.LimitReachedExitCode, code);
        Assert.Contains("limit: token_budget", output.ToString(), StringComparison.Ordinal);
    }

    private (MafDiagnoseCommand Command, StringWriter Output, StringWriter Error, ListAuditSink Audit) Compose(string databaseName)
    {
        Directory.CreateDirectory(_dir);
        var audit = new ListAuditSink();
        var model = new CliScriptedModel();
        var registry = new ToolRegistry(new AlwaysOn());
        registry.Register(new PackageId("bops.packages.system.windows"), new EventsTool());
        var options = new AgentRunnerOptions();
        var broker = new ModelInvocationBroker(model, audit, TimeProvider.System, NullLogger.Instance, options);
        var runner = new AgentRunner(
            model, registry, new ReadOnlyPolicy(), new NoApproval(), audit, new SqliteTaskStore(Path.Combine(_dir, databaseName)),
            TimeProvider.System, NullLogger<AgentRunner>.Instance, options, invocationBroker: broker);
        var output = new StringWriter();
        var error = new StringWriter();
        return (new MafDiagnoseCommand(runner, broker, audit, TimeProvider.System, NullLogger.Instance, output, error), output, error, audit);
    }

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { [MafDiagnoseCommand.EnabledKey] = value }).Build();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private sealed class ListAuditSink : IAuditSink
    {
        private readonly Lock gate = new();
        private readonly List<AuditEvent> events = [];

        public IReadOnlyList<AuditEvent> Events
        {
            get
            {
                lock (gate)
                {
                    return [.. events];
                }
            }
        }

        public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
        {
            lock (gate)
            {
                events.Add(evt);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysOn : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class ReadOnlyPolicy : IPolicyEngine
    {
        public PolicyDecision Evaluate(PolicyContext context) =>
            context.Manifest.Risk == RiskLevel.Read ? new PolicyDecision(PolicyMode.Automatic, "read") : new PolicyDecision(PolicyMode.Forbidden, "not read");
    }

    private sealed class NoApproval : IApprovalProvider
    {
        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default) =>
            throw new InvalidOperationException("No approval is expected in this test.");
    }

    private sealed class EventsTool : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "system.events",
            Description = "Reads recent system events.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success("Kernel-Power 41 at 03:12."));
    }

    /// <summary>Answers by role: the triage agent picks the system specialist, which reads the events once, and the reviewer concludes.</summary>
    private sealed class CliScriptedModel : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var usage = new ModelUsage(100, 20, null);
            var evidence = Regex.Match(string.Join('\n', request.History.Select(turn => turn.Content)), @"ev1:[0-9a-f\-]+:\d+").Value;
            if (request.SystemPrompt.Contains("the triage agent", StringComparison.Ordinal))
            {
                return Task.FromResult(new ModelResponse(
                    """{"problem":"Restarts","assignments":[{"specialist":"system","objective":"Find why Windows restarts."}]}""", [], true, usage));
            }

            if (request.SystemPrompt.Contains("the diagnostic reviewer", StringComparison.Ordinal))
            {
                return Task.FromResult(new ModelResponse(
                    new JsonObject
                    {
                        ["sufficient"] = true,
                        ["diagnosis"] = "Kernel-Power 41 was logged; the cause is not shown.",
                        ["facts"] = new JsonArray(new JsonObject { ["text"] = "Kernel-Power 41 at 03:12.", ["evidence"] = new JsonArray(evidence) }),
                        ["interventionPlan"] = new JsonArray("Advisory: collect a kernel dump at the next restart."),
                    }.ToJsonString(),
                    [], true, usage));
            }

            if (!request.History.Any(turn => turn.Role == bOps.Abstractions.ChatRole.Tool))
            {
                return Task.FromResult(new ModelResponse(null, [new ModelToolCall("c1", "system.events", new ToolArguments())], false, usage));
            }

            return Task.FromResult(new ModelResponse(
                new JsonObject
                {
                    ["summary"] = "One restart event.",
                    ["observations"] = new JsonArray(new JsonObject { ["text"] = "Kernel-Power 41 at 03:12.", ["evidence"] = new JsonArray(evidence) }),
                }.ToJsonString(),
                [], true, usage));
        }
    }
}
