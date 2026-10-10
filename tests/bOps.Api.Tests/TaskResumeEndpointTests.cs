// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// HARDEN-3 / ADR-0040 §4.3, §9 through the real host and its real <c>SqliteTaskStore</c>: a resume answers 202 only after the
/// task was atomically moved to <c>Running</c> under a new execution attempt and the launcher admitted that attempt, so a
/// reader never sees the stale pre-resume state; refusals are 409 with a stable code; a non-admitted attempt is contained and
/// answered 503; two concurrent resumes produce exactly one execution.
/// </summary>
public sealed class TaskResumeEndpointTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(15);

    private static TaskState Stored(AgentTaskStatus status, TaskOrigin origin = TaskOrigin.Ordinary, int lifetimeSteps = 1) =>
        new(Guid.NewGuid(), NodeId.Local, "check the cpu", status,
            [
                new PlanStep(0, "system.cpu", new ModelToolCall("call-0", "system.cpu", ToolArguments.Empty), ToolCallResult.Success("42%"), "42%", 0),
                new PlanStep(1, "Model protocol failure", null, null, "The provider failed."),
            ],
            [new AgentPlan(0, "test plan", [])],
            DateTimeOffset.UtcNow)
        {
            Origin = origin,
            Accounting = new TaskAccounting(100, lifetimeSteps, 0),
            MutationJournalMode = origin == TaskOrigin.Ordinary
                ? TaskMutationJournalMode.Journaled
                : TaskMutationJournalMode.Absent,
        };

    private static async Task<TaskState> SeedAsync(TestAppFactory factory, TaskState task)
    {
        await factory.Services.GetRequiredService<ITaskStore>().SaveAsync(task);
        return task;
    }

    private static Task<HttpResponseMessage> ResumeAsync(HttpClient client, Guid id) =>
        client.PostAsync(new Uri($"/api/agents/tasks/{id}/resume", UriKind.Relative), content: null);

    private static async Task<JsonObject> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonObject>($"/api/agents/tasks/{id}"))!;

    private static int Status(JsonObject view) => view["status"]!.GetValue<int>();

    private static int Attempt(JsonObject view) => view["executionAttempt"]!.GetValue<int>();

    private static async Task<JsonObject> PollUntilTerminalAsync(HttpClient client, Guid id)
    {
        using var timeout = new CancellationTokenSource(PollTimeout);
        while (true)
        {
            var view = await GetAsync(client, id);
            if (Status(view) != (int)AgentTaskStatus.Running)
            {
                return view;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    // H3-22, H3-23 and the real API regression of the incident: POST resume → 202 → immediate GET. The model's first call is
    // held, so the executing attempt is observed deterministically; the old Failed snapshot of attempt 1 is never returned.
    [Fact]
    public async Task H3_23_AfterThe202_AnImmediateGetSeesTheAcceptedAttempt_NeverTheStaleTerminalSnapshot()
    {
        var model = new GatedChatModel(QueueChatModel.Final("resumed and done"));
        using var factory = new TestAppFactory { ChatModel = model };
        using var client = factory.CreateClient();
        var pin = factory.Services.GetRequiredService<ProviderConfigurationCoordinator>().Current.Pin;
        var stored = await SeedAsync(factory, Stored(AgentTaskStatus.Failed) with { PinnedProviderConfiguration = pin });

        var response = await ResumeAsync(client, stored.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(stored.Id, accepted["taskId"]!.GetValue<Guid>());
        Assert.Equal((int)AgentTaskStatus.Running, Status(accepted));
        Assert.Equal(2, Attempt(accepted));
        Assert.True(accepted["executing"]!.GetValue<bool>());
        Assert.False(accepted["resumable"]!.GetValue<bool>());
        Assert.Equal("task_running", accepted["resumeBlockedReason"]!["code"]!.GetValue<string>());
        Assert.False(accepted["legacyConfigurationMigrated"]!.GetValue<bool>());

        var immediately = await GetAsync(client, stored.Id);
        Assert.Equal((int)AgentTaskStatus.Running, Status(immediately));
        Assert.Equal(2, Attempt(immediately));
        Assert.True(immediately["executing"]!.GetValue<bool>());
        Assert.False(immediately["resumable"]!.GetValue<bool>());

        model.Release();
        var final = await PollUntilTerminalAsync(client, stored.Id);
        Assert.Equal((int)AgentTaskStatus.Completed, Status(final));
        Assert.Equal(2, Attempt(final));
        Assert.Equal(3, final["steps"]!.AsArray().Count);
        Assert.Equal(1, model.Calls);
    }

    // H3-23 without holding the model: the worker may already have finished, but whatever the GET sees belongs to attempt 2.
    [Fact]
    public async Task H3_23_AnImmediateGet_SeesRunningOrATerminalStateOfTheSameAcceptedAttempt()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel(QueueChatModel.Final("done")) };
        using var client = factory.CreateClient();
        var stored = await SeedAsync(factory, Stored(AgentTaskStatus.Failed));

        Assert.Equal(HttpStatusCode.Accepted, (await ResumeAsync(client, stored.Id)).StatusCode);
        var immediately = await GetAsync(client, stored.Id);

        Assert.Equal(2, Attempt(immediately));
        Assert.True(Status(immediately) == (int)AgentTaskStatus.Running || immediately["terminalReason"] is not null);
        Assert.Equal((int)AgentTaskStatus.Completed, Status(await PollUntilTerminalAsync(client, stored.Id)));
    }

    // H3-13: two concurrent resumes of the same terminal task through the real SQLite store — exactly one 202, one 409, one
    // new execution attempt, one execution. Repeated to exercise the race.
    [Fact]
    public async Task H3_13_TwoConcurrentResumes_ExactlyOneIsAccepted_AndOnlyOneAttemptExecutes()
    {
        const int Rounds = 5;
        var model = new GatedChatModel(Enumerable.Range(0, Rounds).Select(_ => QueueChatModel.Final("done")).ToArray());
        using var factory = new TestAppFactory
        {
            ChatModel = model,
            ConfigureExtraServices = services => services.AddSingleton(new AgentTaskLauncherOptions { MaxConcurrentTasks = 16 }),
        };
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();
        var ids = new List<Guid>();

        for (var round = 0; round < Rounds; round++)
        {
            var stored = await SeedAsync(factory, Stored(AgentTaskStatus.Failed));
            ids.Add(stored.Id);

            var responses = await Task.WhenAll(ResumeAsync(first, stored.Id), ResumeAsync(second, stored.Id));

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
            var conflict = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            var code = (await conflict.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>();
            Assert.True(code is "resume_conflict" or "task_running", code);
            Assert.Equal(2, Attempt(await GetAsync(first, stored.Id)));
        }

        model.Release();
        foreach (var id in ids)
        {
            var final = await PollUntilTerminalAsync(first, id);
            Assert.Equal(((int)AgentTaskStatus.Completed, 2), (Status(final), Attempt(final)));
        }

        Assert.Equal(Rounds, model.Calls);
    }

    // H3-21, H3-22: the transition succeeded but the launcher could not admit the attempt (its only slot is busy). No 202; the
    // acquired attempt is contained Failed — never a Running orphan — and audited; the 503 has a body and Retry-After.
    [Fact]
    public async Task H3_21_ANonAdmittedAttempt_IsContainedFailed_AndAnswered503_Never202()
    {
        using var factory = new TestAppFactory
        {
            ChatModel = new StallingChatModel(),
            ConfigureExtraServices = services => services.AddSingleton(new AgentTaskLauncherOptions { MaxConcurrentTasks = 1 }),
        };
        using var client = factory.CreateClient();
        var busy = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("occupy the only slot"));
        Assert.Equal(HttpStatusCode.Accepted, busy.StatusCode);
        var busyId = (await busy.Content.ReadFromJsonAsync<TaskAcceptedResponse>())!.TaskId;
        var stored = await SeedAsync(factory, Stored(AgentTaskStatus.Cancelled));

        var response = await ResumeAsync(client, stored.Id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal("executor_unavailable", (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());

        var contained = await GetAsync(client, stored.Id);
        Assert.Equal((int)AgentTaskStatus.Failed, Status(contained));
        Assert.Equal(2, Attempt(contained));
        Assert.Equal((int)TaskTerminalKind.NotAdmitted, contained["terminalReason"]!["kind"]!.GetValue<int>());
        Assert.False(contained["executing"]!.GetValue<bool>());
        Assert.True(contained["resumable"]!.GetValue<bool>());
        Assert.Equal(1, contained["accounting"]!["lifetimeSteps"]!.GetValue<int>());

        var stages = LifecycleStages(factory, stored.Id);
        Assert.Equal([(TaskLifecycleStage.ResumeAccepted, 2), (TaskLifecycleStage.ExecutionTerminal, 2)], stages);

        // The start endpoint's capacity refusal has the same shape.
        var start = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("no slot"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.Equal("5", start.Headers.GetValues("Retry-After").Single());
        Assert.Equal("executor_unavailable", (await start.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());

        await client.DeleteAsync(new Uri($"/api/agents/tasks/{busyId}", UriKind.Relative));
    }

    // H3-03, H3-04, H3-05, H3-08, H3-18, H3-19 at the HTTP boundary: 409 with a stable code, and nothing changes.
    [Theory]
    [InlineData(AgentTaskStatus.Completed, TaskOrigin.Ordinary, 1, "task_completed")]
    [InlineData(AgentTaskStatus.PolicyBlocked, TaskOrigin.Ordinary, 1, "task_policy_blocked")]
    [InlineData(AgentTaskStatus.Running, TaskOrigin.Ordinary, 1, "task_running")]
    [InlineData(AgentTaskStatus.MaxStepsReached, TaskOrigin.Ordinary, 60, "lifetime_steps_exhausted")]
    [InlineData(AgentTaskStatus.Failed, TaskOrigin.Delegated, 1, "task_delegated")]
    [InlineData(AgentTaskStatus.Failed, TaskOrigin.Unknown, 1, "resume_origin_unknown")]
    public async Task ARefusedResume_Is409WithItsCode_AndChangesNothing(AgentTaskStatus status, TaskOrigin origin, int lifetimeSteps, string code)
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();
        var stored = await SeedAsync(factory, Stored(status, origin, lifetimeSteps));

        var response = await ResumeAsync(client, stored.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(code, body["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(body["message"]!.GetValue<string>()));
        var after = await GetAsync(client, stored.Id);
        Assert.Equal(((int)status, 1), (Status(after), Attempt(after)));
        Assert.Equal([(TaskLifecycleStage.ResumeRejected, 1)], LifecycleStages(factory, stored.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADelegatedTask_IsRefusedBeforeProviderResolution_AndItsOwnerIsUntouched(bool hasPersistedPin)
    {
        var registry = new FailingChatModelRegistry();
        var model = new GatedChatModel();
        using var factory = new TestAppFactory
        {
            ChatModel = model,
            ConfigureExtraServices = services => services.AddSingleton<IChatModelRegistry>(registry),
        };
        using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var ownerActor = new ActorIdentity("api-user", "delegation-owner", "Delegation owner");
        var owner = new DelegationRun
        {
            Id = Guid.NewGuid(),
            Node = NodeId.Local,
            Actor = ownerActor,
            Objective = "owned delegated work",
            Status = DelegationStatus.DiagnosisCompleted,
            RootEnvelope = new AuthorityEnvelope(ownerActor, 0, [], [], [], RiskLevel.Read, BlastRadius.Single, [], [],
                new DelegationBudget(0, 0, now.AddHours(1))),
            Roles = [],
            Journal = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await factory.Services.GetRequiredService<IDelegationStore>().StartAsync(owner);
        var stored = await SeedAsync(factory, Stored(AgentTaskStatus.Failed, TaskOrigin.Delegated) with
        {
            DelegationId = owner.Id,
            DelegationRole = AgentRoleKind.Diagnostic,
            PinnedProviderConfiguration = hasPersistedPin
                ? new PinnedProviderConfiguration(1, 1, "unsupported", "https://provider.invalid", "model", false,
                    null, "test", "test", "test", "test", "test", "deliberately-invalid")
                : null,
        });
        var ownerBefore = JsonSerializer.Serialize(await factory.Services.GetRequiredService<IDelegationStore>().LoadAsync(owner.Id));

        var response = await ResumeAsync(client, stored.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("task_delegated", body["code"]!.GetValue<string>());
        Assert.Equal(0, registry.CreateCalls); // no Current build, pin validation, adapter, or execution runner
        Assert.Equal(0, model.Calls);
        var after = await factory.Services.GetRequiredService<ITaskStore>().LoadAsync(stored.Id);
        Assert.NotNull(after);
        Assert.Equal(stored.PinnedProviderConfiguration, after.PinnedProviderConfiguration);
        Assert.Equal((stored.Status, stored.ExecutionAttempt, stored.DelegationId),
            (after.Status, after.ExecutionAttempt, after.DelegationId));
        Assert.Equal(ownerBefore,
            JsonSerializer.Serialize(await factory.Services.GetRequiredService<IDelegationStore>().LoadAsync(owner.Id)));
        Assert.Equal([(TaskLifecycleStage.ResumeRejected, 1)], LifecycleStages(factory, stored.Id));
    }

    // C-12: a persisted Running task with no executor in this host is distinguishable (executing: false) — and still not
    // resumable (H3-05).
    [Fact]
    public async Task TheView_ExposesExecutingAndResumability_ForAnOrphanedRunningTask()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();
        var stored = await SeedAsync(factory, Stored(AgentTaskStatus.Running));

        var view = await GetAsync(client, stored.Id);

        Assert.Equal((int)AgentTaskStatus.Running, Status(view));
        Assert.False(view["executing"]!.GetValue<bool>());
        Assert.False(view["resumable"]!.GetValue<bool>());
        Assert.Equal("task_running", view["resumeBlockedReason"]!["code"]!.GetValue<string>());
        Assert.Equal((int)TaskOrigin.Ordinary, view["origin"]!.GetValue<int>());
        Assert.Equal(1, Attempt(view));
    }

    // C-05 backend: the event stream opened right after a resume starts on the accepted attempt, never on the stale snapshot.
    [Fact]
    public async Task TheEventStream_AfterAResume_StartsOnTheAcceptedAttempt()
    {
        var model = new GatedChatModel(QueueChatModel.Final("done"));
        using var factory = new TestAppFactory { ChatModel = model };
        using var client = factory.CreateClient();
        var stored = await SeedAsync(factory, Stored(AgentTaskStatus.Failed));
        Assert.Equal(HttpStatusCode.Accepted, (await ResumeAsync(client, stored.Id)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/agents/tasks/{stored.Id}/events", UriKind.Relative));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string? line;
        do
        {
            line = await reader.ReadLineAsync();
        }
        while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));

        var first = JsonNode.Parse(line!["data: ".Length..])!.AsObject();
        Assert.Equal(((int)AgentTaskStatus.Running, 2), (Status(first), Attempt(first)));
        model.Release();
    }

    private static List<(TaskLifecycleStage Stage, int ExecutionAttempt)> LifecycleStages(TestAppFactory factory, Guid taskId)
    {
        var path = Path.Combine(factory.TempDirectory, "audit.jsonl");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var stages = new List<(TaskLifecycleStage, int)>();
        while (reader.ReadLine() is { } line)
        {
            if (JsonNode.Parse(line)?["EventJson"]?.GetValue<string>() is { } eventJson
                && JsonNode.Parse(eventJson) is JsonObject evt
                && evt["eventType"]?.GetValue<string>() == "taskLifecycle"
                && evt["TaskId"]!.GetValue<Guid>() == taskId)
            {
                stages.Add(((TaskLifecycleStage)evt["Stage"]!.GetValue<int>(), evt["ExecutionAttempt"]!.GetValue<int>()));
            }
        }

        return stages;
    }

    /// <summary>Replays responses, but holds every call until <see cref="Release"/>.</summary>
    private sealed class GatedChatModel(params ModelResponse[] responses) : IChatModel
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ChatModelDescriptor Descriptor { get; } = new("test", "test-model");

        public void Release() => _gate.TrySetResult();

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            await _gate.Task.WaitAsync(ct);
            return responses[index];
        }
    }

    private sealed class StallingChatModel : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new("test", "test-model");

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Accepts startup registration, but fails and counts any attempt to construct a provider adapter.</summary>
    private sealed class FailingChatModelRegistry : IChatModelRegistry
    {
        private readonly HashSet<string> _providerIds = new(StringComparer.OrdinalIgnoreCase);
        private int _createCalls;

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public IReadOnlyList<string> RegisteredProviderIds => [.. _providerIds];

        public void Register(PackageId package, IModelProviderPackage provider)
        {
            foreach (var providerId in provider.SupportedProviderIds)
            {
                _providerIds.Add(providerId);
            }
        }

        public IChatModel Create(ChatModelOptions options)
        {
            Interlocked.Increment(ref _createCalls);
            throw new InvalidOperationException("Provider resolution must not run for an ordinary resume of a delegated task.");
        }
    }
}
