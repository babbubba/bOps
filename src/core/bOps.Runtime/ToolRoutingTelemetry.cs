// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Telemetry of how a tool moves from the request to execution: <c>announced</c> (offered to the model in the request's tool view),
/// <c>selected</c> (named by a function call in the model's reply), <c>executed</c> (dispatched by the runtime) and <c>refused</c>
/// (rejected by the runtime before anything ran). It is observability only: it writes no audit event and decides nothing, so the
/// audit trail is neither duplicated nor altered. Only names the runtime offered or registered are recorded; arguments, content and
/// unoffered model-proposed names never are.
/// </summary>
internal static class ToolRoutingTelemetry
{
    /// <summary>The tool tag of a proposed call whose name was not in the request's tool view.</summary>
    public const string UnofferedTool = "[unoffered]";

    /// <summary>The tool tag of a refused call whose name resolves to no registered tool.</summary>
    public const string UnregisteredTool = "[unregistered]";

    /// <summary>The announced stage.</summary>
    public const string Announced = "announced";

    /// <summary>The selected stage.</summary>
    public const string Selected = "selected";

    /// <summary>The executed stage.</summary>
    public const string Executed = "executed";

    /// <summary>The refused stage.</summary>
    public const string Refused = "refused";

    /// <summary>Records the request's tool view and the reply's proposed calls for one model call.</summary>
    internal static void RecordModelCall(IReadOnlyList<ToolManifest> offered, ModelResponse response)
    {
        if (offered.Count == 0 && response.ToolCalls.Count == 0)
        {
            return;
        }

        var offeredNames = offered.Select(tool => tool.Name).ToArray();
        var selectedNames = response.ToolCalls.Select(call => SelectedName(offeredNames, call)).ToArray();
        foreach (var name in offeredNames)
        {
            BOpsTelemetry.ToolRoutingEventsTotal.Add(1, Tags(Announced, name, null));
        }

        foreach (var name in selectedNames)
        {
            BOpsTelemetry.ToolRoutingEventsTotal.Add(1, Tags(Selected, name, null));
        }

        Activity.Current?.AddEvent(new ActivityEvent("bops.tool_routing.model_call", tags: new ActivityTagsCollection
        {
            ["bops.tools.announced"] = offeredNames,
            ["bops.tools.selected"] = selectedNames,
        }));
    }

    /// <summary>Records that the runtime dispatched <paramref name="tool"/>.</summary>
    internal static void RecordExecuted(string tool) => Record(Executed, tool, null);

    /// <summary>Records that the runtime refused a call before anything ran; <paramref name="reason"/> is a closed code.</summary>
    internal static void RecordRefused(string tool, string reason) => Record(Refused, tool, reason);

    /// <summary>The tag of a proposed call: its name only when the adapter mapped it to a tool the request offered.</summary>
    internal static string SelectedName(IReadOnlyCollection<string> offeredNames, ModelToolCall call) =>
        call.ToolNameError is null && offeredNames.Contains(call.ToolName, StringComparer.Ordinal) ? call.ToolName : UnofferedTool;

    private static void Record(string stage, string tool, string? reason)
    {
        BOpsTelemetry.ToolRoutingEventsTotal.Add(1, Tags(stage, tool, reason));
        var tags = new ActivityTagsCollection { ["bops.tool"] = tool };
        if (reason is not null)
        {
            tags["bops.reason"] = reason;
        }

        Activity.Current?.AddEvent(new ActivityEvent($"bops.tool_routing.{stage}", tags: tags));
    }

    private static TagList Tags(string stage, string tool, string? reason)
    {
        var tags = new TagList { { "stage", stage }, { "tool", tool } };
        if (reason is not null)
        {
            tags.Add("reason", reason);
        }

        return tags;
    }
}
