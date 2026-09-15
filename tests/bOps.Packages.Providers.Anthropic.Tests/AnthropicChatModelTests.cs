using System.Net;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Providers.Anthropic.Tests;

/// <summary>
/// Contract tests against recorded HTTP fixtures (agentic/04-testing-rules.md), not a live call —
/// this exercises the request/response translation <see cref="AnthropicChatModel"/> owns, which is
/// exactly the part sharing <c>OpenAiCompatibleChatModel</c> would have meant bending around a
/// second wire protocol instead of writing (see the class's own doc comment, ADR-0005).
/// </summary>
public sealed class AnthropicChatModelTests
{
    private static ChatModelOptions Options(bool nativeToolCalling = true) =>
        new("Anthropic", "https://api.anthropic.com", "test-key", "claude-test", nativeToolCalling);

#pragma warning disable CA2000 // The handler/HttpClient pair's lifetime is the test method's —
                               // nothing here holds a real OS handle worth an explicit Dispose,
                               // and the handler is returned so tests can inspect what was sent.
    private static (AnthropicChatModel Model, StubHttpMessageHandler Handler) CreateModel(
        (HttpStatusCode Status, string Body)[] responses, bool nativeToolCalling = true)
    {
        var handler = new StubHttpMessageHandler(responses);
        return (new AnthropicChatModel(Options(nativeToolCalling), new HttpClient(handler)), handler);
    }
#pragma warning restore CA2000

    [Fact]
    public async Task CompleteAsync_ParsesATextOnlyResponse_AsAFinalResult()
    {
        var (model, _) = CreateModel(
            [(HttpStatusCode.OK, """{"content":[{"type":"text","text":"Hi there"}],"stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":5}}""")]);

        var result = await model.CompleteAsync(new ModelRequest("You are a test model.", [ChatTurn.FromUser("hello")], []));

        Assert.True(result.IsFinal);
        Assert.Empty(result.ToolCalls);
        Assert.Equal("Hi there", result.TextResponse);
        Assert.Equal(10, result.Usage!.PromptTokens);
        Assert.Equal(5, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task CompleteAsync_SendsTheSystemPromptSeparatelyFromMessages_AndAuthenticatesWithAnApiKeyHeader()
    {
        var (model, handler) = CreateModel([(HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}]}""")]);

        await model.CompleteAsync(new ModelRequest("You are a test model.", [ChatTurn.FromUser("hello")], []));

        var body = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        Assert.Equal("You are a test model.", body.GetProperty("system").GetString());

        var messages = body.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());

        var request = handler.Requests[0];
        Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
        Assert.EndsWith("/v1/messages", request.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_ParsesAToolUseBlock_IntoAModelToolCall()
    {
        var (model, _) = CreateModel(
            [(HttpStatusCode.OK, """{"content":[{"type":"tool_use","id":"toolu_1","name":"test.read","input":{"path":"/tmp"}}],"stop_reason":"tool_use"}""")]);

        var result = await model.CompleteAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], []));

        Assert.False(result.IsFinal);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("test.read", call.ToolName);
        Assert.Equal("/tmp", call.Arguments.GetRequired<string>("path"));
    }

    [Fact]
    public async Task CompleteAsync_TranslatesToolManifestsIntoAnthropicInputSchemas()
    {
        var (model, handler) = CreateModel([(HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}]}""")]);
        var manifest = new ToolManifest
        {
            Name = "test.read",
            Description = "Reads a thing.",
            Risk = RiskLevel.Read,
            Platforms = ["linux", "windows"],
            Requires = [],
            Parameters = [new ToolParameter("path", ToolParameterType.Path, "The path to read", Required: true)],
        };

        await model.CompleteAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], [manifest]));

        var body = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        var tool = body.GetProperty("tools")[0];
        Assert.Equal("test.read", tool.GetProperty("name").GetString());
        var schema = tool.GetProperty("input_schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        Assert.Contains("path", schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task CompleteAsync_MergesConsecutiveToolTurns_IntoOneUserMessage()
    {
        // Mirrors what the runtime actually produces (rule D-007) when the model proposes more
        // than one tool call in a turn: the executed call's result, then one "not executed" turn
        // per call the runtime did not run — both ChatRole.Tool, appended back to back.
        var (model, handler) = CreateModel([(HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}]}""")]);
        var history = new List<ChatTurn>
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([new ModelToolCall("call-1", "test.read", ToolArguments.Empty)]),
            ChatTurn.FromToolResult("call-1", "result one"),
            ChatTurn.FromToolResult("call-2", "not executed"),
        };

        await model.CompleteAsync(new ModelRequest("system", history, []));

        var messages = JsonDocument.Parse(handler.RequestBodies[0]).RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());

        var mergedContent = messages[2].GetProperty("content");
        Assert.Equal(2, mergedContent.GetArrayLength());
        Assert.All(mergedContent.EnumerateArray(), block => Assert.Equal("tool_result", block.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task CompleteAsync_FallsBackToJsonInPrompt_WhenNativeToolCallingIsDisabled()
    {
        var (model, handler) = CreateModel(
            [(HttpStatusCode.OK, """{"content":[{"type":"text","text":"{\"final\": \"done\"}"}]}""")], nativeToolCalling: false);

        var result = await model.CompleteAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], []));

        Assert.True(result.IsFinal);
        Assert.Equal("done", result.TextResponse);

        // No native tool definitions should be sent in fallback mode — the tool list is embedded
        // in the system prompt as text instead (plan §3.1.1). The source-generated serializer
        // still emits the "tools" key for a null property (same as the OpenAI-compatible adapter),
        // so the meaningful assertion is that it carries no value, not that the key is absent.
        var body = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        if (body.TryGetProperty("tools", out var toolsProperty))
        {
            Assert.Equal(JsonValueKind.Null, toolsProperty.ValueKind);
        }
    }

    [Fact]
    public async Task CompleteAsync_Throws_WhenTheProviderReturnsANonSuccessStatus()
    {
        var (model, _) = CreateModel([(HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"bad key"}}""")]);

        await Assert.ThrowsAsync<ModelProtocolException>(
            () => model.CompleteAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], [])));
    }

    [Fact]
    public async Task CompleteAsync_Throws_WhenTheResponseBodyDoesNotMatchTheExpectedSchema()
    {
        var (model, _) = CreateModel([(HttpStatusCode.OK, "not json")]);

        await Assert.ThrowsAsync<ModelProtocolException>(
            () => model.CompleteAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], [])));
    }
}
