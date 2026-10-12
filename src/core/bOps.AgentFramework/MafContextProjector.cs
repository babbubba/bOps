// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace bOps.AgentFramework;

/// <summary>
/// Projects bOps-owned protected and historical context through the deterministic MAF 1.24.0
/// compaction primitives. Optional governed summarization runs between tool-result compaction and the
/// sliding window, only through the broker-owned <see cref="ModelSummarizationAccess"/>.
/// </summary>
public sealed class MafContextProjector : IModelContextProjector
{
    private const string ConfiguredProfileSource = "Agent.Context.Profiles";
    private const string UnknownProfileSource = "unknown-structural";

    /// <summary>The recovery projection after a provider overflow compacts toward 1/n of the history allowance.</summary>
    internal const int AggressiveBudgetDivisor = 2;

    private readonly ModelContextOptions options;
    private readonly ILogger logger;

    /// <summary>Creates a projector from validated runtime-owned options.</summary>
    public MafContextProjector(AgentRunnerOptions runnerOptions, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(runnerOptions);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        runnerOptions.Validate();
        options = runnerOptions.Context;
    }

    /// <inheritdoc />
    public async Task<ModelContextProjectionResult> ProjectAsync(
        ModelContextRequest request,
        ChatModelDescriptor descriptor,
        ModelSummarizationAccess? summarization = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(descriptor);
        ct.ThrowIfCancellationRequested();

        // Exact provider+model only. No entry means the capacity is UNKNOWN, never a guess from a family or a name.
        var profile = options.Resolve(descriptor);
        var aggressive = request.Aggressive;
        var protectedBefore = MafChatTurnMapper.ToMaf(request.ProtectedBeforeHistory);
        var protectedAfter = MafChatTurnMapper.ToMaf(request.ProtectedAfterHistory);
        var historical = MafChatTurnMapper.ToMaf(request.HistoricalTurns).ToList();
        // G2-2: provenance never travels inside a MAF message (a tag there is only a label). bOps keeps it beside the messages it
        // built, keyed by reference, and re-attaches it to the very same messages after compaction.
        var provenanceByMessage = new Dictionary<ChatMessage, ContextProvenance?>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < historical.Count; index++)
        {
            provenanceByMessage[historical[index]] = request.HistoricalTurns[index].Provenance;
        }
        var fixedMessages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, request.SystemPrompt),
        };
        fixedMessages.AddRange(protectedBefore);
        fixedMessages.AddRange(protectedAfter);

        var protectedTokens = EstimateMessages(fixedMessages);
        var toolTokens = EstimateToolTokens(request.AvailableTools);
        var fixedTokens = protectedTokens + toolTokens;
        int? maxContextTokens = null;
        int? reservedOutputTokens = null;
        int? inputCapacity = null;
        int? historyBudget = null;
        if (profile is { MaxContextTokens: { } max, ReservedOutputTokens: { } reserved })
        {
            maxContextTokens = max;
            reservedOutputTokens = reserved;
            inputCapacity = max - reserved;
            fixedTokens += options.SafetyReserveTokens;
            historyBudget = inputCapacity - fixedTokens;
            if (historyBudget < 0)
            {
                throw new ModelProtocolException(
                    "The protected model context and configured reserves exceed the selected model profile's input capacity.")
                {
                    FailureKind = ModelFailureKind.ContextOverflow,
                };
            }
        }

        var beforeTokens = EstimateMessages(historical);
        var beforeCharacters = EstimateCharacters(historical);
        var beforeGroups = historical.Count;
        var beforeTurns = historical.Count(message => message.Role == Microsoft.Extensions.AI.ChatRole.User);
        var beforeToolGroups = historical.Count(message =>
            message.Role == Microsoft.Extensions.AI.ChatRole.Assistant
            && message.Contents.OfType<FunctionCallContent>().Any());

        // Runtime history consists mainly of assistant tool calls/results. Give MAF a user-turn
        // anchor so its normal conversation index can group those protocol messages. The marker is
        // runtime-authored, never sent to the provider, and the real goal remains protected above.
        var historicalAnchor = new ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User,
            MafSummarization.AnchorText);
        historical.Insert(0, historicalAnchor);

        // The recovery projection after a provider-reported overflow: the estimator under-counted, so the history is compacted
        // with nothing preserved against a reduced allowance. Applied once per logical call by the runner.
        var preservedToolGroups = aggressive ? 0 : options.MinimumPreservedToolGroups;
        var preservedTurns = aggressive ? 0 : options.MinimumPreservedTurns;
        int? compactionBudget = historyBudget is { } budget ? (aggressive ? budget / AggressiveBudgetDivisor : budget) : null;

        // MAF's tokenizer-free estimate is based on textual message bytes. bOps tool results are
        // structured FunctionResultContent, so include those bytes explicitly in both predicates.
        // With an unknown capacity there is no budget to exceed: tool results older than the preserved recent groups are always
        // compacted (a structural bound), and nothing else is removed because nothing says how much would fit.
        CompactionTrigger exceedsBudget = compactionBudget is { } limit
            ? index => EstimateMessages(index.GetIncludedMessages()) > limit
            : _ => true;
        CompactionTrigger fitsBudget = compactionBudget is { } fitLimit
            ? index => EstimateMessages(index.GetIncludedMessages()) <= fitLimit
            : _ => false;
        var toolResults = new ToolResultCompactionStrategy(exceedsBudget, preservedToolGroups, fitsBudget)
        {
            // Never promote raw tool output (including prompt injection) into assistant text.
            ToolCallFormatter = FormatToolGroupWithoutResultData,
        };
        var strategies = new List<CompactionStrategy> { toolResults };
        SummarizationAttemptState? summaryState = null;
        using var summarizer = summarization is null || profile is null || aggressive
            ? null
            : CreateSummarizer(summarization, summaryState = new SummarizationAttemptState());
        if (summarizer is not null && summarization is not null && historyBudget is { } summaryBudget)
        {
            var threshold = (int)(summaryBudget * summarization.Options.TriggerRatio);
            strategies.Add(new SummarizationCompactionStrategy(
                summarizer,
                index => EstimateMessages(index.GetIncludedMessages()) > threshold,
                summarization.Options.RecentGroupsToPreserve,
                MafSummarization.Prompt,
                index => EstimateMessages(index.GetIncludedMessages()) <= threshold));
        }

        if (compactionBudget is not null)
        {
            strategies.Add(new SlidingWindowCompactionStrategy(exceedsBudget, preservedTurns, fitsBudget));
            strategies.Add(new TruncationCompactionStrategy(exceedsBudget, minimumPreservedGroups: 0, fitsBudget));
        }

        var pipeline = new PipelineCompactionStrategy(strategies);

        var compactedMessages = (await CompactionProvider.CompactAsync(pipeline, historical, logger, ct)).ToList();
        // MAF contains non-cancellation summarizer failures and restores the original groups; a cancellation it
        // did not rethrow must still stop the task.
        ct.ThrowIfCancellationRequested();
        compactedMessages.RemoveAll(message => ReferenceEquals(message, historicalAnchor));
        var afterTokens = EstimateMessages(compactedMessages);
        var afterCharacters = EstimateCharacters(compactedMessages);
        if (historyBudget is { } fitBudget && afterTokens > fitBudget)
        {
            throw new ModelProtocolException(
                $"Deterministic history compaction could not fit the selected model profile without changing protected context " +
                $"(estimated history {afterTokens} tokens; budget {fitBudget} tokens).")
            {
                FailureKind = ModelFailureKind.ContextOverflow,
            };
        }

        var compactedHistory = ReattachProvenance(compactedMessages, provenanceByMessage, request.HistoricalTurns);
        var projectedHistory = new List<ChatTurn>(
            request.ProtectedBeforeHistory.Count + compactedHistory.Count + request.ProtectedAfterHistory.Count);
        projectedHistory.AddRange(request.ProtectedBeforeHistory);
        projectedHistory.AddRange(compactedHistory);
        projectedHistory.AddRange(request.ProtectedAfterHistory);
        var projected = new ModelRequest(request.SystemPrompt, projectedHistory, request.AvailableTools);
        var afterToolGroups = compactedMessages.Count(message =>
            message.Role == Microsoft.Extensions.AI.ChatRole.Assistant
            && message.Contents.OfType<FunctionCallContent>().Any());
        var compactedToolGroups = Math.Max(0, beforeToolGroups - afterToolGroups);
        var afterTurns = compactedMessages.Count(message => message.Role == Microsoft.Extensions.AI.ChatRole.User);
        var droppedTurns = Math.Max(0, beforeTurns - afterTurns);
        var droppedGroups = Math.Max(0, beforeGroups - compactedMessages.Count - compactedToolGroups);

        return new ModelContextProjectionResult(
            projected,
            new ModelContextProjectionStats(
                profile is null ? UnknownProfileSource : ConfiguredProfileSource,
                maxContextTokens,
                reservedOutputTokens,
                inputCapacity,
                fixedTokens,
                beforeTokens,
                afterTokens,
                compactedToolGroups,
                droppedTurns,
                droppedGroups)
            {
                EstimatedProtectedTokens = protectedTokens,
                EstimatedToolTokens = toolTokens,
                HistoricalMessagesBefore = beforeGroups,
                HistoricalMessagesAfter = compactedMessages.Count,
                HistoricalGroupsBefore = CountAtomicGroups(request.HistoricalTurns),
                HistoricalGroupsAfter = CountAtomicGroups(compactedHistory),
                HistoricalCharactersBefore = beforeCharacters,
                HistoricalCharactersAfter = afterCharacters,
                Strategies = StrategyLabel(profile is not null, summarizer is not null, aggressive),
                Summary = summarizer is null ? null : SummaryStats(summaryState!, compactedMessages),
            });
    }

    /// <summary>
    /// Maps the compacted messages back to bOps turns, keeping each surviving message's provenance. A message the compaction
    /// created (a tool-group placeholder or a summary) is derived from the historical context, so it is marked
    /// <see cref="ContextProvenance.Derived"/> and takes the most data-like origin among the historical turns it may have been
    /// built from (<see cref="ContextOrigin"/> is ordered from most to least trusted), never a more trusted one. When any of those
    /// turns carries no provenance the derived message carries none either: a legacy turn must not be laundered into an attributed one.
    /// </summary>
    private static List<ChatTurn> ReattachProvenance(
        List<ChatMessage> compacted,
        Dictionary<ChatMessage, ContextProvenance?> provenanceByMessage,
        IReadOnlyList<ChatTurn> historicalTurns)
    {
        var derived = historicalTurns.Any(turn => turn.Provenance is null)
            ? null
            : historicalTurns
                .Select(turn => turn.Provenance)
                .OfType<ContextProvenance>()
                .OrderByDescending(provenance => provenance.Origin)
                .Select(provenance => new ContextProvenance { Origin = provenance.Origin, Derived = true })
                .FirstOrDefault();
        var turns = new List<ChatTurn>(compacted.Count);
        foreach (var message in compacted)
        {
            var provenance = provenanceByMessage.TryGetValue(message, out var original) ? original : derived;
            turns.AddRange(MafChatTurnMapper.FromMaf([message]).Select(turn => turn with { Provenance = provenance }));
        }

        return turns;
    }

    private static string StrategyLabel(bool known, bool summarized, bool aggressive)
    {
        var label = !known
            ? "ToolResult(structural)"
            : summarized ? "ToolResult,Summarization,SlidingWindow,Truncation" : "ToolResult,SlidingWindow,Truncation";
        return aggressive ? label + ",Aggressive" : label;
    }

    private GovernedSummarizationChatClient CreateSummarizer(
        ModelSummarizationAccess access, SummarizationAttemptState state)
    {
        var profile = access.Profile;
        var inputBudget = profile?.MaxContextTokens is { } max && profile.ReservedOutputTokens is { } reserved
            ? max - reserved - options.SafetyReserveTokens
            : 0;
        return new GovernedSummarizationChatClient(access, state, inputBudget, TimeProvider.System);
    }

    private static ModelSummaryStats SummaryStats(
        SummarizationAttemptState state, IReadOnlyList<ChatMessage> finalMessages)
    {
        var present = finalMessages.Any(message => message.Contents.OfType<TextContent>()
            .Any(text => text.Text.Contains(MafSummarization.SummaryHeader, StringComparison.Ordinal)));
        var (outcome, reason) = present
            ? ("applied", null)
            : state.FailureReason is not null
                ? ("failed", state.FailureReason)
                : state.SkipReason is not null
                    ? ("skipped", state.SkipReason)
                    : state.Succeeded
                        ? ("discarded", "summary-removed-by-window-or-truncation")
                        : ("not-triggered", null);
        return new ModelSummaryStats(
            outcome,
            reason,
            state.GroupsFed,
            present ? 1 : state.GroupsFed,
            state.InputEstimatedTokens,
            state.OutputEstimatedTokens);
    }

    private static int EstimateMessages(IEnumerable<ChatMessage> messages)
    {
        var bytes = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                bytes += content switch
                {
                    TextContent text => Encoding.UTF8.GetByteCount(text.Text),
                    FunctionCallContent call => Encoding.UTF8.GetByteCount(call.Name)
                        + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(call.Arguments)),
                    FunctionResultContent result => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result.Result)),
                    _ => 0,
                };
            }
        }

        return bytes / 4;
    }

    private static int EstimateToolTokens(IReadOnlyList<ToolManifest> tools)
    {
        if (tools.Count == 0)
        {
            return 0;
        }

        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tools));
        return bytes / 4;
    }

    private static int EstimateCharacters(IEnumerable<ChatMessage> messages) => messages.Sum(message =>
        message.Contents.Sum(content => content switch
        {
            TextContent text => text.Text.Length,
            FunctionCallContent call => call.Name.Length + JsonSerializer.Serialize(call.Arguments).Length,
            FunctionResultContent result => JsonSerializer.Serialize(result.Result).Length,
            _ => 0,
        }));

    private static int CountAtomicGroups(IReadOnlyList<ChatTurn> turns)
    {
        var groups = 0;
        for (var index = 0; index < turns.Count; index++)
        {
            groups++;
            if (turns[index].Role != bOps.Abstractions.ChatRole.Assistant
                || turns[index].ToolCalls is not { Count: > 0 } calls)
            {
                continue;
            }

            var ids = calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            while (index + 1 < turns.Count
                && turns[index + 1].Role == bOps.Abstractions.ChatRole.Tool
                && turns[index + 1].ToolCallId is { } id
                && ids.Remove(id))
            {
                index++;
            }
        }

        return groups;
    }

    internal static string FormatToolGroupWithoutResultData(CompactionMessageGroup group)
    {
        var calls = group.Messages
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .ToArray();
        var results = group.Messages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .ToDictionary(result => result.CallId, StringComparer.Ordinal);
        var lines = new List<string> { "[Compacted tool interactions; persisted result data omitted]" };
        foreach (var call in calls)
        {
            var status = results.TryGetValue(call.CallId, out var result)
                && ResultWasNotExecuted(result.Result)
                    ? "not executed"
                    : "result recorded";
            lines.Add($"- {call.Name}: {status}");
        }

        return string.Join('\n', lines);
    }

    private static bool ResultWasNotExecuted(object? result) => result is string text
        && (text.Contains("Not executed:", StringComparison.Ordinal)
            || text.Contains("NOT EXECUTED", StringComparison.Ordinal));
}
