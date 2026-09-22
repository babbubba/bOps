// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Reads the <c>/proc/&lt;pid&gt;</c> files the process tools need (ADR-0034). Two failures are
/// expected and both answer <c>null</c> rather than throwing: <c>ENOENT</c>, which means the
/// process exited between listing it and reading it, and <c>EACCES</c>, which means it belongs to
/// another user. The caller distinguishes them by whether the process directory still exists.
/// </summary>
internal static class LinuxProcReader
{
    /// <summary>Clock ticks per second. <c>sysconf(_SC_CLK_TCK)</c> is 100 on every architecture Linux ships for bOps, and the kernel's own <c>/proc</c> documentation states the same value.</summary>
    public const long ClockTicksPerSecond = 100;

    private const string ProcRoot = "/proc";

    /// <summary>Whether the process directory is still there.</summary>
    public static bool Exists(int pid) => Directory.Exists(Path.Combine(ProcRoot, pid.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Every numeric entry under <c>/proc</c>, that is every process this identity can see.</summary>
    public static IReadOnlyList<int> ListPids(int scanCeiling, out bool truncated)
    {
        truncated = false;
        var pids = new List<int>();
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(ProcRoot);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return pids;
        }

        foreach (var directory in directories)
        {
            if (pids.Count >= scanCeiling)
            {
                truncated = true;
                break;
            }

            if (int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                pids.Add(pid);
            }
        }

        return pids;
    }

    /// <summary>Parses <c>/proc/&lt;pid&gt;/stat</c>.</summary>
    public static async Task<LinuxProcStat?> ReadStatAsync(int pid, CancellationToken ct)
    {
        var text = await TryReadAsync(Path.Combine(ProcRoot, pid.ToString(CultureInfo.InvariantCulture), "stat"), ct);
        return text is null ? null : ParseStat(pid, text);
    }

    /// <summary>
    /// Parses <c>/proc/&lt;pid&gt;/stat</c>. The executable name sits in parentheses and may itself
    /// contain spaces and parentheses, so the fields are taken after the *last* <c>)</c> — splitting
    /// the whole line on spaces silently misreads any process whose name has one.
    /// </summary>
    public static LinuxProcStat? ParseStat(int pid, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var nameStart = text.IndexOf('(', StringComparison.Ordinal);
        var nameEnd = text.LastIndexOf(')');
        if (nameStart < 0 || nameEnd < nameStart)
        {
            return null;
        }

        var name = text[(nameStart + 1)..nameEnd];
        var fields = text[(nameEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // fields[0] is field 3 of the record (state), so field N is fields[N - 3].
        return new LinuxProcStat
        {
            Pid = pid,
            Name = string.IsNullOrEmpty(name) ? null : name,
            ParentPid = Integer(fields, 1),
            PageFaults = Sum(Number(fields, 7), Number(fields, 9)),
            CpuTicks = Sum(Number(fields, 11), Number(fields, 12)),
            ThreadCount = Integer(fields, 17),
            VirtualBytes = Number(fields, 20),
        };
    }

    /// <summary>Parses <c>/proc/&lt;pid&gt;/status</c>.</summary>
    public static async Task<LinuxProcStatus?> ReadStatusAsync(int pid, CancellationToken ct)
    {
        var text = await TryReadAsync(Path.Combine(ProcRoot, pid.ToString(CultureInfo.InvariantCulture), "status"), ct);
        return text is null ? null : ParseStatus(text);
    }

    /// <summary>Parses the <c>Uid</c>, memory and thread lines of <c>/proc/&lt;pid&gt;/status</c>.</summary>
    public static LinuxProcStatus ParseStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int? uid = null;
        long? virtualBytes = null;
        long? residentBytes = null;
        long? privateBytes = null;
        int? threads = null;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "Uid":
                    // "real effective saved fs" — the real uid is the one that owns the process.
                    var uids = value.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                    uid = uids.Length > 0 && int.TryParse(uids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedUid) ? parsedUid : null;
                    break;
                case "VmSize":
                    virtualBytes = Kilobytes(value);
                    break;
                case "VmRSS":
                    residentBytes = Kilobytes(value);
                    break;
                case "RssAnon":
                    privateBytes = Kilobytes(value);
                    break;
                case "Threads":
                    threads = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedThreads) ? parsedThreads : null;
                    break;
                default:
                    break;
            }
        }

        return new LinuxProcStatus
        {
            Uid = uid,
            VirtualBytes = virtualBytes,
            ResidentBytes = residentBytes,
            PrivateBytes = privateBytes,
            ThreadCount = threads,
        };
    }

    /// <summary>Reads <c>/proc/&lt;pid&gt;/cmdline</c>, whose arguments are NUL-separated.</summary>
    public static async Task<string?> ReadCommandLineAsync(int pid, CancellationToken ct)
    {
        var text = await TryReadAsync(Path.Combine(ProcRoot, pid.ToString(CultureInfo.InvariantCulture), "cmdline"), ct);
        if (text is null)
        {
            return null;
        }

        var joined = string.Join(' ', text.Split('\0', StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    /// <summary>Resolves the <c>/proc/&lt;pid&gt;/exe</c> symbolic link.</summary>
    public static string? ReadExecutablePath(int pid)
    {
        try
        {
            return File.ResolveLinkTarget(Path.Combine(ProcRoot, pid.ToString(CultureInfo.InvariantCulture), "exe"), returnFinalTarget: false)?.FullName;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <c>rchar</c> and <c>wchar</c> from <c>/proc/&lt;pid&gt;/io</c>: every byte the process
    /// moved through read and write, cached or not, which is the counter that corresponds to
    /// Windows's <c>ReadTransferCount</c>/<c>WriteTransferCount</c>. Only the process itself, its
    /// owner and a privileged identity may read this file.
    /// </summary>
    public static async Task<(long? Read, long? Write)> ReadIoAsync(int pid, CancellationToken ct)
    {
        var text = await TryReadAsync(Path.Combine(ProcRoot, pid.ToString(CultureInfo.InvariantCulture), "io"), ct);
        if (text is null)
        {
            return (null, null);
        }

        long? read = null;
        long? write = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var value = long.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : (long?)null;
            switch (line[..separator])
            {
                case "rchar":
                    read = value;
                    break;
                case "wchar":
                    write = value;
                    break;
                default:
                    break;
            }
        }

        return (read, write);
    }

    /// <summary>Counts the entries of <c>/proc/&lt;pid&gt;/fd</c>.</summary>
    public static int? CountFileDescriptors(int pid)
    {
        try
        {
            var count = 0;
            foreach (var _ in Directory.EnumerateFileSystemEntries(Path.Combine(ProcRoot, pid.ToString(CultureInfo.InvariantCulture), "fd")))
            {
                count++;
            }

            return count;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>Reads <c>/proc/&lt;pid&gt;/maps</c> as raw lines, or <c>null</c> when it cannot be read.</summary>
    public static Task<string?> ReadMapsAsync(int pid, CancellationToken ct) =>
        TryReadAsync(Path.Combine(ProcRoot, pid.ToString(CultureInfo.InvariantCulture), "maps"), ct);

    private static async Task<string?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    // FileNotFoundException and DirectoryNotFoundException (the exit race) both derive from
    // IOException; UnauthorizedAccessException is the permission gap. OperationCanceledException is
    // deliberately absent: cancellation propagates (agentic/02-coding-standards.md).
    private static bool IsExpected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    private static long? Kilobytes(string value)
    {
        var number = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return number.Length > 0 && long.TryParse(number[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed * 1024
            : null;
    }

    private static long? Number(string[] fields, int index) =>
        index < fields.Length && long.TryParse(fields[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static int? Integer(string[] fields, int index) =>
        Number(fields, index) is { } value and >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static long? Sum(long? first, long? second) => first is { } a && second is { } b ? a + b : null;
}
