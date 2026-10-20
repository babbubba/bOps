// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;

namespace bOps.AgentFramework.Tests;

public sealed class KnowledgeBenchmarkComparisonTests
{
    [WindowsOnlyFact]
    public async Task ModelFailureInA_StillExecutesBAndPreservesBothOutcomes()
    {
        using var temporary = new TempDirectory();
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, corpus);
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        var output = temporary.Sub("comparison");
        Directory.CreateDirectory(output);
        var exports = new List<KnowledgeBenchmarkExport>();
        var models = new List<IChatModel>();
        await KnowledgeBenchmarkComparison.RunAsync(output, async (knowledge, export) =>
        {
            exports.Add(export);
            export.World = new KnowledgeRunnerWorld();
            export.CorpusSha256 = hash;
            export.CorpusProvenance = CanaryEventsCorpus.Provenance(corpus, hash);
            export.Configuration = new JsonObject { ["modelSha256"] = new string('a', 64) };
            IChatModel model = knowledge
                ? new BenchmarkProbe([KnowledgeBenchmark.Plan, KnowledgeBenchmark.Read, KnowledgeBenchmark.Events, Script.Final("secret final")])
                : new FailingModel();
            models.Add(model);
            var rig = KnowledgeBenchmark.ComposeReplay(export.World, model, knowledge ? "B" : "A", path, hash,
                knowledge ? raw : null, knowledge ? trust : null,
                knowledge ? KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span) : null,
                knowledge ? KnowledgeBenchmark.GrantAuthorization : null, export.ModelObservation);
            export.Store = rig.Store;
            await export.ExecuteTaskAsync(() => rig.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA, export.RunId));
        }, (knowledge, export) =>
        {
            Assert.Equal(knowledge ? AgentTaskStatus.Completed : AgentTaskStatus.Failed, export.State!.Status);
        });
        Assert.Equal(2, exports.Count);
        Assert.NotSame(models[0], models[1]);
        Assert.Equal(1, exports[0].ModelObservation.Calls);
        Assert.Equal(((BenchmarkProbe)models[1]).Requests.Count, exports[1].ModelObservation.Calls);
        Assert.True(exports[1].ModelObservation.Calls > exports[0].ModelObservation.Calls);
        Assert.Null(exports[0].State!.KnowledgeBinding);
        Assert.Empty(exports[0].World!.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Contains(exports[1].World!.Events.OfType<KnowledgeReadAuditEvent>(), e => e.ResultCode == KnowledgeReadResultCode.Success);
        var a = await ReadAsync(output, "A", "result.json");
        var b = await ReadAsync(output, "B", "result.json");
        Assert.Equal("Failed", a["status"]!.GetValue<string>());
        Assert.Equal("ModelFailure", a["terminalKind"]!.GetValue<string>());
        Assert.Equal("MalformedResponse", a["failureKind"]!.GetValue<string>());
        Assert.Equal("Completed", b["status"]!.GetValue<string>());
        Assert.Null(a["finalObservation"]);
        foreach (var condition in new[] { "A", "B" })
        {
            Assert.True(File.Exists(Path.Combine(output, condition, "audit.jsonl")));
            Assert.DoesNotContain("secret", await File.ReadAllTextAsync(Path.Combine(output, condition, "result.json")), StringComparison.Ordinal);
        }
        var report = await ReadAsync(output, "comparison.json");
        Assert.Equal("Complete", report["comparisonStatus"]!.GetValue<string>());
        Assert.True(report["bothArmsExecuted"]!.GetValue<bool>());
        Assert.True(report["benchmarkExecutionSucceeded"]!.GetValue<bool>());
        Assert.Equal(2, a["schemaVersion"]!.GetValue<int>());
        Assert.Equal(CanaryEventsCorpus.Encode(corpus, hash), await File.ReadAllBytesAsync(path));
    }

    [WindowsOnlyFact]
    public async Task IntegrityRefusalAfterA_PreservesEvidenceAndStopsBeforeB()
    {
        using var temporary = new TempDirectory();
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, corpus);
        var output = temporary.Sub("interrupted");
        Directory.CreateDirectory(output);
        var executed = new List<bool>();
        byte[]? auditBefore = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeBenchmarkComparison.RunAsync(output, async (knowledge, export) =>
        {
            executed.Add(knowledge);
            export.World = new KnowledgeRunnerWorld();
            export.CorpusSha256 = hash;
            var rig = KnowledgeBenchmark.ComposeReplay(export.World,
                new BenchmarkProbe([KnowledgeBenchmark.Plan, KnowledgeBenchmark.Events, Script.Final("done")]), "A", path, hash,
                observation: export.ModelObservation);
            export.Store = rig.Store;
            await export.ExecuteTaskAsync(() => rig.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA, export.RunId));
            auditBefore = await File.ReadAllBytesAsync(export.World.AuditFile);
            // An integrity exception after A must preserve its real outcome and prevent B.
            var changed = (JsonObject)corpus.DeepClone();
            changed["output"] = "tampered secret";
            await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(changed, hash));
            _ = CanaryEventsCorpus.Read(path, hash);
        }, (_, _) => throw new InvalidOperationException("Final validation must not be reached.")));
        Assert.False(Assert.Single(executed));
        Assert.False(Directory.Exists(Path.Combine(output, "B")));
        var resultBytes = await File.ReadAllBytesAsync(Path.Combine(output, "A", "result.json"));
        var auditBytes = await File.ReadAllBytesAsync(Path.Combine(output, "A", "audit.jsonl"));
        Assert.NotEmpty(auditBefore!);
        var result = JsonNode.Parse(resultBytes)!;
        Assert.Equal("Completed", result["status"]!.GetValue<string>());
        Assert.Equal("InfrastructureError", result["terminalCause"]!.GetValue<string>());
        Assert.Equal("InvalidOperationException", result["exceptionType"]!.GetValue<string>());
        Assert.DoesNotContain("tampered secret", System.Text.Encoding.UTF8.GetString(resultBytes), StringComparison.Ordinal);
        var report = await ReadAsync(output, "comparison.json");
        Assert.Equal("InterruptedInfrastructureError", report["comparisonStatus"]!.GetValue<string>());
        Assert.False(report["bothArmsExecuted"]!.GetValue<bool>());
        Assert.False(report["benchmarkExecutionSucceeded"]!.GetValue<bool>());
        await Assert.ThrowsAsync<IOException>(() => KnowledgeBenchmarkComparison.RunAsync(output, (_, _) => Task.CompletedTask, (_, _) => { }));
        Assert.Equal(resultBytes, await File.ReadAllBytesAsync(Path.Combine(output, "A", "result.json")));
        Assert.Equal(auditBytes, await File.ReadAllBytesAsync(Path.Combine(output, "A", "audit.jsonl")));
    }

    private static async Task<JsonNode> ReadAsync(string root, params string[] parts) =>
        JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(new[] { root }.Concat(parts).ToArray())))!;

    private sealed class FailingModel : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new("offline", KnowledgeBenchmark.ModelId);
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            throw new ModelProtocolException("secret provider payload") { FailureKind = ModelFailureKind.MalformedResponse };
    }
}
