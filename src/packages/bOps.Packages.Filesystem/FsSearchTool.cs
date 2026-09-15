// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Enumeration;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>
/// Searches a directory tree for entries (files and directories) whose name matches a glob
/// pattern (<c>*</c>/<c>?</c>). Read-risk. Every candidate's fully resolved path — after symlink
/// resolution — is checked against the read policy both before it is reported and before the
/// search descends into it, so a symlink inside an allowed tree cannot be used to search, or
/// report matches from, outside it (agentic/03-security-rules.md, rule S11). Results are capped
/// at <c>maxResults</c> for the same reason <c>fs.read</c> caps bytes: unbounded tool output can
/// blow the agent loop's context budget (agentic/01-architecture-rules.md, rule C3).
/// </summary>
public sealed class FsSearchTool(FilesystemPathPolicy pathPolicy) : ITool
{
    private const int DefaultMaxResults = 200;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "fs.search",
        Description = "Searches a directory tree for files and directories whose name matches a glob pattern (e.g. '*.log'), returning up to maxResults matches.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("path", ToolParameterType.Path, "The directory to search from."),
            new ToolParameter("namePattern", ToolParameterType.String, "A glob pattern matched against each entry's name, e.g. '*.log'."),
            new ToolParameter("maxResults", ToolParameterType.Integer, "Maximum number of matches to return. Defaults to 200.", Required: false),
        ],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var path = arguments.GetRequired<string>("path");
        var namePattern = arguments.GetRequired<string>("namePattern");
        var maxResults = arguments.TryGet<int>("maxResults", out var requested) && requested > 0 ? requested : DefaultMaxResults;
        var resolvedRoot = FilesystemPathPolicy.Resolve(path);

        if (!pathPolicy.AllowsRead(resolvedRoot))
        {
            return Task.FromResult(ToolCallResult.Failure($"Path not permitted for read access by filesystem policy: {path}"));
        }

        if (!Directory.Exists(resolvedRoot))
        {
            return Task.FromResult(ToolCallResult.Failure(
                File.Exists(resolvedRoot)
                    ? $"'{resolvedRoot}' is a file; fs.search only searches directories."
                    : $"'{resolvedRoot}' does not exist."));
        }

        try
        {
            var matches = new List<string>();
            var truncated = Walk(resolvedRoot, namePattern, maxResults, matches, ct);
            matches.Sort(StringComparer.Ordinal);

            if (matches.Count == 0)
            {
                return Task.FromResult(ToolCallResult.Success("(no matches)"));
            }

            var output = string.Join('\n', matches);
            if (truncated)
            {
                output = string.Format(CultureInfo.InvariantCulture, "{0}\n... [truncated at {1} matches] ...", output, maxResults);
            }

            return Task.FromResult(ToolCallResult.Success(output));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not search '{resolvedRoot}': {ex.Message}"));
        }
    }

    /// <summary>Returns whether the walk stopped early because <paramref name="maxResults"/> was reached.</summary>
    private bool Walk(string root, string namePattern, int maxResults, List<string> matches, CancellationToken ct)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visited = new HashSet<string>(comparer) { root };
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = directories.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var resolvedEntry = FilesystemPathPolicy.Resolve(entry);
                if (!pathPolicy.AllowsRead(resolvedEntry))
                {
                    continue;
                }

                if (FileSystemName.MatchesSimpleExpression(namePattern, Path.GetFileName(resolvedEntry)))
                {
                    matches.Add(resolvedEntry);
                    if (matches.Count >= maxResults)
                    {
                        return true;
                    }
                }

                if (Directory.Exists(resolvedEntry) && visited.Add(resolvedEntry))
                {
                    directories.Push(resolvedEntry);
                }
            }
        }

        return false;
    }
}
