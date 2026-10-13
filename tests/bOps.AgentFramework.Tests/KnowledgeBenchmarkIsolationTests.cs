// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

public sealed class KnowledgeBenchmarkIsolationTests
{
    [Fact]
    public async Task SameReplay_OnlyAuthorizedBReceivesKnowledgeThroughBroker()
    {
        if (Environment.GetEnvironmentVariable("BOPS_KNOW_BENCH_SIGN") == "1") KnowledgeBenchmark.SignDelivery();
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        using var temporary = new TempDirectory();
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, corpus);
        using var a = new KnowledgeRunnerWorld();
        using var b = new KnowledgeRunnerWorld();
        // A even proposes the absent capability: it must receive no content or authority.
        var ma = new BenchmarkProbe([KnowledgeBenchmark.Plan, Script.Knowledge("absent", "discover"), KnowledgeBenchmark.Events, Script.Final("offline done")]);
        var mb = new BenchmarkProbe([KnowledgeBenchmark.Plan, KnowledgeBenchmark.Read, KnowledgeBenchmark.Events, Script.Final("offline done")]);
        var ra = KnowledgeBenchmark.ComposeReplay(a, ma, "A", path, hash);
        var rb = KnowledgeBenchmark.ComposeReplay(b, mb, "B", path, hash, raw, trust,
            KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span), KnowledgeBenchmark.GrantAuthorization);
        Assert.Equal(JsonSerializer.Serialize(ra.Registry.GetAvailableManifests()), JsonSerializer.Serialize(rb.Registry.GetAvailableManifests()));
        var sa = await ra.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA);
        var sb = await rb.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA);
        Assert.Equal(AgentTaskStatus.Completed, sa.Status);
        Assert.Equal(AgentTaskStatus.Completed, sb.Status);
        Assert.Null(sa.KnowledgeBinding);
        Assert.Equal(0, a.Source.Opens);
        Assert.Empty(a.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Empty(a.Events.OfType<KnowledgePackAdmissionAuditEvent>());
        Assert.False(Directory.Exists(a.SnapshotRoot));
        Assert.Equal("tenant-a", sb.KnowledgeBinding!.TenantId);
        Assert.Equal("Knowledge", sb.KnowledgeBinding.Kind);
        Assert.Equal(KnowledgeBenchmark.Reference.PackId, sb.KnowledgeBinding.PackId);
        Assert.False(string.IsNullOrWhiteSpace(sb.KnowledgeBinding.SnapshotId));
        Assert.Equal(2, sb.KnowledgeBinding.ResourceHashes.Count);
        var read = Assert.Single(b.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Equal(KnowledgeReadResultCode.Success, read.ResultCode);
        Assert.Equal(sb.KnowledgeBinding.ManifestHash, read.PackHash);
        foreach (var world in new[] { a, b })
        {
            var call = Assert.Single(world.Events.OfType<ToolCallAuditEvent>());
            Assert.Equal(ToolOutcome.Success, call.Outcome);
            Assert.Equal(hash, call.Summary!["corpusSha256"]!.GetValue<string>());
            Assert.Equal("replay", call.Summary["executionMode"]!.GetValue<string>());
        }
        Assert.Equal(ma.Requests.Count, ra.Maf.ComposedTurns);
        Assert.Equal(mb.Requests.Count, rb.Maf.ComposedTurns);
        var body = System.Text.Encoding.UTF8.GetString(raw.Files["windows-event-triage/references/interpretation.md"].Span);
        var paragraphs = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => char.IsDigit(line[0])).ToArray();
        // Full request serialization includes system prompt, all history, tool schemas and arguments.
        foreach (var request in ma.Requests)
        {
            var serialized = JsonSerializer.Serialize(request);
            Assert.DoesNotContain("runtime.knowledge_read", JsonSerializer.Serialize(request.AvailableTools), StringComparison.Ordinal);
            Assert.DoesNotContain("acme.windows-event-runbooks", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("windows-event-triage", serialized, StringComparison.Ordinal);
            Assert.All(paragraphs, paragraph => Assert.DoesNotContain(JsonSerializer.Serialize(paragraph)[1..^1], serialized, StringComparison.Ordinal));
            Assert.DoesNotContain(request.History, t => t.Provenance?.Origin is ContextOrigin.Knowledge or ContextOrigin.Skill or ContextOrigin.Experience);
        }
        foreach (var request in ma.Requests.Concat(mb.Requests))
        {
            Assert.DoesNotContain(body, request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(request.History, t => t.Role == ChatRole.System && t.Provenance?.Origin == ContextOrigin.Knowledge);
        }
        Assert.All(mb.Requests.Take(2), request => Assert.All(paragraphs, paragraph =>
            Assert.DoesNotContain(JsonSerializer.Serialize(paragraph)[1..^1], JsonSerializer.Serialize(request), StringComparison.Ordinal)));
        var served = Assert.Single(mb.Requests[2].History, t => t.Provenance?.Origin == ContextOrigin.Knowledge);
        Assert.Equal(ChatRole.Tool, served.Role);
        Assert.Contains(body, served.Content, StringComparison.Ordinal);
        Assert.Equal(sb.KnowledgeBinding.ManifestHash, served.Provenance!.PackHash);
        Assert.Equal(read.ContentHash, served.Provenance.ContentHash);
        var aTools = ma.Requests[1].AvailableTools;
        var bTools = mb.Requests[1].AvailableTools;
        Assert.Equal(JsonSerializer.Serialize(aTools), JsonSerializer.Serialize(bTools.Where(t => t.Name != "runtime.knowledge_read").ToArray()));
        Assert.Single(bTools, t => t.Name == "runtime.knowledge_read");
        foreach (var model in new[] { ma, mb })
            Assert.Contains(model.Requests[^1].History, t => t.Provenance?.Origin == ContextOrigin.ToolResult && t.Content?.Contains("benchmarkCorpus", StringComparison.Ordinal) == true);
        Assert.Equal(CanaryEventsCorpus.Encode(corpus, hash), await File.ReadAllBytesAsync(path));
        // Evidence is identical, including the minimized partial/truncated flags, not just equal audit pins.
        Assert.Equal(ma.Requests[^1].History.Last(t => t.Provenance?.Origin == ContextOrigin.ToolResult).Content,
            mb.Requests[^1].History.Last(t => t.Provenance?.Origin == ContextOrigin.ToolResult).Content);
        Assert.DoesNotContain(body, KnowledgeBenchmark.Goal, StringComparison.Ordinal);
        Assert.Equal(2, raw.Files.Count);
    }

    [Fact]
    public async Task MissingGrantRevocationTenantTamperingAndIncompatibleCorpus_FailClosed()
    {
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        using var temporary = new TempDirectory();
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, corpus);
        var replay = KnowledgeBenchmark.Replay(path, hash);
        using (var rejectedWorld = new KnowledgeRunnerWorld())
        {
            var probe = new BenchmarkProbe([]);
            var pin = KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span);
            Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ComposeReplay(rejectedWorld, probe, "unknown", path, hash));
            Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ComposeReplay(rejectedWorld, probe, "A", path, hash, raw, trust, pin, KnowledgeBenchmark.GrantAuthorization));
            Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ComposeReplay(rejectedWorld, probe, "B", path, hash, raw, trust, pin));
            Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ComposeReplay(rejectedWorld, probe, "B", path, hash, raw, trust, pin, "tenant-b"));
            Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ComposeReplay(rejectedWorld, probe, "B", path, hash, raw, trust, new string('0', 64), KnowledgeBenchmark.GrantAuthorization));
            Assert.Empty(probe.Requests);
            Assert.Equal(0, rejectedWorld.Source.Opens);
            Assert.Empty(rejectedWorld.Events);
        }
        using (var deniedWorld = new KnowledgeRunnerWorld())
        {
            var denied = KnowledgeBenchmark.Boot(deniedWorld, true, raw, trust, grant: false);
            var probe = new BenchmarkProbe([]);
            var state = await KnowledgeBenchmark.Compose(denied, probe, true, replay).Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA);
            Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
            Assert.Empty(probe.Requests);
            Assert.Equal(0, deniedWorld.Source.Opens);
            Assert.Equal(KnowledgeAdmissionCode.NotEntitled, Assert.Single(deniedWorld.Events.OfType<KnowledgePackAdmissionAuditEvent>()).Code);
        }
        using var world = new KnowledgeRunnerWorld();
        var first = KnowledgeBenchmark.Boot(world, true, raw, trust);
        var initial = new BenchmarkProbe([KnowledgeBenchmark.Plan, KnowledgeBenchmark.Events]);
        var stopped = await KnowledgeBenchmark.Compose(first, initial, true, replay).Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA);
        Assert.NotNull(stopped.KnowledgeBinding);
        Assert.Equal(AgentTaskStatus.Failed, stopped.Status);
        var keys = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(KnowledgeBenchmark.Delivery, "test-trust.json")))!["keys"]!;
        var key = keys[0]!.Deserialize<PublisherKey>(KnowledgeBenchmark.JsonOptions)!;
        async Task Refused(KnowledgeProcess process, ActorIdentity actor, KnowledgeAdmissionCode expected)
        {
            var model = new BenchmarkProbe([]);
            var stored = (await process.Store.LoadAsync(stopped.Id))!;
            var state = await KnowledgeBenchmark.Compose(process, model, true, replay).Runner.ResumeAsync(stored, actor);
            Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
            Assert.Empty(model.Requests);
            Assert.Equal(expected, world.Events.OfType<KnowledgePackAdmissionAuditEvent>().Last().Code);
            Assert.Empty(world.Events.OfType<KnowledgeReadAuditEvent>());
        }
        await Refused(world.Boot([], trust, providerTenants: [KnowledgeRunnerWorld.TenantA]), KnowledgeRunnerWorld.ActorA, KnowledgeAdmissionCode.NotEntitled);
        await Refused(world.Boot([KnowledgeBenchmark.Grant], new KnowledgeTrustStore([key with { Revoked = true }]), providerTenants: [KnowledgeRunnerWorld.TenantA]), KnowledgeRunnerWorld.ActorA, KnowledgeAdmissionCode.KeyRevoked);
        await Refused(world.Boot([KnowledgeBenchmark.Grant], trust, providerTenants: [KnowledgeRunnerWorld.TenantA]), KnowledgeRunnerWorld.ActorB, KnowledgeAdmissionCode.TenantNotAllowed);
        var altered = new RawKnowledgePack { ManifestBytes = raw.ManifestBytes, SignatureBytes = raw.SignatureBytes, Files = raw.Files.ToDictionary(x => x.Key, x => x.Key.EndsWith("interpretation.md", StringComparison.Ordinal) ? (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes("altered") : x.Value, StringComparer.Ordinal) };
        world.Source.Add(KnowledgeRunnerWorld.TenantA, KnowledgeBenchmark.Reference.PackId, KnowledgeBenchmark.Reference.Version, () => altered);
        await Refused(world.Boot([KnowledgeBenchmark.Grant], trust), KnowledgeRunnerWorld.ActorA, KnowledgeAdmissionCode.HashMismatch);
        world.Source.Add(KnowledgeRunnerWorld.TenantA, KnowledgeBenchmark.Reference.PackId, KnowledgeBenchmark.Reference.Version, () => raw);
        var file = Directory.GetFiles(world.SnapshotRoot, "interpretation.md", SearchOption.AllDirectories).Single();
        File.SetAttributes(file, FileAttributes.Normal);
        await File.WriteAllTextAsync(file, "altered snapshot");
        await Refused(world.Boot([KnowledgeBenchmark.Grant], trust), KnowledgeRunnerWorld.ActorA, KnowledgeAdmissionCode.SnapshotTampered);
        Assert.ThrowsAny<Exception>(() => KnowledgeBenchmark.Replay(path, new string('0', 64)));
        var changed = (JsonObject)corpus.DeepClone();
        var different = KnowledgeBenchmark.Arguments.ToJson();
        different["limit"] = 10;
        changed["arguments"] = CanaryEventsCorpus.Canonical(different);
        await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(changed, CanaryEventsCorpus.Hash(changed)));
        Assert.ThrowsAny<Exception>(() => KnowledgeBenchmark.Replay(path, CanaryEventsCorpus.Hash(changed)));
        using (var incompatibleWorld = new KnowledgeRunnerWorld())
            foreach (var condition in new[] { "A", "B" })
                Assert.ThrowsAny<Exception>(() => KnowledgeBenchmark.ComposeReplay(incompatibleWorld, new BenchmarkProbe([]), condition,
                    path, CanaryEventsCorpus.Hash(changed)));
        await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(corpus, hash));
        using var replayWorld = new KnowledgeRunnerWorld();
        var process = KnowledgeBenchmark.Boot(replayWorld, false);
        var rig = KnowledgeBenchmark.Compose(process, new BenchmarkProbe([]), false, replay);
        // Corruption after composition must also be refused by the governed adapter, without native fallback.
        var tampered = (JsonObject)corpus.DeepClone();
        tampered["output"] = "altered";
        await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(tampered, hash));
        var tamperedResult = await rig.Runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), KnowledgeRunnerWorld.ActorA,
            new ExecutionPlan("offline", "1", "Tampered corpus probe", [new ExecutionPlanStep(0, "system.events", KnowledgeBenchmark.Arguments, "replay")]), null);
        Assert.StartsWith("ERROR", Assert.Single(tamperedResult.Evidence).Data);
        Assert.Equal(ToolOutcome.Failure, replayWorld.Events.OfType<ToolCallAuditEvent>().Last().Outcome);
        foreach (var condition in new[] { "A", "B" })
            Assert.ThrowsAny<Exception>(() => KnowledgeBenchmark.ComposeReplay(replayWorld, new BenchmarkProbe([]), condition, path, hash));
        await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(corpus, hash));
        var incompatible = KnowledgeBenchmark.Arguments.ToJson();
        incompatible["limit"] = 10;
        var result = await rig.Runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), KnowledgeRunnerWorld.ActorA,
            new ExecutionPlan("offline", "1", "Mismatch probe", [new ExecutionPlanStep(0, "system.events", ToolArguments.FromJson(incompatible), "replay")]), null);
        Assert.StartsWith("ERROR", Assert.Single(result.Evidence).Data);
        Assert.All(replayWorld.Events.OfType<ToolCallAuditEvent>(), call => Assert.Equal(ToolOutcome.Failure, call.Outcome));
        Assert.Empty(replayWorld.Events.OfType<ModelCallAuditEvent>());
    }
}
