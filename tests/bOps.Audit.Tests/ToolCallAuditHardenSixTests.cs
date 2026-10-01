// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Audit.Tests;

/// <summary>
/// HARDEN-6 / ADR-0022: the tool-call audit record gains <c>FailureKind</c> and <c>Completeness</c> additively. A record that
/// carries neither serializes exactly as it did before (the hash chain covers the stored text, so history stays verifiable), a
/// record written before HARDEN-6 still deserializes, and the new values round-trip.
/// </summary>
public sealed class ToolCallAuditHardenSixTests : IDisposable
{
    private static readonly ActorIdentity Actor = new("os-user", "alice", "Alice");

    private readonly string filePath = Path.Combine(Path.GetTempPath(), $"bops-audit-h6-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task LegacyResult_WithNeitherField_SerializesWithoutThem_AndTheChainStaysValid()
    {
        var json = await WriteAsync(Event(ToolOutcome.Success));

        Assert.DoesNotContain("FailureKind", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Completeness", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(AuditChainVerifier.VerifyFile(filePath).IsValid);
        var read = Assert.IsType<ToolCallAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(json));
        Assert.Null(read.FailureKind);
        Assert.Null(read.Completeness);
    }

    [Fact]
    public async Task CompleteSuccess_RecordsCompleteness_AndRoundTrips()
    {
        var json = await WriteAsync(Event(ToolOutcome.Success) with { Completeness = ToolResultCompleteness.Complete });

        var read = Assert.IsType<ToolCallAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(json));
        Assert.Equal(ToolResultCompleteness.Complete, read.Completeness);
        Assert.Null(read.FailureKind);
        Assert.DoesNotContain("FailureKind", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(AuditChainVerifier.VerifyFile(filePath).IsValid);
    }

    [Fact]
    public async Task PartialSuccess_RecordsPartial_DistinctFromComplete_AndRoundTrips()
    {
        var json = await WriteAsync(Event(ToolOutcome.Success) with { Completeness = ToolResultCompleteness.Partial });

        var read = Assert.IsType<ToolCallAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(json));
        Assert.Equal(ToolOutcome.Success, read.Outcome);
        Assert.Equal(ToolResultCompleteness.Partial, read.Completeness);
    }

    [Theory]
    [InlineData(ToolFailureKind.Validation)]
    [InlineData(ToolFailureKind.Environment)]
    [InlineData(ToolFailureKind.Internal)]
    [InlineData(ToolFailureKind.Timeout)]
    [InlineData(ToolFailureKind.Authorization)]
    public async Task TypedFailure_RecordsItsKind_AndRoundTrips(ToolFailureKind kind)
    {
        var json = await WriteAsync(Event(ToolOutcome.Failure) with { FailureKind = kind });

        var read = Assert.IsType<ToolCallAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(json));
        Assert.Equal(kind, read.FailureKind);
        Assert.Null(read.Completeness);
        Assert.True(AuditChainVerifier.VerifyFile(filePath).IsValid);
    }

    [Fact]
    public async Task Failure_WithoutHardenSixMetadata_SerializesWithoutThem()
    {
        var json = await WriteAsync(Event(ToolOutcome.Failure));

        Assert.DoesNotContain("FailureKind", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Completeness", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ToolOutcome.Failure, Assert.IsType<ToolCallAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(json)).Outcome);
    }

    [Fact]
    public void RecordWrittenBeforeHardenSix_StillDeserializes()
    {
        const string legacy = """
            {"eventType":"toolCall","Package":"bops.packages.test","Tool":"test.tool","Arguments":{},"Risk":0,"Authorization":0,"Outcome":1,
             "Duration":"00:00:00.0050000","Summary":null,"Verification":null,"SkillRunId":null,"SkillId":null,"CapabilityName":null,"Target":null,
             "Environment":null,"BlastRadius":null,"PlanHash":null,"TimestampUtc":"2026-09-01T10:00:00+00:00","Node":"local",
             "TaskId":"11111111-1111-1111-1111-111111111111","StepIndex":2,"Actor":{"Kind":"os-user","Id":"alice","DisplayName":"Alice"}}
            """;

        var read = JsonSerializer.Deserialize<AuditEvent>(legacy);

        var toolCall = Assert.IsType<ToolCallAuditEvent>(read);
        Assert.Equal("test.tool", toolCall.Tool);
        Assert.Equal(ToolOutcome.Failure, toolCall.Outcome);
        Assert.Null(toolCall.FailureKind);
        Assert.Null(toolCall.Completeness);
    }

    /// <summary>
    /// H6-R4: <c>Fixtures/tool-call-pre-harden6.jsonl</c> was written by the real <see cref="JsonLinesAuditSink"/> of commit
    /// <c>f085971</c>, the last commit before HARDEN-6, for exactly the two events built below; it is a captured artifact, never
    /// regenerated from the code under test. The same events written today must produce the same lines byte for byte, hashes
    /// included, so a record without the HARDEN-6 metadata cannot drift and a pre-HARDEN-6 chain stays verifiable.
    /// </summary>
    [Fact]
    public async Task EventsWithoutHardenSixMetadata_SerializeExactlyAsThePreHardenSixSinkWroteThem()
    {
        var golden = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tool-call-pre-harden6.jsonl"));
        var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        ToolCallAuditEvent[] events =
        [
            new()
            {
                TimestampUtc = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
                Node = NodeId.Local,
                TaskId = taskId,
                StepIndex = 2,
                Actor = Actor,
                Package = new PackageId("bops.packages.system.linux"),
                Tool = "system.crashes",
                Arguments = new JsonObject { ["sinceMinutes"] = 60 },
                Risk = RiskLevel.Read,
                Authorization = AuthorizationKind.Automatic,
                Outcome = ToolOutcome.Success,
                Duration = TimeSpan.FromMilliseconds(5),
                Summary = new JsonObject { ["crashes"] = 2 },
            },
            new()
            {
                TimestampUtc = new DateTimeOffset(2026, 9, 1, 10, 0, 1, TimeSpan.Zero),
                Node = NodeId.Local,
                TaskId = taskId,
                StepIndex = 3,
                Actor = Actor,
                Package = new PackageId("bops.packages.system.linux"),
                Tool = "system.crashes",
                Arguments = new JsonObject { ["sinceMinutes"] = 43200 },
                Risk = RiskLevel.Read,
                Authorization = AuthorizationKind.Automatic,
                Outcome = ToolOutcome.Failure,
                Duration = TimeSpan.FromMilliseconds(1),
            },
        ];

        using (var sink = new JsonLinesAuditSink(filePath))
        {
            foreach (var evt in events)
            {
                await sink.WriteAsync(evt);
            }
        }

        Assert.Equal(golden, await File.ReadAllLinesAsync(filePath));
        Assert.All(golden, line => Assert.DoesNotContain("FailureKind", line, StringComparison.Ordinal));
        Assert.All(golden, line => Assert.DoesNotContain("Completeness", line, StringComparison.Ordinal));
        Assert.True(AuditChainVerifier.VerifyFile(filePath).IsValid);
    }

    [Fact]
    public async Task EventWithBothFields_RoundTripsThroughTheSerializerUnchanged()
    {
        var original = Event(ToolOutcome.Failure) with { FailureKind = ToolFailureKind.Environment, Completeness = ToolResultCompleteness.Unavailable };

        var json = await WriteAsync(original);
        var read = Assert.IsType<ToolCallAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(json));

        Assert.Equal(original.FailureKind, read.FailureKind);
        Assert.Equal(original.Completeness, read.Completeness);
        Assert.Equal(original.Tool, read.Tool);
        Assert.Equal(original.Outcome, read.Outcome);
    }

    private static ToolCallAuditEvent Event(ToolOutcome outcome) => new()
    {
        TimestampUtc = DateTimeOffset.UtcNow,
        Node = NodeId.Local,
        TaskId = Guid.NewGuid(),
        StepIndex = 0,
        Actor = Actor,
        Package = new PackageId("bops.packages.test"),
        Tool = "test.tool",
        Arguments = new JsonObject(),
        Risk = RiskLevel.Read,
        Authorization = AuthorizationKind.Automatic,
        Outcome = outcome,
        Duration = TimeSpan.FromMilliseconds(5),
    };

    /// <summary>Writes <paramref name="evt"/> through the real sink and returns the stored event text the hash chain covers.</summary>
    private async Task<string> WriteAsync(ToolCallAuditEvent evt)
    {
        using (var sink = new JsonLinesAuditSink(filePath))
        {
            await sink.WriteAsync(evt);
        }

        var line = (await File.ReadAllLinesAsync(filePath)).Single();
        var envelope = JsonNode.Parse(line)!.AsObject();
        var eventJson = envelope.First(property => string.Equals(property.Key, "eventJson", StringComparison.OrdinalIgnoreCase)).Value!;
        return eventJson.GetValueKind() == JsonValueKind.String ? eventJson.GetValue<string>() : eventJson.ToJsonString();
    }
}
