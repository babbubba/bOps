// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Knowledge.Tests;
using bOps.Packages.Providers.LlamaCpp;
using bOps.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.AgentFramework.Tests;

public sealed class KnowledgeBenchmarkHttpTests
{
    private static readonly ModelRequest Request = new("offline", [], []);

    [Fact]
    public async Task RetryAndJsonFallback_CountActualHttpAndRefuseSixthBeforeTransport()
    {
        using var temporary = new TempDirectory();
        var output = temporary.Sub("http");
        Directory.CreateDirectory(output);
        using var transport = new OfflineTransport(async (index, ct) =>
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            return index == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Reply(index is 2 or 4 ? "invalid JSON" : "{\"final\":\"offline done\"}");
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
            var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => broker.InvokeAsync(Request, context));
            Assert.Equal(ModelFailureKind.Unknown, failure.FailureKind);
            Assert.Equal(5, transport.Sends);
            Assert.Equal(5, budget.Sent);
            Assert.Equal(1, budget.Refused);
            Assert.Equal(4, export.ModelObservation.Calls);
            Assert.Equal(2, export.ModelObservation.CompletedCalls);
            Assert.Equal(2, export.ModelObservation.FailedCalls);
            Assert.Contains(export.World.Events.OfType<ModelCallAuditEvent>(), evt => evt.RetryDelayMs is not null);
        });
        using var result = await ReadResultAsync(output);
        Assert.Equal(2, result.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("HttpBudgetExhausted", result.RootElement.GetProperty("terminalCause").GetString());
        Assert.Equal(3, result.RootElement.GetProperty("logicalModelCalls").GetInt32());
        Assert.Equal(5, result.RootElement.GetProperty("httpRequestsSent").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("reportedPromptTokens").ValueKind);
        await VerifyAuditAsync(output, "BudgetExhausted", 6);
    }

    [Fact]
    public async Task TimeoutProviderErrorAndBudget_ExportDistinctCausesWithoutInventedUsage()
    {
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
                    for (var i = 0; i < 5; i++) Assert.Null((await model.CompleteAsync(Request)).Usage);
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
            Assert.Equal(scenario == "budget" ? 5 : 1, transport.Sends);
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
            }, scenario == "budget" ? 6 : 1);
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

    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = KnowledgeBenchmark.ModelId,
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
