// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Memory;
using bOps.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0049 hosted behaviour: the single periodic refresh coordinator makes a recovering dependency visible without a restart and
/// never repeats a message for an unchanged state; the retention worker purges at start and by age only.
/// </summary>
public sealed class PrerequisiteBackgroundServicesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bops-background-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task TheCoordinator_RefreshesPeriodically_RecoveryNeedsNoRestart_AndUnchangedStateWritesNothingNew()
    {
        var store = new SqliteSystemMessageStore(Path.Combine(_directory, "coordinator.db"));
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        var tools = new ToolRegistry(registry);
        var skills = new SkillRegistry(registry);
        var check = new CountingCheck(PrerequisiteState.Unavailable);
        registry.Register(new PackageId("package.p"), check);
        tools.Register(new PackageId("package.p"), new Tool("sample.tool", "sample.p"));
        using var readiness = new PrerequisiteReadinessService(
            registry, new PrerequisiteTransitionRecorder(store, NodeId.Local), tools, skills, NullLogger<PrerequisiteReadinessService>.Instance);

        // The boot refresh: the tool is registered but not offered.
        await readiness.RefreshAsync();
        Assert.Null(tools.Resolve("sample.tool"));
        using var coordinator = new PrerequisiteRefreshCoordinator(
            readiness, new PrerequisiteOptions { RefreshIntervalSeconds = 1 }, TimeProvider.System, NullLogger<PrerequisiteRefreshCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => check.Calls >= 3);
            Assert.Null(tools.Resolve("sample.tool"));
            Assert.Single((await store.QueryAsync(new SystemMessageQuery())).Items);

            check.State = PrerequisiteState.Available;
            await WaitUntilAsync(() => tools.Resolve("sample.tool") is not null);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }

        var messages = (await store.QueryAsync(new SystemMessageQuery())).Items;
        Assert.Equal([SystemMessageSeverity.Information, SystemMessageSeverity.Warning], messages.Select(m => m.Severity).Order());
        var callsAfterStop = check.Calls;
        await Task.Delay(1500);
        Assert.Equal(callsAfterStop, check.Calls);
    }

    [Fact]
    public async Task RunCycle_ConvertsAFailureIntoALogLine_NeverAnException()
    {
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        using var readiness = new PrerequisiteReadinessService(
            registry,
            new PrerequisiteTransitionRecorder(new SqliteSystemMessageStore(Path.Combine(_directory, "cycle.db")), NodeId.Local),
            new ToolRegistry(registry),
            new SkillRegistry(registry),
            NullLogger<PrerequisiteReadinessService>.Instance);
        readiness.Dispose(); // a disposed gate makes the cycle throw ObjectDisposedException
        using var coordinator = new PrerequisiteRefreshCoordinator(
            readiness, new PrerequisiteOptions(), TimeProvider.System, NullLogger<PrerequisiteRefreshCoordinator>.Instance);

        await coordinator.RunCycleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Retention_PurgesOnlyWhatIsOlderThanTheConfiguredPeriod()
    {
        var store = new SqliteSystemMessageStore(Path.Combine(_directory, "retention.db"));
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        await store.AppendAsync(Message(now.AddDays(-91), "too old"));
        await store.AppendAsync(Message(now.AddDays(-89), "kept"));
        await store.AppendAsync(Message(now.AddMinutes(-1), "fresh"));
        var worker = new SystemMessageRetentionWorker(
            store, new SystemMessageOptions(), new FixedClock(now), NullLogger<SystemMessageRetentionWorker>.Instance);

        await worker.PurgeAsync(CancellationToken.None);

        Assert.Equal(["fresh", "kept"], (await store.QueryAsync(new SystemMessageQuery())).Items.Select(m => m.Message));
        Assert.Equal(TimeSpan.FromDays(90), new SystemMessageOptions().Retention);
        worker.Dispose();
    }

    [Fact]
    public async Task Retention_RunsOnceAtStartup_NotOnEveryInsertion()
    {
        var store = new SqliteSystemMessageStore(Path.Combine(_directory, "startup.db"));
        var old = Message(DateTimeOffset.UtcNow.AddDays(-120), "expired");
        await store.AppendAsync(old);
        using var worker = new SystemMessageRetentionWorker(
            store, new SystemMessageOptions { RetentionIntervalMinutes = 60 }, TimeProvider.System, NullLogger<SystemMessageRetentionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => store.QueryAsync(new SystemMessageQuery()).GetAwaiter().GetResult().Items.Count == 0);

        // An expired message inserted afterwards is not purged by the insertion; only the next scheduled run would remove it.
        await store.AppendAsync(Message(DateTimeOffset.UtcNow.AddDays(-120), "expired again"));
        await Task.Delay(300);
        Assert.Single((await store.QueryAsync(new SystemMessageQuery())).Items);
        await worker.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 360)]
    [InlineData(3651, 360)]
    [InlineData(90, 4)]
    [InlineData(90, 10081)]
    public void RetentionSettingsOutsideTheirBounds_FailStartup(int days, int minutes) =>
        Assert.Throws<InvalidOperationException>(() => new SystemMessageOptions { RetentionDays = days, RetentionIntervalMinutes = minutes }.Validate());

    private static SystemMessage Message(DateTimeOffset at, string text) => new()
    {
        Id = Guid.CreateVersion7(at),
        TimestampUtc = at,
        Node = NodeId.Local,
        Source = "test/seed",
        Severity = SystemMessageSeverity.Information,
        Code = "test.seeded",
        Message = text,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(50);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingCheck(PrerequisiteState state) : IPrerequisiteCheck
    {
        private int _calls;

        public PrerequisiteState State { get; set; } = state;

        public int Calls => Volatile.Read(ref _calls);

        public PrerequisiteDescriptor Descriptor { get; } = new("sample.p", "Sample prerequisite", "A test prerequisite.") { Remediation = "Fix it." };

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new PrerequisiteCheckOutcome(State, State is PrerequisiteState.Available ? "available" : "unavailable", $"It is {State}."));
        }
    }

    private sealed class Tool(string name, string requires) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "A test tool.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [requires],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(null));
    }
}
