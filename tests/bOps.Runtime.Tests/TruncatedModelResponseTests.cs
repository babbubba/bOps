// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;
using bOps.Packages.Providers.OpenAiCompatible.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

public sealed class TruncatedModelResponseTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("final-test");

    [Fact]
    public async Task Length_EmptyOrPartial_IsAuditedAsMalformedAndUsesOnlyRunnerCorrection()
    {
        foreach (var native in new[] { true, false })
        foreach (var content in new string?[] { null, "{\"final\":\"apparently complete\"}" })
        {
            var plan = "{\"rationale\":\"done\",\"steps\":[]}";
            var planText = native ? plan : new JsonObject { ["final"] = plan }.ToJsonString();
            var truncated = Reply("length", content, completionTokens: 4096);
            using var provider = new StrictOpenAiProvider(Reply("stop", planText), truncated, truncated);
            using var client = new HttpClient(provider);
            var model = new OpenAiCompatibleChatModel(new ChatModelOptions("test", "https://offline.invalid", null, "qwen", native), client);
            var audit = new RecordingAuditSink();
            var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());

            var result = await Runner(model, registry, audit).RunAsync("finish", Actor);

            Assert.Equal(AgentTaskStatus.Failed, result.Status);
            Assert.Equal(TaskTerminalKind.EmptyResponse, result.TerminalReason!.Kind);
            Assert.Equal(3, provider.RequestBodies.Count); // plan + initial answer + existing single correction
            Assert.Equal(10 + 2 * (7 + 4096), result.Accounting!.TokensUsed);
            Assert.Equal(0, provider.Rejections);
            var calls = result.Steps.SelectMany(step => step.ModelCalls ?? []).ToList();
            Assert.Equal(2, calls.Count);
            Assert.All(calls, call =>
            {
                Assert.Equal(ModelCallOutcome.Failure, call.Outcome);
                Assert.Equal(ModelFailureKind.MalformedResponse, call.FailureKind);
                Assert.Equal(ModelRetryDecision.NotRetryable, call.RetryDecision);
                Assert.Equal(200, call.ProviderStatusCode);
                Assert.Equal("length", call.FinishReason);
                Assert.Equal(4096, call.Usage!.CompletionTokens);
                Assert.Equal(truncated, call.ResponseJson);
                Assert.Equal(1, call.ModelAttempt);
            });
            var failedAudits = audit.Events.OfType<ModelCallAuditEvent>().Where(evt => evt.Outcome == ModelCallOutcome.Failure).ToList();
            Assert.Equal(2, failedAudits.Count);
            Assert.All(failedAudits, evt =>
            {
                Assert.Equal(ModelFailureKind.MalformedResponse, evt.FailureKind);
                Assert.Equal(200, evt.ProviderStatusCode);
                Assert.Equal(4096, evt.Usage!.CompletionTokens);
            });
            Assert.DoesNotContain("apparently complete", provider.RequestBodies[2], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StopAndNativeToolCalls_CompleteThroughGovernedExecution()
    {
        var tool = new RecordingReadTool("test.read", []);
        var toolCalls = new JsonArray(new JsonObject
        {
            ["id"] = "call-1", ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "test_read", ["arguments"] = "{}" },
        });
        using var provider = new StrictOpenAiProvider(
            Reply("stop", "{\"rationale\":\"read\",\"steps\":[{\"description\":\"read\",\"expectedTool\":\"test.read\",\"expectedArguments\":{}}]}"),
            Reply("tool_calls", null, toolCalls), Reply("stop", "Done."));
        using var client = new HttpClient(provider);
        var model = new OpenAiCompatibleChatModel(new ChatModelOptions("test", "https://offline.invalid", null, "qwen"), client);
        var audit = new RecordingAuditSink();
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), tool);

        var result = await Runner(model, registry, audit).RunAsync("read", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(3, provider.RequestBodies.Count);
        Assert.Equal(0, provider.Rejections);
        Assert.All(audit.Events.OfType<ModelCallAuditEvent>(), evt => Assert.Equal(ModelCallOutcome.Success, evt.Outcome));
        Assert.Contains(audit.Events, evt => evt is ToolCallAuditEvent { Tool: "test.read", Outcome: ToolOutcome.Success });
        Assert.Equal("Done.", result.Steps[^1].Observation);
    }

    private static AgentRunner Runner(IChatModel model, IToolRegistry registry, IAuditSink audit) =>
        new(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit,
            new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());

    private static string Reply(string finish, string? text, JsonArray? calls = null, int completionTokens = 3) =>
        new JsonObject
        {
            ["model"] = "qwen",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["finish_reason"] = finish,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text, ["tool_calls"] = calls },
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 7, ["completion_tokens"] = completionTokens },
        }.ToJsonString();
}
