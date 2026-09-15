namespace bOps.Packages.Filesystem;

/// <summary>
/// Matches a fully resolved, absolute path against one configured pattern. Deliberately a small,
/// hand-written subset rather than a general-purpose glob library: this check gates real
/// filesystem access (rule S11), and a security allow-list is easier to audit correctly when its
/// matching rules are three lines someone can read than when they depend on a general library's
/// full semantics (recursive-wildcard-across-separators, brace expansion, platform separator
/// handling, and so on) that this project has no need for.
///
/// Two pattern forms are supported:
/// <list type="bullet">
/// <item>An exact path: matches that path only.</item>
/// <item>A path ending in <c>/**</c> (or <c>\**</c>): matches that directory and everything
/// nested under it, at any depth. The trailing segment before the path's final separator may also
/// contain a single <c>*</c>, matching within that one segment only — never across a separator —
/// for patterns like <c>/var/log/*.log</c>.</item>
/// </list>
/// </summary>
internal static class FilesystemPathGlob
{
    public static bool IsMatch(string resolvedPath, string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(resolvedPath);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var path = Normalize(resolvedPath);
        var normalizedPattern = Normalize(pattern);

        if (normalizedPattern.EndsWith("/**", StringComparison.Ordinal))
        {
            var prefix = normalizedPattern[..^3];
            return path.Equals(prefix, comparison) || path.StartsWith(prefix + "/", comparison);
        }

        if (normalizedPattern.Contains('*', StringComparison.Ordinal))
        {
            var patternLastSlash = normalizedPattern.LastIndexOf('/');
            var patternDir = normalizedPattern[..(patternLastSlash + 1)];
            var patternSegment = normalizedPattern[(patternLastSlash + 1)..];

            var pathLastSlash = path.LastIndexOf('/');
            var pathDir = path[..(pathLastSlash + 1)];
            var pathSegment = path[(pathLastSlash + 1)..];

            return pathDir.Equals(patternDir, comparison) && MatchesSingleSegment(pathSegment, patternSegment, comparison);
        }

        return path.Equals(normalizedPattern, comparison);
    }

    private static bool MatchesSingleSegment(string segment, string pattern, StringComparison comparison)
    {
        var starIndex = pattern.IndexOf('*', StringComparison.Ordinal);
        if (starIndex < 0)
        {
            return segment.Equals(pattern, comparison);
        }

        var before = pattern[..starIndex];
        var after = pattern[(starIndex + 1)..];
        return segment.Length >= before.Length + after.Length
            && segment.StartsWith(before, comparison)
            && segment.EndsWith(after, comparison);
    }

    /// <summary>Backslashes become forward slashes and a trailing separator is dropped, so a pattern written with either style matches a resolved path on either OS.</summary>
    private static string Normalize(string value)
    {
        var withForwardSlashes = value.Replace('\\', '/');
        return withForwardSlashes.Length > 1 && withForwardSlashes.EndsWith('/')
            ? withForwardSlashes[..^1]
            : withForwardSlashes;
    }
}
