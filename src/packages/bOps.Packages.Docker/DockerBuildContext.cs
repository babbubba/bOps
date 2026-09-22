// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Tar;

namespace bOps.Packages.Docker;

/// <summary>
/// The outcome of preparing a build context: either a refusal with its reason (<see cref="Error"/>), or a context that passed every
/// check and has been written to a temporary archive ready to send to the daemon. Disposing it deletes the archive.
/// </summary>
internal sealed class PreparedBuildContext : IAsyncDisposable
{
    private PreparedBuildContext(FileStream? archive, string dockerfile, int files, long bytes, string? error)
    {
        Archive = archive;
        Dockerfile = dockerfile;
        Files = files;
        Bytes = bytes;
        Error = error;
    }

    /// <summary>Why the context was refused; <c>null</c> when it was accepted.</summary>
    public string? Error { get; }

    /// <summary>The tar archive, positioned at its start; <c>null</c> when the context was refused.</summary>
    public FileStream? Archive { get; }

    /// <summary>The Dockerfile's path inside the context, with forward slashes.</summary>
    public string Dockerfile { get; }

    /// <summary>How many files the context holds.</summary>
    public int Files { get; }

    /// <summary>How many bytes of file content the context holds.</summary>
    public long Bytes { get; }

    public static PreparedBuildContext Refused(string error) => new(null, string.Empty, 0, 0, error);

    public static PreparedBuildContext Accepted(FileStream archive, string dockerfile, int files, long bytes) =>
        new(archive, dockerfile, files, bytes, null);

    public ValueTask DisposeAsync() => Archive?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// Checks a build context and writes it to an archive (ADR-0033). The context must resolve, symbolic links followed, to one of the
/// operator's configured directories or a directory inside one. The Dockerfile is a relative path inside it. Any symbolic link or
/// reparse point anywhere in the context refuses the build, a <c>.dockerignore</c> refuses it (the daemon does not read it and
/// bOps does not apply it), and the file and byte limits are enforced while the archive is written, so an oversized context is
/// refused before anything reaches the daemon. Each entry is checked as it is added, not only once beforehand.
/// </summary>
internal static class DockerBuildContext
{
    private const int MaximumPathCharacters = 1024;
    private const int MaximumDockerfileCharacters = 256;

    /// <summary>Validates the context and Dockerfile and writes the archive. Never throws for a refusal; cancellation still propagates.</summary>
    public static async Task<PreparedBuildContext> PrepareAsync(
        DockerBuildOptions options, string context, string? dockerfile, string? temporaryDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        if (!options.IsConfigured)
        {
            return Refuse("No build context directory is configured (Docker:Build:Contexts).");
        }

        if (context.Length > MaximumPathCharacters || context.Any(char.IsControl))
        {
            return Refuse("The context path is malformed.");
        }

        if (!Path.IsPathFullyQualified(context))
        {
            return Refuse("The context must be an absolute path.");
        }

        if (!TryNormalizeDockerfile(dockerfile, out var dockerfilePath, out var dockerfileError))
        {
            return Refuse(dockerfileError!);
        }

        var root = ResolveRealPath(context);
        if (!Directory.Exists(root))
        {
            return Refuse("The context is not an existing directory.");
        }

        if (!options.Contexts.Where(allowed => !string.IsNullOrWhiteSpace(allowed)).Any(allowed => IsWithin(ResolveRealPath(allowed), root)))
        {
            return Refuse("The context is not inside a directory allowed by Docker:Build:Contexts.");
        }

        if (File.Exists(Path.Combine(root, ".dockerignore")) || Directory.Exists(Path.Combine(root, ".dockerignore")))
        {
            return Refuse("The context contains a .dockerignore, which bOps does not apply. Build from a directory that holds only what the image needs.");
        }

        var temporary = Path.Combine(temporaryDirectory ?? Path.GetTempPath(), $"bops-docker-build-{Guid.NewGuid():N}.tar");
        var archive = new FileStream(
            temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 81_920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            var written = await WriteArchiveAsync(archive, root, dockerfilePath!, options, ct);
            if (written.Error is not null)
            {
                await archive.DisposeAsync();
                return Refuse(written.Error);
            }

            archive.Position = 0;
            return PreparedBuildContext.Accepted(archive, dockerfilePath!, written.Files, written.Bytes);
        }
        catch
        {
            await archive.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A Dockerfile path inside the context: relative, no <c>..</c> or <c>.</c> segments, no control characters, forward slashes out.
    /// </summary>
    internal static bool TryNormalizeDockerfile(string? value, out string? normalized, out string? error)
    {
        normalized = null;
        var candidate = string.IsNullOrEmpty(value) ? "Dockerfile" : value;
        if (candidate.Length > MaximumDockerfileCharacters || candidate.Any(char.IsControl))
        {
            error = "The Dockerfile path is malformed.";
            return false;
        }

        if (Path.IsPathRooted(candidate) || candidate.StartsWith('/') || candidate.StartsWith('\\') || candidate.Contains(':', StringComparison.Ordinal))
        {
            error = "The Dockerfile must be a relative path inside the context.";
            return false;
        }

        var segments = candidate.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            error = "The Dockerfile path must not contain empty, '.' or '..' segments.";
            return false;
        }

        normalized = string.Join('/', segments);
        error = null;
        return true;
    }

    /// <summary>
    /// The path with every symbolic link in it followed, one segment at a time, so a link in an ancestor directory does not pass
    /// through unresolved. A part that does not exist is kept as written.
    /// </summary>
    internal static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var segments = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var resolved = root;
        foreach (var segment in segments)
        {
            var next = Path.Combine(resolved, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            var target = info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true) : null;
            resolved = target is null ? next : ResolveRealPath(target.FullName);
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or inside it (compared per the platform's case rules).</summary>
    internal static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        return string.Equals(normalizedRoot, normalizedCandidate, comparison)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static async Task<(int Files, long Bytes, string? Error)> WriteArchiveAsync(
        FileStream archive, string root, string dockerfile, DockerBuildOptions options, CancellationToken ct)
    {
        var files = 0;
        long bytes = 0;
        var dockerfileFound = false;
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        await using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                var entries = directory.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');

                    if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return (files, bytes, $"The context contains a symbolic link or reparse point ('{relative}'). bOps does not follow links in a build context.");
                    }

                    if (entry is DirectoryInfo)
                    {
                        continue;
                    }

                    files++;
                    if (files > options.MaximumContextFiles)
                    {
                        return (files, bytes, $"The context holds more than {options.MaximumContextFiles} files (Docker:Build:MaximumContextFiles).");
                    }

                    bytes += ((FileInfo)entry).Length;
                    if (bytes > options.MaximumContextBytes)
                    {
                        return (files, bytes, $"The context holds more than {options.MaximumContextBytes} bytes (Docker:Build:MaximumContextBytes).");
                    }

                    if (string.Equals(relative, dockerfile, comparison))
                    {
                        dockerfileFound = true;
                    }

                    await using var content = new FileStream(
                        entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await writer.WriteEntryAsync(
                        new PaxTarEntry(TarEntryType.RegularFile, relative)
                        {
                            DataStream = content,
                            Mode = mode,
                            ModificationTime = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero),
                        },
                        ct);
                }

                // The stack reverses order, so push in reverse to visit directories in name order.
                foreach (var subdirectory in entries.OfType<DirectoryInfo>().Reverse())
                {
                    pending.Push(subdirectory);
                }
            }
        }

        return dockerfileFound
            ? (files, bytes, null)
            : (files, bytes, $"The Dockerfile '{dockerfile}' does not exist in the context.");
    }

    private static PreparedBuildContext Refuse(string error) => PreparedBuildContext.Refused(error);
}
