// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Audit.Tests;

/// <summary>KNOW-M1-A (ADR-0057, T1): the index event and the extended read event go through the real sink and hash chain.</summary>
public sealed class KnowledgeIndexAuditSinkTests : IDisposable
{
    private static readonly ActorIdentity Actor = new("os-user", "alice", "Alice");

    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bops-audit-index-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    [Fact]
    public async Task WriteAsync_ChainsAPublishedAndASuppressedIndexAndAReadByRef_AndTheChainStaysValid()
    {
        var taskId = Guid.NewGuid();
        var published = new KnowledgeIndexAuditEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow, Node = NodeId.Local, TaskId = taskId, StepIndex = 1, Actor = Actor, ExecutionAttempt = 0,
            Status = KnowledgeIndexStatus.Published, TenantId = "tenant-a", ProviderId = "kp.files.1", PackId = "acme.runbooks",
            PackVersion = "1.0.0", PackHash = new string('a', 64), SelectorVersion = "m1-lexical",
            Listed = [new KnowledgeIndexListing("K1", "nginx-triage", 2, 4)], AuthorizedTotal = 3, Omitted = 2,
            IndexHash = new string('b', 64), AggressiveIndexHash = new string('c', 64),
        };
        var suppressed = new KnowledgeIndexAuditEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow, Node = NodeId.Local, TaskId = taskId, StepIndex = 2, Actor = Actor, ExecutionAttempt = 0,
            Status = KnowledgeIndexStatus.Suppressed, Suppression = KnowledgeIndexSuppression.SnapshotUnavailable,
            CauseCode = KnowledgeAdmissionCode.SnapshotTampered,
        };
        var read = new KnowledgeReadAuditEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow, Node = NodeId.Local, TaskId = taskId, StepIndex = 1, Actor = Actor,
            Operation = "read_resource", Ref = "K1.R1", Repeat = false, ReturnedLength = 0, ResultCode = KnowledgeReadResultCode.OutOfRange,
        };

        using (var sink = new JsonLinesAuditSink(_filePath))
        {
            await sink.WriteAsync(published);
            await sink.WriteAsync(suppressed);
            await sink.WriteAsync(read);
        }

        Assert.True(AuditChainVerifier.VerifyFile(_filePath).IsValid);
        var lines = await File.ReadAllLinesAsync(_filePath);
        AuditEvent Restore(int index) => System.Text.Json.JsonSerializer.Deserialize<AuditEvent>(
            JsonNode.Parse(lines[index])!["EventJson"]!.GetValue<string>())!;

        var restoredPublished = Assert.IsType<KnowledgeIndexAuditEvent>(Restore(0));
        Assert.Equal(published.Listed, restoredPublished.Listed);
        Assert.Equal((1, 3, 2), (restoredPublished.StepIndex, restoredPublished.AuthorizedTotal, restoredPublished.Omitted));
        Assert.Equal(published.IndexHash, restoredPublished.IndexHash);
        var restoredSuppressed = Assert.IsType<KnowledgeIndexAuditEvent>(Restore(1));
        Assert.Equal(
            (KnowledgeIndexSuppression.SnapshotUnavailable, KnowledgeAdmissionCode.SnapshotTampered),
            (restoredSuppressed.Suppression!.Value, restoredSuppressed.CauseCode!.Value));
        var restoredRead = Assert.IsType<KnowledgeReadAuditEvent>(Restore(2));
        Assert.Equal(("K1.R1", false, KnowledgeReadResultCode.OutOfRange), (restoredRead.Ref, restoredRead.Repeat, restoredRead.ResultCode));
        Assert.Contains("\"eventType\":\"knowledgeIndex\"", JsonNode.Parse(lines[0])!["EventJson"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
