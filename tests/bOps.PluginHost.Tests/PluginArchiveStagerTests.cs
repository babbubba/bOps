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
