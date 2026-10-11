// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using bOps.Abstractions;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using MafChatRole = Microsoft.Extensions.AI.ChatRole;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>
/// AF-05 measurement of the ADR-0052 §11 pipeline order. The order is normative
/// (ToolResult -> Summarization -> SlidingWindow -> Truncation); this test MEASURES what the summarizer is given under the
/// normative order and under the reverse, and asserts nothing about changing it. DETERMINISTIC FIXTURE: a capturing client
/// stands in for the summarizer, so only the information offered to it is measured, never summary quality.
/// </summary>
// Shares a collection with GovernedSummarizationTests: its telemetry test listens globally for summary activities.
[Collection("MAF summarization telemetry")]
public sealed class PipelineOrderMeasurementTests
{
    private sealed class CapturingClient : IChatClient
    {
        public string Input { get; private set; } = string.Empty;

        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var builder = new StringBuilder();
            foreach (var message in messages)
            {
                foreach (var content in message.Contents)
                {
                    builder.Append(content switch
                    {
                        TextContent text => text.Text,
                        FunctionCallContent call => call.Name,
                        FunctionResultContent result => result.Result?.ToString(),
                        _ => string.Empty,
                    }).Append('\n');
                }
            }

            Input = builder.ToString();
            return Task.FromResult(new ChatResponse(new ChatMessage(MafChatRole.Assistant, "Summary of earlier tool results.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static async Task<(int InputCharacters, int RawMarkersInSummarizerInput, int SummarizerCalls, int ResultTokensAfter)> Run(
        bool summarizationFirst, IReadOnlyList<ChatTurn> history, string[] markers)
    {
        using var client = new CapturingClient();
        var messages = MafChatTurnMapper.ToMaf(history).ToList();
        messages.Insert(0, new ChatMessage(MafChatRole.User, MafSummarization.AnchorText));
        var toolResults = new ToolResultCompactionStrategy(_ => true, 0)
        {
            ToolCallFormatter = MafContextProjector.FormatToolGroupWithoutResultData,
        };
        var summary = new SummarizationCompactionStrategy(client, _ => true, 3, MafSummarization.Prompt);
        CompactionStrategy[] order = summarizationFirst ? [summary, toolResults] : [toolResults, summary];
        var compacted = (await CompactionProvider.CompactAsync(
            new PipelineCompactionStrategy(order), messages, NullLogger.Instance, CancellationToken.None)).ToList();
        var after = compacted.Sum(message => message.Contents.Sum(content => content is TextContent text ? text.Text.Length / 4 : 0));
        return (client.Input.Length, markers.Count(marker => client.Input.Contains(marker, StringComparison.Ordinal)), client.Calls, after);
    }

    [Fact]
    public async Task Measure_WhatTheSummarizerSeesUnderTheNormativeOrderAndTheReverse()
    {
        var scenario = RegressionScenarios.C508Equivalent();
        var run = await RegressionRunner.RunAsync(
            scenario, new RegressionConfiguration("maf-32k", RegressionRunner.BaseOptions(32768)));
        // The final call's history at 32k is fully verbatim: the goal, then every executed tool group.
        var history = run.Model.FinalRequest!.History.Skip(1).ToList();
        string[] rawMarkers = ["\"kernelCrash\":6", "\"hardwareError\":365", "\"observed\":126", "\"displayFault\":26", "\"unexpectedShutdown\":48"];

        var normative = await Run(summarizationFirst: false, history, rawMarkers);
        var reverse = await Run(summarizationFirst: true, history, rawMarkers);

        BenchmarkOutput.Append(
            "pipeline order — what the summarizer is offered (c508-equivalent final history; DETERMINISTIC FIXTURE)",
            "| Order | Summarizer calls | Summarizer input (chars) | Raw fact markers in the summarizer input (of 5) |\n|---|---:|---:|---:|\n"
            + $"| ToolResult -> Summarization (normative) | {normative.SummarizerCalls} | {normative.InputCharacters} | {normative.RawMarkersInSummarizerInput} |\n"
            + $"| Summarization -> ToolResult (reverse) | {reverse.SummarizerCalls} | {reverse.InputCharacters} | {reverse.RawMarkersInSummarizerInput} |");

        // The normative order never hands raw tool-result data (and so no injected instruction) to the summarizer, at a fraction
        // of the input the reverse order would send.
        Assert.Equal(0, normative.RawMarkersInSummarizerInput);
        Assert.True(reverse.RawMarkersInSummarizerInput > 0);
        Assert.True(reverse.InputCharacters > normative.InputCharacters);
    }
}
