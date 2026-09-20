// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Memory.Tests;

/// <summary>
/// Exercises <see cref="SqliteDelegationStore"/> against a real SQLite file (ADR-0030 section 7): the whole run is
/// stored and replaced as one aggregate, a start is idempotent on the caller's key, and what is resumable or waiting
/// for an operator can be found by status.
/// </summary>
public sealed class SqliteDelegationStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("operator");

    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bops-delegation-tests-{Guid.NewGuid():N}.db");

    private static DelegationRun Run(
        DelegationStatus status = DelegationStatus.Running, string? key = null, ActorIdentity? actor = null, DateTimeOffset? updated = null)
    {
        var envelope = new AuthorityEnvelope(
            actor ?? Operator, Depth: 0, ["sample.skill"], ["sample.remediate"], ["host.info"], RiskLevel.High, BlastRadius.Single,
            ["local"], ["test"], new DelegationBudget(40, 200_000, Start.AddHours(1)));
        var role = new DelegationRoleRun
        {
            Agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Remediation),
            Envelope = envelope with { Depth = 1 },
            Status = DelegationRoleStatus.Running,
            Consumed = new BudgetConsumption(3, 1200),
            StartedAtUtc = Start,
        };
        var journal = new StepJournalEntry
        {
            StepIndex = 0,
            ToolName = "service.restart",
            ArgumentsHash = DelegationHasher.ComputeArgumentsHash(ToolArguments.Empty),
            IntentAtUtc = Start,
            Outcome = new StepOutcome(StepOutcomeKind.Cancelled, Start.AddSeconds(2)),
        };

        return new DelegationRun
        {
            Id = Guid.NewGuid(),
            Node = NodeId.Local,
            Actor = actor ?? Operator,
            Objective = "Find out why the service stopped and fix it.",
            IdempotencyKey = key,
            Status = status,
            RootEnvelope = envelope,
            Roles = [role],
            PlanHash = "abc123",
            Journal = [journal],
            ResumeCount = 1,
            CreatedAtUtc = Start,
            UpdatedAtUtc = updated ?? Start,
        };
    }

    [Fact]
    public async Task StartAsync_ThenLoadAsync_RoundTripsTheWholeRun_WithItsRolesAndJournal()
    {
        var store = new SqliteDelegationStore(_filePath);
        var run = Run();

        var started = await store.StartAsync(run);
        var loaded = await store.LoadAsync(run.Id);

        Assert.True(started.Created);
        Assert.NotNull(loaded);
        Assert.Equal(run.Id, loaded!.Id);
        Assert.Equal(run.Objective, loaded.Objective);
        Assert.Equal(run.Actor, loaded.Actor);
        Assert.Equal(run.RootEnvelope.Budget, loaded.RootEnvelope.Budget);
        Assert.Equal(new BudgetConsumption(3, 1200), Assert.Single(loaded.Roles).Consumed);
        var entry = Assert.Single(loaded.Journal);
        Assert.Equal(StepOutcomeKind.Cancelled, entry.Outcome!.Kind);
        Assert.Equal(run.Journal[0].ArgumentsHash, entry.ArgumentsHash);
        Assert.Equal(1, loaded.ResumeCount);
        Assert.Equal("abc123", loaded.PlanHash);
    }

    [Fact]
    public async Task StartAsync_ThenLoadAsync_KeepsTheRequestTheRunWasStartedWith_SoItCanBeResumed()
    {
        var store = new SqliteDelegationStore(_filePath);
        var input = ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["service"] = "nginx" });
        var run = Run() with
        {
            Authority = new DelegationAuthorityRequest(MaxSteps: 12, MaxTokens: 50_000),
            Remediation = new DelegationRemediationRequest("sample.skill", "sample.remediate", new CapabilityRequest(input, "local", "test", BlastRadius.Single, DryRun: true)),
        };
        await store.StartAsync(run);

        var loaded = await new SqliteDelegationStore(_filePath).LoadAsync(run.Id);

        Assert.Equal(12, loaded!.Authority!.MaxSteps);
        Assert.Equal("sample.remediate", loaded.Remediation!.CapabilityName);
        Assert.Equal("nginx", loaded.Remediation.Request.Input.GetRequired<string>("service"));
        Assert.True(loaded.Remediation.Request.DryRun);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_KeepsWhichAgentGatheredEachPieceOfEvidence()
    {
        // The setter of Evidence.Provenance is not public, so no package can stamp it; the store must still write and read it, or
        // every delegated run that gathered evidence would fail to be saved.
        var agent = AgentId.New();
        var evidence = System.Text.Json.JsonSerializer.Deserialize<Evidence>(
            "{\"Id\":\"discovery-0\",\"Kind\":0,\"Description\":\"Observed.\",\"Data\":\"cpu 91%\",\"SourceTool\":\"host.info\","
            + "\"ObservedAtUtc\":\"2026-09-18T12:00:00+00:00\",\"Provenance\":{\"DelegationId\":\"11111111-1111-1111-1111-111111111111\","
            + $"\"AgentId\":\"{agent.Value}\",\"Role\":0}}}}")!;
        Assert.NotNull(evidence.Provenance);
        var run = Run();
        var role = run.Roles[0] with { Report = new SkillReport([evidence], [], null) };
        var store = new SqliteDelegationStore(_filePath);
        await store.StartAsync(run with { Roles = [role] });

        var loaded = await new SqliteDelegationStore(_filePath).LoadAsync(run.Id);

        var kept = Assert.Single(loaded!.Roles[0].Report!.Evidence);
        Assert.Equal(agent, kept.Provenance!.AgentId);
        Assert.Equal(AgentRoleKind.Discovery, kept.Provenance.Role);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_ForAnUnknownRun()
    {
        Assert.Null(await new SqliteDelegationStore(_filePath).LoadAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task SaveAsync_ReplacesTheStoredRun_AndItsStatus()
    {
        var store = new SqliteDelegationStore(_filePath);
        var run = Run();
        await store.StartAsync(run);

        await store.SaveAsync(run with { Status = DelegationStatus.RequiresReconciliation, Journal = [] });

        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal(DelegationStatus.RequiresReconciliation, loaded!.Status);
        Assert.Empty(loaded.Journal);
        Assert.Empty(await store.ListByStatusAsync(DelegationStatus.Running));
        Assert.Single(await store.ListByStatusAsync(DelegationStatus.RequiresReconciliation));
    }

    [Fact]
    public async Task SaveAsync_ForARunThatWasNeverStarted_Throws_AndStoresNothing()
    {
        var store = new SqliteDelegationStore(_filePath);
        var run = Run();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(run));

        Assert.Null(await store.LoadAsync(run.Id));
    }

    [Fact]
    public async Task StartAsync_WithTheSameKeyForTheSameActor_ReturnsTheEarlierRun_AndWritesNothing()
    {
        var store = new SqliteDelegationStore(_filePath);
        var first = Run(key: "deploy-42");
        var second = Run(key: "deploy-42");
        await store.StartAsync(first);

        var again = await store.StartAsync(second);

        Assert.False(again.Created);
        Assert.Equal(first.Id, again.Run.Id);
        Assert.Null(await store.LoadAsync(second.Id));
    }

    [Fact]
    public async Task StartAsync_WithTheSameKeyForAnotherActor_CreatesAnotherRun()
    {
        var store = new SqliteDelegationStore(_filePath);
        var other = ActorIdentity.FromOperatingSystemUser("someone-else");
        await store.StartAsync(Run(key: "deploy-42"));

        var result = await store.StartAsync(Run(key: "deploy-42", actor: other));

        Assert.True(result.Created);
    }

    [Fact]
    public async Task StartAsync_WithNoKey_AlwaysCreatesARun()
    {
        var store = new SqliteDelegationStore(_filePath);

        var first = await store.StartAsync(Run());
        var second = await store.StartAsync(Run());

        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.NotEqual(first.Run.Id, second.Run.Id);
    }

    [Fact]
    public async Task StartAsync_ConcurrentlyWithTheSameKey_CreatesExactlyOneRun()
    {
        var store = new SqliteDelegationStore(_filePath);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => store.StartAsync(Run(key: "same-key")))));

        Assert.Equal(1, results.Count(r => r.Created));
        Assert.Single(results.Select(r => r.Run.Id).Distinct());
        Assert.Single(await store.ListRecentAsync(50));
    }

    [Fact]
    public async Task StartAsync_WithAnIdThatIsAlreadyStored_Throws_AndKeepsTheOriginal()
    {
        var store = new SqliteDelegationStore(_filePath);
        var run = Run();
        await store.StartAsync(run);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StartAsync(run with { Objective = "replaced" }));

        Assert.Equal(run.Objective, (await store.LoadAsync(run.Id))!.Objective);
    }

    [Fact]
    public async Task ListByStatusAsync_ReturnsOnlyRunsAtThatStatus_OldestFirst()
    {
        var store = new SqliteDelegationStore(_filePath);
        var newer = Run(DelegationStatus.Running, updated: Start.AddMinutes(5));
        var older = Run(DelegationStatus.Running, updated: Start.AddMinutes(1));
        await store.StartAsync(newer);
        await store.StartAsync(older);
        await store.StartAsync(Run(DelegationStatus.Completed));

        var running = await store.ListByStatusAsync(DelegationStatus.Running);

        Assert.Equal([older.Id, newer.Id], running.Select(r => r.Id));
    }

    [Fact]
    public async Task ListRecentAsync_ReturnsTheMostRecentlyWrittenFirst_UpToTheLimit()
    {
        var store = new SqliteDelegationStore(_filePath);
        var runs = Enumerable.Range(0, 4).Select(i => Run(updated: Start.AddMinutes(i))).ToList();
        foreach (var run in runs)
        {
            await store.StartAsync(run);
        }

        var recent = await store.ListRecentAsync(2);

        Assert.Equal([runs[3].Id, runs[2].Id], recent.Select(r => r.Id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListRecentAsync(0));
    }

    [Fact]
    public async Task AStoredRun_SurvivesReopeningTheStoreAgainstTheSameFile()
    {
        var run = Run(key: "k");
        await new SqliteDelegationStore(_filePath).StartAsync(run);

        var reopened = new SqliteDelegationStore(_filePath);

        Assert.Equal(run.Id, (await reopened.LoadAsync(run.Id))!.Id);
        Assert.False((await reopened.StartAsync(Run(key: "k"))).Created);
    }

    [Fact]
    public async Task ARunAndATaskCanShareOneDatabaseFile_WithoutTouchingEachOther()
    {
        var run = Run();
        await new SqliteDelegationStore(_filePath).StartAsync(run);
        var tasks = new SqliteTaskStore(_filePath);

        Assert.Empty(await tasks.ListByStatusAsync(AgentTaskStatus.Running));
        Assert.NotNull(await new SqliteDelegationStore(_filePath).LoadAsync(run.Id));
    }

    [Fact]
    public void TheFile_IsReadableByItsOwnerOnly_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _ = new SqliteDelegationStore(_filePath);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_filePath));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _filePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
