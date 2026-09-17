// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Packages.Filesystem;

/// <summary>Returns a bounded filesystem size summary and optionally creates an exact manifest.</summary>
public sealed class FsSizeTool(
    FilesystemInventoryService inventory,
    FilesystemInventoryOptions options) : IContextualTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.size",
        Description = "Summarizes a file or directory tree within host-enforced depth, entry, duration and output ceilings; optionally creates an exact scoped manifest.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("path", ToolParameterType.Path, "The file or directory to summarize."),
            new ToolParameter("maxDepth", ToolParameterType.Integer, "Maximum traversal depth. Uses the host default when omitted.", Required: false),
            new ToolParameter("maxEntries", ToolParameterType.Integer, "Maximum entries, including the requested root. Uses the host default when omitted.", Required: false),
            new ToolParameter("topEntries", ToolParameterType.Integer, "Number of largest files to return. Uses the host default when omitted.", Required: false),
            new ToolParameter("maxDurationMilliseconds", ToolParameterType.Integer, "Maximum collection time in milliseconds. Uses the host default when omitted.", Required: false),
            new ToolParameter("exact", ToolParameterType.Boolean, "Whether to create an exact durable manifest. Defaults to false.", Required: false),
        ],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.TryGet<bool>("exact", out var exact) && exact)
        {
            return Task.FromResult(ToolCallResult.Failure(
                "An exact filesystem inventory requires host-owned execution context."));
        }

        return ExecuteCoreAsync(arguments, executionContext: null, ct);
    }

    public Task<ToolCallResult> ExecuteAsync(
        ToolArguments arguments,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteCoreAsync(arguments, context, ct);
    }

    private async Task<ToolCallResult> ExecuteCoreAsync(
        ToolArguments arguments,
        ToolExecutionContext? executionContext,
        CancellationToken ct)
    {
        var request = new FilesystemInventoryRequest(
            arguments.GetRequired<string>("path"),
            arguments.TryGet<int>("maxDepth", out var maxDepth) ? maxDepth : options.DefaultMaxDepth,
            arguments.TryGet<int>("maxEntries", out var maxEntries) ? maxEntries : options.DefaultMaxEntries,
            arguments.TryGet<int>("topEntries", out var topEntries) ? topEntries : options.DefaultTopEntries,
            arguments.TryGet<int>("maxDurationMilliseconds", out var durationMilliseconds)
                ? TimeSpan.FromMilliseconds(durationMilliseconds)
                : options.DefaultDuration,
            arguments.TryGet<bool>("exact", out var exact) && exact);

        try
        {
            var result = await inventory.CreateAsync(request, executionContext, ct);
            return ToolCallResult.Success(FilesystemInventoryOutput.Format(result, options.MaximumOutputBytes));
        }
        catch (FilesystemInventoryException ex)
        {
            return ToolCallResult.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return ToolCallResult.Failure($"Could not inventory '{request.Path}': {ex.Message}");
        }
    }
}
