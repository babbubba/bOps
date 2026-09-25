// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0038 in the runtime: the model's whole turn is kept (live and after a resume), a call the provider adapter could
/// not honour never executes, and plan and replan calls offer no native tool.
/// </summary>
public sealed class ToolCallProtocolTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private const string NotExecutedText = "Not executed: only one tool call is executed per step.";

    private static AgentRunner CreateRunner(
        IChatModel model, IToolRegistry registry, IAuditSink? audit = null, IPolicyEngine? policy = null, ITaskStore? store = null) =>
        new(model, registry, policy ?? new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit ?? new RecordingAuditSink(), store ?? new InMemoryTaskStore(), TimeProvider.System,
            NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());

    private static ToolRegistry Registry(params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return registry;
    }

    private static ModelToolCall Call(string id, string name) => new(id, name, ToolArguments.Empty);

    private static ModelResponse Calls(params ModelToolCall[] calls) => new(null, calls, false, null);

    private static ModelResponse Final(string text = "done") => new(text, [], true, null);

    /// <summary>The first breach of the tool protocol in <paramref name="history"/>: a tool result whose id is not in the assistant turn before it, or a call left unanswered.</summary>
    private static string? ProtocolViolation(IReadOnlyList<ChatTurn> history)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < history.Count; i++)
        {
            var turn = history[i];
            if (turn.Role == ChatRole.Tool)
            {
                if (turn.ToolCallId is null || !pending.Remove(turn.ToolCallId))
                {
                    return $"turn {i}: tool result '{turn.ToolCallId}' has no call in the assistant turn before it";
                }

                continue;
            }

            if (pending.Count > 0)
            {
                return $"turn {i}: calls {string.Join(", ", pending)} were never answered";
            }

            foreach (var call in turn.ToolCalls ?? [])
            {
                pending.Add(call.Id);
            }
        }

        return pending.Count > 0 ? $"calls {string.Join(", ", pending)} were never answered" : null;
    }

    private static List<string> Describe(IReadOnlyList<ChatTurn> history) =>
        [.. history.Select(turn => turn.Role switch
        {
            ChatRole.Assistant when turn.ToolCalls is { Count: > 0 } =>
                "assistant:" + string.Join(",", turn.ToolCalls.Select(call => $"{call.Id}={call.ToolName}{call.Arguments.ToJson().ToJsonString()}")),
            ChatRole.Tool => $"tool:{turn.ToolCallId}:{turn.Content}",
            _ => $"{turn.Role}:{turn.Content}",
        })];

    [Fact]
    public async Task H1_09_ASingleToolCall_KeepsAValidHistory_AndRecordsNoUnexecutedCalls()
    {
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Calls(Call("call-a", "test.a")), Final());

        var result = await CreateRunner(model, Registry(new FakeReadTool("test.a", "result-a"))).RunAsync("goal", Actor);

        Assert.Null(result.Steps[0].UnexecutedToolCalls);
        var next = model.Requests[2].History;
        Assert.Null(ProtocolViolation(next));
        Assert.Equal(["call-a"], next.Single(t => t.ToolCalls is not null).ToolCalls!.Select(c => c.Id));
    }

    [Fact]
    public async Task H1_10_H1_HIST1_H1_HIST3_H1_HIST4_AModelTurnWithThreeCalls_IsKeptWhole_AndOnlyTheFirstRuns()
    {
        var b = new RecordingReadTool("test.b", []);
        var c = new RecordingReadTool("test.c", []);
        var audit = new RecordingAuditSink();
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            Calls(Call("call-a", "test.a"), Call("call-b", "test.b"), Call("call-c", "test.c")),
            Final());

        var result = await CreateRunner(model, Registry(new FakeReadTool("test.a", "result-a"), b, c), audit).RunAsync("goal", Actor);

        // Only the selected call ran and was audited.
        Assert.Equal(0, b.ExecutionCount);
        Assert.Equal(0, c.ExecutionCount);
        Assert.Equal(["test.a"], audit.Events.OfType<ToolCallAuditEvent>().Select(e => e.Tool));

        // The persisted step keeps what the model emitted after the executed call.
        var step = result.Steps[0];
        Assert.Equal("call-a", step.ToolCall!.Id);
        Assert.Equal(["call-b", "call-c"], step.UnexecutedToolCalls!.Select(call => call.Id));
        Assert.Equal(["test.b", "test.c"], step.UnexecutedToolCalls!.Select(call => call.ToolName));

        // The next model turn sees every emitted call, the real result for one and "not executed" for the others.
        var history = model.Requests[2].History;
        Assert.Null(ProtocolViolation(history));
        var assistant = history.Single(turn => turn.ToolCalls is not null);
        Assert.Equal(["call-a", "call-b", "call-c"], assistant.ToolCalls!.Select(call => call.Id));
        var results = history.Where(turn => turn.Role == ChatRole.Tool).ToDictionary(turn => turn.ToolCallId!, turn => turn.Content!);
        Assert.Contains("result-a", results["call-a"], StringComparison.Ordinal);
        Assert.Contains(NotExecutedText, results["call-b"], StringComparison.Ordinal);
        Assert.Contains(NotExecutedText, results["call-c"], StringComparison.Ordinal);
        Assert.DoesNotContain("done", results["call-b"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task H1_HIST2_H1_HIST5_ResumeRebuildsTheSameTurns_FromTheStoredStateAlone()
    {
        var liveModel = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            Calls(Call("call-a", "test.a"), Call("call-b", "test.b"), Call("call-c", "test.c")),
            Final());
        var tools = new ITool[] { new FakeReadTool("test.a", "result-a"), new FakeReadTool("test.b"), new FakeReadTool("test.c") };
        var finished = await CreateRunner(liveModel, Registry(tools)).RunAsync("goal", Actor);

        // What a task store keeps: the state as JSON, with no provider payload in it (the model calls are dropped).
        var stored = finished with
        {
            Status = AgentTaskStatus.Running,
            Steps = [finished.Steps[0] with { ModelCalls = null }],
        };
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(stored))!;

        var resumeModel = new FakeChatModel(Final());
        await CreateRunner(resumeModel, Registry(tools)).ResumeAsync(reloaded, Actor);

        var live = liveModel.Requests[2].History;
        var resumed = resumeModel.Requests[0].History;
        Assert.Null(ProtocolViolation(resumed));
        Assert.Equal(Describe(live), Describe(resumed));
        Assert.Equal(3, resumed.Single(turn => turn.ToolCalls is not null).ToolCalls!.Count);
    }

    [Fact]
    public async Task H1_A1_ACallTheAdapterCouldNotMap_NeverExecutes_EvenWhenItsNameIsARealTool()
    {
        var tool = new RecordingReadTool("test.read", []);
        var audit = new RecordingAuditSink();
        var policy = new RecordingPolicyEngine();
        var invented = new ModelToolCall("call-1", "test.read", ToolArguments.Empty)
        {
            ToolNameError = "'test.read' is not a tool that was offered in this request.",
        };
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Calls(invented), PlanningTestSupport.PlanResponse(revision: 1), Final());

        var result = await CreateRunner(model, Registry(tool), audit, policy).RunAsync("goal", Actor);

        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(policy.Seen);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.UnknownTool, Outcome: ToolOutcome.Denied });
        Assert.Contains("was offered", result.Steps[0].Observation!, StringComparison.Ordinal);
        Assert.Equal(2, result.Plans.Count);
    }

    [Fact]
    public async Task H1_12_H1_13_H1_ARG5_MalformedArguments_NeverReachPolicyOrTheTool_AndTheModelIsToldWhy()
    {
        var tool = new RecordingReadTool("test.read", []);
        var audit = new RecordingAuditSink();
        var policy = new RecordingPolicyEngine();
        var malformed = new ModelToolCall("call-1", "test.read", ToolArguments.Empty) { ArgumentsError = "the arguments payload was empty" };
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Calls(malformed), Final());

        var result = await CreateRunner(model, Registry(tool), audit, policy).RunAsync("goal", Actor);

        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(policy.Seen);
        var step = result.Steps[0];
        Assert.Equal(ToolOutcome.Failure, step.Result!.Outcome);
        Assert.Contains("Invalid arguments for 'test.read'", step.Observation!, StringComparison.Ordinal);
        Assert.Contains("the arguments payload was empty", step.Observation!, StringComparison.Ordinal);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "test.read", Outcome: ToolOutcome.Failure });

        // The model sees the failure on its next turn, in a valid history.
        var history = model.Requests[2].History;
        Assert.Null(ProtocolViolation(history));
        Assert.Contains(history, turn => turn.Role == ChatRole.Tool && turn.Content!.Contains("Invalid arguments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task H1_05_PolicyAndAudit_SeeTheCanonicalDottedName()
    {
        var audit = new RecordingAuditSink();
        var policy = new RecordingPolicyEngine();
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Calls(Call("call-1", "fs.size")), Final());

        await CreateRunner(model, Registry(new FakeReadTool("fs.size")), audit, policy).RunAsync("goal", Actor);

        Assert.Equal(["fs.size"], policy.Seen);
        Assert.Equal(["fs.size"], audit.Events.OfType<ToolCallAuditEvent>().Select(e => e.Tool));
    }

    [Fact]
    public async Task H1_14_H1_15_H1_16_PlanAndReplanCallsOfferNoNativeTool_YetTheyStillListWhatTheTaskCanUse()
    {
        var invented = Call("call-1", "does.not.exist");
        var model = new FakeChatModel(
            new ModelResponse("not a plan", [], false, null), // the plan retry is tool-free too
            PlanningTestSupport.PlanResponse(),
            Calls(invented),
            new ModelResponse("still not a plan", [], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
            Final());
        var registry = Registry(new FakeReadTool("fs.size", parameters: [new ToolParameter("path", ToolParameterType.Path, "The secret parameter text", Required: true)]), new FakeReadTool("system.cpu"));

        var result = await CreateRunner(model, registry).RunAsync("goal", Actor);

        Assert.Equal(2, result.Plans.Count);
        var planning = new List<ModelRequest> { model.Requests[0], model.Requests[1], model.Requests[3], model.Requests[4] };
        Assert.All(planning, request =>
        {
            Assert.Empty(request.AvailableTools);
            Assert.Contains("- fs.size [Read]: A fake read-only tool for tests.", request.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("- system.cpu [Read]: A fake read-only tool for tests.", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("secret parameter", request.SystemPrompt, StringComparison.Ordinal);
        });

        // The step calls are unchanged: they still offer both tools natively.
        Assert.Equal(["fs.size", "system.cpu"], model.Requests[2].AvailableTools.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheToolCatalog_KeepsTheNameTheRiskAndTheFirstSentenceOfEachTool_InOrdinalOrder_AndIsBounded()
    {
        static ToolManifest Manifest(string name, string description, RiskLevel risk = RiskLevel.Read) => new()
        {
            Name = name,
            Description = description,
            Risk = risk,
            Platforms = ["linux", "windows"],
            Requires = [],
            Parameters = [],
        };

        var catalog = AgentRunner.DescribeToolCatalog(
        [
            Manifest("z.last", "Second tool. It has a second sentence that must not be listed."),
            Manifest("a.first", "Line one\nof the description.   Then more text."),
            Manifest("m.long", new string('x', 500), RiskLevel.High),
        ]);

        var lines = catalog.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["- a.first [Read]: Line one of the description.", "- m.long [High]: " + new string('x', 120) + "…", "- z.last [Read]: Second tool."], lines);
        Assert.DoesNotContain("second sentence", catalog, StringComparison.Ordinal);
        Assert.Contains("No tools are available", AgentRunner.DescribeToolCatalog([]), StringComparison.Ordinal);
    }

    /// <summary>Records the name of every tool a policy was asked about.</summary>
    private sealed class RecordingPolicyEngine : IPolicyEngine
    {
        public List<string> Seen { get; } = [];

        public PolicyDecision Evaluate(PolicyContext context)
        {
            Seen.Add(context.Manifest.Name);
            return new PolicyDecision(PolicyMode.Automatic, "test: automatic");
        }
    }
}
