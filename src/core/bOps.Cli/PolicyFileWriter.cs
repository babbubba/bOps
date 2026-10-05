// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace bOps.Cli;

/// <summary>
/// Writes a whole <c>policy.yaml</c> atomically (ADR-0044 section 10.1): the content goes to a temporary file in the same
/// directory, is flushed to disk, and is then moved into place — with a create-new move for a new file, so a file that appears in
/// between is never replaced, and with a replace that keeps the existing file's permissions for an overwrite (its ACL on Windows,
/// its mode on Unix). It never reads a file to print it.
/// </summary>
internal static class PolicyFileWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Creates <paramref name="path"/> with <paramref name="content"/>; fails, leaving anything there untouched, if it exists.</summary>
    /// <exception cref="IOException">The file exists, or the write failed.</exception>
    public static void CreateNew(string path, string content)
    {
        var temp = WriteTemporary(path, content);
        try
        {
            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="content"/> only if, immediately before the replacement, the file still
    /// holds exactly <paramref name="checkedContent"/> and <paramref name="recheck"/> still has nothing against it (ADR-0044
    /// section 10.1 condition 5). Returns <c>null</c> when replaced, otherwise why not; the file is then untouched.
    /// </summary>
    /// <param name="path">The policy file.</param>
    /// <param name="checkedContent">What the file held when the conditions were checked.</param>
    /// <param name="recheck">The conditions, re-run on what the file holds now; <c>null</c> when they hold.</param>
    /// <param name="content">The new content.</param>
    /// <param name="beforeRecheck">A test seam: runs after the temporary file is ready and before the re-check.</param>
    public static string? ReplaceIfUnchanged(string path, string checkedContent, Func<string, string?> recheck, string content, Action? beforeRecheck = null)
    {
        ArgumentNullException.ThrowIfNull(recheck);

        var temp = WriteTemporary(path, content);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // Never loosen permissions: the replacement takes the existing file's mode before it becomes visible.
                File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            }

            beforeRecheck?.Invoke();

            var now = File.ReadAllText(path, Utf8);
            if (!string.Equals(now, checkedContent, StringComparison.Ordinal))
            {
                TryDelete(temp);
                return "condition 5: the file changed after it was checked";
            }

            if (recheck(now) is { } refused)
            {
                TryDelete(temp);
                return refused;
            }

            File.Replace(temp, path, destinationBackupFileName: null);
            return null;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static string WriteTemporary(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = Utf8.GetBytes(content);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        return temp;
    }

    private static void TryDelete(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (IOException)
        {
            // A leftover temporary file is harmless and named as one; the policy file is what matters, and it is untouched.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
