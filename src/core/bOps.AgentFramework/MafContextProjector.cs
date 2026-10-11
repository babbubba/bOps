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
    private const string UnknownProfileSource = "unknown-legacy-safe";

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

        var profile = options.Resolve(descriptor);
        if (profile is null || profile.MaxContextTokens is null)
        {
            var legacyTokens = EstimateRequestTokens(request.LegacySafeRequest);
            return new ModelContextProjectionResult(
                request.LegacySafeRequest,
                new ModelContextProjectionStats(
                    profile is null ? UnknownProfileSource : ConfiguredProfileSource + ":unknown-legacy-safe",
                    null, null, null, 0, legacyTokens, legacyTokens, 0, 0, 0)
                {
                    HistoricalMessagesBefore = request.LegacySafeRequest.History.Count,
                    HistoricalMessagesAfter = request.LegacySafeRequest.History.Count,
                    HistoricalGroupsBefore = CountAtomicGroups(request.LegacySafeRequest.History),
                    HistoricalGroupsAfter = CountAtomicGroups(request.LegacySafeRequest.History),
                    HistoricalCharactersBefore = request.LegacySafeRequest.History.Sum(turn => turn.Content?.Length ?? 0),
                    HistoricalCharactersAfter = request.LegacySafeRequest.History.Sum(turn => turn.Content?.Length ?? 0),
                    Strategies = "LegacySafeProjection",
                });
        }

        var protectedBefore = MafChatTurnMapper.ToMaf(request.ProtectedBeforeHistory);
        var protectedAfter = MafChatTurnMapper.ToMaf(request.ProtectedAfterHistory);
        var historical = MafChatTurnMapper.ToMaf(request.HistoricalTurns).ToList();
        var fixedMessages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, request.SystemPrompt),
        };
        fixedMessages.AddRange(protectedBefore);
        fixedMessages.AddRange(protectedAfter);

        var protectedTokens = EstimateMessages(fixedMessages);
        var toolTokens = EstimateToolTokens(request.AvailableTools);
        var fixedTokens = protectedTokens + toolTokens + options.SafetyReserveTokens;
        var maxContextTokens = profile.MaxContextTokens.Value;
        var reservedOutputTokens = profile.ReservedOutputTokens!.Value;
        var inputCapacity = maxContextTokens - reservedOutputTokens;
        var historyBudget = inputCapacity - fixedTokens;
        if (historyBudget < 0)
        {
            throw new ModelProtocolException(
                "The protected model context and configured reserves exceed the selected model profile's input capacity.")
            {
                FailureKind = ModelFailureKind.ContextOverflow,
            };
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
            "[bOps historical tool context]");
        historical.Insert(0, historicalAnchor);

        // MAF's tokenizer-free estimate is based on textual message bytes. bOps tool results are
        // structured FunctionResultContent, so include those bytes explicitly in both predicates.
        CompactionTrigger exceedsBudget = index => EstimateMessages(index.GetIncludedMessages()) > historyBudget;
        CompactionTrigger fitsBudget = index => EstimateMessages(index.GetIncludedMessages()) <= historyBudget;
        var toolResults = new ToolResultCompactionStrategy(
            exceedsBudget,
            options.MinimumPreservedToolGroups,
            fitsBudget)
        {
            // Never promote raw tool output (including prompt injection) into assistant text.
            ToolCallFormatter = FormatToolGroupWithoutResultData,
        };
        var strategies = new List<CompactionStrategy> { toolResults };
        SummarizationAttemptState? summaryState = null;
        using var summarizer = summarization is null
            ? null
            : CreateSummarizer(summarization, summaryState = new SummarizationAttemptState());
        if (summarizer is not null && summarization is not null)
        {
            var threshold = (int)(historyBudget * summarization.Options.TriggerRatio);
            strategies.Add(new SummarizationCompactionStrategy(
                summarizer,
                index => EstimateMessages(index.GetIncludedMessages()) > threshold,
                summarization.Options.RecentGroupsToPreserve,
                MafSummarization.Prompt,
                index => EstimateMessages(index.GetIncludedMessages()) <= threshold));
        }

        strategies.Add(new SlidingWindowCompactionStrategy(exceedsBudget, options.MinimumPreservedTurns, fitsBudget));
        strategies.Add(new TruncationCompactionStrategy(exceedsBudget, minimumPreservedGroups: 0, fitsBudget));
        var pipeline = new PipelineCompactionStrategy(strategies);

        var compactedMessages = (await CompactionProvider.CompactAsync(pipeline, historical, logger, ct)).ToList();
        // MAF contains non-cancellation summarizer failures and restores the original groups; a cancellation it
        // did not rethrow must still stop the task.
        ct.ThrowIfCancellationRequested();
        compactedMessages.RemoveAll(message => ReferenceEquals(message, historicalAnchor));
        var afterTokens = EstimateMessages(compactedMessages);
        var afterCharacters = EstimateCharacters(compactedMessages);
        if (afterTokens > historyBudget)
        {
            throw new ModelProtocolException(
                $"Deterministic history compaction could not fit the selected model profile without changing protected context " +
                $"(estimated history {afterTokens} tokens; budget {historyBudget} tokens).")
            {
                FailureKind = ModelFailureKind.ContextOverflow,
            };
        }

        var compactedHistory = MafChatTurnMapper.FromMaf(compactedMessages);
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
                ConfiguredProfileSource,
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
                Strategies = summarizer is null ? "ToolResult,SlidingWindow,Truncation" : "ToolResult,Summarization,SlidingWindow,Truncation",
                Summary = summarizer is null ? null : SummaryStats(summaryState!, compactedMessages),
            });
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

    private static int EstimateRequestTokens(ModelRequest request) =>
        EstimateMessages(
        [
            new ChatMessage(Microsoft.Extensions.AI.ChatRole.System, request.SystemPrompt),
            .. MafChatTurnMapper.ToMaf(request.History),
        ]) + EstimateToolTokens(request.AvailableTools);

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

    private static string FormatToolGroupWithoutResultData(CompactionMessageGroup group)
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
