// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.AI;

namespace bOps.AgentFramework.Tests;

public sealed class GovernedChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_UsesTheBrokerAndPreservesMessagesUsageAndCancellation()
    {
        var broker = new RecordingBroker();
        var calls = new List<ModelCallRecord>();
        using var cts = new CancellationTokenSource();
        using var client = new GovernedChatClient(
            broker,
            () => new ModelInvocationContext(
                Guid.Parse("31f0216f-37da-43cb-89c1-637040cff540"),
                2,
                ActorIdentity.FromOperatingSystemUser("maf-test"),
                calls));
        var messages = new[]
        {
            new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello"),
            new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
            [
                new TextContent("checking"),
                new FunctionCallContent("call-1", "test.read", new Dictionary<string, object?> { ["path"] = "C:/tmp" }),
            ]),
            new ChatMessage(Microsoft.Extensions.AI.ChatRole.Tool,
            [
                new FunctionResultContent("call-1", "ok"),
            ]),
        };

        var response = await client.GetResponseAsync(
            messages,
            new ChatOptions { Instructions = "system" },
            cts.Token);

        Assert.Equal(cts.Token, broker.CancellationToken);
        Assert.Equal("system", broker.Request!.SystemPrompt);
        Assert.Equal(
            [bOps.Abstractions.ChatRole.User, bOps.Abstractions.ChatRole.Assistant, bOps.Abstractions.ChatRole.Tool],
            broker.Request.History.Select(turn => turn.Role));
        Assert.Equal("call-1", broker.Request.History[1].ToolCalls![0].Id);
        Assert.Equal("call-1", broker.Request.History[2].ToolCallId);
        Assert.Equal("answer", response.Text);
        Assert.Equal(11, response.Usage!.InputTokenCount);
        Assert.Equal(5, response.Usage.OutputTokenCount);
        var function = Assert.IsType<FunctionCallContent>(Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>()));
        Assert.Equal(("out-1", "next.read"), (function.CallId, function.Name));
    }

    [Fact]
    public async Task GetResponseAsync_RejectsFrameworkToolsInsteadOfExecutingOrWideningThem()
    {
        var broker = new RecordingBroker();
        using var client = new GovernedChatClient(
            broker,
            () => new ModelInvocationContext(Guid.NewGuid(), 0, ActorIdentity.FromOperatingSystemUser("maf-test"), []));
        var tool = AIFunctionFactory.Create(() => "must not execute", "dangerous");

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => client.GetResponseAsync(
            [new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
            new ChatOptions { Tools = [tool] }));

        Assert.Contains("tools", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, broker.Invocations);
    }

    private sealed class RecordingBroker : IModelInvocationBroker
    {
        public ChatModelDescriptor Descriptor { get; } = new("governed", "model");
        public int Invocations { get; private set; }
        public ModelRequest? Request { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<ModelResponse> InvokeAsync(
            ModelRequest request,
            ModelInvocationContext context,
            CancellationToken ct = default)
        {
            Invocations++;
            Request = request;
            CancellationToken = ct;
            return Task.FromResult(new ModelResponse(
                "answer",
                [new ModelToolCall("out-1", "next.read", ToolArguments.Empty)],
                false,
                new ModelUsage(11, 5, null)));
        }
    }
}
