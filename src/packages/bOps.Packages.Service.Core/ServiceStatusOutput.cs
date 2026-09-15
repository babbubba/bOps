// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.Packages.Service.Core;

/// <summary>
/// Reads the normalized <c>status</c> field back from a <c>service.status</c> call's own output —
/// the same reader role <c>DockerInspectOutput.TryReadStatus</c> plays for <c>docker.inspect</c>.
/// Used by <see cref="ServiceStartToolBase"/>, <see cref="ServiceStopToolBase"/> and
/// <see cref="ServiceRestartToolBase"/> to evaluate their own verification.
/// </summary>
public static class ServiceStatusOutput
{
    /// <summary>
    /// <c>null</c> means the output could not be read as this shape at all — a verification tool
    /// that answered with something unparseable has confirmed nothing (rule S4), exactly as
    /// inconclusive as it not answering.
    /// </summary>
    public static string? TryReadStatus(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(output) is JsonObject json && json["status"] is JsonNode statusNode
                ? statusNode.GetValue<string>()
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
