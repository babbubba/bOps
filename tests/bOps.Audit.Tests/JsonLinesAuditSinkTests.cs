// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Audit.Tests;

/// <summary>
/// Hash-chaining for tamper evidence (V0.3, ADR-0015; agentic/06-decisions.md, D-008;
/// agentic/03-security-rules.md, rule S9).
/// </summary>
public sealed class JsonLinesAuditSinkTests : IDisposable
{
    private static readonly ActorIdentity Actor = new("os-user", "alice", "Alice");

    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bops-audit-test-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    private static ToolCallAuditEvent SampleEvent(int stepIndex) => new()
    {
        TimestampUtc = DateTimeOffset.UtcNow,
        Node = NodeId.Local,
        TaskId = Guid.NewGuid(),
        StepIndex = stepIndex,
        Actor = Actor,
        Package = new PackageId("bops.packages.test"),
        Tool = "test.tool",
        Arguments = new JsonObject(),
        Risk = RiskLevel.Read,
        Authorization = AuthorizationKind.Automatic,
        Outcome = ToolOutcome.Success,
        Duration = TimeSpan.FromMilliseconds(5),
        Verification = null,
    };

    [Fact]
    public async Task WriteAsync_ProducesAValidChain_ForConsecutiveEvents()
    {
        using (var sink = new JsonLinesAuditSink(_filePath))
        {
            await sink.WriteAsync(SampleEvent(0));
            await sink.WriteAsync(SampleEvent(1));
            await sink.WriteAsync(SampleEvent(2));
        }

        var result = AuditChainVerifier.VerifyFile(_filePath);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public async Task WriteAsync_ContinuesTheChain_AcrossSinkInstances()
    {
        using (var first = new JsonLinesAuditSink(_filePath))
        {
            await first.WriteAsync(SampleEvent(0));
        }

        // A new sink over the same file — simulating a process restart — must not reset the
        // chain to genesis, or every restart would silently break tamper evidence for
        // everything written before it.
        using (var second = new JsonLinesAuditSink(_filePath))
        {
            await second.WriteAsync(SampleEvent(1));
        }

        var lines = await File.ReadAllLinesAsync(_filePath);
        Assert.Equal(2, lines.Length);

        var result = AuditChainVerifier.VerifyFile(_filePath);
        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void VerifyFile_ReturnsValid_ForAFileThatDoesNotExist()
    {
        var result = AuditChainVerifier.VerifyFile(_filePath);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Verify_DetectsATamperedEvent()
    {
        using (var sink = new JsonLinesAuditSink(_filePath))
        {
            await sink.WriteAsync(SampleEvent(0));
            await sink.WriteAsync(SampleEvent(1));
        }

        var lines = await File.ReadAllLinesAsync(_filePath);
        // Flip the tool name inside the second line's event JSON without recomputing its hash —
        // exactly what an editor would do by hand. The event is nested as an escaped JSON string
        // inside the envelope, so the target text itself (not the surrounding quotes, which are
        // escaped as \") is what must be matched.
        lines[1] = lines[1].Replace("test.tool", "evil.tool", StringComparison.Ordinal);

        var result = AuditChainVerifier.Verify(lines);

        Assert.False(result.IsValid);
        Assert.Equal(1, result.BrokenAtSequence);
    }

    [Fact]
    public async Task Verify_DetectsARemovedLine()
    {
        using (var sink = new JsonLinesAuditSink(_filePath))
        {
            await sink.WriteAsync(SampleEvent(0));
            await sink.WriteAsync(SampleEvent(1));
            await sink.WriteAsync(SampleEvent(2));
        }

        var lines = await File.ReadAllLinesAsync(_filePath);
        var withMiddleLineRemoved = new[] { lines[0], lines[2] };

        var result = AuditChainVerifier.Verify(withMiddleLineRemoved);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Verify_ReturnsValid_ForAnEmptySequence()
    {
        var result = AuditChainVerifier.Verify([]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task WriteAsync_ConcurrentWrites_DoNotInterleaveOrCorruptTheChain()
    {
        // agentic/04-testing-rules.md, "Audit": concurrent writes do not interleave.
        using var sink = new JsonLinesAuditSink(_filePath);

        var writes = Enumerable.Range(0, 50).Select(i => sink.WriteAsync(SampleEvent(i)));
        await Task.WhenAll(writes);

        var lines = await File.ReadAllLinesAsync(_filePath);
        Assert.Equal(50, lines.Length);

        var result = AuditChainVerifier.Verify(lines);
        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public async Task Constructor_Throws_WhenTheFileEndsWithAPreChainRawEvent()
    {
        // A pre-V0.3 file: one raw AuditEvent per line, no {Seq, PrevHash, Hash} envelope. It
        // must never be silently adopted as a chain's tail — that would continue the chain with
        // a meaningless (null) PrevHash instead of a real one.
        await File.WriteAllTextAsync(_filePath, """{"eventType":"modelCall","Provider":"OpenRouter"}""" + Environment.NewLine);

        Assert.Throws<InvalidOperationException>(() => new JsonLinesAuditSink(_filePath));
    }
}
