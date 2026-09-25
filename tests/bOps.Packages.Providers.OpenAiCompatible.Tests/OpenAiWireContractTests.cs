// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

// Each handler/HttpClient pair lives exactly as long as its test method; nothing holds an OS handle worth disposing.
#pragma warning disable CA2000

namespace bOps.Packages.Providers.OpenAiCompatible.Tests;

/// <summary>
/// ADR-0038 against a strict upstream: what a request may contain, how a reply's tool calls are mapped back, and what
/// happens to arguments that are not a JSON object. Every request goes through <see cref="StrictOpenAiProvider"/>, which
/// answers the 400 the real incident produced whenever the wire contract is broken.
/// </summary>
public sealed class OpenAiWireContractTests
{
    private static readonly string[] IncidentNames =
    [
        "fs.size", "system.crashes", "system.events", "system.cpu", "storage.health", "system.memory", "system.disk",
        "process.list", "docker.inspect", "fs.delete_tree", "linux.dpkg-status", "network.dns_query",
    ];

    private static ChatModelOptions Options(bool native = true) =>
        new("OpenRouter", "https://openrouter.ai/api/v1", new SecretReference("environment", "TEST_KEY"), "openrouter/free", native)
        {
            ResolvedApiKey = "sk-test",
        };

    private static OpenAiCompatibleChatModel Model(StrictOpenAiProvider provider, bool native = true) =>
        new(Options(native), new HttpClient(provider));

    private static ToolManifest Tool(string name, params ToolParameter[] parameters) => new()
    {
        Name = name,
        Description = $"{name}.",
        Risk = RiskLevel.Read,
        Platforms = ["linux", "windows"],
        Requires = [],
        Parameters = parameters,
    };

    private static ToolManifest[] Tools(params string[] names) => [.. names.Select(name => Tool(name))];

    /// <summary>A reply carrying tool calls; a <c>null</c> arguments value leaves the member out entirely.</summary>
    private static string ReplyWithCalls(params (string Id, string Name, string? Arguments)[] calls)
    {
        var toolCalls = new JsonArray();
        foreach (var (id, name, arguments) in calls)
        {
            var function = new JsonObject { ["name"] = name };
            if (arguments is not null)
            {
                function["arguments"] = arguments;
            }

            toolCalls.Add(new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = function });
        }

        return new JsonObject
        {
            ["model"] = "vendor/picked",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = toolCalls },
                ["finish_reason"] = "tool_calls",
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1 },
        }.ToJsonString();
    }

    private const string TextReply =
        """{"model":"vendor/picked","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    private static ModelRequest StepRequest(IReadOnlyList<ChatTurn> history, params ToolManifest[] tools) =>
        new("system", history, tools);

    private static ModelToolCall Call(string id, string name, string? json = null) =>
        new(id, name, json is null ? ToolArguments.Empty : ToolArguments.FromJson((JsonObject)JsonNode.Parse(json)!));

    private static ChatTurn Result(string id, string text = "result") => ChatTurn.FromToolResult(id, text);

    private const string NotExecuted = "Not executed: only one tool call is executed per step. Ask again next step if still needed.";

    [Fact]
    public void TheStrictProvider_RejectsTheIncidentRequest_ThatSentADottedName()
    {
        const string incident =
            """{"model":"openrouter/free","messages":[{"role":"user","content":"x"}],"tools":[{"type":"function","function":{"name":"fs.size","description":"d","parameters":{"type":"object"}}}],"tool_choice":"auto"}""";

        Assert.Equal(
            "Function at index 0 has an invalid name: \"fs.size\". Only a-z, A-Z, 0-9, underscores, and dashes are allowed.",
            StrictOpenAiProvider.Validate(incident));
    }

    [Fact]
    public async Task H1_06_TheIncidentRequestShape_HundredToolsIncludingFsSize_IsAcceptedByAStrictProvider()
    {
        var names = IncidentNames.Concat(Enumerable.Range(0, 88).Select(i => $"vendor.tool_{i}.probe")).ToArray();
        Assert.Equal(100, names.Length);
        var provider = new StrictOpenAiProvider(TextReply);

        await Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools(names)));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        var sent = body.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
        Assert.Equal(100, sent.Count);
        Assert.Contains("fs_size", sent);
        Assert.DoesNotContain("fs.size", sent);
        Assert.Contains("fs_delete_tree", sent);
    }

    [Fact]
    public async Task H1_02_H1_05_AnAliasInAReply_ReachesTheCallerAsTheCanonicalName()
    {
        var provider = new StrictOpenAiProvider(ReplyWithCalls(("call_1", "fs_size", """{"path":"C:\\"}""")));

        var response = await Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools(IncidentNames)));

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("fs.size", call.ToolName);
        Assert.Null(call.ToolNameError);
        Assert.Null(call.ArgumentsError);
        Assert.Equal("C:\\", call.Arguments.GetRequired<string>("path"));
    }

    [Theory]
    [InlineData("fs.size")]
    [InlineData("fs_sizes")]
    [InlineData("FS_SIZE")]
    [InlineData("fs_size ")]
    [InlineData("system.cpu")]
    [InlineData("does_not_exist")]
    public async Task H1_04_H1_A1_ANameThatIsNotAnAliasOfThisRequest_IsNeverPassedOnAsAToolName(string returned)
    {
        // Only fs.size is offered, so "fs_size" is the one alias of this request; "system.cpu" is a real tool that is not offered.
        var provider = new StrictOpenAiProvider(ReplyWithCalls(("call_1", returned, "{}")));

        var response = await Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools("fs.size")));

        var call = Assert.Single(response.ToolCalls);
        Assert.NotNull(call.ToolNameError);
        Assert.Contains("was offered", call.ToolNameError, StringComparison.Ordinal);
        Assert.Equal(returned, call.ToolName);
        Assert.True(call.Arguments.ToJson().Count == 0);
    }

    [Fact]
    public async Task H1_A1_AToolThatIsOnlyInTheHistory_IsNotResolvableFromAReply()
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Call("call_0", "system.cpu")]),
            Result("call_0"),
        };
        var provider = new StrictOpenAiProvider(ReplyWithCalls(("call_1", "system_cpu", "{}")));

        var response = await Model(provider).CompleteAsync(StepRequest(history, Tools("fs.size")));

        Assert.NotNull(Assert.Single(response.ToolCalls).ToolNameError);
        Assert.Equal(0, provider.Rejections);
    }

    [Fact]
    public async Task H1_A2_AnAlreadyWireSafeCanonicalName_WorksThroughItsIdentityMapping()
    {
        var provider = new StrictOpenAiProvider(ReplyWithCalls(("call_1", "docker_inspect", "{}")));

        var response = await Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools("docker_inspect", "fs.size")));

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("docker_inspect", call.ToolName);
        Assert.Null(call.ToolNameError);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        Assert.Contains(
            body.RootElement.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("function").GetProperty("name").GetString() == "docker_inspect");
    }

    [Fact]
    public async Task H1_A3_TwoToolsThatDifferOnlyByDotAndUnderscore_ReachTheirOwnCanonicalNames()
    {
        var provider = new StrictOpenAiProvider(
            ReplyWithCalls(("c1", "a_2Eb", "{}")), ReplyWithCalls(("c2", "a_b", "{}")));
        var model = Model(provider);
        var request = StepRequest([ChatTurn.FromUser("goal")], Tools("a.b", "a_b"));

        var dotted = await model.CompleteAsync(request);
        var underscored = await model.CompleteAsync(request);

        Assert.Equal("a.b", Assert.Single(dotted.ToolCalls).ToolName);
        Assert.Equal("a_b", Assert.Single(underscored.ToolCalls).ToolName);
        Assert.Equal(0, provider.Rejections);
    }

    [Fact]
    public async Task H1_A4_TheSameRequestIsBuiltIdentically_EveryTime()
    {
        var provider = new StrictOpenAiProvider(TextReply, TextReply);
        var model = Model(provider);
        var request = StepRequest([ChatTurn.FromUser("goal")], Tools("a.b", "a_b", "fs.size", "system.crashes"));

        await model.CompleteAsync(request);
        await model.CompleteAsync(request);

        Assert.Equal(provider.RequestBodies[0], provider.RequestBodies[1]);
    }

    [Fact]
    public async Task AnUnmappableNameSet_FailsBeforeAnythingIsSent()
    {
        var provider = new StrictOpenAiProvider(TextReply);

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(
            () => Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools("a.b", "a_b", "a_2Eb"))));

        Assert.Contains("a_2Eb", failure.Message, StringComparison.Ordinal);
        Assert.Empty(provider.RequestBodies);
    }

    [Fact]
    public async Task H1_08_TheRequest_OmitsEveryMemberThatIsSemanticallyAbsent()
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Call("call_1", "fs.size", """{"path":"/"}""")]),
            Result("call_1"),
        };
        var provider = new StrictOpenAiProvider(TextReply);

        await Model(provider).CompleteAsync(StepRequest(history, Tools("fs.size")));

        var raw = provider.RequestBodies[0];
        Assert.DoesNotContain("null", raw, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(raw);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();
        var assistant = messages[2];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.False(assistant.TryGetProperty("content", out _));
        Assert.False(messages[1].TryGetProperty("tool_calls", out _));
        Assert.False(messages[1].TryGetProperty("tool_call_id", out _));
        Assert.False(messages[3].TryGetProperty("tool_calls", out _));
        Assert.Equal(0, provider.Rejections);
    }

    [Fact]
    public async Task H1_09_ASingleToolCallInHistory_IsValid_AndItsNameIsAliased()
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Call("call_1", "system.crashes", """{"limit":5}""")]),
            Result("call_1"),
        };
        var provider = new StrictOpenAiProvider(TextReply);

        await Model(provider).CompleteAsync(StepRequest(history, Tools("system.crashes")));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        var sentCall = body.RootElement.GetProperty("messages")[2].GetProperty("tool_calls")[0];
        Assert.Equal("system_crashes", sentCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"limit":5}""", sentCall.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task H1_10_H1_11_ATurnWithThreeCalls_AnsweredTruthfully_IsValid()
    {
        var emitted = new[] { Call("call_a", "fs.size"), Call("call_b", "system.crashes"), Call("call_c", "docker.inspect") };
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls(emitted),
            Result("call_a", "real result"),
            Result("call_b", NotExecuted),
            Result("call_c", NotExecuted),
        };
        var provider = new StrictOpenAiProvider(TextReply);

        await Model(provider).CompleteAsync(StepRequest(history, Tools("fs.size", "system.crashes", "docker.inspect")));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        var assistant = body.RootElement.GetProperty("messages")[2];
        Assert.Equal(
            ["fs_size", "system_crashes", "docker_inspect"],
            assistant.GetProperty("tool_calls").EnumerateArray().Select(c => c.GetProperty("function").GetProperty("name").GetString()));
    }

    [Fact]
    public async Task TheOldShape_ToolResultsForCallsTheAssistantTurnNeverMentioned_IsRejectedByAStrictProvider()
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Call("call_a", "fs.size")]),
            Result("call_a"),
            Result("call_b", NotExecuted),
        };
        var provider = new StrictOpenAiProvider(TextReply);

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(
            () => Model(provider).CompleteAsync(StepRequest(history, Tools("fs.size", "system.crashes"))));

        Assert.Contains("400", failure.Message, StringComparison.Ordinal);
        Assert.Contains("call_b", failure.Details!.ResponseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task H1_A1_H1_02_ANameInHistoryThatIsNotOffered_IsStillAliasedOnTheWire()
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Call("call_1", "windows.pnp-registry")]),
            Result("call_1"),
        };
        var provider = new StrictOpenAiProvider(TextReply);

        await Model(provider).CompleteAsync(StepRequest(history, Tools("fs.size")));

        Assert.Equal(0, provider.Rejections);
        Assert.Contains("windows_pnp-registry", provider.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("windows.pnp-registry", provider.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task H1_14_H1_15_ARequestWithNoTools_CarriesNoToolsAndNoToolChoice()
    {
        var provider = new StrictOpenAiProvider(TextReply) { ForbidNativeTools = true };

        await Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("plan this")]));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.False(body.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task H1_ARG1_AnEmptyObject_IsAValidEmptyArgumentSet()
    {
        var provider = new StrictOpenAiProvider(ReplyWithCalls(("call_1", "system_cpu", "{}")));

        var response = await Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools("system.cpu")));

        var call = Assert.Single(response.ToolCalls);
        Assert.Null(call.ArgumentsError);
        Assert.Equal("system.cpu", call.ToolName);
        Assert.Empty(call.Arguments.ToJson());
    }

    [Fact]
    public async Task ANativeCallWithNoArgumentsPayloadAtAll_IsAnExplicitEmptyObject()
    {
        var provider = new StrictOpenAiProvider(ReplyWithCalls(("call_1", "system_cpu", null)));

        var response = await Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools("system.cpu")));

        var call = Assert.Single(response.ToolCalls);
        Assert.Null(call.ArgumentsError);
        Assert.Empty(call.Arguments.ToJson());
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("\n\t", "empty")]
    [InlineData("{", "not valid JSON")]
    [InlineData("{\"limit\":", "not valid JSON")]
    [InlineData("{} {}", "not valid JSON")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "not a JSON object")]
    [InlineData("\"{}\"", "not a JSON object")]
    [InlineData("42", "not a JSON object")]
    [InlineData("null", "not a JSON object")]
    public async Task H1_ARG2_H1_ARG3_H1_ARG4_APayloadThatIsPresentButNotAJsonObject_IsReported_NeverRepairedToEmpty(string payload, string expected)
    {
        var provider = new StrictOpenAiProvider(ReplyWithCalls(("call_1", "system_cpu", payload)));

        var response = await Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools("system.cpu")));

        var call = Assert.Single(response.ToolCalls);
        Assert.NotNull(call.ArgumentsError);
        Assert.Contains(expected, call.ArgumentsError, StringComparison.Ordinal);
        Assert.Equal("system.cpu", call.ToolName);
        Assert.Empty(call.Arguments.ToJson());
        Assert.Null(call.ToolNameError);
    }

    [Fact]
    public async Task TheArgumentsError_NeverContainsAnyOfThePayload()
    {
        var provider = new StrictOpenAiProvider(ReplyWithCalls(("call_1", "system_cpu", """{"password":"hunter2","token":""")));

        var response = await Model(provider).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools("system.cpu")));

        var error = Assert.Single(response.ToolCalls).ArgumentsError!;
        Assert.DoesNotContain("hunter2", error, StringComparison.Ordinal);
        Assert.DoesNotContain("password", error, StringComparison.Ordinal);
        Assert.True(error.Length < 80);
    }

    [Fact]
    public async Task AMalformedCallEchoedBackInHistory_IsSentWithAnEmptyObject_AndStaysValid()
    {
        var malformed = new ModelToolCall("call_1", "system.cpu", ToolArguments.Empty) { ArgumentsError = "the arguments payload was empty" };
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([malformed]),
            Result("call_1", "Invalid arguments"),
        };
        var provider = new StrictOpenAiProvider(TextReply);

        await Model(provider).CompleteAsync(StepRequest(history, Tools("system.cpu")));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        Assert.Equal("{}", body.RootElement.GetProperty("messages")[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task TheFallback_NamesOnlyAToolThatWasOffered()
    {
        var provider = new StrictOpenAiProvider(
            ReplyBody("""{"tool":"fs.size","arguments":{}}"""),
            ReplyBody("""{"tool":"fs_size","arguments":{}}"""));
        var model = Model(provider, native: false);
        var request = StepRequest([ChatTurn.FromUser("goal")], Tools("fs.size"));

        var offered = Assert.Single((await model.CompleteAsync(request)).ToolCalls);
        var invented = Assert.Single((await model.CompleteAsync(request)).ToolCalls);

        Assert.Equal("fs.size", offered.ToolName);
        Assert.Null(offered.ToolNameError);
        Assert.NotNull(invented.ToolNameError);
    }

    [Theory]
    [InlineData("""{"tool":"fs.size","arguments":"{}"}""")]
    [InlineData("""{"tool":"fs.size","arguments":[]}""")]
    [InlineData("""{"tool":"fs.size","arguments":""}""")]
    public async Task TheFallback_TreatsNonObjectArgumentsAsAnUnparseableReply_AndAsksAgain(string badReply)
    {
        var provider = new StrictOpenAiProvider(ReplyBody(badReply), ReplyBody("""{"tool":"fs.size","arguments":{"path":"/"}}"""));

        var response = await Model(provider, native: false).CompleteAsync(StepRequest([ChatTurn.FromUser("goal")], Tools("fs.size")));

        Assert.Equal(2, provider.RequestBodies.Count);
        Assert.Equal("/", Assert.Single(response.ToolCalls).Arguments.GetRequired<string>("path"));
    }

    private static string ReplyBody(string content) =>
        new JsonObject
        {
            ["model"] = "vendor/picked",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                ["finish_reason"] = "stop",
            }),
        }.ToJsonString();
}
