// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA2000 // Worlds are disposed by the enclosing scope.
using System.Reflection;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;

namespace bOps.AgentFramework.Tests;

public sealed class TrustedEventsCaptureTests
{
    [Fact]
    public async Task SyntheticCapture_OneGovernedRead_IdenticalABAndExport()
    {
        var reads = 0;
        using var session = TrustedEventsCapture.Synthetic(_ =>
        {
            reads++;
            return ToolCallResult.Success(KnowledgeBenchmark.Synthetic()["output"]!.GetValue<string>());
        });
        using var captureWorld = new KnowledgeRunnerWorld();
        await session.CaptureAsync(captureWorld);
        Assert.Equal("Synthetic", session.Provenance["classification"]!.GetValue<string>());
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        var outputs = new List<string>();
        using var temp = new TempDirectory();
        var root = temp.Sub("exports");
        Directory.CreateDirectory(root);
        foreach (var condition in new[] { "A", "B" })
        {
            await KnowledgeBenchmarkExport.RunAsync(root, condition, async export =>
            {
                var world = new KnowledgeRunnerWorld();
                export.World = world;
                export.CorpusSha256 = session.Hash;
                export.CorpusProvenance = session.Provenance;
                var model = new BenchmarkProbe([]);
                var rig = session.Compose(world, model, condition, export.RunId,
                    condition == "B" ? raw : null, condition == "B" ? trust : null,
                    condition == "B" ? KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span) : null,
                    condition == "B" ? KnowledgeBenchmark.GrantAuthorization : null);
                var report = await Execute(rig, export.RunId, KnowledgeBenchmark.Arguments);
                outputs.Add(Assert.IsType<string>(Assert.Single(report.Evidence).Data));
                var call = Assert.Single(world.Events.OfType<ToolCallAuditEvent>());
                Assert.Equal(ToolOutcome.Success, call.Outcome);
                Assert.Equal(session.Hash, call.Summary!["corpusSha256"]!.GetValue<string>());
                Assert.Equal(CanaryEventsCorpus.Canonical(session.Provenance), CanaryEventsCorpus.Canonical(call.Summary["corpusProvenance"]!));
                Assert.Empty(model.Requests);
                Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
                if (condition == "A") { Assert.Equal(0, world.Source.Opens); Assert.Empty(world.Events.OfType<KnowledgePackAdmissionAuditEvent>()); }
            });
        }
        Assert.Equal(outputs[0], outputs[1]);
        Assert.Equal(1, reads);
        var a = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "A", "result.json")))!;
        var b = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "B", "result.json")))!;
        Assert.Equal(a["corpusSha256"]!.GetValue<string>(), b["corpusSha256"]!.GetValue<string>());
        Assert.Equal(a["corpusProvenance"]!["controlledCaptureId"]!.GetValue<string>(), b["corpusProvenance"]!["controlledCaptureId"]!.GetValue<string>());
        var clone = session.Provenance;
        clone["classification"] = "Captured";
        Assert.Equal("Synthetic", session.Provenance["classification"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => CanaryEventsCorpus.RequireRealCorpus(session.Provenance));
        Assert.Throws<InvalidOperationException>(() => session.Compose(captureWorld, new BenchmarkProbe([]), "A", Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CaptureAsync(captureWorld));
    }

    [Fact]
    public async Task ImportedClaimsTamperAndIncompatibleRequests_FailClosed()
    {
        using var temp = new TempDirectory();
        var corpus = CanaryEventsCorpus.Build(KnowledgeBenchmark.Arguments,
            KnowledgeBenchmark.Synthetic()["output"]!.GetValue<string>(), "windows-capture", DateTimeOffset.UtcNow,
            CanaryEventsCorpus.CaptureMetadata(Guid.NewGuid().ToString("D")));
        var path = KnowledgeBenchmark.WriteSynthetic(temp, corpus);
        var imported = CanaryEventsCorpus.Read(path, CanaryEventsCorpus.Hash(corpus));
        Assert.Equal("Unverified", CanaryEventsCorpus.Provenance(imported.Corpus, imported.Hash)["classification"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.Replay(path, imported.Hash, requireCaptured: true));
        // The existing native provenance calculation retains Observed in memory; disk import never receives that capability.
        var observed = CanaryEventsCorpus.Provenance(corpus, imported.Hash, capturedHere: true);
        Assert.Equal("Captured", observed["classification"]!.GetValue<string>());
        Assert.Equal(corpus["capture"]!["captureId"]!.GetValue<string>(), observed["captureId"]!.GetValue<string>());
        var reads = 0;
        using var session = TrustedEventsCapture.Synthetic(_ => { reads++; return ToolCallResult.Success(corpus["output"]!.GetValue<string>()); });
        using var world = new KnowledgeRunnerWorld();
        await session.CaptureAsync(world);
        Assert.Equal("Synthetic", session.Provenance["classification"]!.GetValue<string>());
        var task = Guid.NewGuid();
        var rig = session.Compose(world, new BenchmarkProbe([]), "A", task);
        var wrong = KnowledgeBenchmark.Arguments.ToJson(); wrong["limit"] = 10;
        Assert.StartsWith("ERROR", Assert.Single((await Execute(rig, task, ToolArguments.FromJson(wrong))).Evidence).Data);
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        Assert.Throws<InvalidOperationException>(() => session.Compose(world, new BenchmarkProbe([]), "B", Guid.NewGuid(), raw, trust));
        var bTask = Guid.NewGuid();
        var b = session.Compose(world, new BenchmarkProbe([]), "B", bTask, raw, trust,
            KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span), KnowledgeBenchmark.GrantAuthorization);
        var field = typeof(TrustedEventsCapture).GetField("snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var altered = JsonNode.Parse((string)field.GetValue(session)!)!;
        altered["status"] = "altered";
        field.SetValue(session, CanaryEventsCorpus.Canonical(altered)); // Adversarial memory corruption, no product mutation API.
        Assert.StartsWith("ERROR", Assert.Single((await Execute(b, bTask, KnowledgeBenchmark.Arguments)).Evidence).Data);
        Assert.Equal(1, reads);
        Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
        session.Dispose();
        Assert.Throws<InvalidOperationException>(() => session.Hash);
    }

    private static Task<SkillReport> Execute(CanaryRig rig, Guid task, ToolArguments arguments) =>
        rig.Runner.ExecuteExecutionPlanAsync(task, KnowledgeRunnerWorld.ActorA,
            new ExecutionPlan("offline.controlled", "1", "Synthetic controlled probe",
                [new ExecutionPlanStep(0, "system.events", arguments, "offline")]), null);
}
