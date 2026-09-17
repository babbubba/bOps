// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Packages.Filesystem;

public sealed class FsDeleteTreeVerifyTool(FilesystemDeletionService deletion) : IContextualTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.delete_tree.verify",
        Description = "Verifies every exact entry in a consumed deletion manifest without changing the filesystem.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("manifestId", ToolParameterType.String, "The opaque deletion manifest id."),
            new ToolParameter("approvalHash", ToolParameterType.String, "The exact approval hash."),
        ],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        Task.FromResult(ToolCallResult.Failure(
            "Deletion verification requires host-owned node, task and actor context."));

    public async Task<ToolCallResult> ExecuteAsync(
        ToolArguments arguments,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var result = await deletion.VerifyAsync(
                arguments.GetRequired<string>("manifestId"),
                arguments.GetRequired<string>("approvalHash"),
                context,
                ct);
            return ToolCallResult.Success(FilesystemDeletionOutput.FormatVerification(result));
        }
        catch (FilesystemDeletionException ex)
        {
            return ToolCallResult.Failure(ex.Message);
        }
        catch (SqliteException ex)
        {
            return ToolCallResult.Failure($"Could not verify the deletion manifest: {ex.Message}");
        }
    }
}
