// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge.Tests;

namespace bOps.AgentFramework.Tests;

public sealed class KnowledgeBenchmarkSecureExportTests
{
    [Fact]
    public async Task SensitiveSyntheticText_IsWithheldWithoutMutatingSources_InBothConditions()
    {
        using var temporary = new TempDirectory();
        var output = temporary.Sub("sensitive");
        Directory.CreateDirectory(output);
        string[] sensitive = ["sk-fake123456789", "fakeBearer123456", "fakePasswordValue", @"C:\Users\ArtificialPerson\private.txt",
            "artificial.person@example.invalid", "user-id=artificial-person", "Artificial Person", "raw Event Log private message",
            "reserved value without a recognizable token shape"];
        var text = $"api_key={sensitive[0]} Bearer {sensitive[1]} password={sensitive[2]} " + string.Join("; ", sensitive.Skip(3));
        foreach (var condition in new[] { "A", "B" })
        {
            TaskState? original = null;
            KnowledgeBenchmarkExport? observed = null;
            string? snapshot = null;
            string? auditSnapshot = null;
            var failure = new AggregateException(text, new IOException(text));
            var thrown = await Assert.ThrowsAsync<AggregateException>(() => KnowledgeBenchmarkExport.RunAsync(output, condition, async export =>
            {
                observed = export;
                original = State(export.RunId, AgentTaskStatus.Completed, text);
                snapshot = JsonSerializer.Serialize(original);
                export.World = new KnowledgeRunnerWorld();
                await export.World.Audit.WriteAsync(ModelEvent(export.RunId, ModelCallOutcome.Failure, text));
                await export.World.Audit.WriteAsync(ToolEvent(export.RunId, text));
                auditSnapshot = JsonSerializer.Serialize(export.World.Events);
                await export.ExecuteTaskAsync(() => Task.FromResult(original));
                throw failure;
            }));
            Assert.Same(failure, thrown); // Publishing must still rethrow the original failure unchanged.
            Assert.Same(original, observed!.State);
            Assert.Equal(snapshot, JsonSerializer.Serialize(original));
            Assert.Equal(auditSnapshot, JsonSerializer.Serialize(observed.World!.Events));
            Assert.All(observed.World.Events.OfType<ModelCallAuditEvent>(), e => Assert.Equal(text, e.ErrorMessage));
            var json = await File.ReadAllTextAsync(Path.Combine(output, condition, "result.json"));
            using var result = JsonDocument.Parse(json);
            Assert.NotNull(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
            var root = result.RootElement;
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(KnowledgeBenchmarkExport.RedactedFreeText, root.GetProperty("finalObservation").GetString());
            Assert.Equal(KnowledgeBenchmarkExport.RedactedFreeText, root.GetProperty("exceptionType").GetString());
            Assert.Equal(KnowledgeBenchmarkExport.RedactedFreeText, root.GetProperty("tools")[0].GetProperty("tool").GetString());
            Assert.Equal("Completed", root.GetProperty("status").GetString());
            Assert.Equal("InfrastructureError", root.GetProperty("terminalCause").GetString());
            Assert.Equal("MalformedResponse", root.GetProperty("failureKind").GetString());
            Assert.Contains("explicitly redacted", root.GetProperty("freeTextRepresentation").GetString(), StringComparison.Ordinal);
            var exported = root.ToString() + string.Join("", await File.ReadAllLinesAsync(Path.Combine(output, condition, "audit.jsonl")));
            foreach (var value in sensitive) Assert.DoesNotContain(value, exported, StringComparison.Ordinal);
            foreach (var line in await File.ReadAllLinesAsync(Path.Combine(output, condition, "audit.jsonl")))
            {
                using var evt = JsonDocument.Parse(line);
                Assert.False(evt.RootElement.TryGetProperty("ErrorMessage", out _));
                Assert.False(evt.RootElement.TryGetProperty("Arguments", out _));
                if (evt.RootElement.TryGetProperty("Tool", out var tool))
                    Assert.Equal(KnowledgeBenchmarkExport.RedactedFreeText, tool.GetString());
            }
        }
    }

    [Fact]
    public async Task BenignDiagnostics_PreserveStructuredOutcomesMetricsAndNulls_InBothConditions()
    {
        using var temporary = new TempDirectory();
        foreach (var status in new[] { AgentTaskStatus.Completed, AgentTaskStatus.Failed })
        {
            var output = temporary.Sub(status.ToString());
            Directory.CreateDirectory(output);
            string? referenceAudit = null;
            foreach (var condition in new[] { "A", "B" })
            {
                TaskState? original = null;
                KnowledgeBenchmarkExport? observed = null;
                string? snapshot = null;
                var failure = new ModelProtocolException("Controlled offline malformed response") { FailureKind = ModelFailureKind.MalformedResponse };
                var execution = KnowledgeBenchmarkExport.RunAsync(output, condition, async export =>
                {
                    observed = export;
                    original = State(export.RunId, status, "Event ID 1001; bounded technical diagnosis");
                    snapshot = JsonSerializer.Serialize(original);
                    export.World = new KnowledgeRunnerWorld();
                    await export.World.Audit.WriteAsync(ModelEvent(export.RunId,
                        status == AgentTaskStatus.Completed ? ModelCallOutcome.Success : ModelCallOutcome.Failure,
                        status == AgentTaskStatus.Completed ? null : failure.Message));
                    await export.World.Audit.WriteAsync(ToolEvent(export.RunId, "system.events"));
                    export.ModelObservation.Calls = 1;
                    export.ModelObservation.CompletedCalls = status == AgentTaskStatus.Completed ? 1 : 0;
                    export.ModelObservation.FailedCalls = status == AgentTaskStatus.Failed ? 1 : 0;
                    await export.ExecuteTaskAsync(() => Task.FromResult(original));
                    if (status == AgentTaskStatus.Failed) throw failure;
                });
                if (status == AgentTaskStatus.Failed) Assert.Same(failure, await Assert.ThrowsAsync<ModelProtocolException>(() => execution));
                else await execution;
                Assert.Same(original, observed!.State);
                Assert.Equal(snapshot, JsonSerializer.Serialize(original));
                Assert.Equal(2, observed.World!.Events.Count);
                using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, condition, "result.json")));
                var root = result.RootElement;
                Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(status.ToString(), root.GetProperty("status").GetString());
                Assert.Equal(status == AgentTaskStatus.Completed ? "Completed" : "ProviderOrProtocolError", root.GetProperty("terminalCause").GetString());
                Assert.Equal(status == AgentTaskStatus.Completed ? null : "MalformedResponse", root.GetProperty("failureKind").GetString());
                Assert.Equal(status == AgentTaskStatus.Completed ? null : "ModelProtocolException", root.GetProperty("exceptionType").GetString());
                Assert.Equal(status == AgentTaskStatus.Completed ? KnowledgeBenchmarkExport.RedactedFreeText : null, root.GetProperty("finalObservation").GetString());
                Assert.Equal(1, root.GetProperty("llmCalls").GetInt32());
                Assert.Equal(10, root.GetProperty("reportedPromptTokens").GetInt64());
                Assert.Equal(3, root.GetProperty("reportedCompletionTokens").GetInt64());
                Assert.Equal(13, root.GetProperty("accounting").GetProperty("tokensUsed").GetInt64());
                Assert.Equal(1, root.GetProperty("auditedToolsRefused").GetInt32());
                Assert.Equal(0, root.GetProperty("auditedToolsExecuted").GetInt32());
                Assert.Equal("system.events", root.GetProperty("tools")[0].GetProperty("tool").GetString());
                Assert.Equal(JsonValueKind.Null, root.GetProperty("httpRequestsSent").ValueKind);
                Assert.Equal("Complete", root.GetProperty("auditStatus").GetString());
                Assert.Equal(2, root.GetProperty("auditEventCount").GetInt32());
                var audit = new JsonArray();
                foreach (var line in await File.ReadAllLinesAsync(Path.Combine(output, condition, "audit.jsonl")))
                {
                    var evt = JsonNode.Parse(line)!.AsObject();
                    evt.Remove("TaskId");
                    audit.Add(evt);
                }
                if (referenceAudit is null) referenceAudit = audit.ToJsonString();
                else Assert.Equal(referenceAudit, audit.ToJsonString());
            }
        }
        // Present-null and present-empty observations keep their different meanings; absent tasks/failed steps remain null.
        foreach (var observation in new string?[] { null, "" })
        {
            var output = temporary.Sub(observation is null ? "null" : "empty");
            Directory.CreateDirectory(output);
            foreach (var condition in new[] { "A", "B" })
            {
                await KnowledgeBenchmarkExport.RunAsync(output, condition, export => export.ExecuteTaskAsync(() =>
                    Task.FromResult(State(export.RunId, AgentTaskStatus.Completed, observation))));
                using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, condition, "result.json")));
                Assert.Equal(observation is null ? null : KnowledgeBenchmarkExport.RedactedFreeText, result.RootElement.GetProperty("finalObservation").GetString());
                Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("auditError").ValueKind);
            }
        }
    }

    private static TaskState State(Guid id, AgentTaskStatus status, string? observation) =>
        new(id, NodeId.Local, "Offline synthetic task", status, [new PlanStep(0, null, null, null, observation)], [], DateTimeOffset.UnixEpoch)
        {
            Accounting = new TaskAccounting(13, 1, 0),
            TerminalReason = status == AgentTaskStatus.Failed ? new TaskTerminalReason(TaskTerminalKind.ModelFailure)
                { FailureKind = ModelFailureKind.MalformedResponse } : null
        };

    private static ModelCallAuditEvent ModelEvent(Guid id, ModelCallOutcome outcome, string? error) => new()
    {
        TimestampUtc = DateTimeOffset.UnixEpoch, Node = NodeId.Local, TaskId = id, Actor = KnowledgeRunnerWorld.ActorA,
        Provider = "offline", Model = "synthetic", Outcome = outcome, ErrorMessage = error,
        Usage = new ModelUsage(10, 3, null), ModelAttempt = 1,
        FailureKind = outcome == ModelCallOutcome.Failure ? ModelFailureKind.MalformedResponse : null
    };

    private static ToolCallAuditEvent ToolEvent(Guid id, string tool) => new()
    {
        TimestampUtc = DateTimeOffset.UnixEpoch, Node = NodeId.Local, TaskId = id, Actor = KnowledgeRunnerWorld.ActorA,
        Package = new PackageId("offline"), Tool = tool, Arguments = new JsonObject(), Risk = RiskLevel.Read,
        Authorization = AuthorizationKind.PolicyDenied, Outcome = ToolOutcome.Denied, Duration = TimeSpan.Zero
    };

}
