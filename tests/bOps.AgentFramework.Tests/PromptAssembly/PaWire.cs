// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime.Tests;

namespace bOps.AgentFramework.Tests.PromptAssembly;

/// <summary>
/// PA-1: one scripted answer of the offline upstream. The upstream speaks the OpenAI Chat Completions schema, so every
/// answer travels through the real <c>OpenAiCompatibleChatModel</c> parser exactly as a llama.cpp answer would.
/// </summary>
internal sealed record PaReply(string? Content, IReadOnlyList<PaCall> Calls, string FinishReason)
{
    /// <summary>A plain assistant text answer that ended normally.</summary>
    internal static PaReply Text(string content) => new(content, [], "stop");

    /// <summary>An answer with one native tool call. The upstream writes the wire name of <paramref name="tool"/>.</summary>
    internal static PaReply Call(string id, string tool, JsonObject? arguments = null) =>
        new(null, [new PaCall(id, tool, arguments ?? [])], "tool_calls");

    /// <summary>An answer with no text and no tool call that ended normally (the RC1 shape).</summary>
    internal static PaReply Empty() => new(string.Empty, [], "stop");

    /// <summary>An answer with no usable content that stopped at the output limit (<c>finish_reason=length</c>, the RC2 shape).</summary>
    internal static PaReply OutputLimited() => new(string.Empty, [], "length");

    /// <summary>A text answer cut at the output limit.</summary>
    internal static PaReply Truncated(string partial) => new(partial, [], "length");

    /// <summary>A plan answer: the JSON object the planning prompt asks for, as assistant text.</summary>
    internal static PaReply Plan(string rationale, params string[] tools) => Text(new JsonObject
    {
        ["rationale"] = rationale,
        ["steps"] = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject
        {
            ["description"] = $"run {tool}",
            ["expectedTool"] = tool,
        })]),
    }.ToJsonString());

    /// <summary>A native read of the pinned knowledge.</summary>
    internal static PaReply Knowledge(string id, string operation, string skill, string resource) =>
        Call(id, "runtime.knowledge_read", new JsonObject { ["operation"] = operation, ["skill"] = skill, ["resource"] = resource });

    /// <summary>A native evidence read of one persisted result.</summary>
    internal static PaReply EvidenceRead(string id, int step) =>
        Call(id, "runtime.evidence_read", new JsonObject { ["step"] = step, ["source"] = "result", ["offset"] = 0, ["length"] = 4000 });
}

/// <summary>One native tool call of a <see cref="PaReply"/>; <see cref="Tool"/> is the canonical (dotted) name.</summary>
internal sealed record PaCall(string Id, string Tool, JsonObject Arguments);

/// <summary>
/// PA-1: an offline HTTP upstream that replays a fixed script of answers and keeps the body of every request exactly as the
/// adapter sent it. This is the single point where the provider-bound bytes (<c>model</c>, <c>messages</c>, <c>tools</c>,
/// <c>tool_choice</c>, <c>max_tokens</c>) are observed. A call nobody scripted fails loudly, and an unused answer is reported.
/// </summary>
internal sealed class PaUpstream(IEnumerable<PaReply> script) : HttpMessageHandler
{
    private readonly Queue<PaReply> queue = new(script);

    /// <summary>The request bodies, in the order they were sent.</summary>
    internal List<string> Bodies { get; } = [];

    /// <summary>The scripted answers nobody asked for.</summary>
    internal int Unconsumed => queue.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (queue.Count == 0)
        {
            throw new InvalidOperationException($"PA-1: unscripted model call number {Bodies.Count}.");
        }

        var reply = queue.Dequeue();
        var message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Content };
        if (reply.Calls.Count > 0)
        {
            message["tool_calls"] = new JsonArray([.. reply.Calls.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = WireName(call.Tool), ["arguments"] = call.Arguments.ToJsonString() },
            })]);
        }

        var body = new JsonObject
        {
            ["id"] = $"pa1-{Bodies.Count.ToString(CultureInfo.InvariantCulture)}",
            ["model"] = "pa1-fixture-model",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = reply.FinishReason }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 10 },
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    /// <summary>The provider-visible alias the adapter publishes for a canonical name: the dot is not a legal function-name character.</summary>
    private static string WireName(string canonical) => canonical.Replace('.', '_');
}

/// <summary>
/// PA-1: a transparent decorator that keeps the <see cref="ModelRequest"/> the adapter receives (after the governed broker and the
/// MAF context projection, before serialization), because provenance exists at that level and not on the wire.
/// </summary>
internal sealed class PaTapModel(IChatModel inner) : IChatModel
{
    public ChatModelDescriptor Descriptor => inner.Descriptor;

    internal List<ModelRequest> Requests { get; } = [];

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return inner.CompleteAsync(request, ct);
    }
}

/// <summary>PA-1: a deterministic, side-effect-free Read tool with a synthetic manifest.</summary>
internal sealed class PaTool(string name, string description, ToolCallResult result, IReadOnlyList<ToolParameter> parameters) : ITool
{
    internal int Invocations { get; private set; }

    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = description,
        Risk = RiskLevel.Read,
        Platforms = [bOps.Runtime.CurrentPlatform.Id],
        Requires = [],
        Parameters = parameters,
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        Invocations++;
        return Task.FromResult(result);
    }
}

/// <summary>Everything one PA-1 scenario run produced: the final task state, the wire bodies, and the requests the adapter saw.</summary>
internal sealed class PaRun
{
    internal required TaskState State { get; init; }

    internal required IReadOnlyList<string> Bodies { get; init; }

    internal required IReadOnlyList<ModelRequest> Seen { get; init; }

    internal required int Unconsumed { get; init; }

    internal required IReadOnlyDictionary<string, PaTool> Tools { get; init; }

    /// <summary>AE-1: every audit event the run wrote (tool calls, policy decisions, evidence and knowledge reads, lifecycle).</summary>
    internal required RecordingAuditSink Audit { get; init; }

    /// <summary>The wire body of the request with the given zero-based number in the run, parsed.</summary>
    internal JsonObject Body(int index) => PaGolden.Parse(PaGolden.Normalize(Bodies[index]));

    internal PaWireBody Wire(int index) => new(Body(index));
}

/// <summary>A read-only view over one serialized request body with the accessors the structural assertions need.</summary>
internal sealed class PaWireBody(JsonObject body)
{
    internal JsonObject Root { get; } = body;

    internal string Model => Root["model"]!.GetValue<string>();

    internal int? MaxTokens => Root["max_tokens"]?.GetValue<int>();

    internal string? ToolChoice => Root["tool_choice"]?.GetValue<string>();

    internal IReadOnlyList<JsonObject> Messages => [.. Root["messages"]!.AsArray().Select(node => node!.AsObject())];

    internal string System => Messages[0]["content"]!.GetValue<string>();

    /// <summary>The wire names of every offered function, in the order sent.</summary>
    internal IReadOnlyList<string> ToolNames =>
        Root["tools"] is JsonArray tools ? [.. tools.Select(tool => tool!["function"]!["name"]!.GetValue<string>())] : [];

    /// <summary>The offered functions that are not runtime control functions: the operational tools.</summary>
    internal IReadOnlyList<string> OperationalToolNames => [.. ToolNames.Where(name => !name.StartsWith("runtime_", StringComparison.Ordinal))];

    /// <summary>The offered runtime control functions.</summary>
    internal IReadOnlyList<string> ControlToolNames => [.. ToolNames.Where(name => name.StartsWith("runtime_", StringComparison.Ordinal))];

    internal JsonObject Function(string wireName) => Root["tools"]!.AsArray()
        .Select(tool => tool!["function"]!.AsObject()).Single(function => function["name"]!.GetValue<string>() == wireName);

    internal string Role(Index message) => Messages[message]["role"]!.GetValue<string>();

    internal string? Content(Index message) => Messages[message]["content"]?.GetValue<string>();

    /// <summary>The roles of the messages, in order.</summary>
    internal IReadOnlyList<string> Roles => [.. Messages.Select(message => message["role"]!.GetValue<string>())];

    /// <summary>The text of every message that carries any, in order.</summary>
    internal IEnumerable<string> Texts => Messages.Select(message => message["content"]?.GetValue<string>()).OfType<string>();
}
