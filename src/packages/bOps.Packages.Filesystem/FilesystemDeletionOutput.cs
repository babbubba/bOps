// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

internal static class FilesystemDeletionOutput
{
    public static string FormatSummary(DeletionManifestSummary summary)
    {
        var roots = new JsonArray(summary.Roots.Select(root => (JsonNode)root).ToArray());
        var warnings = new JsonArray(summary.Warnings.Select(warning => (JsonNode)warning).ToArray());
        return new JsonObject
        {
            ["manifestId"] = summary.Id,
            ["status"] = StatusName(summary.Status),
            ["permanent"] = true,
            ["roots"] = roots,
            ["warnings"] = warnings,
            ["approvalHash"] = summary.ApprovalHash,
            ["createdAtUtc"] = summary.CreatedAtUtc,
            ["expiresAtUtc"] = summary.ExpiresAtUtc,
            ["entryCount"] = summary.EntryCount,
            ["fileCount"] = summary.FileCount,
            ["directoryCount"] = summary.DirectoryCount,
            ["linkCount"] = summary.LinkCount,
            ["totalBytes"] = summary.TotalBytes,
            ["deletedCount"] = summary.DeletedCount,
            ["failureCount"] = summary.FailureCount,
            ["entriesReference"] = $"deletion-manifests/{summary.Id}/entries",
        }.ToJsonString();
    }

    public static string FormatExecution(DeletionExecutionResult result) => new JsonObject
    {
        ["manifestId"] = result.ManifestId,
        ["approvalHash"] = result.ApprovalHash,
        ["status"] = StatusName(result.Status),
        ["entryCount"] = result.EntryCount,
        ["totalBytes"] = result.TotalBytes,
        ["deletedCount"] = result.DeletedCount,
        ["failureCount"] = result.FailureCount,
        ["failuresReference"] = $"deletion-manifests/{result.ManifestId}/entries",
    }.ToJsonString();

    public static string FormatVerification(DeletionVerificationResult result) => new JsonObject
    {
        ["manifestId"] = result.ManifestId,
        ["approvalHash"] = result.ApprovalHash,
        ["status"] = result.Status.ToString().ToLowerInvariant(),
        ["absentCount"] = result.AbsentCount,
        ["remainingCount"] = result.RemainingCount,
        ["changedCount"] = result.ChangedCount,
        ["inaccessibleCount"] = result.InaccessibleCount,
    }.ToJsonString();

    public static string StatusName(DeletionManifestStatus status) => status switch
    {
        DeletionManifestStatus.PartiallyCompleted => "partially_completed",
        _ => status.ToString().ToLowerInvariant(),
    };
}
