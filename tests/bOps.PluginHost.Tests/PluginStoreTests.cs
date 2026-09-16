// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.PluginHost.Tests;

/// <summary>
/// ADR-0020: every write is atomic (temp file + rename) so an interrupted process never leaves
/// <c>plugins.json</c> half-written, and a rejected operation never touches disk at all.
/// </summary>
public sealed class PluginStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bops-plugin-store-");
    private string StorePath => Path.Combine(_dir.FullName, "plugins.json");

    public void Dispose() => _dir.Delete(recursive: true);

    private static PluginManifest SampleManifest(string id = "acme.sample") =>
        new(1, id, "Acme", "1.0.0", "0.10.0", "Sample.dll", "Acme.Sample.SampleToolProvider", [], [], null);

    private static PluginRecord SampleRecord(string id = "acme.sample", bool enabled = false) =>
        new(id, $"/plugins/{id}", SampleManifest(id), enabled, DateTimeOffset.UtcNow);

    [Fact]
    public void List_IsEmpty_WhenNoStoreFileExistsYet()
    {
        var store = new PluginStore(StorePath);

        Assert.Empty(store.List());
    }

    [Fact]
    public void Add_ThenANewStoreInstance_SeesThePersistedRecord()
    {
        new PluginStore(StorePath).Add(SampleRecord());

        var reopened = new PluginStore(StorePath);

        var record = Assert.Single(reopened.List());
        Assert.Equal("acme.sample", record.Id);
        Assert.False(record.Enabled);
    }

    [Fact]
    public void Add_RestrictsStorePermissions_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        new PluginStore(StorePath).Add(SampleRecord());

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(StorePath));
    }

    [Fact]
    public void Add_RejectsADuplicateId()
    {
        var store = new PluginStore(StorePath);
        store.Add(SampleRecord());

        Assert.Throws<PluginOperationException>(() => store.Add(SampleRecord()));
    }

    [Fact]
    public void Add_RejectedDuplicate_LeavesTheStoreFileUnchanged()
    {
        var store = new PluginStore(StorePath);
        store.Add(SampleRecord());
        var contentBefore = File.ReadAllText(StorePath);

        Assert.ThrowsAny<Exception>(() => store.Add(SampleRecord()));

        Assert.Equal(contentBefore, File.ReadAllText(StorePath));
    }

    [Fact]
    public void SetEnabled_TogglesAndPersistsTheFlag()
    {
        var store = new PluginStore(StorePath);
        store.Add(SampleRecord(enabled: false));

        store.SetEnabled("acme.sample", true);

        Assert.True(new PluginStore(StorePath).Find("acme.sample")!.Enabled);
    }

    [Fact]
    public void SetEnabled_OnAnUnknownId_Throws()
    {
        var store = new PluginStore(StorePath);

        Assert.Throws<PluginOperationException>(() => store.SetEnabled("nonexistent", true));
    }

    [Fact]
    public void Remove_DropsTheRecord()
    {
        var store = new PluginStore(StorePath);
        store.Add(SampleRecord());

        store.Remove("acme.sample");

        Assert.Empty(new PluginStore(StorePath).List());
    }

    [Fact]
    public void Find_ReturnsNull_ForAnUnknownId()
    {
        var store = new PluginStore(StorePath);

        Assert.Null(store.Find("nonexistent"));
    }

    [Fact]
    public void Load_OfACorruptedStoreFile_ThrowsAClearError_RatherThanSilentlyForgettingInstalledPlugins()
    {
        File.WriteAllText(StorePath, "{ this is not valid json");

        var ex = Assert.Throws<PluginOperationException>(() => new PluginStore(StorePath).List());
        Assert.Contains("plugins.json", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
