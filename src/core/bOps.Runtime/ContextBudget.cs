// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The token estimate bOps uses for context budgeting: UTF-8 bytes divided by four, with the same rules the context projector applies
/// to a message (text bytes, a call's name and serialized arguments, a tool result serialized as a JSON string). It is an estimate,
/// never a provider count, and it is the only estimate in the product, so the knowledge budget and the projector agree.
/// </summary>
public static class ContextTokenEstimator
{
    /// <summary>The bytes one estimated token stands for.</summary>
    public const int BytesPerToken = 4;

    /// <summary>The UTF-8 bytes of a text.</summary>
    public static int BytesOfText(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);

    /// <summary>The bytes the projector counts for one conversation turn.</summary>
    public static int BytesOfTurn(ChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var bytes = 0;
        if (turn.Role == ChatRole.Tool)
        {
            bytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(turn.Content));
        }
        else if (turn.Content is not null)
        {
            bytes += Encoding.UTF8.GetByteCount(turn.Content);
        }

        if (turn.ToolCalls is { Count: > 0 } calls)
        {
            foreach (var call in calls)
            {
                bytes += Encoding.UTF8.GetByteCount(call.ToolName) + Encoding.UTF8.GetByteCount(call.Arguments.ToJson().ToJsonString());
            }
        }

        return bytes;
    }

    /// <summary>The bytes the projector counts for a sequence of turns.</summary>
    public static int BytesOfTurns(IEnumerable<ChatTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        return turns.Sum(BytesOfTurn);
    }

    /// <summary>The estimate for a text.</summary>
    public static int OfText(string? text) => BytesOfText(text) / BytesPerToken;

    /// <summary>The estimate for one conversation turn.</summary>
    public static int OfTurn(ChatTurn turn) => BytesOfTurn(turn) / BytesPerToken;

    /// <summary>The estimate for a sequence of turns: the bytes are added up first, as the projector does, and divided once.</summary>
    public static int OfTurns(IEnumerable<ChatTurn> turns) => BytesOfTurns(turns) / BytesPerToken;

    /// <summary>The estimate for the exact tool schemas a request offers.</summary>
    public static int OfTools(IReadOnlyList<ToolManifest> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Count == 0 ? 0 : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tools)) / BytesPerToken;
    }
}

/// <summary>
/// What the runner measured about the request that produced a model reply, handed to the knowledge budget (dynamic knowledge budget).
/// </summary>
/// <param name="InputBudgetTokens">Window minus output reserve minus safety reserve of the selected model, or <c>null</c> when its capacity is unknown.</param>
/// <param name="UsedPromptTokens">The prompt the request carried: the provider-reported figure when there was one, else the estimate of the unprojected request.</param>
/// <param name="UsedIsProviderReported">Whether <paramref name="UsedPromptTokens"/> is the provider's figure.</param>
/// <param name="KnowledgeTokensInContext">Estimated tokens of reference knowledge (the index and served reads) already in that request.</param>
public sealed record KnowledgeContextMeasure(
    int? InputBudgetTokens, int UsedPromptTokens, bool UsedIsProviderReported, int KnowledgeTokensInContext);

/// <summary>
/// How many tokens one more knowledge read may add to the context, decided from the model's real capacity (dynamic knowledge budget).
/// </summary>
/// <param name="AllowedTokens">The most the next read's framed content may take, or <c>null</c> when the capacity is unknown and only the fixed character limits apply.</param>
/// <param name="KnowledgeCeilingTokens">The most reference knowledge the context may hold in total, or <c>null</c> when unknown.</param>
public sealed record KnowledgeReadAllowance(int? AllowedTokens, int? KnowledgeCeilingTokens);

/// <summary>
/// The reference knowledge a model call is about to carry, reported by the runner so the broker can audit it (counts only, estimates).
/// </summary>
/// <param name="TokensInContext">Estimated tokens of the index and the served reads in the request.</param>
/// <param name="ReadsInContext">How many served reads are in the request.</param>
/// <param name="BudgetAvailableTokens">What the dynamic budget would still let a read add, or <c>null</c> when the capacity is unknown.</param>
public sealed record ModelKnowledgeContext(int TokensInContext, int ReadsInContext, int? BudgetAvailableTokens);

/// <summary>
/// The dynamic knowledge budget (never a fixed quota): reference knowledge may use at most <c>share</c> of the input space that is
/// left once everything mandatory (instructions, goal, tool schemas, evidence, history) is counted, and never more than what is still
/// free. Mandatory content is never sacrificed to fit documentation, and the output reserve is never lowered to make room for it.
/// </summary>
public static class KnowledgeContextBudget
{
    /// <summary>Tokens kept free for the assistant call turn and the framing that surround a read result in the next request.</summary>
    public const int ReplyMarginTokens = 256;

    /// <summary>The lowest share an operator may configure.</summary>
    public const double MinimumShare = 0.05;

    /// <summary>The highest share an operator may configure.</summary>
    public const double MaximumShare = 0.90;

    /// <summary>The share used when the operator configures none.</summary>
    public const double DefaultShare = 0.35;

    /// <summary>Computes what the next read may add.</summary>
    public static KnowledgeReadAllowance Allow(KnowledgeContextMeasure measure, double share)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (measure.InputBudgetTokens is not { } inputBudget)
        {
            return new KnowledgeReadAllowance(null, null);
        }

        var knowledge = Math.Max(0, measure.KnowledgeTokensInContext);
        var used = Math.Max(0, measure.UsedPromptTokens);
        var mandatory = Math.Max(0, used - knowledge);
        var free = Math.Max(0, inputBudget - mandatory);
        var ceiling = (int)Math.Floor(free * Math.Clamp(share, MinimumShare, MaximumShare));
        var byShare = ceiling - knowledge;
        var byRoom = inputBudget - used - ReplyMarginTokens;
        return new KnowledgeReadAllowance(Math.Max(0, Math.Min(byShare, byRoom)), ceiling);
    }
}
