// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world and rig passes to the test, which disposes the world.
#pragma warning disable CA2000

using System.Text.Json;
using System.Text.RegularExpressions;
using bOps.AgentFramework;
using bOps.Abstractions;
using bOps.Cli;
using bOps.Memory;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;

namespace bOps.Cli.Tests;

/// <summary>
/// BOPS-0122 / C01-R22: the opt-in MAF governed turn host of the CLI, driven through the code <c>Program.cs</c> runs
/// (<see cref="CliRunnerComposition"/>). Every scenario is run twice over the same scripted model, once on the default path and once with
/// the switch on, and everything the runner can observe is compared: each request offered to the model, the persisted task, the audit
/// trail and the tool executions. Offline: no provider, no network. Run ids, timestamps and durations are the only normalised values.
/// </summary>
public sealed class MafGovernedTurnHostTests
{
    private const string Goal = CliKnowledgeWorld.Goal;

    private static readonly Regex GuidPattern = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex InstantPattern = new(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})", RegexOptions.Compiled);
    private static readonly Regex DurationPattern = new(@"""(\w*(Duration|Elapsed|Latency)\w*)"":\s*(""[\d:.]+""|[-\d.eE+]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] BigToolNames = ["diag.alpha", "diag.beta", "diag.gamma", "diag.delta", "diag.epsilon"];

    private static ActorIdentity Operator => CliKnowledgeWorld.Operator;

    public static TheoryData<string> Scenarios =>
    [
        "plan-tool-final",
        "several-calls-first-wins",
        "unknown-tool-is-rejected",
        "high-risk-tool-is-denied",
        "early-final-replan",
        "provider-failure",
        "compaction-pressure",
    ];

    private static ModelResponse[] Script(string scenario) => scenario switch
    {
        "plan-tool-final" => [CliScript.Plan("system.events"), CliScript.Tool("c1", "system.events"), CliScript.Final("diagnosis")],
        "several-calls-first-wins" =>
        [
            CliScript.Plan("system.events"),
            new ModelResponse(null, [new ModelToolCall("c1", "system.events", ToolArguments.Empty), new ModelToolCall("c2", "system.events", ToolArguments.Empty)], false, new ModelUsage(100, 10, null)),
            CliScript.Final("diagnosis"),
        ],
        "unknown-tool-is-rejected" =>
        [
            CliScript.Plan("system.events"), CliScript.Tool("c1", "no.such.tool"), CliScript.Tool("c2", "system.events"), CliScript.Final("diagnosis"),
        ],
        "high-risk-tool-is-denied" =>
        [
            CliScript.Plan("service.restart"), CliScript.Tool("c1", "service.restart"), CliScript.Final("gave up"), CliScript.Final("gave up"),
        ],
        "early-final-replan" =>
        [
            CliScript.Plan("system.events"), CliScript.Final("too early"), CliScript.Plan("system.events"), CliScript.Tool("c1", "system.events"), CliScript.Final("diagnosis"),
        ],
        "provider-failure" => [CliScript.Plan("system.events")], // the next call is unscripted: the model call fails and the attempt ends Failed
        "compaction-pressure" =>
        [
            CliScript.Plan("diag.alpha", "diag.beta", "diag.gamma", "diag.delta", "diag.epsilon"),
            CliScript.Tool("c1", "diag.alpha"), CliScript.Tool("c2", "diag.beta"), CliScript.Tool("c3", "diag.gamma"),
            CliScript.Tool("c4", "diag.delta"), CliScript.Tool("c5", "diag.epsilon"), CliScript.Final("diagnosis"),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static ITool[] ExtraTools(string scenario) => scenario switch
    {
        "high-risk-tool-is-denied" => [new RestartTool()],
        "compaction-pressure" => [.. BigToolNames.Select(name => (ITool)new BigReadTool(name))],
        _ => [],
    };

    // ---- the composition itself ----

    [Fact]
    public async Task ByDefault_TheCompositionIsTheGovernedBrokerAlone_AndTheMafHostIsComposedOnlyWhenAsked()
    {
        using var off = new CliKnowledgeWorld();
        using var on = new CliKnowledgeWorld { MafGovernedTurn = true };

        var plain = off.Runner(Script("plan-tool-final"), settings: null);
        var hosted = on.Runner(Script("plan-tool-final"), settings: null);

        Assert.IsType<ModelInvocationBroker>(plain.Broker);
        var maf = Assert.IsType<MafGovernedTurnBroker>(hosted.Broker);
        Assert.Equal(0, maf.ComposedTurns);
        await hosted.Runner.RunAsync(Goal, Operator);
        Assert.True(maf.ComposedTurns > 0, "the scenario really went through the MAF governed turn host");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void TheSwitch_IsOffUnlessTheOperatorSetsItToTrue(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CliRunnerComposition.MafGovernedTurnKey] = value }).Build();

        Assert.Equal(expected, CliRunnerComposition.ReadMafGovernedTurn(configuration));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("tru")]
    public void ATypoIsRefused_NeverReadAsOnOrOff(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CliRunnerComposition.MafGovernedTurnKey] = value }).Build();

        var failure = Assert.Throws<InvalidOperationException>(() => CliRunnerComposition.ReadMafGovernedTurn(configuration));
        Assert.Contains(CliRunnerComposition.MafGovernedTurnKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithKnowledgeEnabled_TheExperimentIsRefused_AndNothingIsComposed()
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = true };

        var refusal = Assert.Throws<InvalidOperationException>(() => world.Runner(Script("plan-tool-final"), world.Settings()));

        Assert.Contains("Knowledge", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("C01-R22", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Events, evt => evt is ModelCallAuditEvent);
    }

    [Fact]
    public async Task WithKnowledgeDisabled_TheExperimentIsAllowed()
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = true };

        var rig = world.Runner(Script("plan-tool-final"), world.Settings(enabled: false));
        var state = await rig.Runner.RunAsync(Goal, Operator);

        Assert.Null(rig.Host);
        Assert.Equal(AgentTaskStatus.Completed, state.Status);
    }

    // ---- parity of one run ----

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task AHostedRun_IsObservablyIdenticalToTheDefaultRun(string scenario)
    {
        var taskId = Guid.NewGuid();
        var baseline = await RunAsync(scenario, mafGovernedTurn: false, taskId);
        var hosted = await RunAsync(scenario, mafGovernedTurn: true, taskId);

        Assert.True(hosted.Composed > 0, "the hosted run really used the MAF governed turn host");
        Assert.Equal(0, baseline.Composed);
        Assert.NotEmpty(baseline.Requests); // the comparison is not vacuous
        Assert.NotEmpty(baseline.Audit);
        Assert.Contains("\"Status\"", baseline.StoredState, StringComparison.Ordinal);
        Assert.Equal(baseline.Status, hosted.Status);
        Assert.Equal(baseline.ToolExecutions, hosted.ToolExecutions);
        Assert.Equal(baseline.Requests, hosted.Requests);
        Assert.Equal(baseline.StoredState, hosted.StoredState);
        Assert.Equal(baseline.Audit.Length, hosted.Audit.Length);
        for (var index = 0; index < baseline.Audit.Length; index++)
        {
            Assert.Equal(baseline.Audit[index], hosted.Audit[index]);
        }
    }

    [Fact]
    public async Task TheComparison_DetectsADifference_SoParityIsNotVacuous()
    {
        var taskId = Guid.NewGuid();
        var one = await RunAsync("plan-tool-final", mafGovernedTurn: false, taskId);
        var other = await RunAsync("early-final-replan", mafGovernedTurn: false, taskId);

        Assert.NotEqual(one.Requests, other.Requests);
        Assert.NotEqual(one.StoredState, other.StoredState);
    }

    [Fact]
    public async Task TheHostExecutesNothingItself_OnlyBopsRunsTheToolOnce_AndTheSecondProposedCallIsNeverRun()
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = true };
        var rig = world.Runner(Script("several-calls-first-wins"), settings: null);

        var state = await rig.Runner.RunAsync(Goal, Operator, Guid.NewGuid());

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(1, rig.Events.Executions);
        Assert.Equal(3, rig.Model.Requests.Count); // exactly the three scripted turns: no extra model call
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeniedHighRiskTool_IsNeverExecuted_InEitherMode(bool hosted)
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = hosted };
        var restart = new RestartTool();
        var rig = world.Runner(Script("high-risk-tool-is-denied"), settings: null, policy: null, approval: null, restart);

        await rig.Runner.RunAsync(Goal, Operator, Guid.NewGuid());

        Assert.Equal(0, restart.Executions);
    }

    // ---- switching mode between attempts: a restart or a resume ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ATaskInterruptedInOneMode_ResumesInTheOther_WithTheSameStateAndTheSameRequests(bool firstHosted, bool secondHosted)
    {
        var taskId = Guid.NewGuid();
        var control = await InterruptAndResumeAsync(false, false, taskId);
        var switched = await InterruptAndResumeAsync(firstHosted, secondHosted, taskId);

        Assert.Equal(AgentTaskStatus.Failed, control.Interrupted);
        Assert.Equal(AgentTaskStatus.Failed, switched.Interrupted);
        Assert.Equal(AgentTaskStatus.Completed, control.Resumed);
        Assert.Equal(AgentTaskStatus.Completed, switched.Resumed);
        Assert.Equal(control.FirstRequests, switched.FirstRequests);
        Assert.Equal(control.ResumeRequests, switched.ResumeRequests);
        Assert.Equal(control.StoredAfterInterruption, switched.StoredAfterInterruption);
        Assert.Equal(control.StoredAfterResume, switched.StoredAfterResume);
        Assert.Equal(control.Executions, switched.Executions);
    }

    private static async Task<Interruption> InterruptAndResumeAsync(bool firstHosted, bool secondHosted, Guid taskId)
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = firstHosted };
        var first = world.Runner([CliScript.Plan("system.events")], settings: null); // the next call is unscripted: the attempt ends Failed
        var interrupted = await first.Runner.RunAsync(Goal, Operator, taskId);
        var stored = await StoredAsync(first.Store, taskId);

        // A new process: new store handle, new runner, the other mode.
        world.MafGovernedTurn = secondHosted;
        var second = world.Runner([CliScript.Tool("c1", "system.events"), CliScript.Final("diagnosis")], settings: null);
        var resumed = await second.Runner.ResumeAsync((await second.Store.LoadAsync(taskId))!, Operator);
        var storedAfter = await StoredAsync(second.Store, taskId);
        return new Interruption(
            interrupted.Status, resumed.Status, Project(first.Model.Requests), Project(second.Model.Requests), stored, storedAfter,
            first.Events.Executions + second.Events.Executions);
    }

    // ---- helpers ----

    private sealed record Interruption(
        AgentTaskStatus Interrupted, AgentTaskStatus Resumed, string[] FirstRequests, string[] ResumeRequests,
        string StoredAfterInterruption, string StoredAfterResume, int Executions);

    private sealed record Observed(
        AgentTaskStatus Status, string[] Requests, string StoredState, string[] Audit, int ToolExecutions, int Composed);

    private static async Task<Observed> RunAsync(string scenario, bool mafGovernedTurn, Guid taskId)
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = mafGovernedTurn };
        var rig = world.Runner(Script(scenario), settings: null, policy: null, approval: null, ExtraTools(scenario));
        var state = await rig.Runner.RunAsync(Goal, Operator, taskId);
        var composed = (rig.Broker as MafGovernedTurnBroker)?.ComposedTurns ?? 0;
        var audit = world.Events.Select(evt => NormalizeText(JsonSerializer.Serialize(evt, evt.GetType()))).ToArray();
        return new Observed(state.Status, Project(rig.Model.Requests), await StoredAsync(rig.Store, taskId), audit, rig.Events.Executions, composed);
    }

    /// <summary>Everything a model request carries that the runner authors: prompt, offered tools (name and schema) and the whole history.</summary>
    private static string[] Project(IEnumerable<ModelRequest> requests) =>
    [
        .. requests.Select(request => NormalizeText(JsonSerializer.Serialize(new
        {
            request.SystemPrompt,
            Tools = request.AvailableTools.Select(tool => new { tool.Name, tool.Description, Parameters = tool.Parameters.Select(parameter => new { parameter.Name, parameter.Type, parameter.Required }) }),
            History = request.History,
        }))),
    ];

    private static async Task<string> StoredAsync(SqliteTaskStore store, Guid taskId)
    {
        var state = await store.LoadAsync(taskId);
        Assert.NotNull(state);
        return NormalizeText(JsonSerializer.Serialize(state));
    }

    private static string NormalizeText(string text)
    {
        var withoutInstants = InstantPattern.Replace(text, "<instant>");
        var withoutDurations = DurationPattern.Replace(withoutInstants, match => $"\"{match.Groups[1].Value}\":0");
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return GuidPattern.Replace(withoutDurations, match =>
        {
            if (!ids.TryGetValue(match.Value, out var token))
            {
                token = $"<id{ids.Count}>";
                ids[match.Value] = token;
            }

            return token;
        });
    }

    /// <summary>A Read tool with a big canned answer, so the projector has something to compact. The name is the one the plan expects.</summary>
    private sealed class BigReadTool(string name) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Reads a large canned answer (test).",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(string.Concat(Enumerable.Repeat($"{name} row of canned evidence. ", 220))));
    }
}
