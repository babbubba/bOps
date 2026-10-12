// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world and process passes to the test, which disposes it.
#pragma warning disable CA2000

using bOps.Abstractions;
using bOps.Audit;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// KNOW-01B (ADR-0056), the durable half: a task is admitted and pinned, reads, is persisted to a real SQLite store, every runtime
/// instance is thrown away, and a new process rebuilds the task from the store and keeps reading exactly the verified snapshot it was
/// bound to, or fails closed with a cause. A pin is never an authorisation, so each refusal below comes from the authority the second
/// process holds, not from the first one's memory.
/// </summary>
public sealed class KnowledgeResumeTests
{
    private const string Goal = "fix the nginx 502";
    private const string Checklist = "nginx -t; ss -ltnp :80";
    private static readonly TenantId TenantA = KnowledgeRunnerWorld.TenantA;
    private static readonly ActorIdentity ActorA = KnowledgeRunnerWorld.ActorA;
    private static readonly ActorIdentity ActorB = KnowledgeRunnerWorld.ActorB;

    [Fact]
    public async Task ATaskIsAdmittedPinnedReadPersistedDestroyedRebuiltAndReadsTheSameVerifiedSnapshot()
    {
        using var world = new KnowledgeRunnerWorld();
        var v1 = new TestPack();
        // A newer version exists, is granted, and is what the host would bind to a NEW task now. A resume must never move to it.
        var v2 = new TestPack { Version = "2.0.0", Files = Replaced(v1, "nginx-triage/references/checklist.md", "v2 checklist: restart everything") };
        var (taskId, bindingA) = await FirstAttemptAsync(world, v1);
        var opensAfterFirstAttempt = world.Source.Opens;

        // 1-5: the first process is gone. The second one is built from nothing but the disk, the source and the stored task.
        AddPack(world, v2);
        using var second = world.Boot(
            [Grant(v1), Grant(v2)],
            bindings: new Dictionary<TenantId, PackReference> { [TenantA] = Reference(v2) });
        var stored = (await second.Store.LoadAsync(taskId))!;
        Assert.Equal(AgentTaskStatus.Failed, stored.Status);
        AssertSameBinding(bindingA, stored.KnowledgeBinding!);

        // 6: the resumed attempt reads through the same snapshot.
        var rig = second.Runner(
            [Script.Knowledge("k1", "discover"), Script.Knowledge("k2", "load", "nginx-triage"),
             Script.Knowledge("k3", "read_resource", "nginx-triage", "references/checklist.md"), Script.Final("resumed")]);
        var resumed = await rig.Runner.ResumeAsync(stored, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Equal(2, resumed.ExecutionAttempt);
        AssertSameBinding(bindingA, resumed.KnowledgeBinding!);
        Assert.Equal(bindingA.SnapshotId, resumed.KnowledgeBinding!.SnapshotId);
        var served = string.Join('\n', rig.Model.Requests[^1].History.Where(turn => turn.Provenance is not null).Select(turn => turn.Content));
        Assert.Contains(Checklist, served, StringComparison.Ordinal);
        Assert.DoesNotContain("v2 checklist", served, StringComparison.Ordinal);
        Assert.All(rig.Model.Requests[^1].History.Where(turn => turn.Provenance?.Origin == ContextOrigin.Skill),
            turn => Assert.Equal((bindingA.ManifestHash, "1.0.0", "tenant-a"), (turn.Provenance!.PackHash, turn.Provenance.PackVersion, turn.Provenance.TenantId)));

        // The audit tells the whole story: two admissions of the same content for the same task, and the reads of both attempts.
        var admissions = world.Events.OfType<KnowledgePackAdmissionAuditEvent>().ToArray();
        Assert.Equal(2, admissions.Length);
        Assert.All(admissions, admission => Assert.Equal((KnowledgeAdmissionCode.Admitted, taskId, bindingA.ManifestHash, "1.0.0"), (admission.Code, admission.TaskId, admission.ContentHash, admission.PackVersion)));
        Assert.All(world.Events.OfType<KnowledgeReadAuditEvent>(), read => Assert.Equal((bindingA.ManifestHash, "1.0.0", taskId), (read.PackHash, read.PackVersion, read.TaskId)));
        Assert.Equal(3 + 3, world.Events.OfType<KnowledgeReadAuditEvent>().Count());
        Assert.Equal(opensAfterFirstAttempt + 1, world.Source.Opens); // the version 2 pack was never opened
        Assert.Single(HashDirectories(world)); // the same content-addressed snapshot, not a second copy
        AssertAuditFileIsIntactAndFreeOfContent(world);
    }

    [Fact]
    public async Task APackRepublishedUnderTheSameVersion_IsAPinMismatch_NothingRuns_AndTheTaskStaysResumable()
    {
        using var world = new KnowledgeRunnerWorld();
        var original = new TestPack();
        var (taskId, binding) = await FirstAttemptAsync(world, original);
        var republished = new TestPack { Files = Replaced(original, "nginx-triage/references/checklist.md", "rm -rf /etc/nginx") };
        var forged = republished.Build();
        world.Source.Add(TenantA, original.PackId, original.Version, () => forged);

        using var second = world.Boot([Grant(original)]);
        var (state, rig) = await ResumeAsync(second, taskId, ActorA, []);

        AssertFailedClosed(world, state, rig, taskId, KnowledgeAdmissionCode.PinMismatch, binding);

        // The source is put right; the same stored task resumes, still bound to the original content.
        var raw = original.Build();
        world.Source.Add(TenantA, original.PackId, original.Version, () => raw);
        using var third = world.Boot([Grant(original)]);
        var (healed, healedRig) = await ResumeAsync(third, taskId, ActorA, [Script.Knowledge("k1", "read_resource", "nginx-triage", "references/checklist.md"), Script.Final("ok")]);

        Assert.Equal(AgentTaskStatus.Completed, healed.Status);
        Assert.Equal(3, healed.ExecutionAttempt);
        AssertSameBinding(binding, healed.KnowledgeBinding!);
        Assert.DoesNotContain(healedRig.Model.Requests.SelectMany(request => request.History), turn => turn.Content?.Contains("rm -rf", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("publisher", KnowledgeAdmissionCode.PublisherRevoked)]
    [InlineData("key", KnowledgeAdmissionCode.KeyRevoked)]
    [InlineData("unknown", KnowledgeAdmissionCode.UnknownKey)]
    [InlineData("grant", KnowledgeAdmissionCode.NotEntitled)]
    [InlineData("kind", KnowledgeAdmissionCode.NotEntitled)]
    public async Task APackRevokedOrUnentitledAfterThePin_IsNotResumed_TheCauseIsAuditedAndNothingRuns(string withdrawal, KnowledgeAdmissionCode cause)
    {
        ArgumentNullException.ThrowIfNull(withdrawal);
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        var (taskId, binding) = await FirstAttemptAsync(world, pack);

        var trust = withdrawal switch
        {
            "publisher" => new KnowledgeTrustStore([TestPublisher.Acme.Trusted()], ["acme"]),
            "key" => new KnowledgeTrustStore([TestPublisher.Acme.Trusted(revoked: true)]),
            "unknown" => KnowledgeTrustStore.Empty,
            _ => new KnowledgeTrustStore([TestPublisher.Acme.Trusted()]),
        };
        IEnumerable<PackGrant> grants = withdrawal switch
        {
            "grant" => [],
            "kind" => [new PackGrant(TenantA, Reference(pack), PackKind.Knowledge)],
            _ => [Grant(pack)],
        };
        using var second = world.Boot(grants, trust);
        var (state, rig) = await ResumeAsync(second, taskId, ActorA, []);

        AssertFailedClosed(world, state, rig, taskId, cause, binding);
    }

    [Fact]
    public async Task AResumeByAnActorOfAnotherTenant_CannotChangeTheTenant_AndReadsNothing()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        var (taskId, binding) = await FirstAttemptAsync(world, pack);
        // Tenant B has the same pack id and version, granted, with different content. None of it may be reached.
        var theirs = new TestPack { Files = Replaced(pack, "nginx-triage/references/checklist.md", "tenant b checklist") };
        world.Source.Add(KnowledgeRunnerWorld.TenantB, theirs.PackId, theirs.Version, () => theirs.Build());
        var opens = world.Source.Opens;

        using var second = world.Boot([Grant(pack), new PackGrant(KnowledgeRunnerWorld.TenantB, Reference(pack), PackKind.Skill)]);
        var (state, rig) = await ResumeAsync(second, taskId, ActorB, []);

        AssertFailedClosed(world, state, rig, taskId, KnowledgeAdmissionCode.TenantNotAllowed, binding);
        Assert.Equal(opens, world.Source.Opens);
        Assert.Equal("tenant-b", world.Events.OfType<KnowledgePackAdmissionAuditEvent>().Last().TenantId);
    }

    [Fact]
    public async Task ASnapshotAlteredOnDiskWhileTheTaskWasStopped_IsRefused_AndNeverServed()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        var (taskId, binding) = await FirstAttemptAsync(world, pack);
        var file = Directory.EnumerateFiles(world.SnapshotRoot, "checklist.md", SearchOption.AllDirectories).Single();
        File.SetAttributes(file, FileAttributes.Normal);
        await File.WriteAllTextAsync(file, "rm -rf /etc/nginx");

        using var second = world.Boot([Grant(pack)]);
        var (state, rig) = await ResumeAsync(second, taskId, ActorA, []);

        AssertFailedClosed(world, state, rig, taskId, KnowledgeAdmissionCode.SnapshotTampered, binding);
    }

    [Fact]
    public async Task ASnapshotThatIsGoneAndASourceThatNoLongerSuppliesThePack_FailsClosed_WithItsCause()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        var (taskId, binding) = await FirstAttemptAsync(world, pack);
        DeleteSnapshots(world);
        world.Source.Add(TenantA, pack.PackId, pack.Version, () => null);

        using var second = world.Boot([Grant(pack)]);
        var (state, rig) = await ResumeAsync(second, taskId, ActorA, []);

        AssertFailedClosed(world, state, rig, taskId, KnowledgeAdmissionCode.SourceUnavailable, binding);
    }

    [Fact]
    public async Task ASnapshotThatIsGoneButTheSignedPackIsStillSupplied_IsRebuiltAsTheSameContentAddressedSnapshot()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        var (taskId, binding) = await FirstAttemptAsync(world, pack);
        DeleteSnapshots(world);
        Assert.Empty(HashDirectories(world));

        using var second = world.Boot([Grant(pack)]);
        var (state, rig) = await ResumeAsync(second, taskId, ActorA, [Script.Knowledge("k1", "read_resource", "nginx-triage", "references/checklist.md"), Script.Final("ok")]);

        // Re-verified from the signed bytes against the pin: the same hashes, the same id, the same content.
        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        AssertSameBinding(binding, state.KnowledgeBinding!);
        Assert.Equal(binding.SnapshotId, Assert.Single(HashDirectories(world)).Replace(world.SnapshotRoot + Path.DirectorySeparatorChar, string.Empty, StringComparison.Ordinal).Replace('\\', '/'));
        Assert.Contains(rig.Model.Requests.SelectMany(request => request.History), turn => turn.Content?.Contains(Checklist, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ABoundTaskResumedByARunnerWithNoKnowledgeHost_FailsClosed_ItIsNeverRunWithoutItsKnowledge()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        var (taskId, binding) = await FirstAttemptAsync(world, pack);

        using var second = world.Boot([Grant(pack)]);
        var task = (await second.Store.LoadAsync(taskId))!;
        var rig = second.Runner([], withHost: false);
        var state = await rig.Runner.ResumeAsync(task, ActorA);

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
        Assert.Empty(rig.Model.Requests);
        AssertSameBinding(binding, (await second.Store.LoadAsync(taskId))!.KnowledgeBinding!);
    }

    [Fact]
    public async Task AResumedAttemptThatLosesItsAuthorityMidWay_IsRefusedThePinnedReads_NotServedFromMemory()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        var (taskId, _) = await FirstAttemptAsync(world, pack);
        using var second = world.Boot([Grant(pack)]);
        var task = (await second.Store.LoadAsync(taskId))!;
        var rig = second.Runner(
            [Script.Knowledge("k1", "discover"), Script.Knowledge("k2", "load", "nginx-triage"), Script.Final("done")],
            beforeResponse: index =>
            {
                if (index == 1)
                {
                    Assert.True(second.Registry.Unregister(second.ProviderId));
                }
            });

        var state = await rig.Runner.ResumeAsync(task, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        var resumedReads = world.Events.OfType<KnowledgeReadAuditEvent>().Skip(3).ToArray();
        Assert.Equal([KnowledgeReadResultCode.Success, KnowledgeReadResultCode.NotAuthorized], resumedReads.Select(read => read.ResultCode));
        Assert.Equal(KnowledgeAdmissionCode.ProviderNotAdmitted, resumedReads[1].CauseCode);
    }

    // ---- fixtures ----

    /// <summary>
    /// The first attempt of a task bound to the pack: admitted, pinned, three reads, one executed step, and then the model has nothing
    /// scripted, so the attempt ends <see cref="AgentTaskStatus.Failed"/> and the task is stored resumable. The process is then disposed.
    /// </summary>
    private static async Task<(Guid TaskId, TaskKnowledgeBinding Binding)> FirstAttemptAsync(KnowledgeRunnerWorld world, TestPack pack)
    {
        AddPack(world, pack);
        var taskId = Guid.NewGuid();
        using var first = world.Boot([Grant(pack)], bindings: new Dictionary<TenantId, PackReference> { [TenantA] = Reference(pack) });
        var rig = first.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "discover"), Script.Knowledge("k2", "load", "nginx-triage"),
             Script.Knowledge("k3", "read_resource", "nginx-triage", "references/checklist.md"), Script.Tool("c1", "test.read")]);

        var stopped = await rig.Runner.RunAsync(Goal, ActorA, taskId);

        Assert.Equal(AgentTaskStatus.Failed, stopped.Status);
        Assert.Equal(1, rig.Read.ExecutionCount);
        var stored = (await first.Store.LoadAsync(taskId))!;
        Assert.NotNull(stored.KnowledgeBinding);
        return (taskId, stored.KnowledgeBinding);
    }

    private static async Task<(TaskState State, RunnerRig Rig)> ResumeAsync(
        KnowledgeProcess process, Guid taskId, ActorIdentity actor, IEnumerable<ModelResponse> script)
    {
        var task = (await process.Store.LoadAsync(taskId))!;
        var rig = process.Runner(script);
        return (await rig.Runner.ResumeAsync(task, actor), rig);
    }

    private static void AssertFailedClosed(
        KnowledgeRunnerWorld world, TaskState state, RunnerRig rig, Guid taskId, KnowledgeAdmissionCode cause, TaskKnowledgeBinding pinned)
    {
        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
        Assert.Empty(rig.Model.Requests); // nothing planned, nothing asked of the model
        Assert.Equal(0, rig.Read.ExecutionCount);
        Assert.Equal("Knowledge unavailable", state.Steps[^1].Description);
        Assert.Contains(cause.ToString(), state.Steps[^1].Observation, StringComparison.Ordinal);
        AssertSameBinding(pinned, state.KnowledgeBinding!); // the pin is kept: fixing the authority makes the task resumable again
        var admission = world.Events.OfType<KnowledgePackAdmissionAuditEvent>().Last();
        Assert.Equal((cause, taskId), (admission.Code, admission.TaskId));
        Assert.Equal(3, world.Events.OfType<KnowledgeReadAuditEvent>().Count()); // only the first attempt's reads
        var terminal = world.Events.OfType<TaskLifecycleAuditEvent>().Last(evt => evt.Stage == TaskLifecycleStage.ExecutionTerminal);
        Assert.Equal(taskId, terminal.TaskId);
    }

    private static void AssertSameBinding(TaskKnowledgeBinding expected, TaskKnowledgeBinding actual)
    {
        Assert.Equal(
            (expected.SchemaVersion, expected.AuthorizedBy, expected.TenantId, expected.PackId, expected.Version, expected.Kind, expected.Publisher, expected.KeyId, expected.ManifestHash, expected.SnapshotId),
            (actual.SchemaVersion, actual.AuthorizedBy, actual.TenantId, actual.PackId, actual.Version, actual.Kind, actual.Publisher, actual.KeyId, actual.ManifestHash, actual.SnapshotId));
        Assert.Equal(expected.ResourceHashes.OrderBy(file => file.Key, StringComparer.Ordinal), actual.ResourceHashes.OrderBy(file => file.Key, StringComparer.Ordinal));
    }

    private static void AssertAuditFileIsIntactAndFreeOfContent(KnowledgeRunnerWorld world)
    {
        var lines = File.ReadAllLines(world.AuditFile);
        Assert.True(AuditChainVerifier.Verify(lines).IsValid);
        var persisted = string.Join('\n', lines);
        Assert.DoesNotContain("ss -ltnp", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("v2 checklist", persisted, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Replaced(TestPack pack, string path, string text)
    {
        var files = new Dictionary<string, string>(pack.Files, StringComparer.Ordinal) { [path] = text };
        return files;
    }

    private static IEnumerable<string> HashDirectories(KnowledgeRunnerWorld world) =>
        Directory.Exists(world.SnapshotRoot)
            ? Directory.EnumerateDirectories(world.SnapshotRoot, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path).Length == 64)
            : [];

    private static void DeleteSnapshots(KnowledgeRunnerWorld world)
    {
        foreach (var file in Directory.EnumerateFiles(world.SnapshotRoot, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(world.SnapshotRoot, recursive: true);
    }

    private static PackReference Reference(TestPack pack) => new(pack.PackId, pack.Version);

    private static PackGrant Grant(TestPack pack) => new(TenantA, Reference(pack), pack.Kind);

    private static void AddPack(KnowledgeRunnerWorld world, TestPack pack)
    {
        var raw = pack.Build();
        world.Source.Add(TenantA, pack.PackId, pack.Version, () => raw);
    }
}
