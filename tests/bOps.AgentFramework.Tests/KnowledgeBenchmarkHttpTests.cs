// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Knowledge.Tests;
using bOps.Knowledge;
using bOps.Packages.Providers.LlamaCpp;
using bOps.Runtime;
using bOps.Runtime.Tests;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.AgentFramework.Tests;

public sealed class KnowledgeBenchmarkHttpTests
{
    private static readonly ModelRequest Request = new("offline", [], []);

    [Fact]
    public async Task RetryAndJsonFallback_CountTwentyActualHttpAndRefuseTwentyFirstBeforeTransport()
    {
        using var temporary = new TempDirectory();
        var output = temporary.Sub("http");
        Directory.CreateDirectory(output);
        using var transport = new OfflineTransport(async (index, ct) =>
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            return index == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Reply(index is 2 or 4 ? "invalid JSON" : "{\"final\":\"offline done\"}", usage: true);
        });
        await KnowledgeBenchmarkExport.RunAsync(output, "A", async export =>
        {
            export.World = new KnowledgeRunnerWorld();
            using var budget = new BenchmarkHttpBudget(transport);
            using var client = new HttpClient(budget, disposeHandler: false);
            var model = CreateModel(client, export.ModelObservation, budget, native: false);
            var broker = Broker(model, export.World, export.ModelObservation, new AgentRunnerOptions
            {
                ModelCallMaxAttempts = 2, ModelRetryBaseDelay = TimeSpan.FromMilliseconds(1),
                ModelRetryMaxDelay = TimeSpan.FromMilliseconds(1), ModelCallAttemptTimeout = TimeSpan.FromSeconds(90)
            });
            var context = new ModelInvocationContext(export.RunId, 0, KnowledgeRunnerWorld.ActorA, new List<ModelCallRecord>());
            Assert.True((await broker.InvokeAsync(Request, context)).IsFinal);
            Assert.True((await broker.InvokeAsync(Request, context)).IsFinal);
            for (var i = 5; i < 20; i++) Assert.True((await broker.InvokeAsync(Request, context)).IsFinal);
            var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => broker.InvokeAsync(Request, context));
            Assert.Equal(ModelFailureKind.Unknown, failure.FailureKind);
            Assert.Equal(20, transport.Sends);
            Assert.Equal(20, budget.Sent);
            Assert.Equal(1, budget.Refused);
            Assert.Equal(19, export.ModelObservation.Calls);
            Assert.Equal(17, export.ModelObservation.CompletedCalls);
            Assert.Equal(2, export.ModelObservation.FailedCalls);
            Assert.Contains(export.World.Events.OfType<ModelCallAuditEvent>(), evt => evt.RetryDelayMs is not null);
        });
        using var result = await ReadResultAsync(output);
        Assert.Equal(2, result.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("HttpBudgetExhausted", result.RootElement.GetProperty("terminalCause").GetString());
        Assert.Equal(18, result.RootElement.GetProperty("logicalModelCalls").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("modelRetries").GetInt32());
        Assert.Equal(20, result.RootElement.GetProperty("httpRequestsSent").GetInt32());
        Assert.Equal(20, result.RootElement.GetProperty("httpRequestLimit").GetInt32());
        Assert.Equal(17, result.RootElement.GetProperty("usageReportingAttempts").GetInt32());
        Assert.Equal(119, result.RootElement.GetProperty("reportedPromptTokens").GetInt32());
        Assert.Equal(51, result.RootElement.GetProperty("reportedCompletionTokens").GetInt32());
        await VerifyAuditAsync(output, "BudgetExhausted", 21);
    }

    [Fact]
    public async Task IndependentConditionsAndDeadlines_ExportBudgetTimeoutProviderAndComparisonCorrectly()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var deadlineA = KnowledgeBenchmark.ConditionDeadline(clock);
        clock.Advance(TimeSpan.FromSeconds(1199));
        Assert.False(deadlineA.IsCancellationRequested);
        using var deadlineB = KnowledgeBenchmark.ConditionDeadline(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(deadlineA.IsCancellationRequested);
        Assert.False(deadlineB.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1198));
        Assert.False(deadlineB.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(deadlineB.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromSeconds(1200), KnowledgeBenchmark.RunnerOptions.MaxAttemptDuration);
        Assert.Equal(TimeSpan.FromSeconds(90), KnowledgeBenchmark.RunnerOptions.ModelCallAttemptTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), KnowledgeBenchmark.RunnerOptions.ModelCallBudget);
        Assert.Equal(3, KnowledgeBenchmark.RunnerOptions.ModelCallMaxAttempts);
        KnowledgeBenchmark.RunnerOptions.Validate(TimeSpan.FromSeconds(120));
        Assert.True(KnowledgeBenchmark.RunnerOptions.ModelCallAttemptTimeout < TimeSpan.FromSeconds(120));
        Assert.True(KnowledgeBenchmark.RunnerOptions.ModelCallBudget < KnowledgeBenchmark.RunnerOptions.MaxAttemptDuration);
        using var temporary = new TempDirectory();
        foreach (var scenario in new[] { "timeout", "provider", "budget" })
        {
            var output = temporary.Sub(scenario);
            Directory.CreateDirectory(output);
            using var transport = new OfflineTransport(async (_, ct) =>
            {
                if (scenario == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return scenario == "provider" ? new HttpResponseMessage(HttpStatusCode.BadRequest) : Reply("offline done");
            });
            await Assert.ThrowsAsync<ModelProtocolException>(() => KnowledgeBenchmarkExport.RunAsync(output, "A", async export =>
            {
                export.World = new KnowledgeRunnerWorld();
                using var budget = new BenchmarkHttpBudget(transport);
                using var client = new HttpClient(budget, disposeHandler: false);
                var model = CreateModel(client, export.ModelObservation, budget, native: true);
                if (scenario == "budget")
                    for (var i = 0; i < 20; i++) Assert.Null((await model.CompleteAsync(Request)).Usage);
                var options = new AgentRunnerOptions
                {
                    ModelCallMaxAttempts = 1, ModelCallAttemptTimeout = TimeSpan.FromMilliseconds(50)
                };
                options.Validate(client.Timeout);
                var broker = Broker(model, export.World, export.ModelObservation, options);
                await broker.InvokeAsync(Request, new ModelInvocationContext(export.RunId, 0,
                    KnowledgeRunnerWorld.ActorA, new List<ModelCallRecord>()));
            }));
            using var result = await ReadResultAsync(output);
            var root = result.RootElement;
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(scenario switch { "timeout" => "Timeout", "provider" => "ProviderOrProtocolError", _ => "HttpBudgetExhausted" },
                root.GetProperty("terminalCause").GetString());
            Assert.Equal(scenario == "budget" ? 20 : 1, transport.Sends);
            Assert.Equal(transport.Sends, root.GetProperty("httpRequestsSent").GetInt32());
            Assert.Equal(scenario == "budget" ? 1 : 0, root.GetProperty("httpRequestsRefused").GetInt32());
            Assert.Equal(0, root.GetProperty("usageReportingAttempts").GetInt32());
            foreach (var name in new[] { "reportedPromptTokens", "reportedCompletionTokens", "finalObservation", "accounting" })
                Assert.Equal(JsonValueKind.Null, root.GetProperty(name).ValueKind);
            Assert.Equal(scenario == "timeout" ? "Timeout" : scenario == "budget" ? "Unknown" : "InvalidRequest",
                root.GetProperty("failureKind").GetString());
            await VerifyAuditAsync(output, scenario switch
            {
                "timeout" => "CancelledOrTimedOut", "provider" => "ResponseHeadersReceived", _ => "BudgetExhausted"
            }, scenario == "budget" ? 21 : 1);
        }
        await VerifyIndependentComparisonAsync(temporary);
    }

    private static async Task VerifyIndependentComparisonAsync(TempDirectory temporary)
    {
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, corpus);
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        var output = temporary.Sub("comparison");
        Directory.CreateDirectory(output);
        var observations = new List<BenchmarkModelObservation>();
        await KnowledgeBenchmarkComparison.RunAsync(output, async (knowledge, export) =>
        {
            observations.Add(export.ModelObservation);
            export.World = new KnowledgeRunnerWorld();
            export.CorpusSha256 = hash;
            export.CorpusProvenance = CanaryEventsCorpus.Provenance(corpus, hash);
            export.Configuration = new JsonObject { ["runtimeLimits"] = new JsonObject
                { ["maximumHttpInvocations"] = 20, ["maxAttemptDurationMs"] = 1200000 } };
            export.ProviderRequestTimeout = TimeSpan.FromSeconds(120);
            using var transport = new OfflineTransport((index, _) => Task.FromResult(
                Reply(index % 2 == 1 ? "invalid JSON" : "{\"final\":\"offline wire response\"}")));
            using var budget = new BenchmarkHttpBudget(transport);
            using var client = new HttpClient(budget, disposeHandler: false);
            export.ModelObservation.Http = budget;
            var provider = new LlamaCppProviderPackage(new ClientFactory(client));
            var wireModel = provider.Create(new ChatModelOptions("LlamaCpp", "http://offline.invalid/v1", null,
                KnowledgeBenchmark.ModelId, false) { RequestTimeout = TimeSpan.FromSeconds(120) });
            var rig = KnowledgeBenchmark.ComposeReplay(export.World, new OfflineWorkflow(wireModel, knowledge),
                knowledge ? "B" : "A", path, hash, knowledge ? raw : null, knowledge ? trust : null,
                knowledge ? KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span) : null,
                knowledge ? KnowledgeBenchmark.GrantAuthorization : null, export.ModelObservation);
            export.Store = rig.Store;
            if (!knowledge)
            {
                // Spend this arm's first 18 dispatches through the real broker/adapter before the task loop.
                // Repeated tool decisions would hit the unchanged replan limit before the HTTP ceiling.
                var prelude = Broker(new KnowledgeBenchmark.BenchmarkModel(wireModel, export.ModelObservation),
                    export.World, export.ModelObservation, KnowledgeBenchmark.RunnerOptions);
                var context = new ModelInvocationContext(export.RunId, 0, KnowledgeRunnerWorld.ActorA, new List<ModelCallRecord>());
                for (var i = 0; i < 9; i++) await prelude.InvokeAsync(Request, context);
            }
            using var deadline = KnowledgeBenchmark.ConditionDeadline();
            try
            {
                await export.ExecuteTaskAsync(() => rig.Runner.RunAsync(KnowledgeBenchmark.Goal,
                    KnowledgeRunnerWorld.ActorA, export.RunId, deadline.Token));
            }
            finally { export.ModelObservation.ConditionTimedOut = deadline.IsCancellationRequested; }
            if (knowledge) Assert.InRange(transport.Sends, 8, 18);
            else Assert.Equal(20, transport.Sends);
        }, (knowledge, export) => Assert.Equal(knowledge ? AgentTaskStatus.Completed : AgentTaskStatus.Failed, export.State!.Status));
        Assert.NotSame(observations[0].Http, observations[1].Http);
        Assert.Equal(11, observations[0].Calls);
        Assert.Equal(10, observations[0].CompletedCalls);
        Assert.Equal(1, observations[0].FailedCalls);
        Assert.InRange(observations[1].Calls!.Value, 4, 9);
        Assert.Equal(observations[1].Calls, observations[1].CompletedCalls);
        Assert.Equal(observations[1].Calls * 2, observations[1].Http!.Sent);
        Assert.Equal(0, observations[1].FailedCalls);
        foreach (var condition in new[] { "A", "B" })
        {
            using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, condition, "result.json")));
            var root = result.RootElement;
            Assert.Equal(condition == "A" ? "HttpBudgetExhausted" : "Completed", root.GetProperty("terminalCause").GetString());
            Assert.Equal(observations[condition == "A" ? 0 : 1].Http!.Sent, root.GetProperty("httpRequestsSent").GetInt32());
            Assert.Equal(condition == "A" ? 1 : 0, root.GetProperty("httpRequestsRefused").GetInt32());
            Assert.Equal(20, root.GetProperty("httpRequestLimit").GetInt32());
            Assert.Equal("00:20:00", root.GetProperty("effectiveBenchmarkTimeouts").GetProperty("condition").GetString());
            Assert.Equal(0, root.GetProperty("modelRetries").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("reportedPromptTokens").ValueKind);
        }
        using var comparison = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "comparison.json")));
        Assert.Equal("Complete", comparison.RootElement.GetProperty("comparisonStatus").GetString());
        Assert.True(comparison.RootElement.GetProperty("bothArmsExecuted").GetBoolean());
        Assert.Equal("HttpBudgetExhausted", comparison.RootElement.GetProperty("arms")[0].GetProperty("terminalCause").GetString());
        Assert.Equal("Completed", comparison.RootElement.GetProperty("arms")[1].GetProperty("terminalCause").GetString());
        using var metadata = JsonDocument.Parse("""{"runtimeLimits":{"maximumHttpInvocations":20,"maxAttemptDurationMs":1200000}}""");
        Assert.Equal(20, KnowledgeBenchmarkExport.SafeConfiguration(metadata.RootElement)["runtimeLimits"]!["maximumHttpInvocations"]!.GetValue<int>());
    }

    // Scripted agent decisions over the real adapter and offline HTTP transport; no LLM or native capture.
    private sealed class OfflineWorkflow(IChatModel wireModel, bool complete) : IChatModel
    {
        private int calls;
        public ChatModelDescriptor Descriptor => wireModel.Descriptor;
        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var wireResponse = await wireModel.CompleteAsync(request, ct);
            var decision = ++calls == 1 ? KnowledgeBenchmark.Plan
                : complete && calls == 2 ? KnowledgeBenchmark.Read
                : complete && calls >= 4 ? Script.Final("offline done") : KnowledgeBenchmark.Events;
            return decision with { Usage = wireResponse.Usage };
        }
    }

    private static KnowledgeBenchmark.BenchmarkModel CreateModel(HttpClient client, BenchmarkModelObservation observation,
        BenchmarkHttpBudget budget, bool native)
    {
        observation.Http = budget;
        observation.Calls = 0;
        var provider = new LlamaCppProviderPackage(new ClientFactory(client));
        return new(provider.Create(new ChatModelOptions("LlamaCpp", "http://offline.invalid/v1", null, KnowledgeBenchmark.ModelId, native)
        {
            RequestTimeout = TimeSpan.FromSeconds(120)
        }), observation);
    }

    private static ModelInvocationBroker Broker(IChatModel model, KnowledgeRunnerWorld world,
        BenchmarkModelObservation observation, AgentRunnerOptions options)
    {
        observation.RunnerOptions = options;
        return new(model, world.Audit, TimeProvider.System, NullLogger.Instance, options);
    }

    private static HttpResponseMessage Reply(string text, bool usage = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = KnowledgeBenchmark.ModelId,
            usage = usage ? new { prompt_tokens = 7, completion_tokens = 3 } : null,
            choices = new[] { new { message = new { role = "assistant", content = text }, finish_reason = "stop" } }
        }))
    };

    private static async Task<JsonDocument> ReadResultAsync(string output) =>
        JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "A", "result.json")));

    private static async Task VerifyAuditAsync(string output, string outcome, int count)
    {
        var httpCount = 0;
        var found = false;
        DateTimeOffset? previous = null;
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(output, "A", "audit.jsonl")))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var timestamp = root.GetProperty("TimestampUtc").GetDateTimeOffset();
            Assert.True(previous is null || timestamp >= previous);
            previous = timestamp;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "BenchmarkHttpAttempt") continue;
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
            httpCount++;
            found |= root.GetProperty("Outcome").GetString() == outcome;
            Assert.False(root.TryGetProperty("Usage", out _));
        }
        Assert.Equal(count, httpCount);
        Assert.True(found);
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class OfflineTransport(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(++Sends, cancellationToken);
    }
}
