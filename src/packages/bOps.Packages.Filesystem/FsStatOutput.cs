// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace bOps.Packages.Filesystem;

/// <summary>
/// The single-line JSON shape <c>fs.stat</c> reports, and the shared reader both
/// <see cref="FsWriteTool"/> and <see cref="FsDeleteTool"/> use to evaluate their own
/// verification: <c>fs.stat</c> is each tool's declared <see cref="bOps.Abstractions.VerificationSpec"/>
/// target (agentic/01-architecture-rules.md, rule B3), and the runtime hands its
/// <see cref="bOps.Abstractions.ToolCallResult.Output"/> back as plain text — JSON is what lets
/// that text be read back reliably instead of pattern-matching a sentence.
/// </summary>
internal static class FsStatOutput
{
    public static string ForExisting(string resolvedPath, string type, long? sizeBytes, DateTimeOffset modifiedUtc)
    {
        var json = new JsonObject
        {
            ["path"] = resolvedPath,
            ["exists"] = true,
            ["type"] = type,
            ["sizeBytes"] = sizeBytes,
            ["modifiedUtc"] = modifiedUtc,
        };
        return json.ToJsonString();
    }

    public static string ForMissing(string resolvedPath)
    {
        var json = new JsonObject { ["path"] = resolvedPath, ["exists"] = false };
        return json.ToJsonString();
    }

    /// <summary>
    /// Reads whether the stat call's own output reports the path as existing.
    /// <c>null</c> means the output could not be read as this shape at all — a verification tool
    /// that answered with something unparseable has confirmed nothing (rule S4), which is exactly
    /// as inconclusive as it not answering.
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
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads whether the stat call's own output reports the path as an existing directory —
    /// <c>exists:true</c> alone is not enough evidence for <c>fs.mkdir</c>'s verification: a race
    /// could have replaced the freshly created directory with a non-directory before <c>fs.stat</c>
    /// ran, and <c>exists:true</c> would still be true for that. <c>null</c> means the output could
    /// not be read as this shape at all.
    /// </summary>
    public static bool? TryReadIsDirectory(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(output) is not JsonObject json || json["exists"] is not JsonNode existsNode)
            {
                return null;
            }

            if (!existsNode.GetValue<bool>())
            {
                return false;
            }

            return json["type"] is JsonNode typeNode && string.Equals(typeNode.GetValue<string>(), "directory", StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
