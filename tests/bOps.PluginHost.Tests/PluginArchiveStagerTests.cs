// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;

namespace bOps.PluginHost.Tests;

public sealed class PluginArchiveStagerTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("bops-plugin-archive-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public async Task StageAsync_ExtractsOnlyACompleteStructurallyApprovedArchive()
    {
        await using var archive = CreateArchive(("bops-plugin.json", "{}"), ("lib/plugin.dll", "bytes"));
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits());

        var staged = await stager.StageAsync(archive);

        Assert.True(File.Exists(Path.Combine(staged, "bops-plugin.json")));
        Assert.Equal("bytes", await File.ReadAllTextAsync(Path.Combine(staged, "lib", "plugin.dll")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("dir\\..\\outside.txt")]
    public async Task StageAsync_RejectsTraversalAndRootedNamesBeforeAnyPluginPathIsMaterialized(string name)
    {
        await using var archive = CreateArchive((name, "unsafe"));
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits());

        var error = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => stager.StageAsync(archive));

        Assert.Equal(PluginLifecycleResultCategory.ArchiveInvalid, error.Category);
        var staging = Path.Combine(_directory.FullName, "staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public async Task StageAsync_RejectsCaseInsensitiveDuplicateEntriesBeforeExtraction()
    {
        await using var archive = CreateArchive(("lib/Plugin.dll", "first"), ("lib/plugin.dll", "second"));
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits());

        var error = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => stager.StageAsync(archive));

        Assert.Equal(PluginLifecycleResultCategory.ArchiveInvalid, error.Category);
    }

    [Theory]
    [InlineData(0xA000)] // symbolic link
    [InlineData(0x6000)] // block device
    [InlineData(0x2000)] // character device
    public async Task StageAsync_RejectsLinkAndDeviceEntries_WithoutMaterializingAnything(int unixFileType)
    {
        await using var archive = CreateRawArchive(("lib/link.dll", "../../etc/passwd"u8.ToArray(), unixFileType << 16));
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits());

        var error = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => stager.StageAsync(archive));

        Assert.Equal(PluginLifecycleResultCategory.ArchiveInvalid, error.Category);
        AssertNothingRetained();
    }

    [Theory]
    [InlineData("reserved lifecycle directory", ".lifecycle/state.json")]
    [InlineData("reserved store file", "plugins.json")]
    [InlineData("reserved store file, any case", "Plugins.JSON")]
    public async Task StageAsync_RejectsReservedHostPaths(string _, string name)
    {
        await using var archive = CreateArchive((name, "x"));
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits());

        var error = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => stager.StageAsync(archive));

        Assert.Equal(PluginLifecycleResultCategory.ArchiveInvalid, error.Category);
        AssertNothingRetained();
    }

    [Fact]
    public async Task StageAsync_RejectsTooManyEntries_AsALimitNotAnInvalidArchive()
    {
        await using var archive = CreateArchive(("a.txt", "1"), ("b.txt", "2"), ("c.txt", "3"));
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits(MaximumEntries: 2));

        var error = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => stager.StageAsync(archive));

        Assert.Equal(PluginLifecycleResultCategory.ArchiveLimitExceeded, error.Category);
        AssertNothingRetained();
    }

    [Fact]
    public async Task StageAsync_StopsReadingAnUploadThatExceedsTheCompressedLimit()
    {
        var incompressible = new byte[8 * 1024];
        System.Security.Cryptography.RandomNumberGenerator.Fill(incompressible);
        await using var archive = CreateRawArchive(("lib/plugin.dll", incompressible, 0));
        Assert.True(archive.Length > 1024);
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits(MaximumCompressedBytes: 1024));

        var error = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => stager.StageAsync(archive));

        Assert.Equal(PluginLifecycleResultCategory.ArchiveLimitExceeded, error.Category);
        AssertNothingRetained();
    }

    [Fact]
    public async Task StageAsync_RejectsAnEntryAboveTheSingleEntryLimit()
    {
        var incompressible = new byte[2048];
        System.Security.Cryptography.RandomNumberGenerator.Fill(incompressible);
        await using var archive = CreateRawArchive(("lib/plugin.dll", incompressible, 0));
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits(MaximumUncompressedBytes: 4096, MaximumEntryUncompressedBytes: 1024));

        var error = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => stager.StageAsync(archive));

        Assert.Equal(PluginLifecycleResultCategory.ArchiveLimitExceeded, error.Category);
        AssertNothingRetained();
    }

    [Fact]
    public async Task StageAsync_RejectsAnArchiveWhoseEntriesTogetherExceedTheUncompressedLimit()
    {
        var first = new byte[1000];
        var second = new byte[1000];
        System.Security.Cryptography.RandomNumberGenerator.Fill(first);
        System.Security.Cryptography.RandomNumberGenerator.Fill(second);
        await using var archive = CreateRawArchive(("a.bin", first, 0), ("b.bin", second, 0));
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits(MaximumUncompressedBytes: 1500, MaximumEntryUncompressedBytes: 1024));

        var error = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => stager.StageAsync(archive));

        Assert.Equal(PluginLifecycleResultCategory.ArchiveLimitExceeded, error.Category);
        AssertNothingRetained();
    }

    [Fact]
    public async Task StageAsync_RejectsACompressionBomb_UnderTheDefaultLimits()
    {
        // 8 MiB of zeros deflates to a few kilobytes: far inside every absolute byte limit, far outside the ratio limit.
        await using var archive = CreateRawArchive(("lib/plugin.dll", new byte[8 * 1024 * 1024], 0));
        Assert.True(archive.Length < 64 * 1024);
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits());

        var error = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => stager.StageAsync(archive));

        Assert.Equal(PluginLifecycleResultCategory.ArchiveLimitExceeded, error.Category);
        AssertNothingRetained();
    }

    [Fact]
    public async Task StageAsync_RejectsPathsAboveTheDepthAndLengthLimits()
    {
        await using var deep = CreateArchive(("a/b/c/d.txt", "x"));
        var shallow = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits(MaximumDepth: 3));
        var depthError = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => shallow.StageAsync(deep));
        Assert.Equal(PluginLifecycleResultCategory.ArchiveInvalid, depthError.Category);

        await using var lengthy = CreateArchive((new string('n', 32) + ".txt", "x"));
        var brief = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits(MaximumPathLength: 16));
        var lengthError = await Assert.ThrowsAsync<PluginArchiveValidationException>(() => brief.StageAsync(lengthy));
        Assert.Equal(PluginLifecycleResultCategory.ArchiveInvalid, lengthError.Category);
        AssertNothingRetained();
    }

    [Fact]
    public async Task StageAsync_RejectsLimitsAboveTheAdrHardCeilings_BeforeReadingTheUpload()
    {
        await using var archive = CreateArchive(("bops-plugin.json", "{}"));
        var stager = new PluginArchiveStager(_directory.FullName, new PluginArchiveLimits(MaximumEntries: PluginArchiveLimits.HardMaximumEntries + 1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stager.StageAsync(archive));
        AssertNothingRetained();
    }

    private void AssertNothingRetained()
    {
        foreach (var name in new[] { "staging", "uploads" })
        {
            var directory = Path.Combine(_directory.FullName, name);
            Assert.False(Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(), $"'{name}' retained material after a rejected archive.");
        }
    }

    private static MemoryStream CreateRawArchive(params (string Name, byte[] Content, int ExternalAttributes)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, attributes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.ExternalAttributes = attributes;
                using var output = entry.Open();
                output.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateArchive(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
