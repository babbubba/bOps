// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// Reads whether a <c>process.inspect</c> call's own output reports the inspected process as
/// existing — the same reader role <c>FsStatOutput.TryReadExists</c> plays for <c>fs.stat</c>.
/// Used by <see cref="ProcessStopToolBase"/> and <see cref="ProcessKillToolBase"/> to evaluate
/// their own verification, since both declare <c>process.inspect</c> as their target and both
/// expect the same fact afterward: the process is gone.
/// </summary>
public static class ProcessInspectOutput
{
    /// <summary>
    /// <c>null</c> means the output could not be read as this shape at all — a verification tool
    /// that answered with something unparseable has confirmed nothing (rule S4), exactly as
    /// inconclusive as it not answering.
    /// </summary>
    public static bool? TryReadExists(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(output) is JsonObject json && json["exists"] is JsonNode existsNode
                ? existsNode.GetValue<bool>()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
