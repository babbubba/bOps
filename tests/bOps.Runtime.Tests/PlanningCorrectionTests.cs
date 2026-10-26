// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// AF-PLAN-01: the initial plan call's bounded correction. BENCH-01 R2 B02 proposed a fenced plan whose
/// <c>system.events</c> step had <c>limit: 1000</c> against a declared maximum of 500, twice, after a generic re-ask; the
/// runtime then continued without a plan, which offers no native tool, and the model spent its output on reasoning. The
/// runtime now names the exact problem, never repeats a correction that made no progress, and fails explicitly instead of
/// continuing without a plan. No invalid plan is ever accepted or executed.
/// </summary>
public sealed class PlanningCorrectionTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private const string LimitProblem = "Argument 'limit' = 1000 exceeds maximum 500";

    private static RecordingReadTool EventsTool() => new("system.events",
    [
        new ToolParameter("limit", ToolParameterType.Integer, "Maximum events (1-500).", Required: false) { Minimum = 1, Maximum = 500 },
        new ToolParameter("windowDays", ToolParameterType.Integer, "Look-back in days.", Required: false) { Minimum = 1, Maximum = 180 },
    ]);

    private static AgentRunner Runner(IChatModel model, ITool tool, RecordingAuditSink? audit = null, AgentRunnerOptions? options = null,
        ITaskStore? store = null)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), tool);
        return new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit ?? new RecordingAuditSink(), store ?? new InMemoryTaskStore(), TimeProvider.System,
            NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());
    }

    private static ModelResponse Text(string? text, string finish = "stop", int completion = 10) =>
        new(text, [], false, new ModelUsage(100, completion, null))
        {
            Details = new ModelCallDetails("fake-model", finish, "request", text ?? string.Empty),
        };

    private static string EventsPlan(int limit) => new JsonObject
    {
        ["rationale"] = "Read the recent system events.",
        ["steps"] = new JsonArray(new JsonObject
        {
            ["description"] = "Check warning events",
            ["expectedTool"] = "system.events",
            ["expectedArguments"] = new JsonObject { ["windowDays"] = 180, ["limit"] = limit },
        }),
    }.ToJsonString();

    private static string Fenced(string json) => $"```json\n{json}\n```";

    private static ModelResponse EventsCall(int limit) => new(null,
        [new ModelToolCall("c1", "system.events", ToolArguments.FromJson(new JsonObject { ["windowDays"] = 180, ["limit"] = limit }))],
        false, new ModelUsage(100, 10, null));

    private static ModelResponse Done() => new("Done.", [], true, new ModelUsage(100, 10, null));

    private static string LastUserTurn(ModelRequest request) =>
        request.History.Last(turn => turn.Role == ChatRole.User).Content ?? string.Empty;

    [Fact]
    public async Task B02_AnOutOfRangeLimitInAFencedPlan_IsCorrectedWithTheExactProblem_AndOnlyTheCorrectedPlanRuns()
    {
        var tool = EventsTool();
        var model = new FakeChatModel(Text(Fenced(EventsPlan(1000))), Text(Fenced(EventsPlan(500))), EventsCall(500), Done());

        var task = await Runner(model, tool).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Contains(LimitProblem, LastUserTurn(model.Requests[1]), StringComparison.Ordinal);
        var plan = Assert.Single(task.Plans);
        Assert.Equal(500, plan.Steps[0].ExpectedArguments!.ToJson()["limit"]!.GetValue<int>());
        Assert.Equal(
            [ModelCallOutcome.Failure, ModelCallOutcome.Success], plan.ModelCalls!.Select(call => call.Outcome));
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task B02_TheSameInvalidPlanAfterTheCorrection_FailsExplicitly_WithNoToolAndNoPlanlessStep()
    {
        var tool = EventsTool();
        var audit = new RecordingAuditSink();
        var model = new FakeChatModel(Text(Fenced(EventsPlan(1000))), Text(EventsPlan(1000)));

        var task = await Runner(model, tool, audit).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(TaskTerminalKind.ModelFailure, task.TerminalReason!.Kind);
        Assert.Equal(ModelFailureKind.MalformedResponse, task.TerminalReason.FailureKind);
        Assert.Equal(2, model.Requests.Count);
        Assert.Empty(task.Plans);
        Assert.Equal(0, tool.ExecutionCount);
        var failure = Assert.Single(task.Steps);
        Assert.Null(failure.ToolCall);
        Assert.StartsWith("Planning failed", failure.Observation, StringComparison.Ordinal);
        Assert.Contains(LimitProblem, failure.Observation, StringComparison.Ordinal);
        Assert.Equal(2, failure.ModelCalls!.Count);
        Assert.Equal(220, task.Accounting!.TokensUsed);
        Assert.Equal(2, audit.Events.OfType<ModelCallAuditEvent>()
            .Count(e => e is { StepIndex: -1, FailureKind: ModelFailureKind.MalformedResponse }));
        Assert.DoesNotContain(audit.Events, e => e is ToolCallAuditEvent);
    }

    [Fact]
    public async Task AFencedPlanAmongProseWithBraces_IsParsedFromTheFence()
    {
        var tool = EventsTool();
        var reply = $"Plan follows {{see below}}.\n{Fenced(EventsPlan(200))}\nI will adapt {{if needed}}.";
        var model = new FakeChatModel(Text(reply), EventsCall(200), Done());

        var task = await Runner(model, tool).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(ModelCallOutcome.Success, Assert.Single(task.Plans[0].ModelCalls!).Outcome);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Theory]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("x {y} ```text\nnot json\n``` ```json\n{\"a\":1}\n``` {z}", "{\"a\":1}")]
    [InlineData("prose {\"a\":1} prose", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}", "{\"a\":1}")]
    [InlineData("no object here", null)]
    public void PlanJsonCandidate_PrefersAFencedObject_AndOtherwiseTheOutermostBraces(string text, string? expected) =>
        Assert.Equal(expected, AgentRunner.PlanJsonCandidate(text));

    [Fact]
    public async Task AnOutputLimitedEmptyPlanReply_GetsTheOutputLimitCorrection_WithoutAnEmptyAssistantTurn()
    {
        var model = new FakeChatModel(
            Text(null, "length", 4096), Text(PlanningTestSupport.PlanResponse(stepCount: 0).TextResponse), Done());

        var task = await Runner(model, EventsTool()).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Contains("reached the output limit", LastUserTurn(model.Requests[1]), StringComparison.Ordinal);
        Assert.DoesNotContain(model.Requests[1].History,
            turn => turn.Role == ChatRole.Assistant && string.IsNullOrEmpty(turn.Content) && turn.ToolCalls is null);
    }

    [Fact]
    public async Task ReasoningTruncatedTwice_FailsAfterTwoCalls_WithoutAThirdIdenticalRequest()
    {
        var model = new FakeChatModel(Text(null, "length", 4096), Text(null, "length", 4096));

        var task = await Runner(model, EventsTool()).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal((TaskTerminalKind.ModelFailure, ModelFailureKind.MalformedResponse),
            (task.TerminalReason!.Kind, task.TerminalReason.FailureKind!.Value));
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("empty", Assert.Single(task.Steps).Observation, StringComparison.Ordinal);
        Assert.Equal(2 * (100 + 4096), task.Accounting!.TokensUsed);
    }

    [Fact]
    public async Task ChangingProblems_AreCorrectedAtMostTwice_ThenTheAttemptFails()
    {
        var undeclared = EventsPlan(100).Replace("\"limit\"", "\"maxRows\"", StringComparison.Ordinal);
        var tool = EventsTool();
        var model = new FakeChatModel(Text("I will look around."), Text(EventsPlan(1000)), Text(undeclared));

        var task = await Runner(model, tool).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(1 + AgentRunner.MaxPlanCorrections, model.Requests.Count);
        Assert.Contains("did not contain a JSON object", LastUserTurn(model.Requests[1]), StringComparison.Ordinal);
        Assert.Contains(LimitProblem, LastUserTurn(model.Requests[2]), StringComparison.Ordinal);
        Assert.Contains("undeclared parameter 'maxRows'", task.Steps[0].Observation, StringComparison.Ordinal);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task TheTokenBudget_StopsACorrection_BeforeAnotherPaidCall()
    {
        var model = new FakeChatModel(Text(EventsPlan(1000)), Text(EventsPlan(1000)));

        var task = await Runner(model, EventsTool(), options: new AgentRunnerOptions { MaxTotalTokens = 150 }).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.BudgetExceeded, task.Status);
        Assert.Equal(TaskTerminalKind.TokenBudget, task.TerminalReason!.Kind);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(220, task.Accounting!.TokensUsed);
    }

    [Fact]
    public async Task AResumeAfterAPlanningFailure_PlansAgain_AndKeepsTheLifetimeAccounting()
    {
        var store = new InMemoryTaskStore();
        var tool = EventsTool();
        var failed = await Runner(new FakeChatModel(Text(EventsPlan(1000)), Text(EventsPlan(1000))), tool, store: store)
            .RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, failed.Status);

        var resumeModel = new FakeChatModel(Text(EventsPlan(500)), EventsCall(500), Done());
        var resumed = await Runner(resumeModel, tool, store: store).ResumeAsync((await store.LoadAsync(failed.Id))!, Actor);

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Single(resumed.Plans);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(220 + 330, resumed.Accounting!.TokensUsed);
    }

    [Fact]
    public async Task AnOutputLimitedEmptyStepReply_IsReaskedOnceWithTheOutputLimitNotice()
    {
        var tool = EventsTool();
        var model = new FakeChatModel(Text(EventsPlan(500)), Text(null, "length", 4096), EventsCall(500), Done());

        var task = await Runner(model, tool).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Contains("reached the output limit while reasoning", LastUserTurn(model.Requests[2]), StringComparison.Ordinal);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task AMalformedReplan_IsReaskedWithTheExactProblem()
    {
        var unknown = new ModelToolCall("c0", "does.not.exist", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "does.not.exist"),
            new ModelResponse(null, [unknown], false, null),
            Text(EventsPlan(1000)),
            Text(PlanningTestSupport.PlanResponse(stepCount: 0).TextResponse),
            Done());

        var task = await Runner(model, EventsTool()).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Contains(LimitProblem, LastUserTurn(model.Requests[3]), StringComparison.Ordinal);
        Assert.Equal(2, task.Plans.Count);
    }

    // MAF A/B/C/D pilots (Qwen3.8): plans expected `mode: raw` with `sinceDays`, or both windows, which the tool refuses at
    // execution. The step could then only be satisfied by a call the tool rejects, so the run replanned the same combination until
    // it ran out of steps. The tool's declared relations now refuse such a plan, with the rule, before any step starts.
    private const string ExclusiveRule = "Use either sinceMinutes or sinceDays, never both.";
    private const string RawRule = "sinceDays is valid only with mode aggregate; with mode raw use sinceMinutes (up to 10080).";

    private static RecordingReadTool CrashesTool() => new("test.crashes",
        [
            new ToolParameter("mode", ToolParameterType.Enum, "Output shape.", Required: false, AllowedValues: ["aggregate", "raw"]),
            new ToolParameter("sinceMinutes", ToolParameterType.Integer, "Window in minutes.", Required: false) { Minimum = 1, Maximum = 10080 },
            new ToolParameter("sinceDays", ToolParameterType.Integer, "Window in days.", Required: false) { Minimum = 1, Maximum = 180 },
        ],
        rules:
        [
            ToolArgumentRule.Exclusive("sinceMinutes", "sinceDays", ExclusiveRule),
            ToolArgumentRule.ForbiddenWith("sinceDays", "mode", ["raw"], RawRule),
        ]);

    private static string CrashesPlan(JsonObject expected) => new JsonObject
    {
        ["rationale"] = "Read the recent crashes.",
        ["steps"] = new JsonArray(new JsonObject
        {
            ["description"] = "Check crashes",
            ["expectedTool"] = "test.crashes",
            ["expectedArguments"] = expected,
        }),
    }.ToJsonString();

    private static ModelResponse CrashesCall(JsonObject arguments) => new(null,
        [new ModelToolCall("c1", "test.crashes", ToolArguments.FromJson(arguments))], false, new ModelUsage(100, 10, null));

    [Theory]
    [InlineData("""{"mode":"raw","sinceDays":14}""", RawRule)]
    [InlineData("""{"sinceMinutes":60,"sinceDays":14}""", ExclusiveRule)]
    public async Task APlanThatExpectsAnUnsatisfiableArgumentCombination_IsRefusedWithTheRule_AndTheCorrectedPlanRuns(
        string invalid, string rule)
    {
        var tool = CrashesTool();
        var corrected = new JsonObject { ["mode"] = "raw", ["sinceMinutes"] = 60 };
        var model = new FakeChatModel(
            Text(CrashesPlan(JsonNode.Parse(invalid)!.AsObject())), Text(CrashesPlan(corrected)),
            CrashesCall((JsonObject)corrected.DeepClone()), Done());

        var task = await Runner(model, tool).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Contains(rule, LastUserTurn(model.Requests[1]), StringComparison.Ordinal);
        Assert.Contains("cannot be satisfied by any call of 'test.crashes'", LastUserTurn(model.Requests[1]), StringComparison.Ordinal);
        Assert.Equal(60, Assert.Single(task.Plans).Steps[0].ExpectedArguments!.ToJson()["sinceMinutes"]!.GetValue<int>());
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task APlanThatConstrainsOnlyTheDaysWindow_IsAccepted_BecauseTheCallMayStillChooseAggregate()
    {
        var tool = CrashesTool();
        var model = new FakeChatModel(
            Text(CrashesPlan(new JsonObject { ["sinceDays"] = 14 })),
            CrashesCall(new JsonObject { ["sinceDays"] = 14, ["mode"] = "aggregate" }), Done());

        var task = await Runner(model, tool).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public void ThePlanningCatalog_StatesTheDeclaredRulesOfAToolBesideItsParameters()
    {
        var catalog = AgentRunner.DescribeToolCatalog([CrashesTool().Manifest]);

        Assert.Contains($"Rules for expectedArguments: {ExclusiveRule} {RawRule}", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("Rules for expectedArguments", AgentRunner.DescribeToolCatalog([EventsTool().Manifest]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARuntimeControlFunctionNamedAsAPlanStep_IsRefusedWithAnExplanationOfWhatAPlanStepIs()
    {
        var plan = new JsonObject
        {
            ["rationale"] = "Read the evidence.",
            ["steps"] = new JsonArray(new JsonObject
            {
                ["description"] = "Read earlier evidence",
                ["expectedTool"] = EvidenceRead.ControlFunctionName,
                ["expectedArguments"] = new JsonObject(),
            }),
        }.ToJsonString();
        var model = new FakeChatModel(Text(plan), Text(plan));

        var task = await Runner(model, EventsTool()).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Contains("runtime control function, not a plan step", LastUserTurn(model.Requests[1]), StringComparison.Ordinal);
        Assert.Contains("only a tool from the tool list", LastUserTurn(model.Requests[1]), StringComparison.Ordinal);
    }

    // MAF A/B/C/D pilot B02-D (Qwen3.8): a 17-step plan under Agent:MaxSteps 15. The plan could never finish in the attempt, so
    // the run spent every step and ended MaxStepsReached without an answer. A plan now fits the attempt's remaining steps, one
    // of which is kept for the final answer, and the planner is told that budget.
    private static string EventsPlanOf(int steps) => new JsonObject
    {
        ["rationale"] = "Read the system events in several slices.",
        ["steps"] = new JsonArray([.. Enumerable.Range(1, steps).Select(limit => (JsonNode)new JsonObject
        {
            ["description"] = $"Check slice {limit}",
            ["expectedTool"] = "system.events",
            ["expectedArguments"] = new JsonObject { ["windowDays"] = 180, ["limit"] = limit },
        })]),
    }.ToJsonString();

    [Fact]
    public async Task B02_APlanLongerThanTheAttemptBudget_IsRefusedWithTheBudget_AndTheCorrectedPlanRuns()
    {
        var tool = EventsTool();
        var audit = new RecordingAuditSink();
        var model = new FakeChatModel(Text(EventsPlanOf(3)), Text(EventsPlanOf(2)), EventsCall(1), EventsCall(2), Done());

        var task = await Runner(model, tool, audit, new AgentRunnerOptions { MaxSteps = 3 }).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Contains("The plan lists 3 steps", LastUserTurn(model.Requests[1]), StringComparison.Ordinal);
        Assert.Contains("a plan may list at most 2", LastUserTurn(model.Requests[1]), StringComparison.Ordinal);
        var plan = Assert.Single(task.Plans);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal([ModelCallOutcome.Failure, ModelCallOutcome.Success], plan.ModelCalls!.Select(call => call.Outcome));
        Assert.Equal(1, audit.Events.OfType<ModelCallAuditEvent>()
            .Count(e => e is { StepIndex: -1, FailureKind: ModelFailureKind.MalformedResponse }));
        Assert.Equal(2, tool.ExecutionCount);
    }

    [Fact]
    public async Task APlanThatStillExceedsTheAttemptBudget_FailsThePlanning_BeforeAnyStepIsSpent()
    {
        var tool = EventsTool();
        var model = new FakeChatModel(Text(EventsPlanOf(15)), Text(EventsPlanOf(15)));

        var task = await Runner(model, tool).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal((TaskTerminalKind.ModelFailure, ModelFailureKind.MalformedResponse),
            (task.TerminalReason!.Kind, task.TerminalReason.FailureKind!.Value));
        Assert.Equal(2, model.Requests.Count);
        Assert.Empty(task.Plans);
        Assert.Contains("a plan may list at most 14", Assert.Single(task.Steps).Observation, StringComparison.Ordinal);
        Assert.Equal(0, task.Accounting!.LifetimeSteps);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task APlanThatFillsTheAttemptBudget_LessTheFinalAnswer_IsAccepted_AndCompletes()
    {
        var tool = EventsTool();
        var model = new FakeChatModel(Text(EventsPlanOf(2)), EventsCall(1), EventsCall(2), Done());

        var task = await Runner(model, tool, options: new AgentRunnerOptions { MaxSteps = 3 }).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(3, task.Accounting!.LifetimeSteps);
        Assert.Equal(ModelCallOutcome.Success, Assert.Single(task.Plans[0].ModelCalls!).Outcome);
    }

    [Fact]
    public async Task ThePlanningPrompt_StatesTheAttemptStepBudget()
    {
        var model = new FakeChatModel(Text(EventsPlanOf(1)), EventsCall(1), Done());

        await Runner(model, EventsTool()).RunAsync("diagnose", Actor);

        Assert.Contains(
            "This execution attempt has 15 steps left. Every tool call the runtime executes or refuses, and the final answer, " +
            "uses one step, so a plan may list at most 14 steps (conditional steps included); a longer plan is refused.",
            model.Requests[0].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplanLongerThanTheStepsLeft_IsRefusedWithTheBudget_AndTheCorrectedReplanIsAccepted()
    {
        var unknown = new ModelToolCall("c0", "does.not.exist", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "does.not.exist"),
            new ModelResponse(null, [unknown], false, null),
            Text(EventsPlanOf(3)),
            Text(PlanningTestSupport.PlanResponse(stepCount: 0).TextResponse),
            Done());

        var task = await Runner(model, EventsTool(), options: new AgentRunnerOptions { MaxSteps = 4 }).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Contains("This execution attempt has 3 steps left", model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("a plan may list at most 2", LastUserTurn(model.Requests[3]), StringComparison.Ordinal);
        Assert.Equal(2, task.Plans.Count);
    }

    [Theory]
    [InlineData(15, 60, 0, 0, 15)]
    [InlineData(15, 60, 4, 4, 11)]
    [InlineData(15, 60, 0, 50, 10)]
    [InlineData(15, 60, 0, 60, 0)]
    [InlineData(1, 60, 0, 0, 1)]
    public void TheStepsLeft_AreTheAttemptsOwn_CappedByTheLifetimeBudget(
        int maxSteps, int maxLifetimeSteps, int attemptSteps, int lifetimeSteps, int expected)
    {
        var budget = PlanStepBudget.For(
            new AgentRunnerOptions { MaxSteps = maxSteps, MaxLifetimeSteps = maxLifetimeSteps }, attemptSteps, lifetimeSteps);

        Assert.Equal(expected, budget.StepsLeft);
        Assert.Equal(Math.Max(0, expected - 1), budget.MaxPlanSteps);
    }

    [Fact]
    public void WithOneStepLeft_OnlyAnEmptyPlanFits_AndThePromptSaysSo()
    {
        var budget = PlanStepBudget.For(new AgentRunnerOptions { MaxSteps = 1 }, 0, 0);

        Assert.Null(budget.Problem(0));
        Assert.NotNull(budget.Problem(1));
        Assert.Contains("return an empty \"steps\" array", budget.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplanAfterTheAttemptsLastStep_IsSizedForTheResumeThatRunsIt_AndSaysSo()
    {
        var options = new AgentRunnerOptions { MaxSteps = 15, MaxLifetimeSteps = 60 };

        var within = PlanStepBudget.ForReplan(options, attemptSteps: 9, lifetimeSteps: 9);
        var after = PlanStepBudget.ForReplan(options, attemptSteps: 15, lifetimeSteps: 15);
        var lifetimeSpent = PlanStepBudget.ForReplan(options, attemptSteps: 15, lifetimeSteps: 60);

        Assert.Equal((6, false), (within.StepsLeft, within.NextAttempt));
        Assert.Equal((15, true, 14), (after.StepsLeft, after.NextAttempt, after.MaxPlanSteps));
        Assert.StartsWith(
            "This execution attempt has no step left: this plan runs in the next execution attempt, which has 15 steps.",
            after.Instructions, StringComparison.Ordinal);
        Assert.Contains("the next execution attempt has 15 steps and", after.Problem(15), StringComparison.Ordinal);
        Assert.Equal(0, lifetimeSpent.MaxPlanSteps);
    }
}
