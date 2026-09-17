// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Packages.Filesystem;

internal static class FilesystemInventoryOutput
{
    public static string Format(FilesystemInventoryResult result, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(result);

        var topEntries = new JsonArray(result.TopEntries.Select(entry => (JsonNode)new JsonObject
        {
            ["path"] = Bound(entry.RelativePath, 1024),
            ["sizeBytes"] = entry.SizeBytes,
        }).ToArray());
        var warnings = new JsonArray(result.Warnings.Select(warning => (JsonNode)Bound(warning, 512)).ToArray());
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["path"] = result.Path,
            ["status"] = result.Complete ? "complete" : "partial",
            ["complete"] = result.Complete,
            ["truncated"] = !result.Complete,
            ["approvalReady"] = result.Manifest is not null,
            ["fileCount"] = result.FileCount,
            ["directoryCount"] = result.DirectoryCount,
            ["linkCount"] = result.LinkCount,
            ["totalBytes"] = result.TotalBytes,
            ["elapsedMilliseconds"] = Math.Max(0, (long)result.Elapsed.TotalMilliseconds),
            ["limits"] = new JsonObject
            {
                ["maxDepth"] = result.Limits.MaxDepth,
                ["maxEntries"] = result.Limits.MaxEntries,
                ["topEntries"] = result.Limits.TopEntries,
                ["maxDurationMilliseconds"] = (long)result.Limits.MaxDuration.TotalMilliseconds,
                ["exact"] = result.Limits.Exact,
            },
            ["topEntries"] = topEntries,
            ["warnings"] = warnings,
            ["manifest"] = result.Manifest is null
                ? null
                : new JsonObject
                {
                    ["id"] = result.Manifest.Id,
                    ["contentHash"] = result.Manifest.ContentHash,
                    ["expiresAtUtc"] = result.Manifest.ExpiresAtUtc.ToString("O"),
                    ["entryCount"] = result.Manifest.EntryCount,
                },
        };

        var output = root.ToJsonString();
        while (Encoding.UTF8.GetByteCount(output) > maximumBytes && topEntries.Count > 0)
        {
            topEntries.RemoveAt(topEntries.Count - 1);
            output = root.ToJsonString();
        }

        while (Encoding.UTF8.GetByteCount(output) > maximumBytes && warnings.Count > 0)
        {
            warnings.RemoveAt(warnings.Count - 1);
            output = root.ToJsonString();
        }

        if (Encoding.UTF8.GetByteCount(output) > maximumBytes)
        {
            throw new InvalidOperationException($"Filesystem inventory metadata exceeds the configured {maximumBytes}-byte output limit.");
        }

        return output;
    }

    private static string Bound(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];
}
