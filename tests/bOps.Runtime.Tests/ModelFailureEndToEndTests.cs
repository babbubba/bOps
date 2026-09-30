// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging.Abstractions;

// Each handler/HttpClient pair lives exactly as long as its test method; nothing holds an OS handle worth disposing.
#pragma warning disable CA2000

namespace bOps.Runtime.Tests;

/// <summary>
/// E2E-4 and E2E-5 of the hardening plan (ADR-0039): the real <see cref="AgentRunner"/> and the real OpenAI-compatible
/// adapter over a scripted HTTP provider. A transient provider failure recovers within bounds; a permanent one ends the task
/// with the right kind and an actionable reason; every HTTP request is exactly one audited attempt. The runtime project names
/// no provider; only this test does.
/// </summary>
public sealed class ModelFailureEndToEndTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private const string IncidentBody =
        """
        {"error":{"message":"Provider returned error","code":400,"metadata":{"raw":"{ \"error\": { \"code\": 400, \"message\": \"Validation: Function at index 0 has an invalid name: \\\"fs.size\\\". Only a-z, A-Z, 0-9, underscores, and dashes are allowed.\", \"type\": \"Bad Request\" } }","provider_name":"Poolside","is_byok":false,"previous_errors":[{"code":429,"message":"Rate limit exceeded"},{"code":429,"message":"Rate limit exceeded"}]}},"user_id":"user_2xYzSyntheticIdentifier9"}
        """;

    private static string Text(string content) =>
        new JsonObject
        {
            ["model"] = "vendor/picked",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                ["finish_reason"] = "stop",
            }),
        }.ToJsonString();

    private static readonly string PlanBody = Text("""{"rationale":"r","steps":[]}""");

    private static (AgentRunner Runner, RecordingAuditSink Audit, InMemoryTaskStore Store, List<TimeSpan> Delays, ScriptedProvider Provider) Create(
        params Func<HttpResponseMessage>[] responses)
    {
        var provider = new ScriptedProvider(responses);
        var model = new OpenAiCompatibleChatModel(
            new ChatModelOptions("OpenRouter", "https://openrouter.ai/api/v1", new SecretReference("environment", "TEST_KEY"), "openrouter/free")
            {
                ResolvedApiKey = "sk-or-v1-0123456789abcdef0123456789abcdef",
            },
            new HttpClient(provider));
        var audit = new RecordingAuditSink();
        var store = new InMemoryTaskStore();
        var runner = new AgentRunner(
            model, new ToolRegistry(new AlwaysAvailableCapabilityProbe()), new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit, store, TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());
        var delays = new List<TimeSpan>();
        runner.ModelRetryJitter = () => 0.0;
        runner.ModelRetryDelay = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        return (runner, audit, store, delays, provider);
    }

    private static Func<HttpResponseMessage> Http(HttpStatusCode status, string body, TimeSpan? retryAfter = null) =>
        () =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is { } wait)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
            }

            return response;
        };

    // E2E-4: 429 with Retry-After, then 503, then success.
    [Fact]
    public async Task ATransientProviderFailure_RecoversWithinBounds_AndEveryAttemptIsAudited()
    {
        var (runner, audit, store, delays, provider) = Create(
            Http(HttpStatusCode.OK, PlanBody),
            Http(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit exceeded","code":429}}""", TimeSpan.FromSeconds(2)),
            Http(HttpStatusCode.ServiceUnavailable, "overloaded"),
            Http(HttpStatusCode.OK, Text("All good.")));

        var task = await runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, (await store.LoadAsync(task.Id))!.Status);
        Assert.Equal(4, provider.Requests);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)], delays);
        var events = audit.Events.OfType<ModelCallAuditEvent>().Where(e => e.StepIndex == 0).ToList();
        Assert.Equal(
            [(1, (ModelFailureKind?)ModelFailureKind.RateLimited, (int?)429), (2, ModelFailureKind.Transient, 503), (3, null, null)],
            events.Select(e => (e.ModelAttempt!.Value, e.FailureKind, e.ProviderStatusCode)));
    }

    // E2E-5: the incident 400 on a step call.
    [Fact]
    public async Task ThePermanentIncident400_EndsTheTaskFailed_WithTheUpstreamReason_AndNoRetry()
    {
        var (runner, audit, store, delays, provider) = Create(Http(HttpStatusCode.OK, PlanBody), Http(HttpStatusCode.BadRequest, IncidentBody));

        var task = await runner.RunAsync("check", Actor);

        var stored = (await store.LoadAsync(task.Id))!;
        Assert.Equal(AgentTaskStatus.Failed, stored.Status);
        Assert.Equal(2, provider.Requests);
        Assert.Empty(delays);
        var reason = stored.Steps[^1].Observation!;
        Assert.StartsWith("Provider rejected the model request.", reason, StringComparison.Ordinal);
        Assert.Contains("Function at index 0 has an invalid name", reason, StringComparison.Ordinal);
        Assert.Contains("Poolside", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("user_2xYzSyntheticIdentifier9", reason, StringComparison.Ordinal);
        var failed = Assert.Single(audit.Events.OfType<ModelCallAuditEvent>(), e => e.Outcome == ModelCallOutcome.Failure);
        Assert.Equal((ModelFailureKind.InvalidRequest, ModelRetryDecision.NotRetryable, 400),
            (failed.FailureKind!.Value, failed.RetryDecision!.Value, failed.ProviderStatusCode!.Value));
        Assert.DoesNotContain("user_2xYzSyntheticIdentifier9", failed.ErrorMessage, StringComparison.Ordinal);
    }

    // E2E-5: a 401 on the planning call names the provider configuration.
    [Fact]
    public async Task A401_EndsTheTaskFailed_NamingTheProviderCredential_InOneAttempt()
    {
        var (runner, _, store, delays, provider) = Create(
            Http(HttpStatusCode.Unauthorized, """{"error":{"message":"No auth credentials found","code":401}}"""));

        var task = await runner.RunAsync("check", Actor);

        var stored = (await store.LoadAsync(task.Id))!;
        Assert.Equal(AgentTaskStatus.Failed, stored.Status);
        Assert.Equal(1, provider.Requests);
        Assert.Empty(delays);
        Assert.StartsWith(
            "Provider authentication failed. Check the configured provider credential.", stored.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.Contains("No auth credentials found", stored.Steps[^1].Observation, StringComparison.Ordinal);
    }

    private sealed class ScriptedProvider(Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _requests) - 1;
            return Task.FromResult(responses[index]());
        }
    }

    // E2E-4 (stall): the plan reply arrives, then the provider stops answering — the shape of the original incident.
    private sealed class StallingProvider : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PlanBody) };
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static async Task<(TaskState Task, InMemoryTaskStore Store, RecordingAuditSink Audit, List<TimeSpan> Delays, StallingProvider Provider)>
        RunStalledAsync(TimeSpan attemptTimeout, TimeSpan transportTimeout)
    {
        var provider = new StallingProvider();
        var model = new OpenAiCompatibleChatModel(
            new ChatModelOptions("OpenRouter", "https://openrouter.ai/api/v1", new SecretReference("environment", "TEST_KEY"), "openrouter/free")
            {
                ResolvedApiKey = "sk-or-v1-0123456789abcdef0123456789abcdef",
                RequestTimeout = transportTimeout,
            },
            new HttpClient(provider));
        var audit = new RecordingAuditSink();
        var store = new InMemoryTaskStore();
        var runner = new AgentRunner(
            model, new ToolRegistry(new AlwaysAvailableCapabilityProbe()), new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit, store, TimeProvider.System, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions { ModelCallMaxAttempts = 2, ModelCallAttemptTimeout = attemptTimeout });
        var delays = new List<TimeSpan>();
        runner.ModelRetryJitter = () => 0.0;
        runner.ModelRetryDelay = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        var task = await runner.RunAsync("check", Actor);
        return (task, store, audit, delays, provider);
    }

    private static async Task AssertBoundedTimeoutFailureAsync(
        TaskState task, InMemoryTaskStore store, RecordingAuditSink audit, List<TimeSpan> delays, StallingProvider provider)
    {
        var stored = (await store.LoadAsync(task.Id))!;
        Assert.Equal(AgentTaskStatus.Failed, stored.Status);
        Assert.Empty(await store.ListByStatusAsync(AgentTaskStatus.Running));
        Assert.Equal(3, provider.Requests); // the plan, then exactly two bounded step attempts
        Assert.Single(delays);
        var events = audit.Events.OfType<ModelCallAuditEvent>().Where(e => e.StepIndex == 0).ToList();
        Assert.Equal([1, 2], events.Select(e => e.ModelAttempt!.Value));
        Assert.All(events, e => Assert.Equal(ModelFailureKind.Timeout, e.FailureKind));
        Assert.Equal([ModelRetryDecision.Retry, ModelRetryDecision.AttemptsExhausted], events.Select(e => e.RetryDecision!.Value));
        Assert.All(stored.Steps[^1].ModelCalls!, c => Assert.Equal(ModelFailureKind.Timeout, c.FailureKind));
        Assert.StartsWith("Provider/model call timed out.", stored.Steps[^1].Observation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStalledProvider_WhenTheRuntimeAttemptTimeoutFiresFirst_EndsBoundedAndTerminal()
    {
        var (task, store, audit, delays, provider) = await RunStalledAsync(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(60));

        await AssertBoundedTimeoutFailureAsync(task, store, audit, delays, provider);
    }

    [Fact]
    public async Task AStalledProvider_WhenTheTransportTimeoutFiresFirst_EndsBoundedAndTerminal()
    {
        var (task, store, audit, delays, provider) = await RunStalledAsync(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(150));

        await AssertBoundedTimeoutFailureAsync(task, store, audit, delays, provider);
    }
}
