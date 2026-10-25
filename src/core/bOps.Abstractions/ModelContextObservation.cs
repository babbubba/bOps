// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// What bOps knew about the context window of one model attempt, carried on its <see cref="ModelCallAuditEvent"/>: counts and
/// closed facts only, never content. Every <c>Estimated*</c> member is the bOps estimate (UTF-8 bytes divided by four, the same
/// figure the context projector uses); every <c>Provider*</c> member is what the provider reported. The two are kept apart on
/// purpose, so an estimate is never read as a measurement.
/// </summary>
public sealed record ModelContextObservation
{
    /// <summary>Where the capacity came from: the configured profile, or <c>unknown-structural</c> when the pair has no exact profile.</summary>
    public required string ProfileSource { get; init; }

    /// <summary>The configured context window of the selected provider/model, or <c>null</c> when unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxContextTokens { get; init; }

    /// <summary>The configured output reserve, or <c>null</c> when the capacity is unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReservedOutputTokens { get; init; }

    /// <summary>Window minus output reserve minus safety reserve, or <c>null</c> when the capacity is unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InputBudgetTokens { get; init; }

    /// <summary>Estimated tokens of the instructions and protected turns (never compacted).</summary>
    public int EstimatedProtectedTokens { get; init; }

    /// <summary>Estimated tokens of the offered tool schemas.</summary>
    public int EstimatedToolTokens { get; init; }

    /// <summary>Estimated tokens of the historical turns before projection.</summary>
    public int EstimatedHistoryTokensBefore { get; init; }

    /// <summary>Estimated tokens of the historical turns sent.</summary>
    public int EstimatedHistoryTokensAfter { get; init; }

    /// <summary>Estimated tokens of the whole prompt that was sent (protected, tools and projected history; the safety reserve excluded).</summary>
    public int EstimatedPromptTokens { get; init; }

    /// <summary>Prompt tokens the provider reported for this attempt, when it reported any.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProviderPromptTokens { get; init; }

    /// <summary>
    /// How much of the model's window the prompt takes, in percent: the provider figure when there is one, else the estimate; <c>null</c>
    /// when the window is unknown. <see cref="UsageIsProviderReported"/> says which.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ContextUsagePercent { get; init; }

    /// <summary>Whether <see cref="ContextUsagePercent"/> comes from the provider's prompt tokens (<c>true</c>) or from the estimate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UsageIsProviderReported { get; init; }

    /// <summary>Tool-result groups the deterministic compaction replaced by a placeholder.</summary>
    public int CompactedToolGroups { get; init; }

    /// <summary>Turns the sliding window or truncation dropped.</summary>
    public int DroppedTurns { get; init; }

    /// <summary>Whether this is the one bounded recovery request after a provider-reported context overflow.</summary>
    public bool Aggressive { get; init; }

    /// <summary>Estimated tokens of reference knowledge (index and served reads) held in this prompt; <c>null</c> for a task without knowledge.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KnowledgeTokensInContext { get; init; }

    /// <summary>
    /// The tokens the dynamic knowledge budget would still allow a read to add after this prompt, or <c>null</c> when the task has no
    /// knowledge or the capacity is unknown (then only the fixed character limits apply).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KnowledgeBudgetAvailableTokens { get; init; }

    /// <summary>Knowledge reads whose content is held in this prompt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KnowledgeReadsInContext { get; init; }
}
