// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Packages.Sys.Windows;

/// <summary>The decision of <see cref="WindowsDumpPathPolicy"/> about one requested dump path.</summary>
internal enum DumpPathVerdict
{
    /// <summary>An approved, existing, link-free kernel dump.</summary>
    Authorized,

    /// <summary>Not an approved local kernel-dump location; <see cref="DumpPathDecision.Reason"/> says why.</summary>
    Rejected,

    /// <summary>An approved location, but the file (or a directory on the way) does not exist.</summary>
    NotFound,

    /// <summary>An approved location this identity may not inspect; never evidence that the dump is absent.</summary>
    AccessDenied,
}

/// <summary>One path decision.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Path">The canonical path once it passed the lexical checks, otherwise null.</param>
/// <param name="Kind">kernel-small, kernel-full or live-kernel-report once the root matched, otherwise null.</param>
/// <param name="Reason">A stable reason code for a rejection or a limitation.</param>
internal sealed record DumpPathDecision(DumpPathVerdict Verdict, string? Path, string? Kind, string? Reason);

/// <summary>
/// Authorizes one kernel-dump path (ADR-0048 §5, rule S11). Default deny: only an absolute local drive path with a .dmp or
/// .mdmp extension, made only of ordinary file-name characters, whose canonical form lies under
/// <c>%SystemRoot%\Minidump</c> or <c>%SystemRoot%\LiveKernelReports</c> or is exactly <c>%SystemRoot%\MEMORY.DMP</c>.
/// UNC, device, long-path, relative and alternate-data-stream forms are rejected before canonicalization; after it, every
/// existing component from the drive root to the file is checked so a junction, symbolic link or other reparse point cannot
/// redirect the debugger outside the approved root. The caller re-runs the decision immediately before launching anything.
/// </summary>
internal sealed partial class WindowsDumpPathPolicy
{
    internal const string KindSmall = "kernel-small";
    internal const string KindFull = "kernel-full";
    internal const string KindLiveKernelReport = "live-kernel-report";

    private readonly string minidumpRoot;
    private readonly string liveKernelReportsRoot;
    private readonly string memoryDump;

    /// <summary>Creates the policy for the Windows directory <paramref name="systemRoot"/>.</summary>
    internal WindowsDumpPathPolicy(string systemRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(systemRoot));
        minidumpRoot = Path.Combine(root, "Minidump");
        liveKernelReportsRoot = Path.Combine(root, "LiveKernelReports");
        memoryDump = Path.Combine(root, "MEMORY.DMP");
    }

    /// <summary>The policy for this machine's <c>%SystemRoot%</c>.</summary>
    internal static WindowsDumpPathPolicy ForThisMachine() => new(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>Decides <paramref name="requested"/>.</summary>
    internal DumpPathDecision Decide(string requested)
    {
        if (string.IsNullOrEmpty(requested) || requested.Length > 260)
        {
            return Reject("length");
        }

        if (requested.Any(char.IsControl))
        {
            return Reject("control-characters");
        }

        if (requested.StartsWith(@"\\", StringComparison.Ordinal) || requested.StartsWith("//", StringComparison.Ordinal)
            || requested.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return Reject("unc-or-device-path");
        }

        if (requested.Contains('/', StringComparison.Ordinal))
        {
            return Reject("forward-slash");
        }

        if (!Path.IsPathFullyQualified(requested))
        {
            return Reject("not-absolute");
        }

        if (requested.IndexOf(':', 2) >= 0)
        {
            return Reject("alternate-data-stream");
        }

        if (!AllowedShape().IsMatch(requested))
        {
            return Reject("characters");
        }

        string full;
        try
        {
            full = Path.GetFullPath(requested);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Reject("not-canonicalizable");
        }

        var extension = Path.GetExtension(full);
        if (!extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase))
        {
            return Reject("extension");
        }

        var (kind, root) = Classify(full);
        if (kind is null)
        {
            return Reject("outside-approved-roots");
        }

        return CheckComponents(full, kind, root!);
    }

    private (string? Kind, string? Root) Classify(string full)
    {
        if (full.Equals(memoryDump, StringComparison.OrdinalIgnoreCase))
        {
            return (KindFull, Path.GetDirectoryName(memoryDump));
        }

        if (IsStrictlyUnder(full, minidumpRoot))
        {
            return (KindSmall, minidumpRoot);
        }

        return IsStrictlyUnder(full, liveKernelReportsRoot) ? (KindLiveKernelReport, liveKernelReportsRoot) : (null, null);
    }

    private static bool IsStrictlyUnder(string full, string root) =>
        full.Length > root.Length + 1
        && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && full[root.Length] == Path.DirectorySeparatorChar;

    /// <summary>Walks every component from the drive root to the file; any reparse point is a rejection.</summary>
    private static DumpPathDecision CheckComponents(string full, string kind, string root)
    {
        var drive = Path.GetPathRoot(full)!;
        var current = drive;
        var segments = full[drive.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var isFile = index == segments.Length - 1;

            // File.GetAttributes, not FileInfo.Exists: Exists answers false when the parent cannot be listed, which would turn
            // "this identity may not look" into "there is no dump" — the one misreport this tool must never make.
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new DumpPathDecision(DumpPathVerdict.NotFound, full, kind, isFile ? "file-not-found" : "directory-not-found");
            }
            catch (UnauthorizedAccessException)
            {
                return new DumpPathDecision(DumpPathVerdict.AccessDenied, full, kind, "access-denied");
            }
            catch (IOException)
            {
                return new DumpPathDecision(DumpPathVerdict.AccessDenied, full, kind, "io-error");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new DumpPathDecision(DumpPathVerdict.Rejected, null, null,
                    current.Length <= root.Length ? "approved-root-is-a-link" : "reparse-point");
            }

            if (isFile == ((attributes & FileAttributes.Directory) != 0))
            {
                return new DumpPathDecision(DumpPathVerdict.NotFound, full, kind, isFile ? "file-not-found" : "directory-not-found");
            }
        }

        return new DumpPathDecision(DumpPathVerdict.Authorized, full, kind, null);
    }

    private static DumpPathDecision Reject(string reason) => new(DumpPathVerdict.Rejected, null, null, reason);

    // A drive letter, then backslash-separated segments of letters, digits, space, dot, underscore, hyphen and parentheses.
    // No quote, semicolon, percent, caret, ampersand, pipe, angle bracket, exclamation mark, backtick or dollar can occur,
    // so the path can never be read as debugger or shell syntax even by a component that re-parses its command line.
    [GeneratedRegex(@"^[A-Za-z]:\\(?:[A-Za-z0-9 ._()\-]+\\)*[A-Za-z0-9 ._()\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedShape();
}
