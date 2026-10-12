// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

public sealed class KnowledgeContextContinuityTests
{
    [Fact]
    public async Task SynthesisReceivesExactAdmittedKnowledgeAndOperationalEvidence()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack { Kind = PackKind.Knowledge };
        using var process = Boot(world, pack);
        var rig = process.Runner([
            Script.Plan("test.read"),
            Script.Calls(Script.KnowledgeCall("k1", "read_resource", "nginx-triage", "references/checklist.md"),
                new ModelToolCall("ignored", "test.other", ToolArguments.Empty)),
            Script.Tool("c1", "test.read"), Script.Final("diagnosis")]);

        var state = await rig.Runner.RunAsync("diagnose nginx", KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(4, rig.Model.Requests.Count);
        // These are the requests actually delivered through the governed broker and projector to the scripted provider.
        var initiallyServed = Assert.Single(rig.Model.Requests[2].History,
            turn => turn.Provenance?.Origin == ContextOrigin.Knowledge);
        var synthesis = rig.Model.Requests[3];
        var continued = Assert.Single(synthesis.History, turn => turn.Provenance?.Origin == ContextOrigin.Knowledge);
        Assert.Equal(initiallyServed, continued);
        Assert.Equal(ChatRole.Tool, continued.Role);
        Assert.Contains("ss -ltnp", continued.Content, StringComparison.Ordinal);
        Assert.Equal(state.KnowledgeBinding!.TenantId, continued.Provenance!.TenantId);
        Assert.Equal(state.KnowledgeBinding.ManifestHash, continued.Provenance.PackHash);
        Assert.Equal(state.KnowledgeBinding.Version, continued.Provenance.PackVersion);
        Assert.Equal(state.KnowledgeBinding.PackId, continued.Provenance.PackId);
        Assert.Equal(process.ProviderId, continued.Provenance.AdmittedProviderId);
        Assert.Equal("nginx-triage/references/checklist.md", continued.Provenance.Item);
        Assert.Equal(Assert.Single(world.Events.OfType<KnowledgeReadAuditEvent>()).ContentHash, continued.Provenance.ContentHash);
        var operation = Assert.Single(synthesis.History, turn => turn.Role == ChatRole.Tool && turn.ToolCallId == "c1");
        Assert.Equal(ContextProvenance.ToolResult, operation.Provenance);
        Assert.Contains("done", operation.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(synthesis.History, turn => turn.ToolCallId == "ignored"
            || turn.ToolCalls?.Any(call => call.Id == "ignored") == true);
        Assert.Single(synthesis.History, turn => turn.ToolCalls?.Any(call => call.Id == "k1") == true);
        Assert.Equal((1, 0), (rig.Read.ExecutionCount, rig.Other.ExecutionCount));
        Assert.Equal(1, world.Source.Opens);
    }

    [Fact]
    public async Task SynthesisExcludesUnadmittedRevokedForeignOrUnverifiableKnowledge()
    {
        foreach (var failure in new[] { "unauthorized", "revoked", "tenant", "snapshot", "provenance" })
        {
            using var world = new KnowledgeRunnerWorld();
            var pack = new TestPack { Kind = PackKind.Knowledge };
            using var process = Boot(world, pack);
            var tenants = new ChangingTenantAuthority();
            IKnowledgeTaskHost host = new KnowledgeTaskHost(process.Service, tenants, process.Policy, world.Audit);
            if (failure == "provenance")
            {
                host = new AlteredEvidenceHost(host);
            }

            var rig = process.Runner([
                Script.Plan("test.read"), Script.Knowledge("k1", "read_resource", "nginx-triage", "references/checklist.md"),
                Script.Tool("c1", "test.read"), Script.Final("diagnosis")], host: host,
                beforeResponse: index =>
                {
                    if (index != 2)
                    {
                        return;
                    }

                    if (failure == "revoked")
                    {
                        Assert.True(process.Registry.Unregister(process.ProviderId));
                    }
                    else if (failure == "tenant")
                    {
                        tenants.Current = KnowledgeRunnerWorld.TenantB;
                    }
                    else if (failure == "snapshot")
                    {
                        var file = Directory.EnumerateFiles(world.SnapshotRoot, "checklist.md", SearchOption.AllDirectories).Single();
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.WriteAllText(file, "tampered private evidence");
                    }
                });
            if (failure == "unauthorized")
            {
                tenants.Current = KnowledgeRunnerWorld.TenantB;
                // The second tenant has a binding but no grant: admission must stop before the broker is called.
            }

            var state = await rig.Runner.RunAsync("diagnose nginx", KnowledgeRunnerWorld.ActorA);

            if (failure == "unauthorized")
            {
                Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
                Assert.Empty(rig.Model.Requests);
                continue;
            }

            Assert.Equal(AgentTaskStatus.Completed, state.Status);
            Assert.Equal(4, rig.Model.Requests.Count);
            var synthesis = rig.Model.Requests[3];
            Assert.DoesNotContain(synthesis.History, turn => turn.Provenance?.Origin == ContextOrigin.Knowledge);
            Assert.DoesNotContain(synthesis.History, turn => turn.Content?.Contains("ss -ltnp", StringComparison.Ordinal) == true
                || turn.Content?.Contains("tampered private evidence", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(synthesis.History, turn => turn.ToolCallId == "k1"
                || turn.ToolCalls?.Any(call => call.Id == "k1") == true);
            Assert.Contains(synthesis.History, turn => turn.ToolCallId == "c1" && turn.Provenance == ContextProvenance.ToolResult);
            Assert.Single(world.Events.OfType<KnowledgeReadAuditEvent>());
            Assert.Equal(1, world.Source.Opens);
        }
    }

    private static KnowledgeProcess Boot(KnowledgeRunnerWorld world, TestPack pack)
    {
        var raw = pack.Build();
        world.Source.Add(KnowledgeRunnerWorld.TenantA, pack.PackId, pack.Version, () => raw);
        var reference = new PackReference(pack.PackId, pack.Version);
        return world.Boot([new PackGrant(KnowledgeRunnerWorld.TenantA, reference, pack.Kind)],
            bindings: new Dictionary<TenantId, PackReference>
            {
                [KnowledgeRunnerWorld.TenantA] = reference, [KnowledgeRunnerWorld.TenantB] = reference,
            });
    }

    private sealed class ChangingTenantAuthority : ITenantAuthority
    {
        internal TenantId Current { get; set; } = KnowledgeRunnerWorld.TenantA;

        public TenantId? Resolve(ActorIdentity actor) => Current;
    }

    private sealed class AlteredEvidenceHost(IKnowledgeTaskHost inner) : IKnowledgeTaskHost
    {
        public async Task<KnowledgeTaskOpening> OpenForNewTaskAsync(Guid taskId, ActorIdentity actor, CancellationToken ct = default)
        {
            var opening = await inner.OpenForNewTaskAsync(taskId, actor, ct);
#pragma warning disable CA2000 // The runner owns and disposes the returned session.
            return KnowledgeTaskOpening.Bound(new AlteredEvidenceSession(opening.Session!), opening.Binding!);
#pragma warning restore CA2000
        }

        public Task<KnowledgeTaskOpening> OpenForResumeAsync(Guid taskId, ActorIdentity actor, TaskKnowledgeBinding binding, CancellationToken ct = default) =>
            inner.OpenForResumeAsync(taskId, actor, binding, ct);
    }

    private sealed class AlteredEvidenceSession(IKnowledgeReadSession inner) : IKnowledgeReadSession
    {
        public ToolManifest Manifest => inner.Manifest;

        public bool IsEvidenceAdmitted(ChatTurn result, ModelInvocationContext context) => inner.IsEvidenceAdmitted(result, context);

        public async Task<KnowledgeReadOutcome> HandleAsync(ModelResponse response, KnowledgeReadAuthority authority,
            ModelInvocationContext context, CancellationToken ct = default)
        {
            var outcome = await inner.HandleAsync(response, authority, context, ct);
            return outcome with
            {
                ContinuationTurns = outcome.ContinuationTurns.Select(turn => turn.Provenance?.Origin == ContextOrigin.Knowledge
                    ? turn with { Provenance = turn.Provenance with { TenantId = "tenant-b" } } : turn).ToArray(),
            };
        }

        public void Dispose() => inner.Dispose();
    }
}
