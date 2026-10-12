// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world and process passes to the test, which disposes it.
#pragma warning disable CA2000

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;
using BopsChatRole = bOps.Abstractions.ChatRole;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// KNOW-01B (ADR-0056): <c>runtime.knowledge_read</c> through the real <see cref="AgentRunner"/>, the real broker and MAF turn path, the
/// real SQLite task store, the real signed-pack admission and a scripted model, with temporary data only. The model proposes; the runner
/// and the host decide. Nothing here calls a remote model or touches a production host.
/// </summary>
public sealed class KnowledgeRunnerIntegrationTests
{
    private const string Goal = "fix the nginx 502";
    private const string Checklist = "nginx -t; ss -ltnp :80";
    private static readonly TenantId TenantA = KnowledgeRunnerWorld.TenantA;
    private static readonly TenantId TenantB = KnowledgeRunnerWorld.TenantB;
    private static readonly ActorIdentity ActorA = KnowledgeRunnerWorld.ActorA;
    private static readonly ActorIdentity ActorB = KnowledgeRunnerWorld.ActorB;

    // ---- the whole path, end to end ----

    [Fact]
    public async Task DiscoverLoadAndRead_AreServedThroughTheRealRunner_AsPinnedAttributedData()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        using var process = Boot(world, pack);
        TaskKnowledgeBinding? savedBeforeTheFirstModelCall = null;
        var taskId = Guid.NewGuid();
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "discover"), Script.Knowledge("k2", "load", "nginx-triage"),
             Script.Knowledge("k3", "read_resource", "nginx-triage", "references/checklist.md"), Script.Tool("c1", "test.read"), Script.Final("diagnosis")],
            beforeResponse: index =>
            {
                if (index == 0)
                {
                    savedBeforeTheFirstModelCall = Task.Run(() => process.Store.LoadAsync(taskId)).GetAwaiter().GetResult()?.KnowledgeBinding;
                }
            });

        var final = await rig.Runner.RunAsync(Goal, ActorA, taskId);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Equal("diagnosis", final.Steps[^1].Observation);
        Assert.Equal(1, rig.Read.ExecutionCount);
        Assert.Equal(6, rig.Model.Requests.Count);

        // The pin is bOps's, taken from the admission, and durable before any model call.
        var manifestHash = KnowledgePackFormat.Sha256Hex(pack.Parts().Manifest);
        Assert.NotNull(savedBeforeTheFirstModelCall);
        var stored = await process.Store.LoadAsync(taskId);
        Assert.NotNull(stored);
        var binding = stored.KnowledgeBinding;
        Assert.NotNull(binding);
        Assert.Equal(("tenant-a", "acme.nginx-runbooks", "1.0.0", "Skill", manifestHash), (binding.TenantId, binding.PackId, binding.Version, binding.Kind, manifestHash));
        Assert.Equal(manifestHash, binding.ManifestHash);
        Assert.Equal(ActorA, binding.AuthorizedBy);
        Assert.Equal(KnowledgePackFormat.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(Checklist)), binding.ResourceHashes["nginx-triage/references/checklist.md"]);
        Assert.Equal(3, binding.ResourceHashes.Count);
        Assert.False(string.IsNullOrEmpty(binding.SnapshotId));
        Assert.Equal(binding, savedBeforeTheFirstModelCall with { ResourceHashes = binding.ResourceHashes });

        // The model was offered the step's tool and the one knowledge function, beside each other; the system prompt never changed hands.
        foreach (var request in rig.Model.Requests.Skip(1).Take(4))
        {
            Assert.Equal(["test.read", "runtime.knowledge_read"], request.AvailableTools.Select(tool => tool.Name));
            Assert.DoesNotContain(request.History, turn => turn.Role == BopsChatRole.System);
        }

        Assert.All(rig.Model.Requests, request => Assert.DoesNotContain("ss -ltnp", request.SystemPrompt, StringComparison.Ordinal));

        // Served content: a Tool turn, attributed to the pack, tenant, provider, operation, item and content hash.
        var served = rig.Model.Requests[4].History.Where(turn => turn.Provenance?.Origin == ContextOrigin.Skill).ToArray();
        Assert.Equal(3, served.Length);
        Assert.All(served, turn =>
        {
            Assert.Equal(BopsChatRole.Tool, turn.Role);
            Assert.Equal((manifestHash, "acme.nginx-runbooks", "1.0.0", "tenant-a", process.ProviderId),
                (turn.Provenance!.PackHash, turn.Provenance.PackId, turn.Provenance.PackVersion, turn.Provenance.TenantId, turn.Provenance.AdmittedProviderId));
        });
        Assert.Equal(["discover", "load", "read_resource"], served.Select(turn => turn.Provenance!.Operation));
        Assert.Equal([null, "nginx-triage", "nginx-triage/references/checklist.md"], served.Select(turn => turn.Provenance!.Item));
        Assert.Contains(Checklist, served[2].Content, StringComparison.Ordinal);

        // The audit: one admission, three reads naming task, step, operation, resource and hash; never content.
        var admission = Assert.Single(world.Events.OfType<KnowledgePackAdmissionAuditEvent>());
        Assert.Equal((KnowledgeAdmissionCode.Admitted, "tenant-a", taskId), (admission.Code, admission.TenantId, admission.TaskId));
        var reads = world.Events.OfType<KnowledgeReadAuditEvent>().ToArray();
        Assert.Equal(["discover", "load", "read_resource"], reads.Select(read => read.Operation));
        Assert.All(reads, read =>
        {
            Assert.Equal((KnowledgeReadResultCode.Success, taskId, 0, ActorA), (read.ResultCode, read.TaskId, read.StepIndex, read.Actor));
            Assert.Equal(("tenant-a", process.ProviderId, manifestHash, "acme.nginx-runbooks"), (read.TenantId, read.ProviderId, read.PackHash, read.PackId));
        });
        Assert.Equal("nginx-triage", reads[1].Skill);
        Assert.Equal("references/checklist.md", reads[2].Resource);
        Assert.Equal(served.Select(turn => turn.Provenance!.ContentHash), reads.Select(read => read.ContentHash));
        AssertNoContentPersisted(world);

        // The served turns are context of one logical call: the persisted task holds the binding, not the pack's text.
        Assert.DoesNotContain(stored.Steps, step => step.Observation?.Contains("ss -ltnp", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EveryModelCall_GoesThroughTheBroker_AndTheKnowledgeSessionAddsNone()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack());
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "discover"), Script.Knowledge("k2", "load", "nginx-triage"), Script.Tool("c1", "test.read"), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        // The provider saw exactly the five scripted calls; each one was composed as a governed MAF turn by the broker, and each one is
        // an audited model call. The two knowledge reads in between made no model call of their own.
        Assert.Equal(5, rig.Model.Requests.Count);
        Assert.Equal(5, rig.Maf.ComposedTurns);
        Assert.Equal(5, world.Events.OfType<ModelCallAuditEvent>().Count());
        Assert.DoesNotContain(world.Events.OfType<ModelCallAuditEvent>(), call => call.ContextGateRejection is not null);
    }

    // ---- a task without a binding keeps today's behaviour ----

    [Fact]
    public async Task ALegacyTask_GetsNoKnowledge_AndTheToolIsRefusedAsBefore_EvenWithAHostComposed()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        AddPack(world, pack, TenantA);
        // The host holds a grant and a registered source for tenant-a but binds no pack to any task.
        using var process = world.Boot([new PackGrant(TenantA, new PackReference(pack.PackId, pack.Version), pack.Kind)]);
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "discover"), Script.Tool("c1", "test.read"), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Null(final.KnowledgeBinding);
        Assert.Equal(0, world.Source.Opens);
        Assert.DoesNotContain(world.Events, evt => evt is KnowledgeReadAuditEvent or KnowledgePackAdmissionAuditEvent);
        Assert.DoesNotContain(rig.Model.Requests.SelectMany(request => request.AvailableTools), tool => tool.Name == "runtime.knowledge_read");
        Assert.Contains(rig.Model.Requests.SelectMany(request => request.History),
            turn => turn.Content?.Contains("Only runtime.evidence_read exists", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ALegacyTaskResumedUnderAHostThatNowBindsPacks_StillGetsNoKnowledge_AndAdmitsNothing()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        AddPack(world, pack, TenantA);
        var taskId = Guid.NewGuid();

        // Before knowledge existed: the host binds nothing, and the run stops after its first step (the model has nothing more to say).
        using (var before = world.Boot([]))
        {
            var first = before.Runner([Script.Plan("test.read"), Script.Tool("c1", "test.read")], withHost: false);
            var stopped = await first.Runner.RunAsync(Goal, ActorA, taskId);
            Assert.Equal(AgentTaskStatus.Failed, stopped.Status);
            Assert.Null(stopped.KnowledgeBinding);
        }

        // Later: a host that would bind the pack for tenant-a. The stored task has no binding, so it gets nothing.
        var reference = new PackReference(pack.PackId, pack.Version);
        using var after = world.Boot([new PackGrant(TenantA, reference, pack.Kind)], bindings: new Dictionary<TenantId, PackReference> { [TenantA] = reference });
        var second = after.Runner([Script.Knowledge("k1", "discover"), Script.Final("done")]);
        var task = (await after.Store.LoadAsync(taskId))!;
        var resumed = await second.Runner.ResumeAsync(task, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Null(resumed.KnowledgeBinding);
        Assert.Equal(0, world.Source.Opens);
        Assert.DoesNotContain(world.Events, evt => evt is KnowledgeReadAuditEvent or KnowledgePackAdmissionAuditEvent);
        Assert.DoesNotContain(second.Model.Requests.SelectMany(request => request.AvailableTools), tool => tool.Name == "runtime.knowledge_read");
    }

    // ---- what the model cannot do ----

    [Theory]
    [InlineData("run_skill_script")]
    [InlineData("load_skill")]
    [InlineData("read_skill_resource")]
    public async Task AFrameworkSkillsFunctionOrAScriptRunner_IsRefusedBeforeAnythingRuns_AndNeverOffered(string proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack());
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Tool("s1", proposed), Script.Tool("c1", "test.read"), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Equal(1, rig.Read.ExecutionCount);
        var refused = Assert.Single(world.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Equal(KnowledgeReadResultCode.Unsupported, refused.ResultCode);
        Assert.Null(refused.Skill);
        Assert.Null(refused.ContentHash);
        Assert.DoesNotContain(world.Events.OfType<ToolCallAuditEvent>(), call => call.Tool == proposed);
        var offered = rig.Model.Requests.SelectMany(request => request.AvailableTools.Select(tool => tool.Name)).Distinct().ToArray();
        Assert.DoesNotContain(offered, name => name is "load_skill" or "read_skill_resource" or "run_skill_script");
    }

    [Fact]
    public async Task AnotherRuntimeFunctionName_IsTheRunnersToRefuse_NotTheKnowledgeSessions_AndRunsNothing()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack());
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Tool("s1", "runtime.run_skill_script"), Script.Tool("c1", "test.read"), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Equal(1, rig.Read.ExecutionCount);
        Assert.DoesNotContain(world.Events, evt => evt is KnowledgeReadAuditEvent);
        Assert.Contains(rig.Model.Requests.SelectMany(request => request.History),
            turn => turn.Content?.Contains("Only runtime.evidence_read exists", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(world.Events.OfType<ToolCallAuditEvent>(), call => call.Tool == "runtime.run_skill_script");
    }

    [Fact]
    public async Task AToolOutsideThePlanOrTheManifest_StaysUnderTheOrdinaryChecks_WhileAKnowledgeSessionIsOpen()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack());
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Tool("o1", "test.other"), Script.Tool("c1", "test.read"), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.True(final.Status == AgentTaskStatus.Completed, string.Join(" | ", final.Steps.Select(step => $"{step.Description}: {step.Observation}")));
        var calls = world.Events.OfType<ToolCallAuditEvent>().ToArray();
        Assert.Equal(AuthorizationKind.UnknownTool, calls.Single(call => call.Tool == "test.other").Authorization);
        Assert.Equal(0, rig.Other.ExecutionCount); // registered, but not what the plan allowed at that step
        Assert.Equal(1, rig.Read.ExecutionCount);
        Assert.DoesNotContain(world.Events, evt => evt is KnowledgeReadAuditEvent);
    }

    [Fact]
    public async Task APlanThatNamesTheKnowledgeFunctionAsAStep_IsNotAPlan_AndNoReadIsMade()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack());
        var rig = process.Runner(
            [Script.Plan("runtime.knowledge_read"), Script.Plan("runtime.knowledge_read"), Script.Plan("runtime.knowledge_read")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.NotEqual(AgentTaskStatus.Completed, final.Status);
        Assert.Equal(0, rig.Read.ExecutionCount);
        Assert.DoesNotContain(world.Events, evt => evt is KnowledgeReadAuditEvent);
        // The planning request lists no function of the knowledge session as a plannable tool.
        Assert.All(rig.Model.Requests.Take(1), request => Assert.DoesNotContain(request.AvailableTools, tool => tool.Name == "runtime.knowledge_read"));
    }

    [Fact]
    public async Task ArgumentsTheModelInvents_CannotNameAnotherTenantOrPack_AndTheTenantInProvenanceIsTheHosts()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        var other = new TestPack { PackId = "acme.other", Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["other-skill/SKILL.md"] = TestPack.Skill("other-skill", "another pack", "do other things") } };
        AddPack(world, other, TenantA);
        using var process = Boot(
            world, pack, extraGrants: [new PackGrant(TenantA, new PackReference(other.PackId, other.Version), other.Kind)],
            budget: new KnowledgeReadBudget(MaxReads: 4, MaxRejections: 8, MaxCharactersPerRead: 8000));
        var opensAfterAdmission = 1;
        var rig = process.Runner(
            [Script.Plan("test.read"),
             Script.KnowledgeRaw("k1", new JsonObject { ["operation"] = "discover", ["tenant"] = "tenant-b" }),
             Script.KnowledgeRaw("k2", new JsonObject { ["operation"] = "load", ["skill"] = "nginx-triage", ["pack"] = "acme.other" }),
             Script.KnowledgeRaw("k3", new JsonObject { ["operation"] = "load", ["skill"] = "nginx-triage", ["version"] = "9.9.9" }),
             Script.Knowledge("k4", "load", "other-skill"),
             Script.Knowledge("k5", "load", "../nginx-triage"),
             Script.Knowledge("k6", "load", "nginx-triage"),
             Script.Tool("c1", "test.read"), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        var reads = world.Events.OfType<KnowledgeReadAuditEvent>().ToArray();
        Assert.Equal(
            [KnowledgeReadResultCode.Malformed, KnowledgeReadResultCode.Malformed, KnowledgeReadResultCode.Malformed,
             KnowledgeReadResultCode.UnknownSkill, KnowledgeReadResultCode.UnknownSkill, KnowledgeReadResultCode.Success],
            reads.Select(read => read.ResultCode));
        Assert.All(reads, read => Assert.Equal("tenant-a", read.TenantId));
        // The other pack is granted to the tenant, and still unreachable: the task is bound to one pack, and the source was opened once.
        Assert.Equal(opensAfterAdmission, world.Source.Opens);
        Assert.DoesNotContain(rig.Model.Requests.SelectMany(request => request.History), turn => turn.Content?.Contains("do other things", StringComparison.Ordinal) == true);
    }

    // ---- authorisation and isolation per task ----

    [Fact]
    public async Task ATenantWithoutAGrant_IsRefusedBeforeAnythingIsReadOrPlanned_AndNothingIsBound()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        AddPack(world, pack, TenantB);
        var reference = new PackReference(pack.PackId, pack.Version);
        // Tenant B is served by the provider and has a source copy, but the host never granted the pack to it.
        using var process = world.Boot(
            [new PackGrant(TenantA, reference, pack.Kind)],
            bindings: new Dictionary<TenantId, PackReference> { [TenantB] = reference });
        var rig = process.Runner([Script.Plan("test.read")]);
        var taskId = Guid.NewGuid();

        var final = await rig.Runner.RunAsync(Goal, ActorB, taskId);

        Assert.Equal(AgentTaskStatus.Failed, final.Status);
        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, final.TerminalReason!.Kind);
        Assert.Null(final.KnowledgeBinding);
        Assert.Empty(rig.Model.Requests);
        Assert.Equal(0, rig.Read.ExecutionCount);
        Assert.Equal(0, world.Source.Opens); // nothing is read before the grant exists
        var admission = Assert.Single(world.Events.OfType<KnowledgePackAdmissionAuditEvent>());
        Assert.Equal((KnowledgeAdmissionCode.NotEntitled, "tenant-b", taskId), (admission.Code, admission.TenantId, admission.TaskId));
        Assert.Contains("NotEntitled", final.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Events, evt => evt is KnowledgeReadAuditEvent);
        var persisted = await process.Store.LoadAsync(taskId);
        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, persisted!.TerminalReason!.Kind);
        Assert.Null(persisted.KnowledgeBinding);
    }

    [Fact]
    public async Task TwoTenantsRunningTheSamePackIdAndVersionConcurrently_EachReadOnlyTheirOwnContent()
    {
        using var world = new KnowledgeRunnerWorld();
        var packA = new TestPack();
        var packB = new TestPack
        {
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nginx-triage/SKILL.md"] = TestPack.Skill("nginx-triage", "nginx 502 triage", "tenant b private runbook"),
                ["nginx-triage/references/checklist.md"] = "tenant-b-only checklist",
                ["disk-cleanup/SKILL.md"] = TestPack.Skill("disk-cleanup", "free space safely", "b cleanup"),
            },
        };
        AddPack(world, packA, TenantA);
        AddPack(world, packB, TenantB);
        var reference = new PackReference(packA.PackId, packA.Version);
        using var process = world.Boot(
            [new PackGrant(TenantA, reference, PackKind.Skill), new PackGrant(TenantB, reference, PackKind.Skill)],
            bindings: new Dictionary<TenantId, PackReference> { [TenantA] = reference, [TenantB] = reference });

        async Task<(TaskState State, RunnerRig Rig)> RunAs(ActorIdentity actor)
        {
            var rig = process.Runner(
                [Script.Plan("test.read"), Script.Knowledge("k1", "load", "nginx-triage"),
                 Script.Knowledge("k2", "read_resource", "nginx-triage", "references/checklist.md"), Script.Tool("c1", "test.read"), Script.Final("ok")]);
            return (await rig.Runner.RunAsync(Goal, actor), rig);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => RunAs(index % 2 == 0 ? ActorA : ActorB)));

        for (var index = 0; index < results.Length; index++)
        {
            var (state, rig) = results[index];
            var tenant = index % 2 == 0 ? "tenant-a" : "tenant-b";
            var history = string.Join('\n', rig.Model.Requests[^2].History.Select(turn => turn.Content));
            Assert.Equal(AgentTaskStatus.Completed, state.Status);
            Assert.Equal(tenant, state.KnowledgeBinding!.TenantId);
            Assert.Contains(tenant == "tenant-a" ? Checklist : "tenant-b-only checklist", history, StringComparison.Ordinal);
            Assert.DoesNotContain(tenant == "tenant-a" ? "tenant-b-only" : "ss -ltnp", history, StringComparison.Ordinal);
        }

        Assert.NotEqual(
            results[0].State.KnowledgeBinding!.ManifestHash, results[1].State.KnowledgeBinding!.ManifestHash);
        Assert.NotEqual(results[0].State.KnowledgeBinding!.SnapshotId, results[1].State.KnowledgeBinding!.SnapshotId);
        Assert.All(world.Events.OfType<KnowledgeReadAuditEvent>(), read =>
            Assert.Equal(read.TenantId == "tenant-a" ? results[0].State.KnowledgeBinding!.ManifestHash : results[1].State.KnowledgeBinding!.ManifestHash, read.PackHash));
    }

    // ---- alteration and withdrawal while the task runs ----

    [Fact]
    public async Task ASnapshotAlteredAfterThePin_RefusesTheNextRead_FailsClosed_AndIsAuditedWithItsCause()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack());
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "load", "nginx-triage"),
             Script.Knowledge("k2", "read_resource", "nginx-triage", "references/checklist.md"), Script.Tool("c1", "test.read"), Script.Final("ok")],
            beforeResponse: index =>
            {
                // After the first read was served, the stored checklist is rewritten on disk.
                if (index == 2)
                {
                    var file = Directory.EnumerateFiles(world.SnapshotRoot, "checklist.md", SearchOption.AllDirectories).Single();
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.WriteAllText(file, "rm -rf /etc/nginx");
                }
            });

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        var reads = world.Events.OfType<KnowledgeReadAuditEvent>().ToArray();
        Assert.Equal([KnowledgeReadResultCode.Success, KnowledgeReadResultCode.SnapshotUnavailable], reads.Select(read => read.ResultCode));
        Assert.Equal(KnowledgeAdmissionCode.HashMismatch, reads[1].CauseCode);
        Assert.Null(reads[1].ContentHash);
        Assert.Equal(0, reads[1].ReturnedLength);
        Assert.DoesNotContain(rig.Model.Requests.SelectMany(request => request.History), turn => turn.Content?.Contains("rm -rf", StringComparison.Ordinal) == true);
        Assert.Contains(rig.Model.Requests[^2].History, turn => turn.Content?.Contains("SnapshotUnavailable", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task AProviderWithdrawnWhileTheTaskRuns_RefusesTheNextRead_ThePinIsNotAnAuthorisation()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack());
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "discover"), Script.Knowledge("k2", "load", "nginx-triage"), Script.Tool("c1", "test.read"), Script.Final("ok")],
            beforeResponse: index =>
            {
                if (index == 2)
                {
                    Assert.True(process.Registry.Unregister(process.ProviderId));
                }
            });

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        var reads = world.Events.OfType<KnowledgeReadAuditEvent>().ToArray();
        Assert.Equal([KnowledgeReadResultCode.Success, KnowledgeReadResultCode.NotAuthorized], reads.Select(read => read.ResultCode));
        Assert.Equal(KnowledgeAdmissionCode.ProviderNotAdmitted, reads[1].CauseCode);
        Assert.Null(reads[1].ContentHash);
        Assert.Equal("acme.nginx-runbooks", reads[1].PackId); // the pinned pack is named; nothing of it was served
        var lastSeen = rig.Model.Requests[^2].History;
        Assert.Single(lastSeen, turn => turn.Provenance?.Origin == ContextOrigin.Skill); // only the discovery, served while the authority held
        Assert.DoesNotContain(lastSeen, turn => turn.Content?.Contains("1. read resource", StringComparison.Ordinal) == true);
        Assert.Contains(lastSeen, turn => turn.Content?.Contains("NotAuthorized", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TheReadsOfOneAttempt_ShareOneBudget_ThePinnedPackIsNeverReadMoreThanTheHostAllows()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        AddPack(world, pack, TenantA);
        var reference = new PackReference(pack.PackId, pack.Version);
        using var process = world.Boot(
            [new PackGrant(TenantA, reference, pack.Kind)],
            bindings: new Dictionary<TenantId, PackReference> { [TenantA] = reference },
            budget: new KnowledgeReadBudget(MaxReads: 2, MaxRejections: 4, MaxCharactersPerRead: 8000));
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "discover"), Script.Knowledge("k2", "load", "nginx-triage"),
             Script.Knowledge("k3", "load", "disk-cleanup"), Script.Tool("c1", "test.read"), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Equal(
            [KnowledgeReadResultCode.Success, KnowledgeReadResultCode.Success, KnowledgeReadResultCode.LimitExceeded],
            world.Events.OfType<KnowledgeReadAuditEvent>().Select(read => read.ResultCode));
    }

    // ---- first-call-wins ----

    [Fact]
    public async Task TwoCallsInOneReply_TheKnowledgeReadWins_TheOperationalCallIsAnsweredNotExecuted_AndTheStepIsNotCompleted()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack());
        var rig = process.Runner(
            [Script.Plan("test.read"),
             Script.Calls(Script.KnowledgeCall("k1", "load", "nginx-triage"), new ModelToolCall("c0", "test.read", ToolArguments.Empty)),
             Script.Calls(new ModelToolCall("c1", "test.read", ToolArguments.Empty), Script.KnowledgeCall("k2", "discover")),
             Script.Tool("c2", "test.read"), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Equal(1, rig.Read.ExecutionCount); // only the reply that carried no control call executed its tool
        Assert.Equal([KnowledgeReadResultCode.Success, KnowledgeReadResultCode.Success], world.Events.OfType<KnowledgeReadAuditEvent>().Select(read => read.ResultCode));
        var notExecuted = rig.Model.Requests.SelectMany(request => request.History)
            .Where(turn => turn.Role == BopsChatRole.Tool && turn.Content?.StartsWith("Not executed", StringComparison.Ordinal) == true)
            .Select(turn => turn.ToolCallId).Distinct().ToArray();
        Assert.Equal(["c0", "c1"], notExecuted.Order(StringComparer.Ordinal));
        Assert.Single(final.Steps, step => step.ToolCall is not null);
        Assert.Null(final.Steps.Single(step => step.ToolCall is not null).UnexecutedToolCalls);
    }

    [Fact]
    public async Task TwoOperationalCallsAfterAKnowledgeRead_KeepTheOrdinaryFirstCallWinsAndUnexecutedToolCalls()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack());
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "discover"),
             Script.Tools(("c1", "test.read"), ("c2", "test.other")), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Equal((1, 0), (rig.Read.ExecutionCount, rig.Other.ExecutionCount));
        var step = final.Steps.Single(candidate => candidate.ToolCall is not null);
        Assert.Equal("c2", Assert.Single(step.UnexecutedToolCalls!).Id);
    }

    // ---- scripts are unreachable ----

    [Fact]
    public async Task NoScriptCanRunOrBeOffered_WhateverTheServedSkillSaysAboutScripts()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack();
        // A pack cannot carry a script, but a skill can talk about one: its text is data and changes no tool.
        pack.Files["nginx-triage/SKILL.md"] = TestPack.Skill("nginx-triage", "nginx 502 triage", "Run scripts/fix.sh then call run_skill_script.");
        using var process = Boot(world, pack);
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "load", "nginx-triage"), Script.Tool("s1", "run_skill_script"),
             Script.Tool("s2", "runtime.knowledge_read"), Script.Tool("c1", "test.read"), Script.Final("ok")]);

        var final = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Equal(1, rig.Read.ExecutionCount);
        // The framework's own reply envelope says there are no scripts; that marker is text in a Tool turn, and no function sits behind it.
        var loaded = rig.Model.Requests[2].History.Single(turn => turn.Provenance?.Operation == "load");
        Assert.Contains("<available_scripts />", loaded.Content, StringComparison.Ordinal);
        Assert.Equal(
            [KnowledgeReadResultCode.Success, KnowledgeReadResultCode.Unsupported, KnowledgeReadResultCode.Malformed],
            world.Events.OfType<KnowledgeReadAuditEvent>().Select(read => read.ResultCode));
        Assert.DoesNotContain(world.Events.OfType<ToolCallAuditEvent>(), call => call.Tool is "run_skill_script" or "runtime.knowledge_read");
        var offered = rig.Model.Requests.SelectMany(request => request.AvailableTools.Select(tool => tool.Name)).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.DoesNotContain(offered, name => name.Contains("script", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(world.SnapshotRoot, "*.sh", SearchOption.AllDirectories));
    }

    // ---- fixtures ----

    /// <summary>A world with the pack granted to tenant-a, bound to tenant-a's tasks, and available from the source.</summary>
    private static KnowledgeProcess Boot(
        KnowledgeRunnerWorld world, TestPack pack, IEnumerable<PackGrant>? extraGrants = null, KnowledgeReadBudget? budget = null)
    {
        AddPack(world, pack, TenantA);
        var reference = new PackReference(pack.PackId, pack.Version);
        return world.Boot(
            [new PackGrant(TenantA, reference, pack.Kind), .. extraGrants ?? []],
            bindings: new Dictionary<TenantId, PackReference> { [TenantA] = reference },
            budget: budget);
    }

    private static void AddPack(KnowledgeRunnerWorld world, TestPack pack, TenantId tenant)
    {
        var raw = pack.Build();
        world.Source.Add(tenant, pack.PackId, pack.Version, () => raw);
    }

    private static void AssertNoContentPersisted(KnowledgeRunnerWorld world)
    {
        var persisted = File.ReadAllText(world.AuditFile);
        Assert.Contains("knowledgeRead", persisted, StringComparison.Ordinal);
        Assert.Contains("knowledgePackAdmission", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("ss -ltnp", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("read resource references", persisted, StringComparison.Ordinal);
    }
}
