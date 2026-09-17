// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem.Tests;

public sealed class FsDeleteTreeTests : IDisposable
{
    private readonly DirectoryInfo _sandbox = Directory.CreateTempSubdirectory("bops-delete-tree-");

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
    public async Task Prepare_DeduplicatesOverlappingRoots_AndPagesExactEntries()
    {
        var nested = Directory.CreateDirectory(Path.Combine(Root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "a.txt"), "abc");
        var (_, service, _) = CreateServices();

        var summary = await service.PrepareAsync(
            Request([Root, nested.FullName]),
            Context());

        Assert.Equal(DeletionManifestStatus.Ready, summary.Status);
        Assert.Single(summary.Roots);
        Assert.Equal(Path.GetFullPath(Root), summary.Roots[0]);
        Assert.Contains(summary.Warnings, warning => warning.Contains("overlapping", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, summary.EntryCount);
        Assert.Equal(64, summary.ApprovalHash.Length);

        var firstPage = await service.TryGetPageAsync(summary.Id, Context(), null, 2, null);
        Assert.NotNull(firstPage);
        Assert.Equal(2, firstPage.Entries.Count);
        Assert.NotNull(firstPage.NextCursor);
        var secondPage = await service.TryGetPageAsync(summary.Id, Context(), firstPage.NextCursor, 2, null);
        Assert.NotNull(secondPage);
        Assert.Single(secondPage.Entries);
        Assert.Null(secondPage.NextCursor);
    }

    [Fact]
    public async Task DeleteTree_ConsumesOneApprovedManifest_ThenVerifiesEveryEntryAbsent()
    {
        Directory.CreateDirectory(Path.Combine(Root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(Root, "nested", "a.txt"), "abc");
        var (store, service, _) = CreateServices();
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);
        var tool = new FsDeleteTreeTool(service);
        var arguments = Args(summary);

        Assert.Equal(RiskLevel.High, tool.Manifest.Risk);
        Assert.True(tool.Manifest.RequiresExplicitApproval);
        Assert.NotNull(tool.Manifest.Verification);

        var binding = await tool.BindApprovalAsync(
            arguments,
            context,
            new ApprovalDecision(true, ActorIdentity.FromOperatingSystemUser("approver"), null));
        var execution = await tool.ExecuteAsync(arguments, context);
        var verificationTool = new FsDeleteTreeVerifyTool(service);
        var verification = await verificationTool.ExecuteAsync(arguments, context);
        var outcome = await tool.EvaluateVerificationAsync(arguments, verification);

        Assert.True(binding.Succeeded, binding.ErrorMessage);
        Assert.True(execution.Succeeded, execution.ErrorMessage);
        Assert.False(Directory.Exists(Root));
        Assert.True(verification.Succeeded, verification.ErrorMessage);
        Assert.Equal(VerificationStatus.Confirmed, outcome.Status);
        var stored = await store.TryGetSummaryAsync(
            summary.Id,
            FilesystemManifestScope.FromExecutionContext(context),
            DateTimeOffset.UtcNow);
        Assert.Equal(DeletionManifestStatus.Verified, stored!.Status);
        Assert.Equal(summary.EntryCount, stored.DeletedCount);
        Assert.Equal(0, stored.FailureCount);
    }

    [Fact]
    public async Task DeleteTree_FailsClosedBeforeDeleting_WhenAnEntryWasAdded()
    {
        Directory.CreateDirectory(Root);
        var approvedFile = Path.Combine(Root, "approved.txt");
        await File.WriteAllTextAsync(approvedFile, "approved");
        var (_, service, _) = CreateServices();
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);
        var addedFile = Path.Combine(Root, "added.txt");
        await File.WriteAllTextAsync(addedFile, "not approved");
        var tool = new FsDeleteTreeTool(service);
        var arguments = Args(summary);

        Assert.True((await tool.BindApprovalAsync(
            arguments,
            context,
            new ApprovalDecision(true, ActorIdentity.FromOperatingSystemUser("approver"), null))).Succeeded);
        var execution = await tool.ExecuteAsync(arguments, context);

        Assert.False(execution.Succeeded);
        Assert.True(File.Exists(approvedFile));
        Assert.True(File.Exists(addedFile));
        var json = JsonNode.Parse(execution.ErrorMessage!)!;
        Assert.Equal(0, json["deletedCount"]!.GetValue<int>());
        Assert.Equal(1, json["failureCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ApprovalBinding_RejectsAlteredHashCrossTaskReuseAndDuplicateConsumption()
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, "a.txt"), "a");
        var (_, service, _) = CreateServices();
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);

        Assert.Equal(
            DeletionApprovalBindingResult.HashMismatch,
            await service.BindDecisionAsync(summary.Id, new string('0', 64), context, approved: true, CancellationToken.None));
        Assert.Equal(
            DeletionApprovalBindingResult.NotFound,
            await service.BindDecisionAsync(
                summary.Id,
                summary.ApprovalHash,
                context with { TaskId = Guid.NewGuid() },
                approved: true,
                CancellationToken.None));
        Assert.Equal(
            DeletionApprovalBindingResult.NotFound,
            await service.BindDecisionAsync(
                summary.Id,
                summary.ApprovalHash,
                context with { Node = new NodeId("another-node") },
                approved: true,
                CancellationToken.None));
        Assert.Equal(
            DeletionApprovalBindingResult.NotFound,
            await service.BindDecisionAsync(
                summary.Id,
                summary.ApprovalHash,
                context with { Actor = ActorIdentity.FromOperatingSystemUser("another-actor") },
                approved: true,
                CancellationToken.None));
        Assert.Equal(
            DeletionApprovalBindingResult.Bound,
            await service.BindDecisionAsync(
                summary.Id, summary.ApprovalHash, context, approved: true, CancellationToken.None));
        Assert.Equal(
            DeletionApprovalBindingResult.InvalidState,
            await service.BindDecisionAsync(
                summary.Id, summary.ApprovalHash, context, approved: true, CancellationToken.None));
    }

    [Fact]
    public async Task ApprovalBinding_ExpiresWithoutDeleting()
    {
        Directory.CreateDirectory(Root);
        var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var (_, service, _) = CreateServices(clock);
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);
        clock.Advance(TimeSpan.FromMinutes(2));

        var result = await service.BindDecisionAsync(
            summary.Id, summary.ApprovalHash, context, approved: true, CancellationToken.None);

        Assert.Equal(DeletionApprovalBindingResult.Expired, result);
        Assert.True(Directory.Exists(Root));
    }

    [SymlinkCapableFact]
    public async Task DeleteTree_DetectsSymlinkReplacement_WithoutTouchingItsTarget()
    {
        Directory.CreateDirectory(Root);
        var approvedFile = Path.Combine(Root, "approved.txt");
        await File.WriteAllTextAsync(approvedFile, "approved");
        var outside = Path.Combine(_sandbox.FullName, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside");
        var (_, service, _) = CreateServices(additionalPolicyPath: outside);
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);
        File.Delete(approvedFile);
        File.CreateSymbolicLink(approvedFile, outside);

        Assert.Equal(
            DeletionApprovalBindingResult.Bound,
            await service.BindDecisionAsync(
                summary.Id, summary.ApprovalHash, context, approved: true, CancellationToken.None));
        var result = await service.ExecuteAsync(summary.Id, summary.ApprovalHash, context);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.FailureCount);
        Assert.True(File.Exists(outside));
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteTree_FailsClosedBeforeDeleting_WhenAnEntryWasRemovedOrRenamed(bool rename)
    {
        Directory.CreateDirectory(Root);
        var approved = Path.Combine(Root, "approved.txt");
        await File.WriteAllTextAsync(approved, "approved");
        var (_, service, _) = CreateServices();
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);
        if (rename)
        {
            File.Move(approved, Path.Combine(Root, "renamed.txt"));
        }
        else
        {
            File.Delete(approved);
        }

        await ApproveAsync(service, summary, context);
        var result = await service.ExecuteAsync(summary.Id, summary.ApprovalHash, context);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.FailureCount);
        Assert.True(Directory.Exists(Root));
    }

    [Fact]
    public async Task DeleteTree_FailsClosedBeforeDeleting_WhenTheParentWasReplaced()
    {
        Directory.CreateDirectory(Root);
        var (_, service, _) = CreateServices();
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);
        Directory.Delete(Root);
        Directory.CreateDirectory(Root);
        Directory.SetLastWriteTimeUtc(Root, DateTime.UtcNow.AddMinutes(-10));

        await ApproveAsync(service, summary, context);
        var result = await service.ExecuteAsync(summary.Id, summary.ApprovalHash, context);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.FailureCount);
        Assert.True(Directory.Exists(Root));
    }

    [Fact]
    public async Task DeleteTree_RecordsExactPartialCompletion_WhenDeletionFailsAfterOneEntry()
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(Root, "b.txt"), "b");
        var (_, service, _) = CreateServices(
            beforeDelete: (attempt, _) => attempt == 1
                ? Task.FromException(new IOException("injected deletion failure"))
                : Task.CompletedTask);
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);

        await ApproveAsync(service, summary, context);
        var result = await service.ExecuteAsync(summary.Id, summary.ApprovalHash, context);
        var page = await service.TryGetPageAsync(summary.Id, context, null, 20, null);

        Assert.Equal(DeletionManifestStatus.PartiallyCompleted, result.Status);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.FailureCount);
        Assert.NotNull(page);
        Assert.Single(page.Entries, entry => entry.Outcome == "deleted");
        Assert.Single(page.Entries, entry => entry.Outcome == "failed"
            && entry.Error!.Contains("injected deletion failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteTree_CancellationAfterOneEntry_RemainsVisibleAsPartialCompletion()
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(Root, "b.txt"), "b");
        var (_, service, _) = CreateServices(
            beforeDelete: (attempt, token) => attempt == 1
                ? Task.FromCanceled(new CancellationToken(canceled: true))
                : Task.CompletedTask);
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);
        await ApproveAsync(service, summary, context);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => service.ExecuteAsync(summary.Id, summary.ApprovalHash, context));
        var stored = await service.TryGetSummaryAsync(summary.Id, context);

        Assert.NotNull(stored);
        Assert.Equal(DeletionManifestStatus.PartiallyCompleted, stored.Status);
        Assert.Equal(1, stored.DeletedCount);
    }

    [Fact]
    public async Task DeleteTree_HandlesALongPathWithinTheConfiguredPolicy()
    {
        var current = Root;
        while (current.Length < 280)
        {
            current = Path.Combine(current, "long-segment");
        }

        Directory.CreateDirectory(current);
        await File.WriteAllTextAsync(Path.Combine(current, "value.txt"), "value");
        var (_, service, _) = CreateServices();
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);

        await ApproveAsync(service, summary, context);
        var result = await service.ExecuteAsync(summary.Id, summary.ApprovalHash, context);

        Assert.Equal(summary.EntryCount, result.DeletedCount);
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task Prepare_PreservesCaseDistinctEntries_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, "Case.txt"), "upper");
        await File.WriteAllTextAsync(Path.Combine(Root, "case.txt"), "lower");
        var (_, service, _) = CreateServices();
        var summary = await service.PrepareAsync(Request([Root]), Context());

        Assert.Equal(2, summary.FileCount);
        Assert.Equal(3, summary.EntryCount);
    }

    [Fact]
    public async Task DeleteTree_ReportsLockedFileAsPartial_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "locked.txt");
        await File.WriteAllTextAsync(path, "locked");
        var (_, service, _) = CreateServices();
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);
        await ApproveAsync(service, summary, context);
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await service.ExecuteAsync(summary.Id, summary.ApprovalHash, context);

        Assert.Equal(DeletionManifestStatus.PartiallyCompleted, result.Status);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.FailureCount);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DeleteTree_ReportsPermissionDeniedAsPartial_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "protected.txt");
        await File.WriteAllTextAsync(path, "protected");
        var (_, service, _) = CreateServices();
        var context = Context();
        var summary = await service.PrepareAsync(Request([Root]), context);
        await ApproveAsync(service, summary, context);
        var originalMode = File.GetUnixFileMode(Root);
        try
        {
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var result = await service.ExecuteAsync(summary.Id, summary.ApprovalHash, context);

            Assert.Equal(DeletionManifestStatus.PartiallyCompleted, result.Status);
            Assert.Equal(0, result.DeletedCount);
            Assert.Equal(1, result.FailureCount);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.SetUnixFileMode(Root, originalMode);
        }
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task PrepareAndPage_TenThousandFiles_WithoutReturningOneUnboundedList()
    {
        Directory.CreateDirectory(Root);
        for (var index = 0; index < 10_000; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(Root, $"f-{index:D5}.txt"), string.Empty);
        }

        var (_, service, _) = CreateServices();
        var summary = await service.PrepareAsync(Request([Root]) with
        {
            MaxEntries = 10_001,
            MaxDuration = TimeSpan.FromMinutes(2),
        }, Context());
        var page = await service.TryGetPageAsync(summary.Id, Context(), null, 200, null);

        Assert.Equal(10_001, summary.EntryCount);
        Assert.NotNull(page);
        Assert.Equal(200, page.Entries.Count);
        Assert.NotNull(page.NextCursor);
    }

    private (SqliteDeletionManifestStore Store, FilesystemDeletionService Service, FilesystemInventoryOptions Options) CreateServices(
        TimeProvider? timeProvider = null,
        string? additionalPolicyPath = null,
        Func<int, CancellationToken, Task>? beforeDelete = null)
    {
        var options = new FilesystemInventoryOptions
        {
            ManifestStorePath = StorePath,
            DefaultMaxEntries = 20_000,
            MaximumEntries = 20_000,
            DefaultDuration = TimeSpan.FromSeconds(30),
            MaximumDuration = TimeSpan.FromMinutes(2),
            DeletionApprovalWindow = TimeSpan.FromMinutes(1),
        };
        var readPatterns = new List<string> { Path.Combine(Root, "**") };
        if (additionalPolicyPath is not null)
        {
            readPatterns.Add(additionalPolicyPath);
        }

        var policy = new FilesystemPathPolicy(readPatterns, readPatterns);
        var inventoryStore = new SqliteFilesystemManifestStore(StorePath);
        var inventory = new FilesystemInventoryService(policy, options, inventoryStore, timeProvider ?? TimeProvider.System);
        var deletionStore = new SqliteDeletionManifestStore(StorePath);
        var service = beforeDelete is null
            ? new FilesystemDeletionService(
                policy,
                options,
                inventory,
                inventoryStore,
                deletionStore,
                timeProvider)
            : new FilesystemDeletionService(
                policy,
                options,
                inventory,
                inventoryStore,
                deletionStore,
                timeProvider,
                beforeDelete);
        return (deletionStore, service, options);
    }

    private static DeletionManifestRequest Request(IReadOnlyList<string> roots) => new(
        roots,
        MaxDepth: 32,
        MaxEntries: 20_000,
        MaxDuration: TimeSpan.FromSeconds(30));

    private static ToolExecutionContext Context() => new(
        NodeId.Local,
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        ActorIdentity.FromOperatingSystemUser("delete-test"));

    private static ToolArguments Args(DeletionManifestSummary summary) => ToolArguments.FromJson(new JsonObject
    {
        ["manifestId"] = summary.Id,
        ["approvalHash"] = summary.ApprovalHash,
    });

    private static async Task ApproveAsync(
        FilesystemDeletionService service,
        DeletionManifestSummary summary,
        ToolExecutionContext context) =>
        Assert.Equal(
            DeletionApprovalBindingResult.Bound,
            await service.BindDecisionAsync(
                summary.Id, summary.ApprovalHash, context, approved: true, CancellationToken.None));

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            _now += duration;
            _timestamp += duration.Ticks;
        }
    }
}
