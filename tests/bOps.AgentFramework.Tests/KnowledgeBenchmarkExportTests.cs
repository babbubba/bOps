// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using bOps.Abstractions;
using bOps.Knowledge.Tests;

namespace bOps.AgentFramework.Tests;

public sealed class KnowledgeBenchmarkExportTests
{
    [Fact]
    public async Task CompletedSyntheticTask_ExportsObservedMetricsAndOrderedAudit()
    {
        using var temporary = new TempDirectory();
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, corpus);
        var output = temporary.Sub("completed");
        Directory.CreateDirectory(output);
        int eventCount = 0;
        int actualCalls = 0;
        await KnowledgeBenchmarkExport.RunAsync(output, "A", async export =>
        {
            export.World = new KnowledgeRunnerWorld();
            export.Commit = "offline-commit";
            using var metadata = JsonDocument.Parse("""{"ctxSize":8192,"sampling":{"temperature":0.5,"credential":"secret"},"retryPolicy":{"maxAttempts":2},"runtimeLimits":{"maxSteps":15}}""");
            export.Configuration = KnowledgeBenchmarkExport.SafeConfiguration(metadata.RootElement);
            export.CorpusSha256 = hash;
            var model = new BenchmarkProbe([KnowledgeBenchmark.Plan with { Usage = new ModelUsage(10, 3, null) },
                KnowledgeBenchmark.Events with { Usage = new ModelUsage(20, 4, null) },
                Script.Final("offline done") with { Usage = new ModelUsage(30, 5, null) }]);
            var rig = KnowledgeBenchmark.ComposeReplay(export.World, model, "A", path, hash, observation: export.ModelObservation);
            export.Store = rig.Store;
            await export.ExecuteTaskAsync(() => rig.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA, export.RunId));
            eventCount = export.World.Events.Count;
            actualCalls = model.Requests.Count;
            Assert.Equal(AgentTaskStatus.Completed, export.State!.Status);
        });
        using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "A", "result.json")));
        var root = result.RootElement;
        Assert.Equal("Completed", root.GetProperty("status").GetString());
        Assert.Equal("Completed", root.GetProperty("terminalCause").GetString());
        Assert.StartsWith("offline done", root.GetProperty("finalObservation").GetString(), StringComparison.Ordinal);
        Assert.Equal(hash, root.GetProperty("corpusSha256").GetString());
        Assert.Equal(0.5, root.GetProperty("configuration").GetProperty("sampling").GetProperty("temperature").GetDouble());
        Assert.False(root.GetProperty("configuration").GetProperty("sampling").TryGetProperty("credential", out _));
        Assert.Equal(actualCalls, root.GetProperty("llmCalls").GetInt32());
        Assert.Equal(60, root.GetProperty("reportedPromptTokens").GetInt64());
        Assert.Equal(12, root.GetProperty("reportedCompletionTokens").GetInt64());
        Assert.Equal(72, root.GetProperty("accounting").GetProperty("tokensUsed").GetInt64());
        Assert.Equal(1, root.GetProperty("auditedToolsExecuted").GetInt32());
        Assert.Equal(0, root.GetProperty("auditedToolsRefused").GetInt32());
        Assert.False(root.GetProperty("knowledgeUsed").GetBoolean());
        var lines = await File.ReadAllLinesAsync(Path.Combine(output, "A", "audit.jsonl"));
        Assert.Equal(eventCount, lines.Length);
        DateTimeOffset? previous = null;
        foreach (var line in lines)
        {
            using var evt = JsonDocument.Parse(line);
            var timestamp = evt.RootElement.GetProperty("TimestampUtc").GetDateTimeOffset();
            Assert.True(previous is null || timestamp >= previous);
            previous = timestamp;
            Assert.False(evt.RootElement.TryGetProperty("Arguments", out _));
            Assert.False(evt.RootElement.TryGetProperty("Summary", out _));
        }
    }

    [Fact]
    public async Task InjectedFailures_ExportNullsCausesAndNeverOverwrite()
    {
        using var temporary = new TempDirectory();
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, corpus);
        foreach (var scenario in new[] { "before", "during", "protocol", "timeout", "cancel", "limit" })
        {
            var output = temporary.Sub(scenario);
            Directory.CreateDirectory(output);
            var execution = KnowledgeBenchmarkExport.RunAsync(output, "A", async export =>
            {
                if (scenario == "before") throw new IOException("secret payload must not be exported");
                export.World = new KnowledgeRunnerWorld();
                if (scenario == "limit")
                {
                    await export.ExecuteTaskAsync(() => ExhaustCallLimitAsync(export.ModelObservation));
                    return;
                }
                var model = scenario == "protocol" ? (IChatModel)new FailingModel() : new BenchmarkProbe([]);
                var rig = KnowledgeBenchmark.ComposeReplay(export.World, model, "A", path, hash, observation: export.ModelObservation);
                export.Store = rig.Store;
                await export.ExecuteTaskAsync(() => scenario switch
                {
                    "during" => FailAfterTaskCreatedAsync(rig, export.RunId),
                    "timeout" => throw new TimeoutException("secret timeout payload"),
                    "cancel" => throw new OperationCanceledException("secret cancellation payload"),
                    _ => rig.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA, export.RunId)
                });
            });
            if (scenario is "before" or "during" or "timeout" or "cancel" or "limit") await Assert.ThrowsAnyAsync<Exception>(() => execution);
            else await execution;
            var resultPath = Path.Combine(output, "A", "result.json");
            var bytes = await File.ReadAllBytesAsync(resultPath);
            using var result = JsonDocument.Parse(bytes);
            var root = result.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("finalObservation").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("reportedPromptTokens").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("reportedCompletionTokens").ValueKind);
            var expected = scenario switch { "before" => "InfrastructureBeforeObservedTask", "during" => "InfrastructureError",
                "protocol" => "ProviderOrProtocolError", "timeout" => "Timeout", "cancel" => "Cancelled", _ => "LlmCallLimit" };
            Assert.Equal(expected, root.GetProperty("terminalCause").GetString());
            if (scenario == "before")
            {
                Assert.Equal(JsonValueKind.Null, root.GetProperty("status").ValueKind);
                Assert.Equal(JsonValueKind.Null, root.GetProperty("accounting").ValueKind);
                Assert.Equal(JsonValueKind.Null, root.GetProperty("llmCalls").ValueKind);
                Assert.Equal(JsonValueKind.Null, root.GetProperty("knowledgeReads").ValueKind);
                Assert.Equal("NotObserved", root.GetProperty("auditStatus").GetString());
            }
            if (scenario == "during") Assert.Equal("Persisted", root.GetProperty("stateSource").GetString());
            if (scenario == "protocol") Assert.Equal("Failed", root.GetProperty("status").GetString());
            if (scenario == "limit") Assert.Equal(5, root.GetProperty("llmCalls").GetInt32());
            foreach (var line in await File.ReadAllLinesAsync(Path.Combine(output, "A", "audit.jsonl")))
            {
                using var evt = JsonDocument.Parse(line);
                Assert.False(evt.RootElement.TryGetProperty("ErrorMessage", out _));
            }
            Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            await Assert.ThrowsAsync<IOException>(() => KnowledgeBenchmarkExport.RunAsync(output, "A", _ => Task.CompletedTask));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(resultPath));
        }
        var occupied = temporary.Sub("file");
        await File.WriteAllTextAsync(occupied, "occupied");
        var error = await Assert.ThrowsAsync<IOException>(() => KnowledgeBenchmarkExport.RunAsync(occupied, "A", _ => Task.CompletedTask));
        Assert.Contains("no artifacts saved", error.Message, StringComparison.Ordinal);
    }

    private static async Task<TaskState> ExhaustCallLimitAsync(BenchmarkModelObservation observation)
    {
        var probe = new BenchmarkProbe(Enumerable.Repeat(KnowledgeBenchmark.Events with { Usage = null }, 6));
        var bounded = new KnowledgeBenchmark.BenchmarkModel(probe, observation);
        observation.Calls = 0;
        try
        {
            for (var i = 0; i < 6; i++) await bounded.CompleteAsync(new ModelRequest("offline", [], []));
            throw new InvalidOperationException("The budget probe did not stop.");
        }
        finally { Assert.Equal(5, probe.Requests.Count); }
    }

    private static async Task<TaskState> FailAfterTaskCreatedAsync(CanaryRig rig, Guid id)
    {
        await rig.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA, id);
        throw new IOException("secret post-task payload");
    }

    private sealed class FailingModel : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new("offline", KnowledgeBenchmark.ModelId);
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            throw new ModelProtocolException("secret provider payload") { FailureKind = ModelFailureKind.MalformedResponse };
    }
}
