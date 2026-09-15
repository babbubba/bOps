// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>Lists the immediate entries of a directory. Read-risk; never recurses.</summary>
public sealed class FsListTool(FilesystemPathPolicy pathPolicy) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.list",
        Description = "Lists the immediate entries of a directory: name, type, size in bytes, and last-write time (UTC).",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("path", ToolParameterType.Path, "The directory to list.")],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var path = arguments.GetRequired<string>("path");
        var resolvedPath = FilesystemPathPolicy.Resolve(path);

        if (!pathPolicy.AllowsRead(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Failure($"Path not permitted for read access by filesystem policy: {path}"));
        }

        if (!Directory.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Failure(
                File.Exists(resolvedPath)
                    ? $"'{resolvedPath}' is a file; fs.list only lists directories."
                    : $"'{resolvedPath}' does not exist."));
        }

        try
        {
            var entries = new DirectoryInfo(resolvedPath)
                .EnumerateFileSystemInfos()
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .Select(FormatEntry)
                .ToList();

            return Task.FromResult(ToolCallResult.Success(entries.Count == 0 ? "(empty)" : string.Join('\n', entries)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not list '{resolvedPath}': {ex.Message}"));
        }
    }

    private static string FormatEntry(FileSystemInfo entry)
    {
        var isDirectory = entry is DirectoryInfo;
        var size = isDirectory ? "-" : ((FileInfo)entry).Length.ToString(CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        builder.Append(isDirectory ? 'd' : '-').Append(' ');
        builder.Append(size).Append(' ');
        builder.Append(entry.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append(' ');
        builder.Append(entry.Name);
        return builder.ToString();
    }
}
