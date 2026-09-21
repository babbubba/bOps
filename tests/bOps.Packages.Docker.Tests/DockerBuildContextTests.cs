// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Tar;

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// The build context checks (ADR-0033) against real directories: what may be built from, what is refused before anything reaches
/// the daemon, and the archive that is produced. No daemon is needed.
/// </summary>
public sealed class DockerBuildContextTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("bops-docker-ctx-");
    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("bops-docker-tmp-");

    public void Dispose()
    {
        _root.Delete(recursive: true);
        _scratch.Delete(recursive: true);
    }

    // Synchronous on purpose: fixture setup, and the file-system calls are not what these async tests measure.
    private static void Write(string path, string content) => File.WriteAllText(path, content);

    private static void WriteBytes(string path, byte[] content) => File.WriteAllBytes(path, content);

    private string Allowed => Directory.CreateDirectory(Path.Combine(_root.FullName, "allowed")).FullName;

    private DockerBuildOptions Options(int maxFiles = 100, long maxBytes = 1_000_000, params string[] contexts) => new()
    {
        Contexts = contexts.Length == 0 ? [Allowed] : contexts,
        MaximumContextFiles = maxFiles,
        MaximumContextBytes = maxBytes,
    };

    private string Context(string name = "app", string dockerfile = "FROM scratch\nCOPY hello.txt /hello.txt\n")
    {
        var context = Directory.CreateDirectory(Path.Combine(Allowed, name)).FullName;
        Write(Path.Combine(context, "Dockerfile"), dockerfile);
        Write(Path.Combine(context, "hello.txt"), "hello");
        return context;
    }

    private Task<PreparedBuildContext> PrepareAsync(DockerBuildOptions options, string context, string? dockerfile = null, CancellationToken ct = default) =>
        DockerBuildContext.PrepareAsync(options, context, dockerfile, _scratch.FullName, ct);

    private int LeftoverArchives => _scratch.GetFiles("bops-docker-build-*.tar").Length;

    private static async Task<Dictionary<string, string>> ReadAsync(PreparedBuildContext prepared)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = new TarReader(prepared.Archive!, leaveOpen: true);
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            using var content = new StreamReader(entry.DataStream!);
            entries[entry.Name] = await content.ReadToEndAsync();
        }

        prepared.Archive!.Position = 0;
        return entries;
    }

    // ---- accepted ----

    [Fact]
    public async Task AContextInsideAnAllowedDirectory_IsArchivedWithItsFilesAndDockerfile()
    {
        var context = Context();
        Directory.CreateDirectory(Path.Combine(context, "src", "nested"));
        Write(Path.Combine(context, "src", "nested", "deep.txt"), "deep");
        Write(Path.Combine(context, "src", "top.txt"), "top");

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Null(prepared.Error);
        Assert.Equal("Dockerfile", prepared.Dockerfile);
        Assert.Equal(4, prepared.Files);
        Assert.Equal(("FROM scratch\nCOPY hello.txt /hello.txt\n".Length + 5 + 4 + 3), prepared.Bytes);
        Assert.Equal(0, prepared.Archive!.Position);

        var entries = await ReadAsync(prepared);
        Assert.Equal(["Dockerfile", "hello.txt", "src/nested/deep.txt", "src/top.txt"], entries.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("hello", entries["hello.txt"]);
        Assert.Equal("deep", entries["src/nested/deep.txt"]);
        Assert.Equal("top", entries["src/top.txt"]);
    }

    [Fact]
    public async Task TheAllowedDirectoryItselfCanBeTheContext_AndASubdirectoryDockerfileIsUsed()
    {
        var allowed = Allowed;
        Directory.CreateDirectory(Path.Combine(allowed, "build"));
        Write(Path.Combine(allowed, "build", "Dockerfile.prod"), "FROM scratch\n");

        await using var prepared = await PrepareAsync(Options(), allowed, "build\\Dockerfile.prod");

        Assert.Null(prepared.Error);
        Assert.Equal("build/Dockerfile.prod", prepared.Dockerfile);
        Assert.Equal(1, prepared.Files);
    }

    [Fact]
    public async Task ADisposedContextDeletesItsArchive()
    {
        var prepared = await PrepareAsync(Options(), Context());
        Assert.Equal(1, LeftoverArchives);
        Assert.Equal(prepared.Archive!.Name, Assert.Single(_scratch.GetFiles("bops-docker-build-*.tar")).FullName);

        await prepared.DisposeAsync();

        Assert.Equal(0, LeftoverArchives);
    }

    [Fact]
    public async Task FilesAreArchivedInAStableOrder()
    {
        var context = Context();
        foreach (var name in new[] { "b.txt", "a.txt", "Z.txt", "c" })
        {
            Write(Path.Combine(context, name), name);
        }

        await using var prepared = await PrepareAsync(Options(), context);
        var names = new List<string>();
        await using var reader = new TarReader(prepared.Archive!, leaveOpen: true);
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            names.Add(entry.Name);
        }

        Assert.Equal(["Dockerfile", "Z.txt", "a.txt", "b.txt", "c", "hello.txt"], names);
    }

    // ---- refused: configuration and the context path ----

    [Fact]
    public async Task WithNoConfiguredContextNothingCanBeBuilt()
    {
        var context = Context();

        await using var prepared = await PrepareAsync(new DockerBuildOptions(), context);

        Assert.Contains("Docker:Build:Contexts", prepared.Error, StringComparison.Ordinal);
        Assert.Null(prepared.Archive);
        Assert.Equal(0, LeftoverArchives);
    }

    [Fact]
    public async Task ABlankConfiguredContextAllowsNothing()
    {
        await using var prepared = await PrepareAsync(new DockerBuildOptions { Contexts = ["", "   "] }, Context());

        Assert.Contains("Docker:Build:Contexts", prepared.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("./app")]
    [InlineData("app")]
    public async Task ARelativeContextIsRefused(string context)
    {
        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains("absolute", prepared.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextWithControlCharactersOrTooLong_IsRefused()
    {
        await using var control = await PrepareAsync(Options(), Path.Combine(Allowed, "a\nb"));
        await using var tooLong = await PrepareAsync(Options(), Path.Combine(Allowed, new string('a', 1_100)));

        Assert.Contains("malformed", control.Error, StringComparison.Ordinal);
        Assert.Contains("malformed", tooLong.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextThatDoesNotExist_IsRefused()
    {
        await using var prepared = await PrepareAsync(Options(), Path.Combine(Allowed, "missing"));

        Assert.Contains("existing directory", prepared.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileIsNotAContext()
    {
        var file = Path.Combine(Allowed, "file.txt");
        Write(file, "x");

        await using var prepared = await PrepareAsync(Options(), file);

        Assert.Contains("existing directory", prepared.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextOutsideEveryAllowedDirectory_IsRefused()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root.FullName, "outside")).FullName;
        Write(Path.Combine(outside, "Dockerfile"), "FROM scratch\n");

        await using var prepared = await PrepareAsync(Options(), outside);

        Assert.Contains("not inside a directory allowed", prepared.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASiblingWhoseNameOnlyStartsWithAnAllowedDirectory_IsRefused()
    {
        var sibling = Directory.CreateDirectory(Path.Combine(_root.FullName, "allowed-evil")).FullName;
        Write(Path.Combine(sibling, "Dockerfile"), "FROM scratch\n");
        Directory.CreateDirectory(Allowed);

        await using var prepared = await PrepareAsync(Options(), sibling);

        Assert.Contains("not inside a directory allowed", prepared.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathWithDotDotThatLeavesTheAllowedDirectory_IsRefused()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root.FullName, "outside2")).FullName;
        Write(Path.Combine(outside, "Dockerfile"), "FROM scratch\n");
        var sneaky = Path.Combine(Allowed, "..", "outside2");

        await using var prepared = await PrepareAsync(Options(), sneaky);

        Assert.Contains("not inside a directory allowed", prepared.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondAllowedDirectoryIsHonoured()
    {
        var other = Directory.CreateDirectory(Path.Combine(_root.FullName, "other")).FullName;
        var context = Directory.CreateDirectory(Path.Combine(other, "svc")).FullName;
        Write(Path.Combine(context, "Dockerfile"), "FROM scratch\n");

        await using var prepared = await PrepareAsync(Options(100, 1_000_000, Allowed, other), context);

        Assert.Null(prepared.Error);
    }

    // ---- refused: the Dockerfile ----

    [Theory]
    [InlineData("../Dockerfile")]
    [InlineData("a/../../Dockerfile")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("C:\\Dockerfile")]
    [InlineData("C:Dockerfile")]
    [InlineData("./Dockerfile")]
    [InlineData("a/./Dockerfile")]
    [InlineData("a//Dockerfile")]
    [InlineData("a/")]
    [InlineData("Docker\nfile")]
    public void ADockerfilePathThatIsNotPlainlyInsideTheContextIsRefused(string dockerfile)
    {
        Assert.False(DockerBuildContext.TryNormalizeDockerfile(dockerfile, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData(null, "Dockerfile")]
    [InlineData("", "Dockerfile")]
    [InlineData("Dockerfile", "Dockerfile")]
    [InlineData("build/Dockerfile.dev", "build/Dockerfile.dev")]
    [InlineData("build\\Dockerfile.dev", "build/Dockerfile.dev")]
    [InlineData("a/b/c.Dockerfile", "a/b/c.Dockerfile")]
    public void ADockerfilePathInsideTheContextIsNormalized(string? dockerfile, string expected)
    {
        Assert.True(DockerBuildContext.TryNormalizeDockerfile(dockerfile, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ADockerfilePathIsLimitedToTwoHundredFiftySixCharacters()
    {
        Assert.True(DockerBuildContext.TryNormalizeDockerfile(new string('a', 256), out _, out _));
        Assert.False(DockerBuildContext.TryNormalizeDockerfile(new string('a', 257), out _, out var error));
        Assert.Contains("malformed", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsafeDockerfileIsRefusedBeforeTheContextIsRead()
    {
        await using var prepared = await PrepareAsync(Options(), Context(), "../Dockerfile");

        Assert.Contains("Dockerfile", prepared.Error, StringComparison.Ordinal);
        Assert.Equal(0, LeftoverArchives);
    }

    [Fact]
    public async Task AMissingDockerfileIsRefused()
    {
        var context = Context();
        File.Delete(Path.Combine(context, "Dockerfile"));

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains("does not exist in the context", prepared.Error, StringComparison.Ordinal);
        Assert.Equal(0, LeftoverArchives);
    }

    [Fact]
    public async Task ADockerfileNamedByADirectoryIsNotADockerfile()
    {
        var context = Context();
        File.Delete(Path.Combine(context, "Dockerfile"));
        Directory.CreateDirectory(Path.Combine(context, "Dockerfile"));

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains("does not exist in the context", prepared.Error, StringComparison.Ordinal);
    }

    // ---- refused: what is inside ----

    [Fact]
    public async Task ADockerignoreRefusesTheBuild_BecauseBOpsDoesNotApplyIt()
    {
        var context = Context();
        Write(Path.Combine(context, ".dockerignore"), "secrets\n");

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains(".dockerignore", prepared.Error, StringComparison.Ordinal);
        Assert.Equal(0, LeftoverArchives);
    }

    [Fact]
    public async Task ADockerignoreThatIsADirectory_RefusesTheBuildToo()
    {
        var context = Context();
        Directory.CreateDirectory(Path.Combine(context, ".dockerignore"));

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains(".dockerignore", prepared.Error, StringComparison.Ordinal);
    }

    [SymlinkCapableFact]
    public async Task ASymbolicLinkPointingOutsideTheContext_RefusesTheBuild()
    {
        var context = Context();
        var secret = Path.Combine(_root.FullName, "secret.txt");
        Write(secret, "top secret");
        File.CreateSymbolicLink(Path.Combine(context, "leak.txt"), secret);

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains("symbolic link", prepared.Error, StringComparison.Ordinal);
        Assert.Contains("leak.txt", prepared.Error, StringComparison.Ordinal);
        Assert.Equal(0, LeftoverArchives);
    }

    [SymlinkCapableFact]
    public async Task AFileLinkInsideTheContext_RefusesTheBuildToo()
    {
        var context = Context();
        File.CreateSymbolicLink(Path.Combine(context, "alias.txt"), Path.Combine(context, "hello.txt"));

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains("alias.txt", prepared.Error, StringComparison.Ordinal);
        Assert.Equal(0, LeftoverArchives);
    }

    [SymlinkCapableFact]
    public async Task ADirectoryLinkInsideTheContext_RefusesTheBuild()
    {
        var context = Context();
        var outside = Directory.CreateDirectory(Path.Combine(_root.FullName, "outside-dir")).FullName;
        Write(Path.Combine(outside, "x.txt"), "x");
        Directory.CreateSymbolicLink(Path.Combine(context, "linked"), outside);

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains("linked", prepared.Error, StringComparison.Ordinal);
        Assert.Equal(0, LeftoverArchives);
    }

    [SymlinkCapableFact]
    public async Task ALinkNestedInASubdirectory_RefusesTheBuild()
    {
        var context = Context();
        var nested = Directory.CreateDirectory(Path.Combine(context, "a", "b")).FullName;
        File.CreateSymbolicLink(Path.Combine(nested, "deep-link"), Path.Combine(context, "hello.txt"));

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains("a/b/deep-link", prepared.Error, StringComparison.Ordinal);
    }

    [SymlinkCapableFact]
    public async Task ADockerfileThatIsALink_IsRefused()
    {
        var context = Context();
        var real = Path.Combine(_root.FullName, "real.Dockerfile");
        Write(real, "FROM scratch\n");
        File.Delete(Path.Combine(context, "Dockerfile"));
        File.CreateSymbolicLink(Path.Combine(context, "Dockerfile"), real);

        await using var prepared = await PrepareAsync(Options(), context);

        Assert.Contains("symbolic link", prepared.Error, StringComparison.Ordinal);
    }

    [SymlinkCapableFact]
    public async Task AContextReachedThroughALinkThatEscapesTheAllowedDirectory_IsRefused()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root.FullName, "outside-target")).FullName;
        Write(Path.Combine(outside, "Dockerfile"), "FROM scratch\n");
        var link = Path.Combine(Allowed, "innocent");
        Directory.CreateSymbolicLink(link, outside);

        await using var prepared = await PrepareAsync(Options(), link);

        Assert.Contains("not inside a directory allowed", prepared.Error, StringComparison.Ordinal);
    }

    [SymlinkCapableFact]
    public async Task AnAncestorLinkThatEscapesTheAllowedDirectory_IsRefused()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root.FullName, "outside-parent")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(outside, "svc")).FullName;
        Write(Path.Combine(inner, "Dockerfile"), "FROM scratch\n");
        Directory.CreateSymbolicLink(Path.Combine(Allowed, "hop"), outside);

        await using var prepared = await PrepareAsync(Options(), Path.Combine(Allowed, "hop", "svc"));

        Assert.Contains("not inside a directory allowed", prepared.Error, StringComparison.Ordinal);
    }

    [SymlinkCapableFact]
    public async Task ALinkThatResolvesInsideAnAllowedDirectory_IsAccepted_WhenItIsTheContextItself()
    {
        var real = Context("real");
        var link = Path.Combine(Allowed, "via-link");
        Directory.CreateSymbolicLink(link, real);

        await using var prepared = await PrepareAsync(Options(), link);

        Assert.Null(prepared.Error);
        Assert.Equal(2, prepared.Files);
    }

    [SymlinkCapableFact]
    public async Task AnAllowedDirectoryThatIsItselfALink_IsResolvedBeforeComparing()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root.FullName, "real-allowed")).FullName;
        var context = Directory.CreateDirectory(Path.Combine(real, "svc")).FullName;
        Write(Path.Combine(context, "Dockerfile"), "FROM scratch\n");
        var alias = Path.Combine(_root.FullName, "alias-allowed");
        Directory.CreateSymbolicLink(alias, real);

        await using var viaAlias = await PrepareAsync(Options(100, 1_000_000, alias), Path.Combine(alias, "svc"));
        await using var direct = await PrepareAsync(Options(100, 1_000_000, alias), context);

        Assert.Null(viaAlias.Error);
        Assert.Null(direct.Error);
    }

    // ---- bounds ----

    [Fact]
    public async Task TheFileLimitIsInclusive()
    {
        var context = Context();

        await using var atLimit = await PrepareAsync(Options(maxFiles: 2), context);
        await using var over = await PrepareAsync(Options(maxFiles: 1), context);

        Assert.Null(atLimit.Error);
        Assert.Equal(2, atLimit.Files);
        Assert.Contains("more than 1 files", over.Error, StringComparison.Ordinal);
        Assert.Equal(1, LeftoverArchives);
    }

    [Fact]
    public async Task TheByteLimitIsInclusive()
    {
        var context = Context();
        var total = new FileInfo(Path.Combine(context, "Dockerfile")).Length + new FileInfo(Path.Combine(context, "hello.txt")).Length;

        await using var atLimit = await PrepareAsync(Options(maxBytes: total), context);
        await using var over = await PrepareAsync(Options(maxBytes: total - 1), context);

        Assert.Null(atLimit.Error);
        Assert.Equal(total, atLimit.Bytes);
        Assert.Contains($"more than {total - 1} bytes", over.Error, StringComparison.Ordinal);
        Assert.Equal(1, LeftoverArchives);
    }

    [Fact]
    public async Task AnOversizedContextIsRefusedWhileTheArchiveIsWritten_NotAfterwards()
    {
        var context = Context();
        WriteBytes(Path.Combine(context, "big.bin"), new byte[200_000]);

        await using var prepared = await PrepareAsync(Options(maxBytes: 100_000), context);

        Assert.Contains("more than 100000 bytes", prepared.Error, StringComparison.Ordinal);
        Assert.True(prepared.Archive is null);
        Assert.Equal(0, LeftoverArchives);
    }

    // ---- cancellation ----

    [Fact]
    public async Task ACancelledPreparation_StopsAndLeavesNoArchive()
    {
        var context = Context();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrepareAsync(Options(), context, ct: cancelled.Token));

        Assert.Equal(0, LeftoverArchives);
    }

    // ---- path helpers ----

    [Fact]
    public void IsWithin_ComparesWholeSegments_NotPrefixes()
    {
        var root = Path.Combine(Path.GetTempPath(), "bops-root");

        Assert.True(DockerBuildContext.IsWithin(root, root));
        Assert.True(DockerBuildContext.IsWithin(root, root + Path.DirectorySeparatorChar));
        Assert.True(DockerBuildContext.IsWithin(root + Path.DirectorySeparatorChar, root));
        Assert.True(DockerBuildContext.IsWithin(root, Path.Combine(root, "a", "b")));
        Assert.False(DockerBuildContext.IsWithin(root, root + "-evil"));
        Assert.False(DockerBuildContext.IsWithin(root, Path.GetDirectoryName(root)!));
        Assert.False(DockerBuildContext.IsWithin(Path.Combine(root, "a"), root));
    }

    [Fact]
    public void IsWithin_FollowsThePlatformsCaseRule()
    {
        var root = Path.Combine(Path.GetTempPath(), "bops-Case");

        Assert.Equal(OperatingSystem.IsWindows(), DockerBuildContext.IsWithin(root, root.ToUpperInvariant()));
        Assert.Equal(OperatingSystem.IsWindows(), DockerBuildContext.IsWithin(root, Path.Combine(root.ToLowerInvariant(), "child")));
    }

    [Fact]
    public void ResolveRealPath_KeepsAPathThatDoesNotExist_AndNormalizesDots()
    {
        var missing = Path.Combine(Allowed, "not", "there");

        Assert.Equal(missing, DockerBuildContext.ResolveRealPath(missing));
        Assert.Equal(missing, DockerBuildContext.ResolveRealPath(Path.Combine(Allowed, "not", "x", "..", "there")));
        Assert.Equal(Allowed, DockerBuildContext.ResolveRealPath(Allowed + Path.DirectorySeparatorChar));
    }
}
