// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;
using bOps.Packages.Providers.OpenAiCompatible.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// Generation limits (finish reason, truncation, reasoning, requested limit) from the llama.cpp reply up to the audit trail: what bOps
/// asked for, what the provider reported and how the runtime classified it. Offline only: scripted replies shaped like the
/// Qwen3.6-35B/llama.cpp ones, no live call.
/// </summary>
public sealed class ModelOutputObservationTests
{
    private const string ReasoningSentinel = "SENTINEL-REASONING-MUST-NOT-LEAK";
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("output-test");

    // ---- the classifier: a pure function of the reply and the requested limit ----

    public static TheoryData<string?, string?, int, bool, int, int?, int?, ModelOutputClass, ModelOutputLimitEvidence> Replies => new()
    {
        // finish, text, content chars, with a usable call, tool call count, completion tokens, requested, class, evidence
        { "stop", "an answer", 9, false, 0, 40, null, ModelOutputClass.Complete, ModelOutputLimitEvidence.NotTruncated },
        { "tool_calls", null, 0, true, 1, 12, 8192, ModelOutputClass.ToolCall, ModelOutputLimitEvidence.NotTruncated },
        { "length", null, 0, false, 0, 8192, 8192, ModelOutputClass.TruncatedBeforeContent, ModelOutputLimitEvidence.RequestedLimitReached },
        { "length", null, 4000, false, 0, 8192, 8192, ModelOutputClass.TruncatedContent, ModelOutputLimitEvidence.RequestedLimitReached },
        { "length", null, 0, false, 0, 3000, 8192, ModelOutputClass.TruncatedBeforeContent, ModelOutputLimitEvidence.BelowRequestedLimit },
        { "length", null, 0, false, 0, 30000, null, ModelOutputClass.TruncatedBeforeContent, ModelOutputLimitEvidence.UnrequestedLimit },
        { "length", null, 0, false, 0, null, 8192, ModelOutputClass.TruncatedBeforeContent, ModelOutputLimitEvidence.Unknown },
        { "length", null, 0, false, 1, 8192, 8192, ModelOutputClass.TruncatedToolCall, ModelOutputLimitEvidence.RequestedLimitReached },
        { "length", null, 0, true, 1, 8192, 8192, ModelOutputClass.ToolCall, ModelOutputLimitEvidence.RequestedLimitReached },
        { "stop", null, 0, false, 0, 1, null, ModelOutputClass.Empty, ModelOutputLimitEvidence.NotTruncated },
        { null, null, 0, false, 0, 1, null, ModelOutputClass.Empty, ModelOutputLimitEvidence.NotTruncated },
        { "content_filter", null, 0, false, 0, 1, null, ModelOutputClass.Other, ModelOutputLimitEvidence.NotTruncated },
    };

    [Theory]
    [MemberData(nameof(Replies))]
    public void TheClassifier_SeparatesATruncatedReplyFromAnEmptyOne_AndSaysWhichLimitTheUsagePointsTo(
        string? finish, string? text, int contentChars, bool usableCall, int callCount, int? completion, int? requested,
        ModelOutputClass expectedClass, ModelOutputLimitEvidence expectedEvidence)
    {
        List<ModelToolCall> calls = [];
        for (var i = 0; i < callCount; i++)
        {
            calls.Add(new ModelToolCall($"c{i}", "test.read", ToolArguments.Empty)
            {
                ArgumentsError = usableCall ? null : "the arguments payload was not valid JSON",
            });
        }

        var response = new ModelResponse(text, calls, false, completion is null ? null : new ModelUsage(10, completion.Value, null))
        {
            Details = new ModelCallDetails("qwen", finish, "{}", "{}") { ContentCharacters = contentChars, ReasoningCharacters = 5000 },
        };

        var observed = ModelOutputClassifier.Observe(response, requested);

        Assert.Equal(expectedClass, observed.Class);
        Assert.Equal(expectedEvidence, observed.LimitEvidence);
        Assert.Equal(string.Equals(finish, "length", StringComparison.Ordinal), observed.Truncated);
        Assert.Equal(requested, observed.RequestedMaxOutputTokens);
        Assert.Equal(completion, observed.CompletionTokens);
        Assert.Equal(5000, observed.ReasoningCharacters);
        Assert.Equal(callCount, observed.ToolCallCount);
    }

    [Fact]
    public void AFinishReason_IsBoundedAndReducedToAClosedAlphabet()
    {
        Assert.Null(ModelOutputClassifier.BoundFinishReason(null));
        Assert.Null(ModelOutputClassifier.BoundFinishReason("  "));
        Assert.Equal("length", ModelOutputClassifier.BoundFinishReason(" length "));
        Assert.Equal("a?b?c", ModelOutputClassifier.BoundFinishReason("a b\nc"));
        var bounded = ModelOutputClassifier.BoundFinishReason(new string('x', 500));
        Assert.Equal(ModelOutputClassifier.MaxFinishReasonCharacters, bounded!.Length);
    }

    // ---- the broker: the requested limit, the stamp, the record and the audit event ----

    [Fact]
    public async Task TheBroker_StampsTheConfiguredLimit_ButNeverOverridesOneTheCallerSet()
    {
        var model = new FakeChatModel(new ModelResponse("done", [], true, new ModelUsage(7, 3, null)), new ModelResponse("done", [], true, null));
        var broker = new ModelInvocationBroker(
            model, new RecordingAuditSink(), TimeProvider.System, NullLogger.Instance, new AgentRunnerOptions { MaxOutputTokens = 8192 });
        var calls = new List<ModelCallRecord>();
        var context = new ModelInvocationContext(Guid.NewGuid(), 0, Actor, calls);

        await broker.InvokeAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], []), context);
        await broker.InvokeAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], []) { MaxOutputTokens = 512 }, context);

        Assert.Equal([8192, 512], model.Requests.Select(request => request.MaxOutputTokens));
        Assert.Equal([8192, 512], calls.Select(call => call.Output!.RequestedMaxOutputTokens));
    }

    [Fact]
    public async Task TheBroker_WithNoConfiguredLimit_RequestsNone_AndSaysSoInTheRecord()
    {
        var model = new FakeChatModel(new ModelResponse("done", [], true, new ModelUsage(7, 3, null)));
        var audit = new RecordingAuditSink();
        var calls = new List<ModelCallRecord>();
        var broker = new ModelInvocationBroker(model, audit, TimeProvider.System, NullLogger.Instance, new AgentRunnerOptions());

        await broker.InvokeAsync(
            new ModelRequest("system", [ChatTurn.FromUser("hello")], []), new ModelInvocationContext(Guid.NewGuid(), 0, Actor, calls));

        Assert.Null(model.Requests[0].MaxOutputTokens);
        var output = Assert.Single(calls).Output!;
        Assert.Null(output.RequestedMaxOutputTokens);
        Assert.Equal(ModelOutputClass.Complete, output.Class);
        Assert.Equal((7, 3), (output.PromptTokens, output.CompletionTokens));
        Assert.Equal(output, Assert.Single(audit.Events.OfType<ModelCallAuditEvent>()).Output);
    }

    [Fact]
    public async Task AnAttemptThatFails_KeepsWhatWasRequested_AsNoResponse()
    {
        var model = new ScriptedChatModel((_, _) => throw new ModelProtocolException("slow") { FailureKind = ModelFailureKind.Timeout });
        var audit = new RecordingAuditSink();
        var calls = new List<ModelCallRecord>();
        var broker = new ModelInvocationBroker(
            model, audit, TimeProvider.System, NullLogger.Instance, new AgentRunnerOptions { MaxOutputTokens = 8192, ModelCallMaxAttempts = 1 });

        await Assert.ThrowsAsync<ModelProtocolException>(() => broker.InvokeAsync(
            new ModelRequest("system", [ChatTurn.FromUser("hello")], []), new ModelInvocationContext(Guid.NewGuid(), 0, Actor, calls)));

        var output = Assert.Single(calls).Output!;
        Assert.Equal((ModelOutputClass.NoResponse, 8192), (output.Class, output.RequestedMaxOutputTokens));
        Assert.Null(output.CompletionTokens);
        Assert.Equal(output, Assert.Single(audit.Events.OfType<ModelCallAuditEvent>()).Output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(AgentRunnerOptions.MaxOutputTokensCeiling + 1)]
    public void ALimitOutsideItsBounds_IsAnOptionsError(int limit)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new AgentRunnerOptions { MaxOutputTokens = limit }.Validate());
        Assert.Contains("Agent:MaxOutputTokens", error.Message, StringComparison.Ordinal);
        new AgentRunnerOptions { MaxOutputTokens = 8192 }.Validate();
        new AgentRunnerOptions().Validate();
    }

    // ---- end to end through the real adapter: the Qwen/llama.cpp shape ----

    [Fact]
    public async Task AReplyThatSpendsItsBudgetReasoning_IsClassifiedWithItsLengthAndNeverItsText()
    {
        foreach (var native in new[] { true, false })
        {
            var plan = native ? PlanText : new JsonObject { ["final"] = PlanText }.ToJsonString();
            var truncated = Reply("length", content: string.Empty, reasoning: ReasoningSentinel + new string('r', 4000), completionTokens: 8192);
            using var provider = new StrictOpenAiProvider(Reply("stop", plan), truncated, truncated);
            using var client = new HttpClient(provider);
            var model = new OpenAiCompatibleChatModel(new ChatModelOptions("test", "https://offline.invalid", null, "qwen", native), client);
            var audit = new RecordingAuditSink();

            var result = await Runner(model, audit, new AgentRunnerOptions { MaxOutputTokens = 8192 }).RunAsync("finish", Actor);

            Assert.Equal(TaskTerminalKind.EmptyResponse, result.TerminalReason!.Kind);
            Assert.Equal(3, provider.RequestBodies.Count);
            Assert.Equal(0, provider.Rejections);
            Assert.All(provider.RequestBodies, body => Assert.Equal(8192, JsonNode.Parse(body)!["max_tokens"]!.GetValue<int>()));

            var calls = result.Steps.SelectMany(step => step.ModelCalls ?? []).ToList();
            Assert.Equal(2, calls.Count);
            Assert.All(calls, call =>
            {
                var output = call.Output!;
                Assert.Equal(ModelOutputClass.TruncatedBeforeContent, output.Class);
                Assert.True(output.Truncated);
                Assert.Equal(ModelOutputLimitEvidence.RequestedLimitReached, output.LimitEvidence);
                Assert.Equal((8192, 8192, "length"), (output.RequestedMaxOutputTokens, output.CompletionTokens, output.FinishReason));
                Assert.Equal(ReasoningSentinel.Length + 4000, output.ReasoningCharacters);
                Assert.Equal(0, output.ContentCharacters);
            });

            // The audit trail and the output records carry lengths and classes, never the reasoning.
            var failed = audit.Events.OfType<ModelCallAuditEvent>().Where(evt => evt.Outcome == ModelCallOutcome.Failure).ToList();
            Assert.Equal(2, failed.Count);
            Assert.All(failed, evt => Assert.Equal(ModelOutputClass.TruncatedBeforeContent, evt.Output!.Class));
            foreach (var evt in audit.Events.OfType<ModelCallAuditEvent>())
            {
                Assert.DoesNotContain(ReasoningSentinel, JsonSerializer.Serialize(evt), StringComparison.Ordinal);
            }

            Assert.All(calls, call => Assert.DoesNotContain(ReasoningSentinel, JsonSerializer.Serialize(call.Output), StringComparison.Ordinal));
            // The reasoning is not sent back to the model on the correction either.
            Assert.DoesNotContain(ReasoningSentinel, provider.RequestBodies[2], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithoutAConfiguredLimit_NoMaxTokensIsSent_AndATruncationIsAttributedToAnUnrequestedLimit()
    {
        var truncated = Reply("length", content: null, reasoning: "thinking", completionTokens: 30000);
        using var provider = new StrictOpenAiProvider(Reply("stop", PlanText), truncated, truncated);
        using var client = new HttpClient(provider);
        var model = new OpenAiCompatibleChatModel(new ChatModelOptions("test", "https://offline.invalid", null, "qwen"), client);

        var result = await Runner(model, new RecordingAuditSink(), new AgentRunnerOptions()).RunAsync("finish", Actor);

        Assert.All(provider.RequestBodies, body => Assert.False(JsonNode.Parse(body)!.AsObject().ContainsKey("max_tokens")));
        var output = result.Steps.SelectMany(step => step.ModelCalls ?? []).First(call => call.Output!.Truncated).Output!;
        Assert.Null(output.RequestedMaxOutputTokens);
        Assert.Equal(ModelOutputLimitEvidence.UnrequestedLimit, output.LimitEvidence);
        Assert.Equal(30000, output.CompletionTokens);
    }

    [Fact]
    public async Task ReasoningTokens_AreRecordedWhenTheProviderSeparatesThem()
    {
        var reply = new JsonObject
        {
            ["model"] = "qwen",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = PlanText, ["reasoning"] = "short" },
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 7, ["completion_tokens"] = 90,
                ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 60 },
            },
        }.ToJsonString();
        using var provider = new StrictOpenAiProvider(reply);
        using var client = new HttpClient(provider);
        var model = new OpenAiCompatibleChatModel(new ChatModelOptions("test", "https://offline.invalid", null, "qwen"), client);

        var response = await model.CompleteAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], []));

        Assert.Equal((60, 5, PlanText.Length), (response.Details!.ReasoningTokens, response.Details.ReasoningCharacters, response.Details.ContentCharacters));
    }

    private const string PlanText = "{\"rationale\":\"done\",\"steps\":[]}";

    private static AgentRunner Runner(IChatModel model, IAuditSink audit, AgentRunnerOptions options) =>
        new(model, new ToolRegistry(new AlwaysAvailableCapabilityProbe()), new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit,
            new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance, options);

    private static string Reply(string finish, string? content, string? reasoning = null, int completionTokens = 3) =>
        new JsonObject
        {
            ["model"] = "qwen",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["finish_reason"] = finish,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content, ["reasoning_content"] = reasoning },
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 7, ["completion_tokens"] = completionTokens },
        }.ToJsonString();
}
