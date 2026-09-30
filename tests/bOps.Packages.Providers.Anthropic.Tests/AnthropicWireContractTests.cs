// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

// Each handler/HttpClient pair lives exactly as long as its test method; nothing holds an OS handle worth disposing.
#pragma warning disable CA2000

namespace bOps.Packages.Providers.Anthropic.Tests;

/// <summary>
/// ADR-0038 against the Anthropic tool contract: every request goes through <see cref="StrictAnthropicProvider"/>, which
/// answers <c>400 invalid_request_error</c> whenever a name, a tool_use/tool_result pairing or a member breaks the contract.
/// </summary>
public sealed class AnthropicWireContractTests
{
    private static readonly string[] IncidentNames =
    [
        "fs.size", "system.crashes", "system.events", "system.cpu", "storage.health", "system.memory", "system.disk",
        "process.list", "docker.inspect", "fs.delete_tree", "linux.dpkg-status", "network.dns_query",
    ];

    private const string TextReply = """{"model":"claude-test","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""";

    private const string NotExecuted = "Not executed: only one tool call is executed per step. Ask again next step if still needed.";

    private static AnthropicChatModel Model(StrictAnthropicProvider provider, bool native = true) =>
        new(
            new ChatModelOptions("Anthropic", "https://api.anthropic.com", new SecretReference("environment", "TEST_KEY"), "claude-test", native)
            {
                ResolvedApiKey = "test-key",
            },
            new HttpClient(provider));

    private static ToolManifest Tool(string name) => new()
    {
        Name = name,
        Description = $"{name}.",
        Risk = RiskLevel.Read,
        Platforms = ["linux", "windows"],
        Requires = [],
        Parameters = [new ToolParameter("path", ToolParameterType.Path, "A path", Required: false)],
    };

    private static ModelRequest Request(IReadOnlyList<ChatTurn> history, params string[] tools) =>
        new("system", history, [.. tools.Select(Tool)]);

    private static ModelToolCall Call(string id, string name) => new(id, name, ToolArguments.Empty);

    private static string ReplyWithToolUse(params (string Id, string Name, string? Input)[] calls)
    {
        var content = new JsonArray();
        foreach (var (id, name, input) in calls)
        {
            var block = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name };
            if (input is not null)
            {
                block["input"] = JsonNode.Parse(input);
            }

            content.Add(block);
        }

        return new JsonObject { ["model"] = "claude-test", ["content"] = content, ["stop_reason"] = "tool_use" }.ToJsonString();
    }

    private static string FallbackReply(string json) =>
        new JsonObject
        {
            ["model"] = "claude-test",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = json }),
            ["stop_reason"] = "end_turn",
        }.ToJsonString();

    /// <summary>
    /// A <c>tool_use</c> reply whose <c>input</c> is embedded as raw, unparsed text — unlike <see cref="ReplyWithToolUse"/>,
    /// which builds it through <see cref="JsonNode"/> and so cannot represent a repeated property name without
    /// throwing while the test itself constructs the reply.
    /// </summary>
    private static string ReplyWithRawToolUseInput(string id, string name, string rawInputJson) =>
        $$"""{"model":"claude-test","content":[{"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{{rawInputJson}}}],"stop_reason":"tool_use"}""";

    [Fact]
    public void TheStrictProvider_RejectsADottedToolName()
    {
        const string incident =
            """{"model":"m","max_tokens":1,"messages":[{"role":"user","content":[{"type":"text","text":"x"}]}],"tools":[{"name":"fs.size","description":"d","input_schema":{"type":"object"}}]}""";

        Assert.Contains("tools.0.name", StrictAnthropicProvider.Validate(incident), StringComparison.Ordinal);
    }

    [Fact]
    public async Task H1_07_TheIncidentToolSurface_HundredToolsIncludingFsSize_IsAcceptedByAStrictProvider()
    {
        var names = IncidentNames.Concat(Enumerable.Range(0, 88).Select(i => $"vendor.tool_{i}.probe")).ToArray();
        var provider = new StrictAnthropicProvider(TextReply);

        await Model(provider).CompleteAsync(Request([ChatTurn.FromUser("goal")], names));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        var sent = body.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(100, sent.Count);
        Assert.Contains("fs_size", sent);
        Assert.DoesNotContain("fs.size", sent);
    }

    [Fact]
    public async Task H1_02_H1_05_AnAliasInAReply_ReachesTheCallerAsTheCanonicalName()
    {
        var provider = new StrictAnthropicProvider(ReplyWithToolUse(("toolu_1", "system_crashes", """{"path":"/x"}""")));

        var response = await Model(provider).CompleteAsync(Request([ChatTurn.FromUser("goal")], IncidentNames));

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("system.crashes", call.ToolName);
        Assert.Null(call.ToolNameError);
        Assert.Equal("/x", call.Arguments.GetRequired<string>("path"));
    }

    [Theory]
    [InlineData("fs.size")]
    [InlineData("fs_sizes")]
    [InlineData("FS_SIZE")]
    [InlineData("system.cpu")]
    [InlineData("")]
    public async Task H1_04_H1_A1_ANameThatIsNotAnAliasOfThisRequest_IsNeverPassedOnAsAToolName(string returned)
    {
        var provider = new StrictAnthropicProvider(ReplyWithToolUse(("toolu_1", returned, "{}")));

        var response = await Model(provider).CompleteAsync(Request([ChatTurn.FromUser("goal")], "fs.size"));

        var call = Assert.Single(response.ToolCalls);
        Assert.NotNull(call.ToolNameError);
        Assert.Equal(returned, call.ToolName);
        Assert.Empty(call.Arguments.ToJson());
    }

    [Fact]
    public async Task H1_A2_A3_IdentityMappingsAndCollidingNames_ReachTheirOwnCanonicalNames()
    {
        var provider = new StrictAnthropicProvider(
            ReplyWithToolUse(("t1", "docker_inspect", "{}")),
            ReplyWithToolUse(("t2", "a_2Eb", "{}")),
            ReplyWithToolUse(("t3", "a_b", "{}")));
        var model = Model(provider);
        var request = Request([ChatTurn.FromUser("goal")], "docker_inspect", "a.b", "a_b");

        var identity = await model.CompleteAsync(request);
        var dotted = await model.CompleteAsync(request);
        var underscored = await model.CompleteAsync(request);

        Assert.Equal("docker_inspect", Assert.Single(identity.ToolCalls).ToolName);
        Assert.Equal("a.b", Assert.Single(dotted.ToolCalls).ToolName);
        Assert.Equal("a_b", Assert.Single(underscored.ToolCalls).ToolName);
        Assert.Equal(0, provider.Rejections);
    }

    [Fact]
    public async Task AnUnmappableNameSet_FailsBeforeAnythingIsSent()
    {
        var provider = new StrictAnthropicProvider(TextReply);

        await Assert.ThrowsAsync<ModelProtocolException>(
            () => Model(provider).CompleteAsync(Request([ChatTurn.FromUser("goal")], "a.b", "a_b", "a_2Eb")));

        Assert.Empty(provider.RequestBodies);
    }

    [Fact]
    public async Task H1_08_TheRequest_OmitsEveryMemberThatIsSemanticallyAbsent()
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Call("call_1", "fs.size")]),
            ChatTurn.FromToolResult("call_1", "42"),
        };
        var provider = new StrictAnthropicProvider(TextReply);

        await Model(provider).CompleteAsync(Request(history, "fs.size"));

        Assert.Equal(0, provider.Rejections);
        Assert.DoesNotContain("null", provider.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task H1_09_ASingleToolCallInHistory_IsValid_AndItsNameIsAliased()
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Call("call_1", "system.crashes")]),
            ChatTurn.FromToolResult("call_1", "rows"),
        };
        var provider = new StrictAnthropicProvider(TextReply);

        await Model(provider).CompleteAsync(Request(history, "system.crashes"));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        var toolUse = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.Equal("system_crashes", toolUse.GetProperty("name").GetString());
    }

    [Fact]
    public async Task H1_10_H1_11_ATurnWithThreeCalls_AnsweredTruthfully_IsValid()
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Call("call_a", "fs.size"), Call("call_b", "system.crashes"), Call("call_c", "docker.inspect")]),
            ChatTurn.FromToolResult("call_a", "real result"),
            ChatTurn.FromToolResult("call_b", NotExecuted),
            ChatTurn.FromToolResult("call_c", NotExecuted),
        };
        var provider = new StrictAnthropicProvider(TextReply);

        await Model(provider).CompleteAsync(Request(history, "fs.size", "system.crashes", "docker.inspect"));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        var results = body.RootElement.GetProperty("messages")[2].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(3, results.Count);
        Assert.All(results, block => Assert.Equal("tool_result", block.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task TheOldShape_ToolResultsForCallsTheAssistantTurnNeverMentioned_IsRejectedByAStrictProvider()
    {
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([Call("call_a", "fs.size")]),
            ChatTurn.FromToolResult("call_a", "real"),
            ChatTurn.FromToolResult("call_b", NotExecuted),
        };
        var provider = new StrictAnthropicProvider(TextReply);

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => Model(provider).CompleteAsync(Request(history, "fs.size")));

        Assert.Contains("call_b", failure.Details!.ResponseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task H1_14_H1_15_ARequestWithNoTools_CarriesNoTools()
    {
        var provider = new StrictAnthropicProvider(TextReply) { ForbidNativeTools = true };

        await Model(provider).CompleteAsync(Request([ChatTurn.FromUser("plan this")]));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task AToolUseWithNoInput_IsAnExplicitEmptyObject_AndTheRuntimeStillEnforcesRequiredParameters()
    {
        var provider = new StrictAnthropicProvider(ReplyWithToolUse(("toolu_1", "fs_size", null)));

        var response = await Model(provider).CompleteAsync(Request([ChatTurn.FromUser("goal")], "fs.size"));

        var call = Assert.Single(response.ToolCalls);
        Assert.Null(call.ArgumentsError);
        Assert.Empty(call.Arguments.ToJson());
    }

    [Fact]
    public async Task TheFallback_NamesOnlyAToolThatWasOffered_AndTreatsNonObjectArgumentsAsUnparseable()
    {
        var provider = new StrictAnthropicProvider(
            FallbackReply("""{"tool":"fs.size","arguments":{}}"""),
            FallbackReply("""{"tool":"fs_size","arguments":{}}"""),
            FallbackReply("""{"tool":"fs.size","arguments":"{}"}"""),
            FallbackReply("""{"tool":"fs.size","arguments":{"path":"/"}}"""));
        var model = Model(provider, native: false);
        var request = Request([ChatTurn.FromUser("goal")], "fs.size");

        var offered = Assert.Single((await model.CompleteAsync(request)).ToolCalls);
        var invented = Assert.Single((await model.CompleteAsync(request)).ToolCalls);
        var afterRetry = Assert.Single((await model.CompleteAsync(request)).ToolCalls);

        Assert.Null(offered.ToolNameError);
        Assert.NotNull(invented.ToolNameError);
        Assert.Equal("/", afterRetry.Arguments.GetRequired<string>("path"));
        Assert.Equal(4, provider.RequestBodies.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("way-too-long-a-tool-name-that-exceeds-the-sixty-four-character-provider-limit-by-far")]
    public async Task M1_ARejectedRawToolName_NeverPollutesTheNextRequest_EvenWhenEmptyOrOverLong(string invalidRawName)
    {
        var rejected = new ModelToolCall("call-0", invalidRawName, ToolArguments.Empty) { ToolNameError = "rejected" };
        var history = new[]
        {
            ChatTurn.FromUser("goal"),
            ChatTurn.FromAssistantToolCalls([rejected]),
            ChatTurn.FromToolResult("call-0", "Unknown tool"),
        };
        var provider = new StrictAnthropicProvider(TextReply);

        await Model(provider).CompleteAsync(Request(history, "fs.size"));

        Assert.Equal(0, provider.Rejections);
        using var body = JsonDocument.Parse(provider.RequestBodies[0]);
        var sentName = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("name").GetString();
        Assert.NotNull(sentName);
        Assert.InRange(sentName!.Length, 1, 64);
        Assert.All(sentName, c => Assert.True(char.IsLetterOrDigit(c) || c is '_' or '-'));
        Assert.NotEqual(invalidRawName, sentName);
    }

    [Fact]
    public async Task M2_03_DuplicateJsonKeysInNativeInput_AreRejectedAsMalformed_NeverAsArgumentException()
    {
        var provider = new StrictAnthropicProvider(ReplyWithRawToolUseInput("toolu_1", "fs_size", """{"a":1,"a":2}"""));

        var response = await Model(provider).CompleteAsync(Request([ChatTurn.FromUser("goal")], "fs.size"));

        var call = Assert.Single(response.ToolCalls);
        Assert.NotNull(call.ArgumentsError);
        Assert.Null(call.ToolNameError);
        Assert.Equal("fs.size", call.ToolName);
        Assert.Empty(call.Arguments.ToJson());
        Assert.Equal(0, provider.Rejections);
    }

    [Fact]
    public async Task M2_04_DuplicateJsonKeysInFallbackArguments_AreRejectedAsUnparseable_AndRetried()
    {
        var provider = new StrictAnthropicProvider(
            FallbackReply("""{"tool":"fs.size","arguments":{"a":1,"a":2}}"""),
            FallbackReply("""{"tool":"fs.size","arguments":{"path":"/"}}"""));

        var response = await Model(provider, native: false).CompleteAsync(Request([ChatTurn.FromUser("goal")], "fs.size"));

        Assert.Equal(2, provider.RequestBodies.Count);
        Assert.Equal("/", Assert.Single(response.ToolCalls).Arguments.GetRequired<string>("path"));
    }
}
