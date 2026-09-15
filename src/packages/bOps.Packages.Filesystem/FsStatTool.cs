// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>
/// Reports whether a path exists and, if so, its type, size and last-write time, as a single-line
/// JSON observation (<see cref="FsStatOutput"/>). Read-risk: it never touches the filesystem
/// beyond metadata. Also the verification target <see cref="FsWriteTool"/> and
/// <see cref="FsDeleteTool"/> declare, so a missing path is reported here as a successful
/// observation of a negative fact (<c>exists: false</c>), not as <see cref="ToolOutcome.Failure"/>
/// — <see cref="ToolOutcome.Failure"/> is reserved for this tool being unable to check at all
/// (denied by the path policy, or a real I/O error), which is the distinction
/// <see cref="FsWriteTool.EvaluateVerificationAsync"/> and <see cref="FsDeleteTool.EvaluateVerificationAsync"/>
/// rely on to tell "confirmed absent" apart from "could not check" (rule S4).
/// </summary>
public sealed class FsStatTool(FilesystemPathPolicy pathPolicy) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.stat",
        Description = "Reports whether a path exists and, if so, its type, size in bytes, and last-write time (UTC).",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("path", ToolParameterType.Path, "The path to inspect.")],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var path = arguments.GetRequired<string>("path");
        var resolvedPath = FilesystemPathPolicy.Resolve(path);

        if (!pathPolicy.AllowsRead(resolvedPath) && !pathPolicy.AllowsWrite(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Failure($"Path not permitted by filesystem policy: {path}"));
        }

        try
        {
            if (Directory.Exists(resolvedPath))
            {
                var info = new DirectoryInfo(resolvedPath);
                return Task.FromResult(ToolCallResult.Success(
                    FsStatOutput.ForExisting(resolvedPath, "directory", sizeBytes: null, info.LastWriteTimeUtc)));
            }

            if (File.Exists(resolvedPath))
            {
                var info = new FileInfo(resolvedPath);
                return Task.FromResult(ToolCallResult.Success(
                    FsStatOutput.ForExisting(resolvedPath, "file", info.Length, info.LastWriteTimeUtc)));
            }

            return Task.FromResult(ToolCallResult.Success(FsStatOutput.ForMissing(resolvedPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not stat '{resolvedPath}': {ex.Message}"));
        }
    }
}
