// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>ADR-0051 §14.2 through the real API host and SQLite journal.</summary>
public sealed class TaskMutationJournalEndpointTests
{
    private static readonly string ArgumentsHash = new('a', 64);

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("operator", HttpStatusCode.Forbidden)]
    public async Task Recover_RequiresAdministrator(string? role, HttpStatusCode expected)
    {
        using var factory = new TestAppFactory { Roles = role is null ? [] : [role] };
        using var client = role is null ? factory.CreateAnonymousClient() : factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/agents/tasks/{Guid.NewGuid()}/recover", new { executionAttempt = 1 });

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("operator", HttpStatusCode.Forbidden)]
    public async Task Reconcile_RequiresAdministrator(string? role, HttpStatusCode expected)
    {
        using var factory = new TestAppFactory { Roles = role is null ? [] : [role] };
        using var client = role is null ? factory.CreateAnonymousClient() : factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/agents/tasks/{Guid.NewGuid()}/reconcile", new { action = "acceptDone" });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Administrator_CanRecoverThenAccept_AndTheViewExposesOnlyTheSafeProjection()
    {
        using var factory = new TestAppFactory { Roles = ["viewer", "administrator"] };
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ITaskStore>();
        var task = await SeedPendingAsync(store, (ITaskMutationJournalStore)store);

        var before = (await client.GetFromJsonAsync<JsonObject>($"/api/agents/tasks/{task.Id}"))!;
        Assert.True(before["recoverable"]!.GetValue<bool>());
        Assert.Equal("task_running", before["resumeBlockedReason"]!["code"]!.GetValue<string>());
        Assert.Equal(1, before["mutationJournal"]!["unsettledCount"]!.GetValue<int>());
        var entry = before["mutationJournal"]!["entries"]![0]!;
        Assert.Equal("aaaaaaaaaaaa", entry["argumentsFingerprint"]!.GetValue<string>());
        Assert.Null(entry["arguments"]);
        Assert.Null(entry["output"]);
        Assert.Equal("OutcomeUnknown", before["executionPlan"]!["revisions"]![0]!["steps"]![0]!["status"]!.GetValue<string>());

        var recovered = await client.PostAsJsonAsync($"/api/agents/tasks/{task.Id}/recover", new { executionAttempt = 1 });
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var recoveredView = (await recovered.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.False(recoveredView["recoverable"]!.GetValue<bool>());
        Assert.Equal("task_not_running", recoveredView["recoveryBlockedReason"]!["code"]!.GetValue<string>());

        var reconciled = await client.PostAsJsonAsync($"/api/agents/tasks/{task.Id}/reconcile",
            new { action = "acceptDone", note = "confirmed by the administrator" });
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        var body = (await reconciled.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(0, body["unsettledCount"]!.GetValue<int>());
        Assert.True(body["resumable"]!.GetValue<bool>());
        Assert.Null(body["resumeBlockedReason"]);
        Assert.Equal("ReconciledDone", body["results"]![0]!["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("acceptDone")]
    [InlineData("abandon")]
    public async Task Administrator_IsAllowedToInvokeEveryReconcileAction(string action)
    {
        using var factory = new TestAppFactory { Roles = ["administrator"] };
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ITaskStore>();
        var task = await SeedPendingAsync(store, (ITaskMutationJournalStore)store, AgentTaskStatus.Failed);

        var response = await client.PostAsJsonAsync($"/api/agents/tasks/{task.Id}/reconcile", new { action });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Recover_ValidatesAttempt_AndReconcileValidatesActionAndNote()
    {
        using var factory = new TestAppFactory { Roles = ["administrator"] };
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/agents/tasks/{id}/recover", new { executionAttempt = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/agents/tasks/{id}/reconcile", new { action = "retry" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/agents/tasks/{id}/reconcile", new { action = "verify", note = new string('x', 501) })).StatusCode);
    }

    private static async Task<TaskState> SeedPendingAsync(
        ITaskStore store, ITaskMutationJournalStore journal, AgentTaskStatus status = AgentTaskStatus.Running)
    {
        var task = new TaskState(Guid.NewGuid(), NodeId.Local, "change a setting", status, [],
            [new AgentPlan(0, "change it", [new PlannedStep(0, "change the setting", "test.mutate")])], DateTimeOffset.UtcNow)
        {
            Origin = TaskOrigin.Ordinary,
            MutationJournalMode = TaskMutationJournalMode.Journaled,
        };
        await store.SaveAsync(task);
        if (status != AgentTaskStatus.Running)
        {
            var running = task with { Status = AgentTaskStatus.Running };
            await store.SaveAsync(running);
            task = running;
        }

        Assert.True(await journal.TryRecordIntentAsync(new TaskMutationIntent
        {
            Key = new TaskMutationKey(task.Id, task.ExecutionAttempt, 0),
            ToolName = "test.mutate",
            ArgumentsHash = ArgumentsHash,
            Risk = RiskLevel.Medium,
            VerificationToolName = "test.observe",
            PlanRevision = 0,
            PlannedStepIndex = 0,
            IntentAtUtc = DateTimeOffset.UtcNow,
        }));

        if (status != AgentTaskStatus.Running)
        {
            var terminal = task with { Status = status, TerminalReason = new TaskTerminalReason(TaskTerminalKind.RuntimeFailure) };
            Assert.True(await journal.TryTransitionAsync(terminal, AgentTaskStatus.Running, task.ExecutionAttempt));
            task = terminal;
        }

        return task;
    }
}
