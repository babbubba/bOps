// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Audit.Tests;

public sealed class DiagnosticAssuranceAuditTests
{
    [Fact]
    public async Task RealSink_RoundtripsClosedReasons_AndPreservesLegacyAbsentField()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bops-assurance-{Guid.NewGuid():N}.jsonl");
        try
        {
            var legacy = Event();
            var projected = legacy with
            {
                Assurance = new DiagnosticAssuranceResult(DiagnosticAssuranceLevel.Qualified,
                    [new AssuranceReason(AssuranceReasonCode.GroundingNotChecked, 1)]),
            };
            using (var sink = new JsonLinesAuditSink(path))
            {
                await sink.WriteAsync(legacy);
                await sink.WriteAsync(projected);
            }

            Assert.True(AuditChainVerifier.VerifyFile(path).IsValid);
            var lines = await File.ReadAllLinesAsync(path);
            var oldJson = JsonNode.Parse(lines[0])!["EventJson"]!.GetValue<string>();
            var newJson = JsonNode.Parse(lines[1])!["EventJson"]!.GetValue<string>();
            Assert.DoesNotContain("Assurance", oldJson, StringComparison.Ordinal);
            Assert.Null(Assert.IsType<TaskLifecycleAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(oldJson)).Assurance);
            var restored = Assert.IsType<TaskLifecycleAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(newJson));
            Assert.Equal(DiagnosticAssuranceLevel.Qualified, restored.Assurance!.Level);
            Assert.Equal(new AssuranceReason(AssuranceReasonCode.GroundingNotChecked, 1), Assert.Single(restored.Assurance.Reasons));
            Assert.Contains("GroundingNotChecked", newJson, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static TaskLifecycleAuditEvent Event() => new()
    {
        TimestampUtc = DateTimeOffset.UtcNow, Node = NodeId.Local, TaskId = Guid.NewGuid(), StepIndex = 2,
        Actor = new ActorIdentity("os-user", "test", null), Stage = TaskLifecycleStage.ExecutionTerminal,
        ExecutionAttempt = 1, Origin = TaskOrigin.Ordinary, Status = AgentTaskStatus.Completed,
        TerminalKind = TaskTerminalKind.Completed, TokensUsed = 5, LifetimeSteps = 2, LifetimeReplans = 0,
        MaxSteps = 10, MaxLifetimeSteps = 10, MaxReplans = 2, MaxLifetimeReplans = 2,
    };
}
