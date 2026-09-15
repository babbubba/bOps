// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>
/// Reads a text file's content, capped at a configurable byte limit (default
/// <see cref="DefaultMaxBytes"/>). Read-risk. Truncation is deterministic — head-only, marked
/// explicitly — mirroring the history-truncation rule the agent loop itself applies to oversized
/// tool output (agentic/01-architecture-rules.md, rule C3), so a large file cannot blow the
/// context budget through this tool either.
/// </summary>
public sealed class FsReadTool(FilesystemPathPolicy pathPolicy) : ITool
{
    private const int DefaultMaxBytes = 65536;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.read",
        Description = "Reads a text file's content, UTF-8 decoded, truncated to maxBytes (default 65536) if larger.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("path", ToolParameterType.Path, "The file to read."),
            new ToolParameter("maxBytes", ToolParameterType.Integer, "Maximum bytes to read before truncating. Defaults to 65536.", Required: false),
        ],
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
                    ? $"'{resolvedPath}' is a directory; fs.read only reads files."
                    : $"'{resolvedPath}' does not exist.");
        }

        var maxBytes = arguments.TryGet<int>("maxBytes", out var requested) && requested > 0 ? requested : DefaultMaxBytes;

        try
        {
            await using var stream = File.OpenRead(resolvedPath);
            var toRead = (int)Math.Min(stream.Length, maxBytes);
            var buffer = new byte[toRead];
            var readSoFar = 0;
            while (readSoFar < toRead)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(readSoFar, toRead - readSoFar), ct);
                if (read == 0)
                {
                    break;
                }

                readSoFar += read;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, readSoFar);
            if (stream.Length > maxBytes)
            {
                text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}\n... [truncated; file is {1} bytes, showing the first {2}] ...",
                    text, stream.Length, maxBytes);
            }

            return ToolCallResult.Success(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return ToolCallResult.Failure($"Could not read '{resolvedPath}': {ex.Message}");
        }
    }
}
