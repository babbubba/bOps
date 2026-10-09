// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Memory;
using bOps.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>PRE-4: the additive, minimal <c>executionPlan</c> projection on the task view, GET and SSE.</summary>
public sealed class TaskExecutionPlanEndpointsTests
{
    private const string Secret = "SECRET-MARKER";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private static PlannedStep Planned(int index) =>
        new(index, $"objective {index}", "system.events")
        {
            ExpectedArguments = ToolArguments.FromJson(new JsonObject { ["arg"] = Secret }),
            Activation = index == 2 ? new EvidenceFactExists(0, $"{Secret}-type", $"{Secret}-key") : null,
        };

    private static AgentPlan Plan(int revision, int steps = 3) =>
        new(revision, $"rationale {Secret}", [.. Enumerable.Range(0, steps).Select(Planned)]) { SemanticContractVersion = 1 };

    private static PlanStep Executed(int revision, int index, PlannedStepExecutionClassification classification, int stepIndex) =>
        new(stepIndex, "step", new ModelToolCall("c", "system.events", ToolArguments.FromJson(new JsonObject { ["arg"] = Secret })),
            ToolCallResult.Success(Secret), Secret, revision)
        {
            PlannedStepIndex = index,
            ExecutionClassification = classification,
        };

    private static TaskState Stored(AgentTaskStatus status, IReadOnlyList<AgentPlan> plans, params PlanStep[] steps) =>
        new(Guid.NewGuid(), NodeId.Local, "goal", status, steps, plans, DateTimeOffset.UtcNow)
        {
            Origin = TaskOrigin.Ordinary,
            Accounting = new TaskAccounting(0, steps.Length, 0),
        };

    private static async Task<TaskState> SeedAsync(TestAppFactory factory, TaskState task)
    {
        await factory.Services.GetRequiredService<ITaskStore>().SaveAsync(task);
        return task;
    }

    private static async Task<JsonObject> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonObject>($"/api/agents/tasks/{id}"))!;

    private static string[] StatusesOf(JsonObject view, int revision) =>
        [.. view["executionPlan"]!["revisions"]!.AsArray()
            .Single(r => r!["revision"]!.GetValue<int>() == revision)!["steps"]!.AsArray()
            .Select(s => s!["status"]!.GetValue<string>())];

    [Fact]
    public async Task Get_ExposesTheActiveRevision_AndAnInterruptedTaskHasNoRunningOrCurrentStep()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        // Stored as Running, but no executor of this host holds it: an interrupted task (ADR-0040 §9).
        var task = await SeedAsync(factory, Stored(AgentTaskStatus.Running, [Plan(0)],
            Executed(0, 0, PlannedStepExecutionClassification.Matched, 0)));

        var view = await GetAsync(client, task.Id);

        Assert.False(view["executing"]!.GetValue<bool>());
        Assert.Equal(0, view["executionPlan"]!["activeRevision"]!.GetValue<int>());
        // Conditional step 2 is not reached yet; step 1 is not executing, so it is neither Running nor current.
        Assert.Equal(["Completed", "Pending", "Pending"], StatusesOf(view, 0));
        Assert.DoesNotContain(view["executionPlan"]!["revisions"]!.AsArray().SelectMany(r => r!["steps"]!.AsArray()),
            s => s!["current"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Get_AConditionalStepWithoutItsFact_IsSkipped()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var task = await SeedAsync(factory, Stored(AgentTaskStatus.Running, [Plan(0)],
            Executed(0, 0, PlannedStepExecutionClassification.Matched, 0),
            Executed(0, 1, PlannedStepExecutionClassification.Matched, 1)));

        var view = await GetAsync(client, task.Id);

        Assert.Equal(["Completed", "Completed", "Skipped"], StatusesOf(view, 0));
    }

    [Fact]
    public async Task List_CarriesTheProjectionToo()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var task = await SeedAsync(factory, Stored(AgentTaskStatus.Running, [Plan(0)]));

        var list = await client.GetFromJsonAsync<JsonArray>("/api/agents/tasks?status=Running");

        var view = list!.Select(item => item!.AsObject()).Single(item => item["id"]!.GetValue<Guid>() == task.Id);
        Assert.Equal(0, view["executionPlan"]!["activeRevision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Get_IsStableAcrossReads_AndExecutionPlanIsNullBeforeAnyPlan()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var planned = await SeedAsync(factory, Stored(AgentTaskStatus.Running, [Plan(0), Plan(1)]));
        var unplanned = await SeedAsync(factory, Stored(AgentTaskStatus.Running, []));

        var first = (await GetAsync(client, planned.Id))["executionPlan"]!.ToJsonString();
        var second = (await GetAsync(client, planned.Id))["executionPlan"]!.ToJsonString();

        Assert.Equal(first, second);
        Assert.Null((await GetAsync(client, unplanned.Id))["executionPlan"]);
    }

    [Fact]
    public async Task Get_ALegacyPlan_DoesNotBreakTheTaskView()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var legacy = new AgentPlan(0, "legacy", [new PlannedStep(0, "legacy objective", "system.events")]);
        var task = await SeedAsync(factory, Stored(AgentTaskStatus.Completed, [legacy],
            new PlanStep(0, "s", new ModelToolCall("c", "system.events", ToolArguments.Empty), ToolCallResult.Success("ok"), "ok", 0)));

        var view = await GetAsync(client, task.Id);

        Assert.Equal(["Completed"], StatusesOf(view, 0));
    }

    [Fact]
    public async Task ExecutionPlan_HoldsNoForbiddenData()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var fact = new EvidenceFact($"{Secret}-type", $"{Secret}-key", ToolParameterType.String, JsonValue.Create(Secret)!);
        var matched = Executed(0, 0, PlannedStepExecutionClassification.Matched, 0);
        var task = await SeedAsync(factory, Stored(AgentTaskStatus.Running, [Plan(0), Plan(1)],
            matched with { Result = matched.Result! with { Facts = [fact] } },
            Executed(0, 1, PlannedStepExecutionClassification.Matched, 1),
            Executed(1, 0, PlannedStepExecutionClassification.ArgumentValidationFailure, 2)));

        var view = await GetAsync(client, task.Id);
        var plan = view["executionPlan"]!.ToJsonString();

        // The fact-backed conditional step was activated: the qualifier is exposed, the fact's type, key and value are not.
        Assert.Equal("Activated", view["executionPlan"]!["revisions"]![0]!["steps"]![2]!["conditionOutcome"]!.GetValue<string>());

        Assert.DoesNotContain(Secret, plan, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "expectedArguments", "arguments", "activation", "factType", "factKey", "rationale", "output", "observation", "requestJson", "responseJson", "facts" })
        {
            Assert.DoesNotContain(forbidden, plan, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task EventStream_EmitsANewSnapshot_WhenAReplanIsAcceptedWithoutANewStep()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var task = await SeedAsync(factory, Stored(AgentTaskStatus.Running, [Plan(0)],
            Executed(0, 0, PlannedStepExecutionClassification.Matched, 0)));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/agents/tasks/{task.Id}/events", UriKind.Relative));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var timeout = new CancellationTokenSource(ReadTimeout);

        var first = await NextSnapshotAsync(reader, timeout.Token);
        Assert.Equal(0, first["executionPlan"]!["activeRevision"]!.GetValue<int>());

        await factory.Services.GetRequiredService<ITaskStore>().SaveAsync(task with { Plans = [Plan(0), Plan(1)] });
        var second = await NextSnapshotAsync(reader, timeout.Token);

        Assert.Equal(task.Steps.Count, second["steps"]!.AsArray().Count);
        Assert.Equal(1, second["executionPlan"]!["activeRevision"]!.GetValue<int>());
        Assert.Equal(["Completed", "Superseded", "Superseded"], StatusesOf(second, 0));
    }

    [Fact]
    public async Task Projection_IsIdenticalAfterTheStoreIsReopened()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bops-plan-reopen-{Guid.NewGuid():N}.db");
        try
        {
            var fact = new EvidenceFact($"{Secret}-type", $"{Secret}-key", ToolParameterType.String, System.Text.Json.Nodes.JsonValue.Create(Secret)!);
            var matched = Executed(0, 0, PlannedStepExecutionClassification.Matched, 0);
            var task = Stored(AgentTaskStatus.Running, [Plan(0), Plan(1)],
                matched with { Result = matched.Result! with { Facts = [fact] } },
                Executed(0, 1, PlannedStepExecutionClassification.ArgumentValidationFailure, 1),
                Executed(0, 1, PlannedStepExecutionClassification.ArgumentValidationFailure, 2),
                Executed(1, 0, PlannedStepExecutionClassification.SemanticMismatch, 3));

            var before = ExecutionPlanProjector.Project(task, executing: true);
            await new SqliteTaskStore(path).SaveAsync(task);
            var reopened = await new SqliteTaskStore(path).LoadAsync(task.Id);
            var after = ExecutionPlanProjector.Project(reopened!, executing: true);

            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(after));
            Assert.Equal(["Completed", "Superseded", "Superseded"], before!.Revisions[0].Steps.Select(s => s.Status.ToString()));
            Assert.Equal(ProjectedStepStatus.Correcting, before.Revisions[1].Steps[0].Status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static async Task<JsonObject> NextSnapshotAsync(StreamReader reader, CancellationToken ct)
    {
        string? line;
        do
        {
            line = await reader.ReadLineAsync(ct);
        }
        while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));

        return JsonNode.Parse(line!["data: ".Length..])!.AsObject();
    }
}
