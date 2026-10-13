// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA2000 // World disposes the processes created by ComposeReplay; each world has a using scope.
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;
using System.Text.Json;

namespace bOps.AgentFramework.Tests;

public sealed class KnowledgeCorpusProvenanceTests
{
    [Fact]
    public async Task Synthetic_IdenticalGovernedABReplayWithoutModelCalls()
    {
        using var temporary = new TempDirectory();
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, corpus);
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        var outputs = new List<string>();
        var provenances = new List<string>();
        foreach (var condition in new[] { "A", "B" })
        {
            using var world = new KnowledgeRunnerWorld();
            var probe = new BenchmarkProbe([]);
            var rig = KnowledgeBenchmark.ComposeReplay(world, probe, condition, path, hash,
                condition == "B" ? raw : null, condition == "B" ? trust : null,
                condition == "B" ? KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span) : null,
                condition == "B" ? KnowledgeBenchmark.GrantAuthorization : null);
            var report = await ExecuteAsync(rig);
            outputs.Add(Assert.IsType<string>(Assert.Single(report.Evidence).Data));
            var audit = Assert.Single(world.Events.OfType<ToolCallAuditEvent>());
            Assert.Equal(ToolOutcome.Success, audit.Outcome);
            Assert.Equal(hash, audit.Summary!["corpusSha256"]!.GetValue<string>());
            var provenance = audit.Summary["corpusProvenance"]!.AsObject();
            Assert.Equal("Synthetic", provenance["classification"]!.GetValue<string>());
            provenances.Add(CanaryEventsCorpus.Canonical(provenance));
            Assert.Equal("synthetic", JsonNode.Parse(outputs[^1])!["benchmarkCorpus"]!["origin"]!.GetValue<string>());
            Assert.Empty(probe.Requests);
            Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
        }
        Assert.Equal(outputs[0], outputs[1]);
        Assert.Equal(provenances[0], provenances[1]);
        Assert.Equal(CanaryEventsCorpus.Encode(corpus, hash), await File.ReadAllBytesAsync(path));
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.Replay(path, hash, requireCaptured: true));
    }

    [Fact]
    public async Task ForgedCaptureAlteredHashAndInconsistentMetadata_RefusedForRealBenchmark()
    {
        using var temporary = new TempDirectory();
        var synthetic = KnowledgeBenchmark.Synthetic();
        // Fully coherent, independently rehashed forgery still cannot prove acquisition.
        var captureId = Guid.NewGuid().ToString("D");
        var forged = CanaryEventsCorpus.Build(KnowledgeBenchmark.Arguments,
            synthetic["output"]!.GetValue<string>(), "windows-capture", DateTimeOffset.UnixEpoch.AddDays(1), new JsonObject
            {
                ["captureId"] = captureId,
                ["auditReference"] = "ToolCallAuditEvent.Summary.corpusProvenance.captureId=" + captureId,
                ["invokedTool"] = typeof(WindowsSystemEventsTool).FullName,
                ["manifestSha256"] = CanaryEventsCorpus.JsonHash(JsonSerializer.SerializeToNode(SystemToolManifests.Events("windows"))!),
                ["schemaSha256"] = CanaryEventsCorpus.JsonHash(JsonSerializer.SerializeToNode(SystemToolManifests.Events("windows").Parameters)!),
                ["environment"] = "Windows/X64",
            });
        var hash = CanaryEventsCorpus.Hash(forged);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, forged);
        Assert.Equal("Unverified", CanaryEventsCorpus.Provenance(CanaryEventsCorpus.Read(path, hash).Corpus, hash)["classification"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.Replay(path, hash, requireCaptured: true));
        using var world = new KnowledgeRunnerWorld();
        var rig = KnowledgeBenchmark.ComposeReplay(world, new BenchmarkProbe([]), "A", path, hash);
        var report = await ExecuteAsync(rig);
        Assert.Equal("unverified", JsonNode.Parse(Assert.IsType<string>(Assert.Single(report.Evidence).Data))!["benchmarkCorpus"]!["origin"]!.GetValue<string>());
        Assert.Equal("Unverified", Assert.Single(world.Events.OfType<ToolCallAuditEvent>()).Summary!["corpusProvenance"]!["classification"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.Replay(path, new string('0', 64), requireCaptured: true));
        var changed = (JsonObject)forged.DeepClone();
        changed["complete"] = !changed["complete"]!.GetValue<bool>();
        var changedHash = CanaryEventsCorpus.Hash(changed);
        await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(changed, changedHash));
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.Replay(path, changedHash, requireCaptured: true));
        // Mutation after composition is checked again by governed dispatch.
        var refused = await ExecuteAsync(rig);
        Assert.StartsWith("ERROR", Assert.Single(refused.Evidence).Data);
        Assert.Equal(ToolOutcome.Failure, world.Events.OfType<ToolCallAuditEvent>().Last().Outcome);
        Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
    }

    private static Task<SkillReport> ExecuteAsync(CanaryRig rig) =>
        rig.Runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), KnowledgeRunnerWorld.ActorA,
            new ExecutionPlan("offline.provenance", "1", "Artificial replay integrity probe",
                [new ExecutionPlanStep(0, "system.events", KnowledgeBenchmark.Arguments, "offline replay")]), null);
}
