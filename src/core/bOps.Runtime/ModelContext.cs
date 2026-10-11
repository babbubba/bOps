// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// A configured context window for one exact provider/model pair. The operator states it; bOps never infers a capacity from a
/// provider family or a model name, and an unlisted pair (a router such as <c>openrouter/free</c>, an unprofiled model) has an unknown one.
/// </summary>
public sealed record ModelContextProfile
{
    /// <summary>The provider identifier reported by <see cref="ChatModelDescriptor"/>.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>The exact model identifier reported by <see cref="ChatModelDescriptor"/>.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The model's total context window, in tokens. Required and positive.</summary>
    public int? MaxContextTokens { get; init; }

    /// <summary>Tokens reserved for the model response. Required, positive and below <see cref="MaxContextTokens"/>.</summary>
    public int? ReservedOutputTokens { get; init; }
}

/// <summary>Runtime-owned context budgeting and compaction configuration.</summary>
public sealed record ModelContextOptions
{
    /// <summary>Exact provider/model profiles. There is deliberately no guessed provider default.</summary>
    public IReadOnlyList<ModelContextProfile> Profiles { get; init; } = [];

    /// <summary>Extra input headroom retained after fixed prompt costs.</summary>
    public int SafetyReserveTokens { get; init; } = 1024;

    /// <summary>
    /// Recent logical tool groups kept verbatim. With a known profile this is a floor under budget pressure (tool-result replacement
    /// never touches them while older groups can still be replaced); with an unknown capacity it is the ONLY bound: older tool
    /// results are always replaced, so no capacity is invented. The default 3 is the HARDEN-8 verbatim window, kept because the
    /// AF-05 fixtures show 2 drops the oldest of a three-step diagnostic that nothing required dropping.
    /// </summary>
    public int MinimumPreservedToolGroups { get; init; } = 3;

    /// <summary>Recent conversation turns protected from sliding-window removal.</summary>
    public int MinimumPreservedTurns { get; init; } = 1;

    internal void Validate()
    {
        if (SafetyReserveTokens < 0)
        {
            throw new InvalidOperationException("'Agent:Context:SafetyReserveTokens' must not be negative.");
        }

        if (MinimumPreservedToolGroups < 0)
        {
            throw new InvalidOperationException("'Agent:Context:MinimumPreservedToolGroups' must not be negative.");
        }

        if (MinimumPreservedTurns < 0)
        {
            throw new InvalidOperationException("'Agent:Context:MinimumPreservedTurns' must not be negative.");
        }

        var seen = new HashSet<(string Provider, string Model)>();
        for (var index = 0; index < Profiles.Count; index++)
        {
            var profile = Profiles[index];
            var key = $"Agent:Context:Profiles:{index}";
            if (string.IsNullOrWhiteSpace(profile.Provider) || string.IsNullOrWhiteSpace(profile.Model))
            {
                throw new InvalidOperationException($"'{key}:Provider' and '{key}:Model' must be non-empty.");
            }

            if (profile.MaxContextTokens is null or <= 0)
            {
                // An entry exists only to state a capacity. Leaving a model out of the list is how its capacity stays unknown.
                throw new InvalidOperationException($"'{key}:MaxContextTokens' must be set and positive.");
            }
            else if (profile.ReservedOutputTokens is null
                || profile.ReservedOutputTokens <= 0
                || profile.ReservedOutputTokens >= profile.MaxContextTokens)
            {
                throw new InvalidOperationException(
                    $"'{key}:ReservedOutputTokens' must be positive and below '{key}:MaxContextTokens'.");
            }

            if (!seen.Add((profile.Provider, profile.Model)))
            {
                throw new InvalidOperationException(
                    $"'{key}' duplicates the exact provider/model profile '{profile.Provider}/{profile.Model}'.");
            }
        }
    }

    /// <summary>Resolves only an exact configured provider/model pair.</summary>
    public ModelContextProfile? Resolve(ChatModelDescriptor descriptor) => Profiles.FirstOrDefault(profile =>
        string.Equals(profile.Provider, descriptor.ProviderId, StringComparison.Ordinal)
        && string.Equals(profile.Model, descriptor.ModelId, StringComparison.Ordinal));
}

/// <summary>
/// A model request split into runtime-protected context (never compacted) and historical context the context layer may
/// project for the candidate actually about to be called. The split is made by the runtime, not inferred by the context layer.
/// </summary>
public sealed record ModelContextRequest(
    string SystemPrompt,
    IReadOnlyList<ChatTurn> ProtectedBeforeHistory,
    IReadOnlyList<ChatTurn> HistoricalTurns,
    IReadOnlyList<ChatTurn> ProtectedAfterHistory,
    IReadOnlyList<ToolManifest> AvailableTools)
{
    /// <summary>
    /// Whether this is the one bounded recovery request after a provider-reported <see cref="ModelFailureKind.ContextOverflow"/>:
    /// the context layer then projects the historical context more aggressively than for a normal request. It never changes the
    /// protected context or the offered tools.
    /// </summary>
    public bool Aggressive { get; init; }

    /// <summary>Adds runtime-authored continuation turns after historical context.</summary>
    public ModelContextRequest AppendProtected(params ChatTurn[] turns) => this with
    {
        ProtectedAfterHistory = [.. ProtectedAfterHistory, .. turns],
    };

    /// <summary>
    /// The request with no projection: protected context, then every historical turn verbatim. It is what a host that composes no
    /// context projector sends; every host in this repository composes one. Each observation is already bounded by
    /// <c>MaxObservationCharacters</c>, and a provider-reported overflow is still classified and recovered once.
    /// </summary>
    public ModelRequest ToUnprojectedRequest() =>
        new(SystemPrompt, [.. ProtectedBeforeHistory, .. HistoricalTurns, .. ProtectedAfterHistory], AvailableTools);
}

/// <summary>Non-content metadata produced for one candidate-specific projection.</summary>
public sealed record ModelContextProjectionStats(
    string ProfileSource,
    int? MaxContextTokens,
    int? ReservedOutputTokens,
    int? InputBudgetTokens,
    int EstimatedFixedTokens,
    int EstimatedHistoryTokensBefore,
    int EstimatedHistoryTokensAfter,
    int CompactedToolGroups,
    int DroppedTurns,
    int DroppedGroups)
{
    /// <summary>Estimated tokens occupied by system and protected turns, excluding tools/reserves.</summary>
    public int EstimatedProtectedTokens { get; init; }

    /// <summary>Estimated tokens occupied by exact offered tool schemas.</summary>
    public int EstimatedToolTokens { get; init; }

    /// <summary>
    /// The estimated whole prompt before projection: system and protected turns, offered tool schemas and the full historical
    /// context. It excludes the safety reserve, which is headroom and never a prompt token, so it is comparable with the
    /// provider-reported prompt tokens.
    /// </summary>
    public int EstimatedPromptTokensBefore => EstimatedProtectedTokens + EstimatedToolTokens + EstimatedHistoryTokensBefore;

    /// <summary>The estimated whole prompt after projection; see <see cref="EstimatedPromptTokensBefore"/>.</summary>
    public int EstimatedPromptTokensAfter => EstimatedProtectedTokens + EstimatedToolTokens + EstimatedHistoryTokensAfter;

    /// <summary>Historical message counts before and after projection.</summary>
    public int HistoricalMessagesBefore { get; init; }
    public int HistoricalMessagesAfter { get; init; }

    /// <summary>Atomic historical group counts before and after projection.</summary>
    public int HistoricalGroupsBefore { get; init; }
    public int HistoricalGroupsAfter { get; init; }

    /// <summary>Historical character counts before and after projection.</summary>
    public int HistoricalCharactersBefore { get; init; }
    public int HistoricalCharactersAfter { get; init; }

    /// <summary>The fixed deterministic pipeline, safe for telemetry.</summary>
    public string Strategies { get; init; } = "ToolResult,SlidingWindow,Truncation";

    /// <summary>Summarization measurements, or <c>null</c> when summarization is not configured for this projection.</summary>
    public ModelSummaryStats? Summary { get; init; }
}

/// <summary>The provider request and its safe, aggregate projection measurements.</summary>
public sealed record ModelContextProjectionResult(ModelRequest Request, ModelContextProjectionStats Stats);

/// <summary>Provider-neutral boundary implemented by the isolated MAF integration.</summary>
public interface IModelContextProjector
{
    /// <summary>Projects one request for the provider/model candidate actually about to be called.</summary>
    Task<ModelContextProjectionResult> ProjectAsync(
        ModelContextRequest request,
        ChatModelDescriptor descriptor,
        ModelSummarizationAccess? summarization = null,
        CancellationToken ct = default);
}
