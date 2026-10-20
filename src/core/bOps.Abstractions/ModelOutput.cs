// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// How one model reply ended, in provider-neutral terms, decided by the runtime from what the adapter observed. It describes the
/// reply; it grants nothing and no tool authority follows from it. Values are appended, never reordered.
/// </summary>
public enum ModelOutputClass
{
    /// <summary>No reply was obtained (a transport failure, a timeout, a refusal): only what was requested is known.</summary>
    NoResponse = 0,

    /// <summary>The reply carries usable text and the provider did not report a limit stop.</summary>
    Complete = 1,

    /// <summary>The reply carries at least one native tool call that the adapter could map, and the provider did not report a limit stop.</summary>
    ToolCall = 2,

    /// <summary>The provider stopped at its output limit before any content text: the budget was spent while reasoning (or on nothing visible).</summary>
    TruncatedBeforeContent = 3,

    /// <summary>The provider stopped at its output limit after some content text. The text is kept only in the raw diagnostics.</summary>
    TruncatedContent = 4,

    /// <summary>The provider stopped at its output limit and no tool call in the reply is usable (cut, unknown name or malformed arguments).</summary>
    TruncatedToolCall = 5,

    /// <summary>The provider stopped normally but the reply has neither text nor a tool call.</summary>
    Empty = 6,

    /// <summary>Any other stop the runtime does not interpret (for example a provider-side content filter).</summary>
    Other = 7,
}

/// <summary>
/// What the observed token usage says about which limit ended a truncated reply. Evidence only: the runtime never reads a provider's
/// configuration, so a server default stays unknown unless the reply shows where it cut.
/// </summary>
public enum ModelOutputLimitEvidence
{
    /// <summary>The reply was not cut at a limit.</summary>
    NotTruncated = 0,

    /// <summary>The reply was cut and the provider reported no completion token count, so the limit cannot be attributed.</summary>
    Unknown = 1,

    /// <summary>bOps requested no output limit: the cut came from the server's own default or from the end of the context, neither of which bOps knows.</summary>
    UnrequestedLimit = 2,

    /// <summary>bOps requested an output limit and the reply used at least that many completion tokens: the requested limit ended it.</summary>
    RequestedLimitReached = 3,

    /// <summary>bOps requested an output limit and the reply was cut with fewer completion tokens: a server limit or the context ended it first.</summary>
    BelowRequestedLimit = 4,
}

/// <summary>
/// The minimum telemetry of one model reply (generation limits): what bOps asked for, what the provider reported, and how the runtime
/// classified the outcome. It never contains the reply, the reasoning or the request: only counts, the provider's finish reason and
/// closed classifications, so it can be audited and exported.
/// </summary>
public sealed record ModelOutputObservation
{
    /// <summary>The maximum output tokens bOps asked the provider for; <c>null</c> when it asked for none, which means the provider's default applies and is unknown to bOps.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestedMaxOutputTokens { get; init; }

    /// <summary>The finish reason the provider reported, verbatim and bounded; <c>null</c> when it reported none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; init; }

    /// <summary>Prompt tokens the provider reported, when it did.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PromptTokens { get; init; }

    /// <summary>Completion tokens the provider reported, when it did.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletionTokens { get; init; }

    /// <summary>Reasoning tokens the provider reported, when it separates them; never the reasoning itself.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReasoningTokens { get; init; }

    /// <summary>The length in characters of the reasoning the provider returned, when it returned any; never the reasoning itself.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReasoningCharacters { get; init; }

    /// <summary>The length in characters of the content text the provider returned (zero when there was none).</summary>
    public int ContentCharacters { get; init; }

    /// <summary>How many native tool calls the reply carried, usable or not.</summary>
    public int ToolCallCount { get; init; }

    /// <summary>Whether the provider stopped at an output limit (finish reason <c>length</c>).</summary>
    public bool Truncated { get; init; }

    /// <summary>How the runtime classified the reply.</summary>
    public ModelOutputClass Class { get; init; }

    /// <summary>Which limit the usage points to, for a truncated reply.</summary>
    public ModelOutputLimitEvidence LimitEvidence { get; init; }
}
