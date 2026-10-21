// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

public sealed class TaskAssuranceEndpointsTests
{
    [Fact]
    public async Task GetAndList_DeriveAssuranceFromStoredEvidence_WithoutExposingPayloadsInTheProjection()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var task = Sample(AgentTaskStatus.Completed);
        await factory.Services.GetRequiredService<ITaskStore>().SaveAsync(task);

        var first = (await client.GetFromJsonAsync<JsonObject>($"/api/agents/tasks/{task.Id}"))!;
        var second = (await client.GetFromJsonAsync<JsonObject>($"/api/agents/tasks/{task.Id}"))!;
        var list = (await client.GetFromJsonAsync<JsonArray>("/api/agents/tasks?status=Completed"))!;
        var listed = list.Single(item => item!["id"]!.GetValue<Guid>() == task.Id)!;

        var assurance = first["assurance"]!;
        Assert.Equal("Qualified", assurance["level"]!.GetValue<string>());
        Assert.Contains(assurance["reasons"]!.AsArray(), reason => reason!["code"]!.GetValue<string>() == "EvidencePartial");
        Assert.Contains(assurance["reasons"]!.AsArray(), reason => reason!["code"]!.GetValue<string>() == "GroundingNotChecked");
        Assert.Equal(assurance.ToJsonString(), second["assurance"]!.ToJsonString());
        Assert.Equal(assurance.ToJsonString(), listed["assurance"]!.ToJsonString());
        Assert.DoesNotContain("PRIVATE-PAYLOAD", assurance.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal((int)AgentTaskStatus.Completed, first["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunningTask_HasNoAssuranceProjection()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var task = Sample(AgentTaskStatus.Running);
        await factory.Services.GetRequiredService<ITaskStore>().SaveAsync(task);

        var view = (await client.GetFromJsonAsync<JsonObject>($"/api/agents/tasks/{task.Id}"))!;

        Assert.Null(view["assurance"]);
    }

    private static TaskState Sample(AgentTaskStatus status) =>
        new(Guid.NewGuid(), NodeId.Local, "PRIVATE-PAYLOAD", status,
            [new PlanStep(0, "PRIVATE-PAYLOAD", new ModelToolCall("c", "sample.collect", ToolArguments.Empty),
                ToolCallResult.Success("PRIVATE-PAYLOAD") with
                {
                    Completeness = ToolResultCompleteness.Partial,
                    Facts = [new EvidenceFact("sample.count", "all", ToolParameterType.Integer, JsonValue.Create(14)!)],
                }, "PRIVATE-PAYLOAD"),
             new PlanStep(1, "Final response", null, null, "PRIVATE-PAYLOAD")], [], DateTimeOffset.UtcNow);
}
