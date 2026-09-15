// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>
/// Computes a file's SHA-256 content hash, hex-encoded, and its size in bytes. Read-risk: it
/// never writes anything. Exists so a future content-identity check (e.g. verifying <c>fs.move</c>
/// moved the same bytes) has a typed tool to call rather than reading the whole file back through
/// <c>fs.read</c>.
/// </summary>
public sealed class FsHashTool(FilesystemPathPolicy pathPolicy) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.hash",
        Description = "Computes a file's SHA-256 content hash, hex-encoded, along with its size in bytes.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("path", ToolParameterType.Path, "The file to hash.")],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var path = arguments.GetRequired<string>("path");
        var resolvedPath = FilesystemPathPolicy.Resolve(path);

        if (!pathPolicy.AllowsRead(resolvedPath))
        {
            return ToolCallResult.Failure($"Path not permitted for read access by filesystem policy: {path}");
        }

        if (!File.Exists(resolvedPath))
        {
            return ToolCallResult.Failure(
                Directory.Exists(resolvedPath)
                    ? $"'{resolvedPath}' is a directory; fs.hash only hashes files."
                    : $"'{resolvedPath}' does not exist.");
        }

        try
        {
            await using var stream = File.OpenRead(resolvedPath);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return ToolCallResult.Success(FsHashOutput.Format(resolvedPath, hash, stream.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolCallResult.Failure($"Could not hash '{resolvedPath}': {ex.Message}");
        }
    }
}
