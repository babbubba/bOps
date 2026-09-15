// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>The role of one turn in a model conversation.</summary>
public enum ChatRole
{
    /// <summary>The standing instructions given to the model.</summary>
    System,

    /// <summary>The operator's goal or input.</summary>
    User,

    /// <summary>The model's own text or requested tool calls.</summary>
    Assistant,

    /// <summary>Carries the result of one tool call back to the model, identified by <see cref="ChatTurn.ToolCallId"/>.</summary>
    Tool,
}

/// <summary>One tool call requested by the model, carrying the id it must be answered with.</summary>
public sealed record ModelToolCall
{
    /// <summary>Creates a model tool call.</summary>
    /// <param name="Id">The id the model assigned this call; the matching result must carry it back.</param>
    /// <param name="ToolName">The name of the tool the model wants to call.</param>
    /// <param name="Arguments">The arguments the model supplied.</param>
    public ModelToolCall(string Id, string ToolName, ToolArguments Arguments)
    {
        this.Id = Id;
        this.ToolName = ToolName;
        this.Arguments = Arguments;
    }

    /// <summary>The id the model assigned this call; the matching result must carry it back.</summary>
    public string Id { get; init; }

    /// <summary>The name of the tool the model wants to call.</summary>
    public string ToolName { get; init; }

    /// <summary>The arguments the model supplied.</summary>
    public ToolArguments Arguments { get; init; }
}

/// <summary>
/// One turn of a conversation with a model. An <see cref="ChatRole.Assistant"/> turn requesting
/// tools carries <see cref="ToolCalls"/>; the matching <see cref="ChatRole.Tool"/> turns carry
/// <see cref="ToolCallId"/>. A flat (role, text) pair cannot represent native OpenAI-style tool
/// calling, which is why this shape exists from V0.1 rather than being added later as a
/// breaking change (agentic/06-decisions.md, D-007).
/// </summary>
public sealed record ChatTurn
{
    /// <summary>Who this turn is from.</summary>
    public required ChatRole Role { get; init; }

    /// <summary>The turn's text, when it has any.</summary>
    public string? Content { get; init; }

    /// <summary>Present on an <see cref="ChatRole.Assistant"/> turn that requests one or more tool calls.</summary>
    public IReadOnlyList<ModelToolCall>? ToolCalls { get; init; }

    /// <summary>Present on a <see cref="ChatRole.Tool"/> turn, matching the <see cref="ModelToolCall.Id"/> it answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Builds a <see cref="ChatRole.User"/> turn.</summary>
    public static ChatTurn FromUser(string content) => new() { Role = ChatRole.User, Content = content };

    /// <summary>Builds a <see cref="ChatRole.System"/> turn.</summary>
    public static ChatTurn FromSystem(string content) => new() { Role = ChatRole.System, Content = content };

    /// <summary>Builds an <see cref="ChatRole.Assistant"/> turn carrying only text, with no tool calls.</summary>
    public static ChatTurn FromAssistantText(string content) => new() { Role = ChatRole.Assistant, Content = content };

    /// <summary>Builds an <see cref="ChatRole.Assistant"/> turn requesting one or more tool calls.</summary>
    public static ChatTurn FromAssistantToolCalls(IReadOnlyList<ModelToolCall> toolCalls) =>
        new() { Role = ChatRole.Assistant, ToolCalls = toolCalls };

    /// <summary>Builds a <see cref="ChatRole.Tool"/> turn carrying the result of one call.</summary>
    public static ChatTurn FromToolResult(string toolCallId, string content) =>
        new() { Role = ChatRole.Tool, ToolCallId = toolCallId, Content = content };
}

/// <summary>Token and cost accounting for one model call, carried into <see cref="ModelCallAuditEvent"/>.</summary>
public sealed record ModelUsage
{
    /// <summary>Creates a model usage record.</summary>
    /// <param name="PromptTokens">Tokens consumed by the request.</param>
    /// <param name="CompletionTokens">Tokens consumed by the response.</param>
    /// <param name="EstimatedCostUsd">An estimated cost, when the provider reports pricing.</param>
    public ModelUsage(int PromptTokens, int CompletionTokens, decimal? EstimatedCostUsd)
    {
        this.PromptTokens = PromptTokens;
        this.CompletionTokens = CompletionTokens;
        this.EstimatedCostUsd = EstimatedCostUsd;
    }

    /// <summary>Tokens consumed by the request.</summary>
    public int PromptTokens { get; init; }

    /// <summary>Tokens consumed by the response.</summary>
    public int CompletionTokens { get; init; }

    /// <summary>An estimated cost, when the provider reports pricing.</summary>
    public decimal? EstimatedCostUsd { get; init; }
}

/// <summary>Identifies which provider and model served a response, for audit — never for routing.</summary>
public sealed record ChatModelDescriptor
{
    /// <summary>Creates a chat model descriptor.</summary>
    /// <param name="ProviderId">The provider id, e.g. <c>"OpenRouter"</c>.</param>
    /// <param name="ModelId">The specific model, e.g. <c>"anthropic/claude-sonnet-4.5"</c>.</param>
    public ChatModelDescriptor(string ProviderId, string ModelId)
    {
        this.ProviderId = ProviderId;
        this.ModelId = ModelId;
    }

    /// <summary>The provider id, e.g. <c>"OpenRouter"</c>.</summary>
    public string ProviderId { get; init; }

    /// <summary>The specific model, e.g. <c>"anthropic/claude-sonnet-4.5"</c>.</summary>
    public string ModelId { get; init; }
}

/// <summary>A request for the model's next step: the standing instructions, the conversation so far, and what tools exist.</summary>
public sealed record ModelRequest
{
    /// <summary>Creates a model request.</summary>
    /// <param name="SystemPrompt">The standing instructions, including the rule that tool output is data, never instruction (agentic/03-security-rules.md, rule S5).</param>
    /// <param name="History">The conversation so far, in order.</param>
    /// <param name="AvailableTools">The manifests of every tool visible on this node right now.</param>
    public ModelRequest(string SystemPrompt, IReadOnlyList<ChatTurn> History, IReadOnlyList<ToolManifest> AvailableTools)
    {
        this.SystemPrompt = SystemPrompt;
        this.History = History;
        this.AvailableTools = AvailableTools;
    }

    /// <summary>The standing instructions, including the rule that tool output is data, never instruction.</summary>
    public string SystemPrompt { get; init; }

    /// <summary>The conversation so far, in order.</summary>
    public IReadOnlyList<ChatTurn> History { get; init; }

    /// <summary>The manifests of every tool visible on this node right now.</summary>
    public IReadOnlyList<ToolManifest> AvailableTools { get; init; }
}

/// <summary>
/// The model's response: text, zero or more requested tool calls, and whether the model
/// considers the task complete. Models may emit several tool calls in one turn; the V0.1
/// runtime executes them one at a time and feeds the rest back unexecuted — a deliberate,
/// reversible runtime choice, distinct from this contract shape (agentic/06-decisions.md, D-007).
/// </summary>
public sealed record ModelResponse
{
    /// <summary>Creates a model response.</summary>
    /// <param name="TextResponse">The model's text, when it has any.</param>
    /// <param name="ToolCalls">Zero or more tool calls the model is requesting.</param>
    /// <param name="IsFinal">Whether the model considers the task complete.</param>
    /// <param name="Usage">Token and cost accounting for this call, when the provider reports it.</param>
    public ModelResponse(string? TextResponse, IReadOnlyList<ModelToolCall> ToolCalls, bool IsFinal, ModelUsage? Usage)
    {
        this.TextResponse = TextResponse;
        this.ToolCalls = ToolCalls;
        this.IsFinal = IsFinal;
        this.Usage = Usage;
    }

    /// <summary>The model's text, when it has any.</summary>
    public string? TextResponse { get; init; }

    /// <summary>Zero or more tool calls the model is requesting.</summary>
    public IReadOnlyList<ModelToolCall> ToolCalls { get; init; }

    /// <summary>Whether the model considers the task complete.</summary>
    public bool IsFinal { get; init; }

    /// <summary>Token and cost accounting for this call, when the provider reports it.</summary>
    public ModelUsage? Usage { get; init; }
}

/// <summary>
/// A model bOps can talk to. Deliberately minimal and independent of any specific vendor SDK —
/// concrete adapters live in provider packages and translate to/from whatever SDK they use, so
/// that a breaking change in a vendor library touches one adapter, never the runtime.
/// </summary>
public interface IChatModel
{
    /// <summary>Which provider and model this instance talks to, for audit.</summary>
    ChatModelDescriptor Descriptor { get; }

    /// <summary>Asks the model for its next step, given the conversation so far and the tools available.</summary>
    /// <param name="request">The system prompt, history, and available tools.</param>
    /// <param name="ct">Cancelled if the call should be abandoned.</param>
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default);
}

/// <summary>
/// Thrown by an <see cref="IChatModel"/> adapter when a provider's response cannot be turned
/// into a valid <see cref="ModelResponse"/> — malformed JSON, a schema mismatch, or (in the
/// JSON-schema-fallback strategy) a model that will not comply with the required output shape
/// even after a retry. The plan is explicit that this must fail the step, never execute a tool
/// "guessed" from a fuzzy parse (§3.1.1) — the agent loop catches this exception and ends the
/// task rather than letting it escape (agentic/01-architecture-rules.md, rule C1).
/// </summary>
public sealed class ModelProtocolException : Exception
{
    /// <summary>Creates a model protocol exception with no message. Prefer the overload that takes one — CA1032 requires this constructor to exist, not that it be used.</summary>
    public ModelProtocolException()
    {
    }

    /// <summary>Creates a model protocol exception.</summary>
    public ModelProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a model protocol exception wrapping the underlying parse or transport failure.</summary>
    public ModelProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
