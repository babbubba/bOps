// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Filesystem;

/// <summary>Which of the two independently configured pattern lists a path is being checked against.</summary>
public enum FilesystemAccess
{
    /// <summary>Reading content or metadata: <c>fs.list</c>, <c>fs.read</c>.</summary>
    Read,

    /// <summary>Creating, overwriting or removing something: <c>fs.write</c>, <c>fs.delete</c>.</summary>
    Write,
}

/// <summary>
/// The path policy required by agentic/03-security-rules.md, rule S11: every <c>fs.*</c> tool
/// checks the fully resolved path — after symlink resolution and <c>..</c> normalization —
/// immediately before touching the filesystem, against a configured allow-list, and denies by
/// default. There is no core equivalent of this: <see cref="bOps.Abstractions.IPolicyEngine"/>
/// decides whether a call needs approval from its declared <see cref="bOps.Abstractions.RiskLevel"/>;
/// it has no notion of "which path," so a second, tool-owned check is required here (rule A1 — the
/// core never learns what a path even is).
/// </summary>
public sealed class FilesystemPathPolicy(IReadOnlyList<string> readPatterns, IReadOnlyList<string> writePatterns)
{
    /// <summary>
    /// Resolves <paramref name="requestedPath"/> to the path that will actually be touched:
    /// <see cref="Path.GetFullPath(string)"/> normalization, then following every symlink in the
    /// longest existing ancestor chain to its final target. A requested path that does not yet
    /// exist (the common case for <c>fs.write</c> creating a new file) still has its existing
    /// parent directories resolved, so writing through a symlinked directory cannot be used to
    /// land outside the policy — resolving only the leaf, or only once at the very top, would be
    /// exactly the time-of-check/time-of-use gap rule S11 exists to close.
    /// </summary>
    public static string Resolve(string requestedPath)
    {
        var full = Path.GetFullPath(requestedPath);

        if (Directory.Exists(full) || File.Exists(full))
        {
            return ResolveLinkChain(full);
        }

        var tailSegments = new List<string>();
        var ancestor = full;
        while (!Directory.Exists(ancestor) && !File.Exists(ancestor))
        {
            var parent = Path.GetDirectoryName(ancestor);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, ancestor, PathComparison))
            {
                // No ancestor exists at all (a path under a drive/root that isn't there yet).
                // Nothing left to resolve; the full-path normalization above already stands.
                return full;
            }

            tailSegments.Add(Path.GetFileName(ancestor));
            ancestor = parent;
        }

        tailSegments.Reverse();
        var resolvedAncestor = ResolveLinkChain(ancestor);
        return tailSegments.Count == 0 ? resolvedAncestor : Path.Combine([resolvedAncestor, .. tailSegments]);
    }

    /// <summary>Whether the resolved path is covered by a configured read pattern.</summary>
    public bool AllowsRead(string resolvedPath) => Matches(resolvedPath, readPatterns);

    /// <summary>Whether the resolved path is covered by a configured write pattern.</summary>
    public bool AllowsWrite(string resolvedPath) => Matches(resolvedPath, writePatterns);

    /// <summary>
    /// Resolves every symlink along <paramref name="existingPath"/>, not only a symlink at the
    /// leaf: <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> only follows a link when the
    /// entry it is called on is itself one, so a symlinked ancestor directory (e.g. resolving
    /// <c>allowed/link/data.txt</c> where <c>link</c>, not <c>data.txt</c>, is the symlink) would
    /// otherwise pass through unresolved — exactly the gap rule S11 requires closing. Walks the
    /// path one segment at a time from the root, substituting each ancestor's real target before
    /// combining the next segment onto it, mirroring POSIX <c>realpath()</c>.
    /// </summary>
    private static string ResolveLinkChain(string existingPath)
    {
        var root = Path.GetPathRoot(existingPath) ?? string.Empty;
        var segments = existingPath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var resolved = root;
        foreach (var segment in segments)
        {
            resolved = ResolveIfLink(Path.Combine(resolved, segment));
        }

        return resolved;
    }

    private static string ResolveIfLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var finalTarget = info.ResolveLinkTarget(returnFinalTarget: true);
        return finalTarget?.FullName ?? Path.GetFullPath(path);
    }

    private static bool Matches(string resolvedPath, IReadOnlyList<string> patterns) =>
        patterns.Any(pattern => FilesystemPathGlob.IsMatch(resolvedPath, pattern));

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
