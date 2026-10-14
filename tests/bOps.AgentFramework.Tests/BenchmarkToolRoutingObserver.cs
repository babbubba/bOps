// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// KNOW-FIX-01: collects the runtime's tool-routing activity events (announced, selected, executed, refused) of exactly one task.
/// The listener is process-wide, so every event is attributed through its activity chain to the task id and the rest is ignored.
/// It reads telemetry only; it writes no audit event and changes nothing the runner decides.
/// </summary>
internal sealed class BenchmarkToolRoutingObserver : IDisposable
{
    private const string Prefix = "bops.tool_routing.";
    private static readonly HashSet<string> ApprovedTools =
    [
        "system.events", EvidenceRead.ControlFunctionName, KnowledgeReadBridge.PublicToolName,
        ToolRoutingTelemetry.UnofferedTool, ToolRoutingTelemetry.UnregisteredTool,
    ];
    private static readonly HashSet<string> ApprovedReasons =
    [
        .. Enum.GetNames<AuthorizationKind>(), .. Enum.GetNames<EvidenceReadResultCode>(), .. Enum.GetNames<KnowledgeReadResultCode>(),
        "SemanticMismatch", "ArgumentValidation", "Unknown",
    ];

    private readonly Guid taskId;
    private readonly object gate = new();
    private readonly List<(DateTimeOffset At, long Order, JsonObject Entry)> entries = [];
    private readonly ActivityListener listener;
    private long order;

    internal BenchmarkToolRoutingObserver(Guid taskId)
    {
        this.taskId = taskId;
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BOpsTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Collect,
        };
        ActivitySource.AddActivityListener(listener);
    }

    /// <summary>The task's routing events in time order, each reduced to approved names and closed codes.</summary>
    internal JsonArray Snapshot()
    {
        lock (gate)
        {
            return [.. entries.OrderBy(entry => entry.At).ThenBy(entry => entry.Order).Select(entry => (JsonNode)entry.Entry.DeepClone())];
        }
    }

    public void Dispose() => listener.Dispose();

    private void Collect(Activity activity)
    {
        if (!BelongsToTask(activity))
        {
            return;
        }

        foreach (var evt in activity.Events)
        {
            if (!evt.Name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var stage = evt.Name[Prefix.Length..];
            var entry = new JsonObject { ["stage"] = stage };
            foreach (var tag in evt.Tags)
            {
                switch (tag.Key, tag.Value)
                {
                    case ("bops.tools.announced", string[] announced):
                        entry["announced"] = new JsonArray([.. announced.Select(name => (JsonNode)SafeTool(name)!)]);
                        break;
                    case ("bops.tools.selected", string[] selected):
                        entry["selected"] = new JsonArray([.. selected.Select(name => (JsonNode)SafeTool(name)!)]);
                        break;
                    case ("bops.tool", string tool):
                        entry["tool"] = SafeTool(tool);
                        break;
                    case ("bops.reason", string reason):
                        entry["reason"] = ApprovedReasons.Contains(reason) ? reason : KnowledgeBenchmarkExport.RedactedFreeText;
                        break;
                }
            }

            lock (gate)
            {
                entries.Add((evt.Timestamp, order++, entry));
            }
        }
    }

    private bool BelongsToTask(Activity activity)
    {
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (current.GetTagItem("bops.task_id") is Guid id)
            {
                return id == taskId;
            }
        }

        return false;
    }

    private static string SafeTool(string name) => ApprovedTools.Contains(name) ? name : KnowledgeBenchmarkExport.RedactedFreeText;
}
