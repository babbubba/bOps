// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Packages.Filesystem;

public sealed class FsDeleteTreePrepareTool(
    FilesystemDeletionService deletion,
    FilesystemInventoryOptions options) : IContextualTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.delete_tree.prepare",
        Description = "Builds a complete, expiring, exact manifest for permanent recursive or batch deletion. This read-only preflight does not delete anything.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("paths", ToolParameterType.PathList, "The exact file or directory roots to include. Globs are rejected."),
            new ToolParameter("maxDepth", ToolParameterType.Integer, "Maximum traversal depth. Uses the host default when omitted.", Required: false),
            new ToolParameter("maxEntries", ToolParameterType.Integer, "Maximum entries across every root. Uses the host default when omitted.", Required: false),
            new ToolParameter("maxDurationMilliseconds", ToolParameterType.Integer, "Maximum total preflight time. Uses the host default when omitted.", Required: false),
        ],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Failure(
            "Deletion preflight requires host-owned node, task and actor context."));

    public async Task<ToolCallResult> ExecuteAsync(
        ToolArguments arguments,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        var request = new DeletionManifestRequest(
            arguments.GetRequired<string[]>("paths"),
            arguments.TryGet<int>("maxDepth", out var maxDepth) ? maxDepth : options.DefaultMaxDepth,
            arguments.TryGet<int>("maxEntries", out var maxEntries) ? maxEntries : options.DefaultMaxEntries,
            arguments.TryGet<int>("maxDurationMilliseconds", out var durationMilliseconds)
                ? TimeSpan.FromMilliseconds(durationMilliseconds)
                : options.DefaultDuration);

        try
        {
            var summary = await deletion.PrepareAsync(request, context, ct);
            return ToolCallResult.Success(FilesystemDeletionOutput.FormatSummary(summary));
        }
        catch (FilesystemDeletionException ex)
        {
            return ToolCallResult.Failure(ex.Message);
        }
        catch (SqliteException ex)
        {
            return ToolCallResult.Failure($"Could not persist the deletion manifest: {ex.Message}");
        }
    }
}
