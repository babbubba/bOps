// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;
using bOps.Packages.Providers.OpenAiCompatible.Tests;
using Microsoft.Extensions.Logging.Abstractions;

// Each handler/HttpClient pair lives exactly as long as its test method; nothing holds an OS handle worth disposing.
#pragma warning disable CA2000

namespace bOps.Runtime.Tests;

/// <summary>
/// The whole path of ADR-0038 in one place: the real <see cref="AgentRunner"/>, the real OpenAI-compatible adapter and
/// the strict fake provider that answers the incident's HTTP 400 for any request breaking the wire contract. The runtime
/// project names no provider; only this test does.
/// </summary>
public sealed class ProviderWireEndToEndTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private static readonly string[] BaseNames =
    [
        "fs.size", "system.crashes", "system.events", "system.cpu", "storage.health", "system.memory", "system.disk",
        "process.list", "docker.inspect", "fs.delete_tree", "linux.dpkg-status", "network.dns_query",
    ];

    private static bool IsPlanningRequest(string body) =>
        body.Contains("lay out your plan", StringComparison.Ordinal) || body.Contains("Revise it", StringComparison.Ordinal);

    private static OpenAiCompatibleChatModel Model(StrictOpenAiProvider provider) =>
        new(
            new ChatModelOptions("OpenRouter", "https://openrouter.ai/api/v1", new SecretReference("environment", "TEST_KEY"), "openrouter/free", true)
            {
                ResolvedApiKey = "sk-test",
            },
            new HttpClient(provider));

    private static ToolRegistry Registry(IEnumerable<ITool> tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return registry;
    }

    private static AgentRunner Runner(IChatModel model, IToolRegistry registry, IAuditSink audit, IPolicyEngine? policy = null, ITaskStore? store = null) =>
        new(model, registry, policy ?? new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit,
            store ?? new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());

    private static string Text(string content) =>
        new JsonObject
        {
            ["model"] = "vendor/picked",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                ["finish_reason"] = "stop",
            }),
        }.ToJsonString();

    private static string Plan(string? expectedTool = null, int steps = 1) =>
        Text(new JsonObject
        {
            ["rationale"] = "r",
            ["steps"] = new JsonArray(Enumerable.Range(0, steps).Select(i =>
                (JsonNode)new JsonObject { ["description"] = $"step {i}", ["expectedTool"] = expectedTool }).ToArray()),
        }.ToJsonString());

    private static string ToolCalls(params (string Id, string Name, string Arguments)[] calls) =>
        new JsonObject
        {
            ["model"] = "vendor/picked",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments },
                    }).ToArray()),
                },
                ["finish_reason"] = "tool_calls",
            }),
        }.ToJsonString();

    private static JsonElement[] Messages(string requestJson) =>
        [.. JsonDocument.Parse(requestJson).RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone())];

    private sealed class RecordingPolicy : IPolicyEngine
    {
        public List<string> Seen { get; } = [];

        public PolicyDecision Evaluate(PolicyContext context)
        {
            Seen.Add(context.Manifest.Name);
            return new PolicyDecision(PolicyMode.Automatic, "test: automatic");
        }
    }

    [Fact]
    public async Task TheIncidentTask_HundredToolsIncludingFsSize_ReplansAndFinishesAgainstAStrictProvider()
    {
        var names = BaseNames.Concat(Enumerable.Range(0, 88).Select(i => $"vendor.tool_{i}.probe")).ToArray();
        Assert.Equal(100, names.Length);
        var provider = new StrictOpenAiProvider(
            Plan("fs.size"),                                   // 0 plan: one planned step
            ToolCalls(("call_1", "fs_size", "{}")),            // 1 step 0
            ToolCalls(("call_2", "system_crashes", "{}")),     // 2 step 1: the plan is exhausted, so the run replans
            Plan("system.cpu"),                                // 3 the replan
            Text("Finished."))                                 // 4 step 2
        {
            ForbidNativeToolsWhen = IsPlanningRequest,
        };
        var audit = new RecordingAuditSink();
        var policy = new RecordingPolicy();

        var result = await Runner(Model(provider), Registry(names.Select(name => (ITool)new FakeReadTool(name))), audit, policy)
            .RunAsync("analizza i blocchi di sistema", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, provider.Rejections);
        Assert.Equal(2, result.Plans.Count);

        // The plan and the replan carried no native tool and no tool_choice at all.
        var planning = provider.RequestBodies.Where(IsPlanningRequest).ToList();
        Assert.Equal(2, planning.Count);
        Assert.All(planning, body =>
        {
            Assert.DoesNotContain("\"tools\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"tool_choice\"", body, StringComparison.Ordinal);
            Assert.Contains("- fs.size [Read]:", body, StringComparison.Ordinal);
        });

        // A step request offers the tool only under its wire alias, and the history repeats it the same way.
        var step = provider.RequestBodies.Single(body => !IsPlanningRequest(body) && body.Contains("call_1", StringComparison.Ordinal) && !body.Contains("call_2", StringComparison.Ordinal));
        using (var document = JsonDocument.Parse(step))
        {
            var offered = document.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
            Assert.Equal(100, offered.Count);
            Assert.Contains("fs_size", offered);
            Assert.DoesNotContain("fs.size", offered);
        }

        // Policy and audit only ever saw the canonical names.
        Assert.Equal(["fs.size", "system.crashes"], policy.Seen);
        Assert.Equal(["fs.size", "system.crashes"], audit.Events.OfType<ToolCallAuditEvent>().Select(e => e.Tool));
        Assert.All(audit.Events.OfType<ToolCallAuditEvent>(), e => Assert.DoesNotContain('_', e.Tool));
        Assert.Equal(["fs.size", "system.crashes"], result.Steps.Where(s => s.ToolCall is not null).Select(s => s.ToolCall!.ToolName));
    }

    [Fact]
    public async Task ATurnWithThreeCalls_IsAcceptedByAStrictProvider_LiveAndAfterAResume()
    {
        var tools = new ITool[] { new FakeReadTool("fs.size", "42"), new FakeReadTool("system.cpu"), new FakeReadTool("process.list") };
        var live = new StrictOpenAiProvider(
            Plan(),
            ToolCalls(("call_a", "fs_size", "{}"), ("call_b", "system_cpu", "{}"), ("call_c", "process_list", "{}")),
            Text("Finished."));

        var finished = await Runner(Model(live), Registry(tools), new RecordingAuditSink()).RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Completed, finished.Status);
        Assert.Equal(0, live.Rejections);

        // Resume from the stored state alone (no provider payloads), against a fresh strict provider.
        var stored = finished with { Status = AgentTaskStatus.Running, Steps = [finished.Steps[0] with { ModelCalls = null }] };
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(stored))!;
        var resumed = new StrictOpenAiProvider(Text("Finished again."));

        await Runner(Model(resumed), Registry(tools), new RecordingAuditSink()).ResumeAsync(reloaded, Actor);

        Assert.Equal(0, resumed.Rejections);
        var liveMessages = Messages(live.RequestBodies[2]);
        var resumedMessages = Messages(resumed.RequestBodies[0]);
        Assert.Equal(liveMessages.Skip(1).Select(m => m.GetRawText()), resumedMessages.Skip(1).Select(m => m.GetRawText()));
        Assert.Equal(3, liveMessages[2].GetProperty("tool_calls").GetArrayLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[1,2]")]
    public async Task MalformedArguments_AreAuditedAsAFailedStep_NeverExecuted_AndTheModelSeesWhy(string payload)
    {
        var tool = new RecordingReadTool("system.cpu", []);
        var provider = new StrictOpenAiProvider(Plan(), ToolCalls(("call_1", "system_cpu", payload)), Text("Finished."));
        var audit = new RecordingAuditSink();
        var policy = new RecordingPolicy();

        var result = await Runner(Model(provider), Registry([tool]), audit, policy).RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(policy.Seen);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "system.cpu", Outcome: ToolOutcome.Failure });
        Assert.Equal(0, provider.Rejections);
        var toolMessage = Messages(provider.RequestBodies[2]).Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains("Invalid arguments for 'system.cpu'", toolMessage.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameThatIsNotAnAliasOfTheRequest_NeverExecutesARealTool()
    {
        var tool = new RecordingReadTool("fs.size", []);
        var provider = new StrictOpenAiProvider(
            Plan(),
            ToolCalls(("call_1", "fs.size", "{}")),  // the canonical name, not the wire alias of this request
            Plan(),
            Text("Finished."));
        var audit = new RecordingAuditSink();

        var result = await Runner(Model(provider), Registry([tool]), audit).RunAsync("goal", Actor);

        Assert.Equal(0, tool.ExecutionCount);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.UnknownTool });
        Assert.Contains("was offered", result.Steps[0].Observation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The core HARDEN-1 review M-1 regression: a model returning an invalid tool name (empty, or over the wire
    /// length bound) is rejected safely, and — because that raw name is excluded from alias construction — neither
    /// the very next request in the same run nor a request built after a full persist/<see cref="AgentRunner.ResumeAsync"/>
    /// round trip is ever blocked by it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("way-too-long-a-tool-name-that-exceeds-the-sixty-four-character-provider-limit-by-far")]
    public async Task M1_ARejectedRawToolName_NeverBlocksTheNextRequest_LiveOrAfterResume(string invalidRawName)
    {
        var tool = new RecordingReadTool("fs.size", []);
        var live = new StrictOpenAiProvider(
            Plan("fs.size"),                                  // 0 plan: one planned step
            ToolCalls(("call_1", invalidRawName, "{}")),       // 1 TURN N: the model returns an invalid tool name
            Plan("fs.size"),                                   // 2 the replan an UnknownTool deviation triggers
            ToolCalls(("call_2", "fs_size", "{}")),             // 3 TURN N+1: a legitimate offered tool — must not be blocked
            Text("Finished."));                                 // 4 final
        var audit = new RecordingAuditSink();

        var result = await Runner(Model(live), Registry([tool]), audit).RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, live.Rejections);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.UnknownTool });
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "fs.size", Outcome: ToolOutcome.Success });

        // Persist and resume: the rejected call is still in history, and still must not block the next request.
        var stored = result with { Status = AgentTaskStatus.Running, Steps = [.. result.Steps.Select(s => s with { ModelCalls = null })] };
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(stored))!;
        var resumed = new StrictOpenAiProvider(Text("Finished again."));

        var resumedResult = await Runner(Model(resumed), Registry([tool]), new RecordingAuditSink()).ResumeAsync(reloaded, Actor);

        Assert.Equal(0, resumed.Rejections);
        Assert.Equal(AgentTaskStatus.Completed, resumedResult.Status);
    }

    [Fact]
    public async Task M2_05_M2_06_M2_07_DuplicateJsonKeysInArguments_NeverExecute_AndNeverLeaveTheTaskRunning()
    {
        var tool = new RecordingReadTool("system.cpu", []);
        var provider = new StrictOpenAiProvider(Plan(), ToolCalls(("call_1", "system_cpu", """{"a":1,"a":2}""")), Text("Finished."));
        var audit = new RecordingAuditSink();

        var result = await Runner(Model(provider), Registry([tool]), audit).RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.NotEqual(AgentTaskStatus.Running, result.Status);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal(0, provider.Rejections);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "system.cpu", Outcome: ToolOutcome.Failure });
    }
}
