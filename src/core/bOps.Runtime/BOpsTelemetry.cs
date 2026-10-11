// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace bOps.Runtime;

/// <summary>
/// The runtime's tracing and metrics instruments. The agent loop is a tree of nested steps —
/// literally a trace — so this exists from V0.1 rather than being retrofitted later
/// (agentic/06-decisions.md, D-009). This class only names the instruments; a host wires the
/// actual OpenTelemetry SDK pipeline (exporters, sampling) in its own composition root.
/// Arguments and tool output never appear here — they are audit material, with a different
/// retention and a different audience (agentic/01-architecture-rules.md, rule D).
/// </summary>
public static class BOpsTelemetry
{
    /// <summary>The name a host passes to <c>AddSource(...)</c> to collect bOps traces.</summary>
    public const string ActivitySourceName = "bOps.Runtime";

    /// <summary>The name a host passes to <c>AddMeter(...)</c> to collect bOps metrics.</summary>
    public const string MeterName = "bOps.Runtime";

    /// <summary>The activity source for the agent loop: one activity per task, one child per step.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Duration of one agent loop step, in milliseconds.</summary>
    public static Histogram<double> StepDurationMs { get; } =
        Meter.CreateHistogram<double>("bops.step.duration_ms", unit: "ms", description: "Duration of one agent loop step.");

    /// <summary>Duration of one tool execution, in milliseconds, tagged by tool name and outcome.</summary>
    public static Histogram<double> ToolDurationMs { get; } =
        Meter.CreateHistogram<double>("bops.tool.duration_ms", unit: "ms", description: "Duration of one tool execution.");

    /// <summary>How many times a task's plan was revised — a step deviated from what the plan expected, or the model continued past every step the plan named (rule C8).</summary>
    public static Counter<long> ReplansTotal { get; } =
        Meter.CreateCounter<long>("bops.replans_total", description: "How many times a task's plan was revised.");

    /// <summary>Estimated model input tokens before history compaction.</summary>
    public static Histogram<long> ContextTokensBefore { get; } =
        Meter.CreateHistogram<long>("bops.context.tokens_before", unit: "tokens");

    /// <summary>Estimated model input tokens after history compaction.</summary>
    public static Histogram<long> ContextTokensAfter { get; } =
        Meter.CreateHistogram<long>("bops.context.tokens_after", unit: "tokens");

    /// <summary>Difference between provider-reported and locally estimated input tokens.</summary>
    public static Histogram<long> ContextEstimateDelta { get; } =
        Meter.CreateHistogram<long>("bops.context.estimate_delta", unit: "tokens");

    /// <summary>One bounded retry after a provider classified the request as context overflow.</summary>
    public static Counter<long> ContextOverflowRetriesTotal { get; } =
        Meter.CreateCounter<long>("bops.context.overflow_retries_total");

    /// <summary>Summarization outcomes (requested/applied/skipped/failed), tagged by outcome and dedicated flag. Never carries content.</summary>
    public static Counter<long> SummaryEventsTotal { get; } =
        Meter.CreateCounter<long>("bops.summary.events_total");

    /// <summary>Estimated tokens sent to a summarizer in one governed summary call.</summary>
    public static Histogram<long> SummaryInputEstimatedTokens { get; } =
        Meter.CreateHistogram<long>("bops.summary.input_estimated_tokens", unit: "tokens");

    /// <summary>Provider-reported tokens by summary part (prompt/completion) for one logical call.</summary>
    public static Histogram<long> SummaryTokens { get; } =
        Meter.CreateHistogram<long>("bops.summary.tokens", unit: "tokens");

    /// <summary>Wall-clock duration spent in summary inference for one logical call, in milliseconds.</summary>
    public static Histogram<double> SummaryDurationMs { get; } =
        Meter.CreateHistogram<double>("bops.summary.duration_ms", unit: "ms");

    /// <summary>Tokens of one logical call split into primary reasoning, summary and all-in.</summary>
    public static Histogram<long> TaskCallTokens { get; } =
        Meter.CreateHistogram<long>("bops.task.call_tokens", unit: "tokens");
}
