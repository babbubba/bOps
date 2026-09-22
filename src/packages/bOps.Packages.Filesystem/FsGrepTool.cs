// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

public sealed class FsGrepTool(FilesystemPathPolicy pathPolicy) : ITool
{
    private const int DefaultMaxMatches = 200;
    private const int HardMaxMatches = 2000;
    private const int DefaultMaxOutputBytes = 32768;
    private const int HardMaxOutputBytes = 131072;
    private const int MaximumLineBytes = 2048;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.grep",
        Description = "Searches text files line by line, optionally recursively and with a bounded regular expression.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("path", ToolParameterType.Path, "The regular file or directory to search."),
            new ToolParameter("pattern", ToolParameterType.String, "Text or regular expression to find."),
            new ToolParameter("recursive", ToolParameterType.Boolean, "Search child directories when path is a directory. Defaults to false.", Required: false),
            new ToolParameter("regex", ToolParameterType.Boolean, "Interpret pattern as a regular expression. Defaults to false.", Required: false),
            new ToolParameter("caseSensitive", ToolParameterType.Boolean, "Match case exactly. Defaults to true.", Required: false),
            new ToolParameter("maxMatches", ToolParameterType.Integer, "Maximum matches, 1 through 2000. Defaults to 200.", Required: false),
            new ToolParameter("maxOutputBytes", ToolParameterType.Integer, "Maximum UTF-8 output bytes, 1 through 131072. Defaults to 32768.", Required: false),
        ],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var requestedPath = arguments.GetRequired<string>("path");
        var root = FilesystemPathPolicy.Resolve(requestedPath);
        if (!pathPolicy.AllowsRead(root))
        {
            return ToolCallResult.Failure($"Path not permitted for read access by filesystem policy: {requestedPath}");
        }

        var pattern = arguments.GetRequired<string>("pattern");
        var recursive = arguments.TryGet<bool>("recursive", out var recursiveValue) && recursiveValue;
        var regex = arguments.TryGet<bool>("regex", out var regexValue) && regexValue;
        var caseSensitive = !arguments.TryGet<bool>("caseSensitive", out var caseSensitiveValue) || caseSensitiveValue;
        var maxMatches = Bounded(arguments, "maxMatches", DefaultMaxMatches, HardMaxMatches);
        var maxOutputBytes = Bounded(arguments, "maxOutputBytes", DefaultMaxOutputBytes, HardMaxOutputBytes);
        if (maxMatches is null || maxOutputBytes is null)
        {
            return ToolCallResult.Failure("maxMatches or maxOutputBytes is outside its supported range.");
        }

        Regex? compiled = null;
        if (regex)
        {
            try
            {
                compiled = new Regex(pattern, RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                return ToolCallResult.Failure($"Invalid regular expression: {ex.Message}");
            }
        }

        if (File.Exists(root))
        {
            return await SearchFileAsync(root, compiled, pattern, caseSensitive, maxMatches.Value, maxOutputBytes.Value, ct);
        }

        if (!Directory.Exists(root))
        {
            return ToolCallResult.Failure($"'{root}' does not exist.");
        }

        if (IsLinkOrReparsePoint(root))
        {
            return ToolCallResult.Failure("fs.grep does not traverse a symlink or reparse-point directory.");
        }

        var matches = new List<string>();
        var outputBytes = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0 && matches.Count < maxMatches.Value && outputBytes < maxOutputBytes.Value)
            {
                ct.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsLinkOrReparsePoint(entry))
                    {
                        continue;
                    }

                    var resolved = FilesystemPathPolicy.Resolve(entry);
                    if (!pathPolicy.AllowsRead(resolved))
                    {
                        continue;
                    }

                    if (Directory.Exists(resolved))
                    {
                        if (recursive) pending.Push(resolved);
                        continue;
                    }

                    if (!File.Exists(resolved)) continue;
                    var result = await FindMatchesAsync(resolved, compiled, pattern, caseSensitive, maxMatches.Value - matches.Count, maxOutputBytes.Value - outputBytes, ct);
                    matches.AddRange(result.Lines);
                    outputBytes += result.Bytes;
                    if (result.Stopped) break;
                }

                if (!recursive) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolCallResult.Failure($"Could not search '{root}': {ex.Message}");
        }

        return ToolCallResult.Success(matches.Count == 0 ? "(no matches)" : string.Join('\n', matches));
    }

    private static int? Bounded(ToolArguments arguments, string name, int fallback, int maximum) =>
        !arguments.TryGet<int>(name, out var value) ? fallback : value > 0 && value <= maximum ? value : null;

    private static async Task<ToolCallResult> SearchFileAsync(string path, Regex? regex, string pattern, bool caseSensitive, int maxMatches, int maxOutputBytes, CancellationToken ct)
    {
        if (IsLinkOrReparsePoint(path)) return ToolCallResult.Failure("fs.grep does not read symlinks or reparse points.");
        var result = await FindMatchesAsync(path, regex, pattern, caseSensitive, maxMatches, maxOutputBytes, ct);
        return ToolCallResult.Success(result.Lines.Count == 0 ? "(no matches)" : string.Join('\n', result.Lines));
    }

    private static async Task<GrepFileResult> FindMatchesAsync(string path, Regex? regex, string pattern, bool caseSensitive, int remainingMatches, int remainingBytes, CancellationToken ct)
    {
        await using var probe = File.OpenRead(path);
        var initial = new byte[Math.Min(4096, (int)Math.Min(probe.Length, 4096))];
        var probed = await probe.ReadAsync(initial, ct);
        if (IsProbablyBinary(initial.AsSpan(0, probed))) return new GrepFileResult([], 0, false);
        probe.Position = 0;
        using var reader = new StreamReader(probe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var lines = new List<string>();
        var bytes = 0;
        var lineNumber = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            bool matched;
            try { matched = regex?.IsMatch(line) ?? line.Contains(pattern, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase); }
            catch (RegexMatchTimeoutException) { return new GrepFileResult(lines, bytes, true); }
            if (!matched) continue;
            var boundedLine = BoundUtf8(line, MaximumLineBytes);
            var output = JsonSerializer.Serialize(new { file = path, lineNumber, boundedLine });
            var outputLength = Encoding.UTF8.GetByteCount(output) + 1;
            if (lines.Count >= remainingMatches || bytes + outputLength > remainingBytes) return new GrepFileResult(lines, bytes, true);
            lines.Add(output);
            bytes += outputLength;
        }

        return new GrepFileResult(lines, bytes, false);
    }

    private static bool IsProbablyBinary(ReadOnlySpan<byte> bytes) => bytes.IndexOf((byte)0) >= 0;

    private static bool IsLinkOrReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string BoundUtf8(string value, int limit)
    {
        if (Encoding.UTF8.GetByteCount(value) <= limit) return value;
        var length = value.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > limit - 3) length--;
        return string.Concat(value.AsSpan(0, length), "...");
    }

    private sealed record GrepFileResult(IReadOnlyList<string> Lines, int Bytes, bool Stopped);
}
