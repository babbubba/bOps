// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Memory.Tests;

/// <summary>
/// Exercises <see cref="SqliteTaskStore"/> against a real SQLite file (agentic/04-testing-rules.md
/// — a database is a real target, not something to mock) rather than an in-memory fake, so a
/// round trip actually proves the JSON column and the schema, not just that a dictionary works.
/// </summary>
public sealed class SqliteTaskStoreTests : IDisposable
{
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bops-memory-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTheFullTaskState()
    {
        var store = new SqliteTaskStore(_filePath);
        var task = SampleTask(AgentTaskStatus.Running);

        await store.SaveAsync(task);
        var loaded = await store.LoadAsync(task.Id);

        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded!.Id);
        Assert.Equal(task.Goal, loaded.Goal);
        Assert.Equal(task.Status, loaded.Status);
        Assert.Equal(task.Steps.Count, loaded.Steps.Count);
        Assert.Equal(task.Steps[0].ToolCall!.ToolName, loaded.Steps[0].ToolCall!.ToolName);
        Assert.Equal(task.Steps[0].ToolCall!.Arguments.GetRequired<string>("path"), loaded.Steps[0].ToolCall!.Arguments.GetRequired<string>("path"));
        Assert.Equal(task.Plans.Count, loaded.Plans.Count);
        Assert.Equal(task.Plans[0].Rationale, loaded.Plans[0].Rationale);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_ForAnUnknownTaskId()
    {
        var store = new SqliteTaskStore(_filePath);

        var loaded = await store.LoadAsync(Guid.NewGuid());

        Assert.Null(loaded);
    }

    [Fact]
    public async Task SaveAsync_Twice_ReplacesTheStoredStateForTheSameId()
    {
        var store = new SqliteTaskStore(_filePath);
        var running = SampleTask(AgentTaskStatus.Running);

        await store.SaveAsync(running);
        var completed = running with { Status = AgentTaskStatus.Completed };
        await store.SaveAsync(completed);

        var loaded = await store.LoadAsync(running.Id);
        Assert.Equal(AgentTaskStatus.Completed, loaded!.Status);

        var stillRunning = await store.ListByStatusAsync(AgentTaskStatus.Running);
        Assert.DoesNotContain(stillRunning, t => t.Id == running.Id);
    }

    [Fact]
    public async Task ListByStatusAsync_ReturnsOnlyTasksAtThatStatus()
    {
        var store = new SqliteTaskStore(_filePath);
        var running = SampleTask(AgentTaskStatus.Running);
        var completed = SampleTask(AgentTaskStatus.Completed);

        await store.SaveAsync(running);
        await store.SaveAsync(completed);

        var results = await store.ListByStatusAsync(AgentTaskStatus.Running);

        Assert.Single(results);
        Assert.Equal(running.Id, results[0].Id);
    }

    [Fact]
    public async Task SavedState_SurvivesReopeningTheStoreAgainstTheSameFile()
    {
        var task = SampleTask(AgentTaskStatus.Running);
        await new SqliteTaskStore(_filePath).SaveAsync(task);

        var reopened = new SqliteTaskStore(_filePath);
        var loaded = await reopened.LoadAsync(task.Id);

        Assert.NotNull(loaded);
        Assert.Equal(task.Goal, loaded!.Goal);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_KeepsEveryModelCallWithItsBodies()
    {
        // The bodies are what troubleshooting needs: quotes, accents and a very long request must come back byte for byte.
        var request = "{\"content\":\"perché il mio pc è lento? \\\"x\\\" 😀\"}" + new string('q', 300_000);
        var call = new ModelCallRecord(
            "OpenRouter", "openrouter/free", "vendor/picked-model", DateTimeOffset.UtcNow, 4321, ModelCallOutcome.Success,
            new ModelUsage(10116, 788, null), "stop", null, request, "{\"choices\":[]}", PayloadTruncated: false);
        var task = SampleTask(AgentTaskStatus.Completed);
        task = task with
        {
            Steps = [task.Steps[0] with { ModelCalls = [call] }],
            Plans = [task.Plans[0] with { ModelCalls = [call with { DurationMs = 7 }] }],
        };

        var store = new SqliteTaskStore(_filePath);
        await store.SaveAsync(task);
        var loaded = await store.LoadAsync(task.Id);

        Assert.Equal(call, Assert.Single(loaded!.Steps[0].ModelCalls!));
        Assert.Equal(7, Assert.Single(loaded.Plans[0].ModelCalls!).DurationMs);
    }

    [Fact]
    public async Task LoadAsync_ReadsATaskStoredBeforeModelCallsWereKept_WithNone()
    {
        var id = Guid.NewGuid();
        const string legacy = """
            {"Id":"00000000-0000-0000-0000-000000000000","Node":"local","Goal":"g","Status":1,
             "Steps":[{"Index":0,"Description":"Final response","ToolCall":null,"Result":null,"Observation":"x","PlanRevision":0}],
             "Plans":[{"Revision":0,"Rationale":"r","Steps":[]}],"CreatedAtUtc":"2026-09-19T19:04:57.3626736+00:00"}
            """;
        _ = new SqliteTaskStore(_filePath); // creates the schema
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tasks (id, status, updated_at_utc, state_json) VALUES ($id, 'Completed', $at, $json)";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$json", legacy.Replace("00000000-0000-0000-0000-000000000000", id.ToString()));
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await new SqliteTaskStore(_filePath).LoadAsync(id);

        Assert.Equal("x", loaded!.Steps[0].Observation);
        Assert.Null(loaded.Steps[0].ModelCalls);
        Assert.Null(loaded.Plans[0].ModelCalls);
    }

    private static TaskState SampleTask(AgentTaskStatus status)
    {
        var arguments = ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["path"] = "/tmp/example" });
        var step = new PlanStep(
            0, "fs.read", new ModelToolCall("call-0", "fs.read", arguments), ToolCallResult.Success("contents"), "contents", 0);
        var plan = new AgentPlan(0, "Read the file, then report back.", [new PlannedStep(0, "Read the file", "fs.read")]);

        return new TaskState(Guid.NewGuid(), NodeId.Local, "read the config file", status, [step], [plan], DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by default, which keeps the file locked after
        // the last `using var connection` in SqliteTaskStore disposes — clearing the pool is what
        // actually releases the handle, not disposing the connection object itself.
        SqliteConnection.ClearAllPools();

        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
