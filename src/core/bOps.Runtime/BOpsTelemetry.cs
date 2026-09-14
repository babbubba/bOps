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
}
