// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>
/// AF-TOOLROUTE-01: deterministic reproduction of BENCH B03 (archive 20261012T111826Z, candidate AF-PLAN-01 <c>1292266</c>,
/// OpenRouter Nemotron). DETERMINISTIC FIXTURE on the real runner, broker, MAF context projector and OpenAI-compatible adapter,
/// against a scripted upstream that reproduces the live model's observed BEHAVIOUR (synthetic data, no live text):
/// <list type="number">
/// <item>it plans five read checks and runs them, calling each offered tool by the name it is offered under;</item>
/// <item>once the plan is complete it wants a wider crash window and calls <c>system_crashes</c>, which is not offered;</item>
/// <item>asked to revise the plan, it re-proposes the five completed checks unchanged, unless the runtime tells it exactly why
/// that plan is rejected, in which case it plans the wider window;</item>
/// <item>after it has seen a <c>_2E</c>-escaped tool name in its own history it escapes the next one the same way (live step 7).</item>
/// </list>
/// Live, the adapter rejection of step 2 was lost on the MAF history round trip: the raw name re-entered later requests as a
/// canonical call, collided with <c>system.crashes</c>, and the offered tool was renamed <c>system_2Ecrashes</c>; three replans
/// re-ran the completed checks until <c>ReplanLimitReached</c>. These are reusable behavioural guarantees for ARCH-02.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed partial class ToolRoutingRegressionTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("regression");

    private static readonly JsonObject CrashesHour = new() { ["mode"] = "raw", ["sinceMinutes"] = 60, ["limit"] = 100 };
    private static readonly JsonObject CrashesWeek = new() { ["mode"] = "raw", ["sinceMinutes"] = 10_080, ["limit"] = 200 };
    private static readonly string[] SingleRunTools = ["system.events", "system.memory", "system.disk", "system.cpu"];
    private static readonly JsonObject EventsHour = new() { ["minSeverity"] = "error", ["windowMinutes"] = 60, ["limit"] = 200 };

    [Fact]
    public async Task B03_TheOfferedWireNameIsStable_TheRejectedNameNeverReturns_AndTheWiderWindowRunsOnce()
    {
        var run = await B03Async(correctsWhenTold: true);

        // Name coherence: each canonical tool is published under exactly one wire name for the whole task.
        Assert.Equal(["system_crashes"], run.Upstream.OfferedNames("system.crashes"));
        Assert.Equal(["system_events"], run.Upstream.OfferedNames("system.events"));
        Assert.DoesNotContain(run.Upstream.Bodies, body => body.Contains("_2E", StringComparison.Ordinal));

        // The rejected raw name stays rejected in every later request: it is the non-executable placeholder, never a canonical call.
        var rejected = Assert.Single(run.State.Steps, step => step.ToolCall?.ToolNameError is not null);
        Assert.Equal("system_crashes", rejected.ToolCall!.ToolName);
        var later = run.Upstream.Requests.Where(request => request.Contains(rejected.ToolCall.Id, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(later);
        Assert.All(later, request => Assert.Equal("_bops_invalid_tool", AssistantCallName(request, rejected.ToolCall.Id)));

        // No unjustified repetition: every check ran once; the deliberately wider crash window is a new check and ran once.
        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.Equal(2, run.Tools["system.crashes"].Successes);
        Assert.All(SingleRunTools, tool => Assert.Equal(1, run.Tools[tool].Successes));
        var executed = run.State.Steps.Where(step => step.Result is { Outcome: ToolOutcome.Success } && step.ToolCall is not null)
            .Select(step => $"{step.ToolCall!.ToolName} {step.ToolCall.Arguments.ToJson().ToJsonString()}").ToList();
        Assert.Equal(executed.Count, executed.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(executed, call => call.Contains("10080", StringComparison.Ordinal));

        // The repeated plan was refused with the exact problem through the governed, audited corrective re-ask; one replan accepted.
        Assert.Equal(2, run.State.Plans.Count);
        var replanCalls = run.State.Plans[1].ModelCalls!;
        Assert.Equal([ModelCallOutcome.Failure, ModelCallOutcome.Success], replanCalls.Select(call => call.Outcome));
        Assert.Contains("repeats completed task step 0", replanCalls[0].ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, run.Audit.Events.OfType<ToolCallAuditEvent>().Count(e => e.Authorization == AuthorizationKind.UnknownTool));
    }

    [Fact]
    public async Task B03_AModelThatKeepsRepeatingTheCompletedPlan_FailsExplicitly_WithoutRerunningAnyCheck()
    {
        var run = await B03Async(correctsWhenTold: false);

        Assert.Equal(AgentTaskStatus.Failed, run.State.Status);
        Assert.Equal(TaskTerminalKind.ModelFailure, run.State.TerminalReason!.Kind);
        Assert.Equal(ModelFailureKind.MalformedResponse, run.State.TerminalReason.FailureKind);
        Assert.Single(run.State.Plans);
        Assert.All(run.Tools.Values, tool => Assert.Equal(1, tool.Successes));
        Assert.Equal(0, run.State.Accounting!.LifetimeReplans);
        var failedReplan = run.State.Steps[^1].ModelCalls!;
        Assert.Equal(2, failedReplan.Count);
        Assert.All(failedReplan, call => Assert.Contains("repeats completed task step 0", call.ErrorMessage, StringComparison.Ordinal));
    }

    private static string? AssistantCallName(string requestBody, string callId)
    {
        using var document = JsonDocument.Parse(requestBody);
        foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (message.TryGetProperty("tool_calls", out var calls))
            {
                foreach (var call in calls.EnumerateArray())
                {
                    if (call.GetProperty("id").GetString() == callId)
                    {
                        return call.GetProperty("function").GetProperty("name").GetString();
                    }
                }
            }
        }

        return null;
    }

    private sealed record B03Run(TaskState State, B03Upstream Upstream, RecordingAuditSink Audit, IReadOnlyDictionary<string, ParameterizedFixtureTool> Tools);

    private static async Task<B03Run> B03Async(bool correctsWhenTold)
    {
        static ToolCallResult Complete(string output) => ToolCallResult.Success(output) with { Completeness = ToolResultCompleteness.Complete };
        var tools = new Dictionary<string, ParameterizedFixtureTool>(StringComparer.Ordinal)
        {
            ["system.crashes"] = new("system.crashes", Complete("{\"observedItems\":0,\"items\":[]}"),
            [
                new ToolParameter("mode", ToolParameterType.String, "Output mode.", Required: false) { AllowedValues = ["raw", "aggregate"] },
                new ToolParameter("sinceMinutes", ToolParameterType.Integer, "Window in minutes.", Required: false),
                new ToolParameter("limit", ToolParameterType.Integer, "Maximum items.", Required: false),
            ]),
            ["system.events"] = new("system.events", Complete("{\"observedEvents\":0,\"events\":[]}"),
            [
                new ToolParameter("minSeverity", ToolParameterType.String, "Minimum severity.", Required: false)
                {
                    AllowedValues = ["critical", "error", "warning", "information", "verbose"],
                },
                new ToolParameter("windowMinutes", ToolParameterType.Integer, "Window in minutes.", Required: false),
                new ToolParameter("limit", ToolParameterType.Integer, "Maximum events.", Required: false),
            ]),
            ["system.memory"] = new("system.memory", Complete("Memory: 21254 MB used of 32475 MB total."), []),
            ["system.disk"] = new("system.disk", Complete("C:: 392763 MB used of 975723 MB total."), []),
            ["system.cpu"] = new("system.cpu", Complete("CPU usage: 0.0%"), []),
        };
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools.Values)
        {
            registry.Register(new PackageId("regression.package"), tool);
        }

#pragma warning disable CA2000 // The HttpClient owns and disposes the handler; the handler is returned so tests can inspect what was sent.
        var upstream = new B03Upstream(correctsWhenTold);
        using var http = new HttpClient(upstream);
#pragma warning restore CA2000
        var model = new OpenAiCompatibleChatModel(
            new ChatModelOptions("OpenRouter", "https://openrouter.invalid/api/v1", null, "fixture/nemotron-shape") { ResolvedApiKey = "fixture" },
            http);
        var options = RegressionRunner.BaseOptions(null);
        var audit = new RecordingAuditSink();
        var broker = new ModelInvocationBroker(model, audit, TimeProvider.System, NullLogger.Instance, options,
            contextProjector: new MafContextProjector(options, NullLogger.Instance));
        var runner = new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(),
            TimeProvider.System, NullLogger<AgentRunner>.Instance, options, invocationBroker: broker);
        var state = await runner.RunAsync("trova la causa dei continui crash che ha questo computer", Actor);
        return new B03Run(state, upstream, audit, tools);
    }

    /// <summary>The scripted upstream: an OpenAI Chat Completions endpoint answering like the live B03 model did.</summary>
    private sealed partial class B03Upstream(bool correctsWhenTold) : HttpMessageHandler
    {
        private static readonly (string Tool, JsonObject Arguments)[] InitialPlan =
        [
            ("system.crashes", CrashesHour), ("system.events", EventsHour), ("system.memory", []), ("system.disk", []), ("system.cpu", []),
        ];

        private int _calls;
        private bool _widerWindowRan;

        public List<string> Bodies { get; } = [];

        public IEnumerable<string> Requests => Bodies;

        public IReadOnlyList<string> OfferedNames(string canonical) =>
        [
            .. Bodies.SelectMany(body => OfferedWireNames(body))
                .Where(name => string.Equals(Canonical(name), canonical, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            _calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Answer(body).ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }

        private JsonObject Answer(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var messages = root.GetProperty("messages").EnumerateArray().ToList();
            var system = messages[0].GetProperty("content").GetString() ?? string.Empty;
            var lastUser = messages.LastOrDefault(message => message.GetProperty("role").GetString() == "user")
                .TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : string.Empty;

            if (system.Contains("lay out your plan", StringComparison.Ordinal))
            {
                return Text(Plan(InitialPlan));
            }

            if (system.Contains("Revise it.", StringComparison.Ordinal))
            {
                return correctsWhenTold && lastUser.Contains("repeats completed task step", StringComparison.Ordinal)
                    ? Text(Plan([("system.crashes", CrashesWeek)]))
                    : Text(Plan(InitialPlan));
            }

            var operational = OfferedWireNames(body).Where(name => name != "runtime_evidence_read").ToList();
            if (operational.Count == 1)
            {
                var offered = operational[0];
                var canonical = Canonical(offered)!;
                var arguments = PlannedArguments(system, canonical);
                if (canonical == "system.crashes" && arguments["sinceMinutes"]?.GetValue<int>() == 10_080)
                {
                    _widerWindowRan = true;
                }

                // Live step 7: having seen an escaped name in its own history, the model escaped the next name the same way.
                var name = body.Contains("\"name\":\"system_2E", StringComparison.Ordinal) && !offered.Contains("_2E", StringComparison.Ordinal)
                    ? canonical.Replace(".", "_2E", StringComparison.Ordinal)
                    : offered;
                return Call(name, arguments);
            }

            // Plan complete: the live model wanted a wider crash window and called a tool it was not offered.
            return _widerWindowRan
                ? Text("Findings: no crash or error record was observed in the hour or the week examined; the cause is not determined.")
                : Call("system_crashes", CrashesWeek);
        }

        private static JsonObject PlannedArguments(string system, string canonical)
        {
            var match = PlanLine().Matches(system).LastOrDefault(line => line.Groups["tool"].Value == canonical);
            return match is null ? [] : JsonNode.Parse(match.Groups["args"].Value)!.AsObject();
        }

        private static IEnumerable<string> OfferedWireNames(string body)
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("tools", out var tools))
            {
                return [];
            }

            return [.. tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!)];
        }

        private static string? Canonical(string wire) => wire switch
        {
            _ when wire.StartsWith("system_2E", StringComparison.Ordinal) => "system." + wire["system_2E".Length..],
            _ when wire.StartsWith("system_", StringComparison.Ordinal) => "system." + wire["system_".Length..],
            _ => null,
        };

        private static string Plan(IEnumerable<(string Tool, JsonObject Arguments)> steps) => new JsonObject
        {
            ["rationale"] = "Collect crash and system evidence.",
            ["steps"] = new JsonArray([.. steps.Select(step => (JsonNode)new JsonObject
            {
                ["description"] = $"Check {step.Tool}",
                ["expectedTool"] = step.Tool,
                ["expectedArguments"] = step.Arguments.DeepClone(),
            })]),
        }.ToJsonString();

        private JsonObject Text(string text) => Reply(new JsonObject { ["role"] = "assistant", ["content"] = text }, "stop");

        private JsonObject Call(string name, JsonObject arguments) => Reply(new JsonObject
        {
            ["role"] = "assistant",
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = $"call-{_calls}",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.ToJsonString() },
            }),
        }, "tool_calls");

        private JsonObject Reply(JsonObject message, string finish) => new()
        {
            ["id"] = $"gen-{_calls}",
            ["model"] = "fixture/nemotron-shape",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finish }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 10 },
        };

        [GeneratedRegex(@"\[(?<tool>[a-z_.]+)\] expectedArguments=(?<args>\{[^\n]*\})")]
        private static partial Regex PlanLine();
    }
}
