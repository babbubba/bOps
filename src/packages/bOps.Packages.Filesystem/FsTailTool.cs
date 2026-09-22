// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

public sealed class FsTailTool(FilesystemPathPolicy pathPolicy) : ITool
{
    private const int DefaultLines = 100;
    private const int MaximumLines = 5000;
    private const int DefaultMaxBytes = 65536;
    private const int MaximumBytes = 1024 * 1024;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.tail",
        Description = "Reads the final text lines of a regular UTF-8 file without following it.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("path", ToolParameterType.Path, "The regular file to tail."),
            new ToolParameter("lines", ToolParameterType.Integer, "Number of final lines, 1 through 5000. Defaults to 100.", Required: false),
            new ToolParameter("maxBytes", ToolParameterType.Integer, "Maximum bytes to read backwards, 1 through 1048576. Defaults to 65536.", Required: false),
        ],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var requested = arguments.GetRequired<string>("path");
        var path = FilesystemPathPolicy.Resolve(requested);
        if (!pathPolicy.AllowsRead(path)) return ToolCallResult.Failure($"Path not permitted for read access by filesystem policy: {requested}");
        if (!File.Exists(path) || Directory.Exists(path)) return ToolCallResult.Failure($"'{path}' is not an existing regular file.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return ToolCallResult.Failure("fs.tail does not read symlinks or reparse points.");
        var lines = !arguments.TryGet<int>("lines", out var requestedLines) ? DefaultLines : requestedLines;
        var maxBytes = !arguments.TryGet<int>("maxBytes", out var requestedBytes) ? DefaultMaxBytes : requestedBytes;
        if (lines is < 1 or > MaximumLines || maxBytes is < 1 or > MaximumBytes) return ToolCallResult.Failure("lines or maxBytes is outside its supported range.");

        try
        {
            await using var stream = File.OpenRead(path);
            var totalLength = stream.Length;
            var bytesToRead = (int)Math.Min(totalLength, maxBytes);
            var truncatedFromStart = bytesToRead < totalLength;
            stream.Seek(-bytesToRead, SeekOrigin.End);
            var bytes = new byte[bytesToRead];
            var read = 0;
            while (read < bytes.Length)
            {
                var chunk = await stream.ReadAsync(bytes.AsMemory(read), ct);
                if (chunk == 0) break;
                read += chunk;
            }

            var span = bytes.AsSpan(0, read);
            if (span.IndexOf((byte)0) >= 0) return ToolCallResult.Failure("Unsupported or binary encoding; fs.tail supports UTF-8 text only.");

            var offset = 0;
            if (truncatedFromStart)
            {
                // The read window started mid-file, so the first partial "line" may also begin
                // mid multi-byte UTF-8 sequence. Drop everything up to (and including) the first
                // newline, mirroring POSIX tail's own handling of a partial first line, so a valid
                // boundary split is never misreported as invalid encoding.
                var newline = span.IndexOf((byte)'\n');
                offset = newline >= 0 ? newline + 1 : span.Length;
            }
            else if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            {
                offset = 3; // UTF-8 byte-order mark, only meaningful at the true start of the file.
            }

            string text;
            try
            {
                // Strict decoding: throwOnInvalidBytes rejects malformed sequences instead of
                // silently substituting U+FFFD, so binary or non-UTF-8 content is caught reliably.
                var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                text = strictUtf8.GetString(span[offset..]);
            }
            catch (DecoderFallbackException)
            {
                return ToolCallResult.Failure("Unsupported or binary encoding; fs.tail supports UTF-8 text only.");
            }

            var allLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var lineCount = allLines.Length > 0 && allLines[^1].Length == 0 ? allLines.Length - 1 : allLines.Length;
            var start = Math.Max(0, lineCount - lines);
            var output = string.Join('\n', allLines[start..lineCount]);
            if (truncatedFromStart) output = "... [truncated by maxBytes] ...\n" + output;
            return ToolCallResult.Success(output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolCallResult.Failure($"Could not tail '{path}': {ex.Message}");
        }
    }
}
