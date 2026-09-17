// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem.Tests;

public sealed class FsSizeTests : IDisposable
{
    private readonly DirectoryInfo _sandbox = Directory.CreateTempSubdirectory("bops-fs-size-");

    private string Root => Path.Combine(_sandbox.FullName, "target");

    private string StorePath => Path.Combine(_sandbox.FullName, "state", "manifests.db");

    public void Dispose()
    {
        if (_sandbox.Exists)
        {
            _sandbox.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FsSize_ReturnsCompleteBoundedSummary_ForNestedTree()
    {
        Directory.CreateDirectory(Path.Combine(Root, "sub"));
        Directory.CreateDirectory(Path.Combine(Root, "empty"));
        await File.WriteAllBytesAsync(Path.Combine(Root, "small.bin"), new byte[2]);
        await File.WriteAllBytesAsync(Path.Combine(Root, "sub", "large.bin"), new byte[7]);
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(Args(
            ("path", Root),
            ("topEntries", 1),
            ("maxEntries", 20),
            ("maxDepth", 10)));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal("complete", json["status"]!.GetValue<string>());
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.False(json["approvalReady"]!.GetValue<bool>());
        Assert.Equal(2, json["fileCount"]!.GetValue<int>());
        Assert.Equal(3, json["directoryCount"]!.GetValue<int>());
        Assert.Equal(9, json["totalBytes"]!.GetValue<long>());
        Assert.Single(json["topEntries"]!.AsArray());
        Assert.Equal("sub/large.bin", json["topEntries"]![0]!["path"]!.GetValue<string>());
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= 32 * 1024);
    }

    [Fact]
    public async Task FsSize_ReturnsOneFile_WhenRootIsAFile()
    {
        Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, "only.bin");
        await File.WriteAllBytesAsync(file, new byte[11]);
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(Args(("path", file)));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal(1, json["fileCount"]!.GetValue<int>());
        Assert.Equal(0, json["directoryCount"]!.GetValue<int>());
        Assert.Equal(11, json["totalBytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task ExactInventory_IsDeterministicDurableAndScopeBound()
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, "b.txt"), "bb");
        await File.WriteAllTextAsync(Path.Combine(Root, "a.txt"), "a");
        var (tool, store, _) = CreateTool();
        var context = Context();
        var arguments = Args(("path", Root), ("exact", true), ("maxEntries", 20));

        var first = await tool.ExecuteAsync(arguments, context);
        var second = await tool.ExecuteAsync(arguments, context);

        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True(second.Succeeded, second.ErrorMessage);
        var firstJson = JsonNode.Parse(first.Output!)!;
        var secondJson = JsonNode.Parse(second.Output!)!;
        Assert.True(firstJson["approvalReady"]!.GetValue<bool>());
        Assert.Equal(
            firstJson["manifest"]!["contentHash"]!.GetValue<string>(),
            secondJson["manifest"]!["contentHash"]!.GetValue<string>());
        Assert.NotEqual(
            firstJson["manifest"]!["id"]!.GetValue<string>(),
            secondJson["manifest"]!["id"]!.GetValue<string>());

        var manifestId = firstJson["manifest"]!["id"]!.GetValue<string>();
        var scope = FilesystemManifestScope.FromExecutionContext(context);
        Assert.NotNull(await store.TryGetReadyAsync(manifestId, scope, DateTimeOffset.UtcNow));
        Assert.Null(await store.TryGetReadyAsync(
            manifestId,
            scope with { ActorId = "someone-else" },
            DateTimeOffset.UtcNow));

        Assert.Equal(2, await store.CleanupExpiredAsync(DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Null(await store.TryGetReadyAsync(manifestId, scope, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ExactInventory_DoesNotReturnManifest_WhenEntryCeilingIsReached()
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(Root, "b.txt"), "b");
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(
            Args(("path", Root), ("exact", true), ("maxEntries", 2)),
            Context());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.False(json["approvalReady"]!.GetValue<bool>());
        Assert.Null(json["manifest"]);
        Assert.Contains(json["warnings"]!.AsArray(), node =>
            node!.GetValue<string>().Contains("entry count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FsSize_MarksDepthLimitedTreeAsPartial()
    {
        Directory.CreateDirectory(Path.Combine(Root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(Root, "sub", "a.txt"), "a");
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(Args(("path", Root), ("maxDepth", 1)));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.True(json["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task FsSize_Fails_WhenResolvedPathIsNotAllowed()
    {
        Directory.CreateDirectory(Root);
        var options = Options();
        var store = new SqliteFilesystemManifestStore(StorePath);
        var service = new FilesystemInventoryService(new FilesystemPathPolicy([], []), options, store);
        var tool = new FsSizeTool(service, options);

        var result = await tool.ExecuteAsync(Args(("path", Root)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Contains("policy", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FsSize_Fails_WhenRequestedLimitExceedsHostCeiling()
    {
        Directory.CreateDirectory(Root);
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(Args(("path", Root), ("maxEntries", 20_001)));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Contains("20000", result.ErrorMessage, StringComparison.Ordinal);
    }

    [SymlinkCapableFact]
    public async Task FsSize_CountsButDoesNotFollowSymlink()
    {
        var target = Directory.CreateDirectory(Path.Combine(Root, "target"));
        await File.WriteAllTextAsync(Path.Combine(target.FullName, "inside.txt"), "not traversed");
        Directory.CreateSymbolicLink(Path.Combine(Root, "link"), target.FullName);
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(Args(("path", Path.Combine(Root, "link"))));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal(1, json["linkCount"]!.GetValue<int>());
        Assert.Equal(0, json["fileCount"]!.GetValue<int>());
        Assert.Equal(0, json["directoryCount"]!.GetValue<int>());
    }

    [SymlinkCapableFact]
    public async Task FsSize_MarksSymlinkEscapeAsPartial_WithoutFollowingIt()
    {
        Directory.CreateDirectory(Root);
        var outside = Directory.CreateDirectory(Path.Combine(_sandbox.FullName, "outside"));
        await File.WriteAllTextAsync(Path.Combine(outside.FullName, "secret.txt"), "secret");
        Directory.CreateSymbolicLink(Path.Combine(Root, "escape"), outside.FullName);
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(Args(("path", Root)));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.Equal(0, json["fileCount"]!.GetValue<int>());
        Assert.Contains(json["warnings"]!.AsArray(), node =>
            node!.GetValue<string>().Contains("outside", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAsync_ObservesCancellation()
    {
        Directory.CreateDirectory(Root);
        var (_, _, service) = CreateTool();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(
            Request(Root), Context(), cts.Token));
    }

    [Fact]
    public async Task CreateAsync_MarksTimeLimitedEnumerationAsPartial()
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, "a.txt"), "a");
        var options = Options();
        var store = new SqliteFilesystemManifestStore(StorePath);
        var service = new FilesystemInventoryService(
            Policy(), options, store, new IncrementingTimeProvider(TimeSpan.FromMilliseconds(100)), entryObserved: null);

        var result = await service.CreateAsync(
            Request(Root) with { MaxDuration = TimeSpan.FromMilliseconds(50) }, Context());

        Assert.False(result.Complete);
        Assert.Contains(result.Warnings, warning => warning.Contains("duration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExactInventory_DetectsConcurrentFileMutation()
    {
        Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, "a.txt");
        await File.WriteAllTextAsync(file, "before");
        var options = Options();
        var store = new SqliteFilesystemManifestStore(StorePath);
        var mutated = false;
        var service = new FilesystemInventoryService(Policy(), options, store, TimeProvider.System, count =>
        {
            if (count == 2 && !mutated)
            {
                mutated = true;
                File.AppendAllText(file, "-after");
            }
        });

        var result = await service.CreateAsync(Request(Root) with { Exact = true }, Context());

        Assert.False(result.Complete);
        Assert.Null(result.Manifest);
        Assert.Contains(result.Warnings, warning => warning.Contains("changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Totals_DetectsIntegerOverflow()
    {
        Assert.False(InventoryTotals.TryAddBytes(long.MaxValue, 1, out var total));
        Assert.Equal(long.MaxValue, total);
    }

    [LinuxOnlyFact]
    [Trait("Platform", "Linux")]
    public async Task FsSize_MarksInaccessibleDirectoryAsPartial_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var blocked = Directory.CreateDirectory(Path.Combine(Root, "blocked"));
        await File.WriteAllTextAsync(Path.Combine(blocked.FullName, "secret.txt"), "secret");
        File.SetUnixFileMode(blocked.FullName, UnixFileMode.None);
        try
        {
            var (tool, _, _) = CreateTool();
            var result = await tool.ExecuteAsync(Args(("path", Root)));

            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.Equal("partial", JsonNode.Parse(result.Output!)!["status"]!.GetValue<string>());
        }
        finally
        {
            File.SetUnixFileMode(blocked.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task FsSize_StreamsTenThousandFiles_WithBoundedResponse()
    {
        Directory.CreateDirectory(Root);
        for (var index = 0; index < 10_000; index++)
        {
            using var stream = File.Create(Path.Combine(Root, $"f-{index:D5}.txt"));
        }

        var (tool, _, _) = CreateTool();
        var result = await tool.ExecuteAsync(Args(
            ("path", Root),
            ("maxEntries", 10_001),
            ("topEntries", 5),
            ("maxDurationMilliseconds", 120_000)));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Equal(10_000, json["fileCount"]!.GetValue<int>());
        Assert.True(json["topEntries"]!.AsArray().Count <= 5);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= 32 * 1024);
    }

    private (FsSizeTool Tool, SqliteFilesystemManifestStore Store, FilesystemInventoryService Service) CreateTool()
    {
        var options = Options();
        var store = new SqliteFilesystemManifestStore(StorePath);
        var service = new FilesystemInventoryService(Policy(), options, store);
        return (new FsSizeTool(service, options), store, service);
    }

    private FilesystemPathPolicy Policy() =>
        new([Path.Combine(Root, "**")], []);

    private FilesystemInventoryOptions Options() => new()
    {
        ManifestStorePath = StorePath,
        DefaultMaxEntries = 20_000,
        MaximumEntries = 20_000,
        DefaultDuration = TimeSpan.FromSeconds(30),
        MaximumDuration = TimeSpan.FromMinutes(2),
    };

    private static FilesystemInventoryRequest Request(string path) => new(
        path,
        MaxDepth: 32,
        MaxEntries: 20_000,
        TopEntries: 10,
        MaxDuration: TimeSpan.FromSeconds(30),
        Exact: false);

    private static ToolExecutionContext Context() => new(
        NodeId.Local,
        Guid.NewGuid(),
        ActorIdentity.FromOperatingSystemUser("inventory-test"));

    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = JsonValue.Create(value);
        }

        return ToolArguments.FromJson(json);
    }

    private sealed class IncrementingTimeProvider(TimeSpan increment) : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            Interlocked.Add(ref _timestamp, increment.Ticks);

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(Interlocked.Read(ref _timestamp));
    }
}
