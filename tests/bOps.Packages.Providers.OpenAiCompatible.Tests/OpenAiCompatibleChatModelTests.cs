// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Providers.OpenAiCompatible.Tests;

/// <summary>
/// Contract tests against recorded HTTP fixtures (agentic/04-testing-rules.md), not a live call: what the adapter sends,
/// what it makes of the reply, and what it keeps about the call so that a strange answer can be understood afterwards.
/// </summary>
public sealed class OpenAiCompatibleChatModelTests
{
    private const string ApiKey = "sk-test-key-do-not-leak";

    private static readonly ModelRequest Request = new("You are a test model.", [ChatTurn.FromUser("perché il mio pc è lento?")], []);

    private static ChatModelOptions Options(bool nativeToolCalling = true, string model = "openrouter/free") =>
        new("OpenRouter", "https://openrouter.ai/api/v1", new SecretReference("environment", "TEST_KEY"), model, nativeToolCalling)
        {
            ResolvedApiKey = ApiKey,
        };

#pragma warning disable CA2000 // The handler/HttpClient pair's lifetime is the test method's; the handler is returned so tests can inspect what was sent.
    private static (OpenAiCompatibleChatModel Model, StubHttpMessageHandler Handler) CreateModel(
        (HttpStatusCode Status, string Body)[] responses, bool nativeToolCalling = true, string model = "openrouter/free")
    {
        var handler = new StubHttpMessageHandler(responses);
        return (new OpenAiCompatibleChatModel(Options(nativeToolCalling, model), new HttpClient(handler)), handler);
    }
#pragma warning restore CA2000

    private const string TextReply =
        """{"id":"gen-1","model":"vendor/picked-model","choices":[{"message":{"role":"assistant","content":"Hi there"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

    [Fact]
    public async Task CompleteAsync_ParsesATextReply_AsAFinalResultWithItsUsage()
    {
        var (model, _) = CreateModel([(HttpStatusCode.OK, TextReply)]);

        var result = await model.CompleteAsync(Request);

        Assert.True(result.IsFinal);
        Assert.Equal("Hi there", result.TextResponse);
        Assert.Equal(10, result.Usage!.PromptTokens);
        Assert.Equal(5, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task CompleteAsync_ReportsTheModelThatActuallyAnswered_NotJustTheOneRequested()
    {
        var (model, _) = CreateModel([(HttpStatusCode.OK, TextReply)]);

        var result = await model.CompleteAsync(Request);

        Assert.Equal("openrouter/free", model.Descriptor.ModelId);
        Assert.Equal("vendor/picked-model", result.Details!.ActualModel);
        Assert.Equal("stop", result.Details.FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_KeepsTheReplyBodyExactlyAsReceived()
    {
        var (model, _) = CreateModel([(HttpStatusCode.OK, TextReply)]);

        var result = await model.CompleteAsync(Request);

        Assert.Equal(TextReply, result.Details!.ResponseJson);
    }

    [Fact]
    public async Task CompleteAsync_KeepsTheRequestBodyExactlyAsSent_WithoutTheCredential()
    {
        var (model, handler) = CreateModel([(HttpStatusCode.OK, TextReply)]);

        var result = await model.CompleteAsync(Request);

        Assert.Equal(handler.RequestBodies[0], result.Details!.RequestJson);
        var sent = JsonDocument.Parse(result.Details.RequestJson!).RootElement;
        Assert.Equal("openrouter/free", sent.GetProperty("model").GetString());
        Assert.Equal("perché il mio pc è lento?", sent.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.DoesNotContain(ApiKey, result.Details.RequestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_StillAuthenticatesWithABearerHeader_AndSendsJson()
    {
        var (model, handler) = CreateModel([(HttpStatusCode.OK, TextReply)]);

        await model.CompleteAsync(Request);

        var request = handler.Requests[0];
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(ApiKey, request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task CompleteAsync_KeepsAnEmptyReplyAsItCame_SoTheBodyShowsWhereTheAnswerWent()
    {
        // The case that started this: 788 tokens generated, no content. The provider put the text in another field;
        // the adapter does not treat that as the answer, but the body it keeps shows it.
        const string reasoningOnly =
            """{"model":"vendor/thinking-model","choices":[{"message":{"role":"assistant","content":"","reasoning":"The PC is slow because memory is at 85%."},"finish_reason":"stop"}],"usage":{"prompt_tokens":10116,"completion_tokens":788}}""";
        var (model, _) = CreateModel([(HttpStatusCode.OK, reasoningOnly)]);

        var result = await model.CompleteAsync(Request);

        Assert.True(result.IsFinal);
        Assert.True(string.IsNullOrEmpty(result.TextResponse));
        Assert.Equal(788, result.Usage!.CompletionTokens);
        Assert.Contains("memory is at 85%", result.Details!.ResponseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_ParsesAToolCall_AndKeepsTheDetails()
    {
        const string toolReply =
            """{"model":"vendor/picked-model","choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"system.cpu","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""";
        var (model, _) = CreateModel([(HttpStatusCode.OK, toolReply)]);

        var result = await model.CompleteAsync(Request);

        Assert.False(result.IsFinal);
        Assert.Equal("system.cpu", Assert.Single(result.ToolCalls).ToolName);
        Assert.Equal("tool_calls", result.Details!.FinishReason);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task CompleteAsync_LeavesTheActualModelEmpty_WhenTheProviderDoesNotSayIt()
    {
        var (model, _) = CreateModel([(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""")]);

        var result = await model.CompleteAsync(Request);

        Assert.Null(result.Details!.ActualModel);
        Assert.Null(result.Details.FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_KeepsTheBodiesOfTheAttemptThatWorked_AfterATransientFailure()
    {
        var (model, handler) = CreateModel([(HttpStatusCode.ServiceUnavailable, "overloaded"), (HttpStatusCode.OK, TextReply)]);

        var result = await model.CompleteAsync(Request);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(TextReply, result.Details!.ResponseJson);
    }

    [Fact]
    public async Task CompleteAsync_AttachesTheBodiesToTheFailure_WhenTheProviderRefusesTheRequest()
    {
        const string refusal = """{"error":{"message":"No endpoints found that support tool use.","code":404}}""";
        var (model, _) = CreateModel([(HttpStatusCode.NotFound, refusal)]);

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));

        Assert.Contains("HTTP 404", failure.Message, StringComparison.Ordinal);
        Assert.Equal(refusal, failure.Details!.ResponseJson);
        Assert.Contains("openrouter/free", failure.Details.RequestJson, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, failure.Details.RequestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_AttachesTheBodiesToTheFailure_WhenTheReplyIsNotTheExpectedJson()
    {
        var (model, _) = CreateModel([(HttpStatusCode.OK, "<html>a gateway page</html>")]);

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));

        Assert.Contains("did not match the expected schema", failure.Message, StringComparison.Ordinal);
        Assert.Equal("<html>a gateway page</html>", failure.Details!.ResponseJson);
    }

    [Fact]
    public async Task CompleteAsync_FailsClearly_WhenTheReplyHasNoChoices()
    {
        var (model, _) = CreateModel([(HttpStatusCode.OK, """{"error":{"message":"provider returned no completion"},"choices":[]}""")]);

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));

        Assert.Contains("no choices", failure.Message, StringComparison.Ordinal);
        Assert.Contains("provider returned no completion", failure.Details!.ResponseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_InTheJsonFallback_KeepsTheDetailsOfTheReplyItAccepted()
    {
        const string first = """{"model":"vendor/one","choices":[{"message":{"role":"assistant","content":"not json"},"finish_reason":"stop"}]}""";
        const string second = """{"model":"vendor/two","choices":[{"message":{"role":"assistant","content":"{\"final\":\"done\"}"},"finish_reason":"stop"}]}""";
        var (model, handler) = CreateModel([(HttpStatusCode.OK, first), (HttpStatusCode.OK, second)], nativeToolCalling: false);

        var result = await model.CompleteAsync(Request);

        Assert.Equal("done", result.TextResponse);
        Assert.Equal("vendor/two", result.Details!.ActualModel);
        Assert.Equal(second, result.Details.ResponseJson);
        Assert.Equal(handler.RequestBodies[1], result.Details.RequestJson);
    }

    [Fact]
    public async Task CompleteAsync_InTheJsonFallback_AttachesTheLastAttemptToTheFailure()
    {
        const string bad = """{"model":"vendor/one","choices":[{"message":{"role":"assistant","content":"still not json"},"finish_reason":"stop"}]}""";
        var (model, _) = CreateModel([(HttpStatusCode.OK, bad), (HttpStatusCode.OK, bad)], nativeToolCalling: false);

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));

        Assert.Contains("did not return valid JSON", failure.Message, StringComparison.Ordinal);
        Assert.Equal(bad, failure.Details!.ResponseJson);
        Assert.Equal("vendor/one", failure.Details.ActualModel);
    }
}
