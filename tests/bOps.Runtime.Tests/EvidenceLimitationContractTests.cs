// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0044 section 16: the additive limitation contracts round-trip (rule A2), serialize enums by name, keep their explicit
/// numeric values, leave a historical row readable as "not recorded" (never "none"), and are produced by the same classification
/// as the ADR-0042 digest so the two can never disagree.
/// </summary>
public sealed class EvidenceLimitationContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void EvidenceLimitation_RoundTrips_WithEnumsByName()
    {
        var value = new EvidenceLimitation
        {
            StepIndex = 3,
            ToolName = "system.events",
            UnknownTool = false,
            Outcome = ToolOutcome.Success,
            FailureKind = ToolFailureKind.Unspecified,
            Completeness = ToolResultCompleteness.Partial,
            ShortenedFromCharacters = 32_768,
            EvidenceId = "discovery-3",
        };

        var json = JsonSerializer.Serialize(value);

        Assert.Equal(value, RoundTrip(value));
        Assert.Contains("\"Outcome\":\"Success\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Completeness\":\"Partial\"", json, StringComparison.Ordinal);
        Assert.Contains("\"FailureKind\":\"Unspecified\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceLimitation_OmitsWhatItDoesNotHave()
    {
        var json = JsonSerializer.Serialize(new EvidenceLimitation
        {
            StepIndex = 0, UnknownTool = true, Outcome = ToolOutcome.Failure, FailureKind = ToolFailureKind.Validation, Completeness = ToolResultCompleteness.Unspecified,
        });

        Assert.DoesNotContain("ToolName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EvidenceId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ShortenedFromCharacters", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticReplyOutcome_RoundTrips_WithEnumsByName()
    {
        var value = new DiagnosticReplyOutcome { Status = DiagnosticReplyStatus.Malformed, Problem = DiagnosticReplyProblem.DuplicateProperty, DiscardedFindings = 2 };

        var json = JsonSerializer.Serialize(value);

        Assert.Equal(value, RoundTrip(value));
        Assert.Contains("\"Status\":\"Malformed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Problem\":\"DuplicateProperty\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewEnums_KeepTheirExplicitValues()
    {
        Assert.Equal([0, 1, 2], Enum.GetValues<DiagnosticReplyStatus>().Select(v => (int)v));
        Assert.Equal(
            [(0, "None"), (1, "NoJsonObject"), (2, "InvalidJson"), (3, "DuplicateProperty"), (4, "TooLarge"), (5, "NotAnObject"), (6, "MissingFindingsArray"), (7, "TooManyFindings")],
            Enum.GetValues<DiagnosticReplyProblem>().Select(v => ((int)v, v.ToString())));
    }

    [Fact]
    public void DelegationRoleRun_WithLimitations_RoundTripsThroughTheDelegationContext()
    {
        var role = Role() with
        {
            EvidenceLimitations = [new EvidenceLimitation { StepIndex = 1, Outcome = ToolOutcome.Failure, FailureKind = ToolFailureKind.Environment, Completeness = ToolResultCompleteness.Unspecified, ToolName = "disk.read" }],
            EvidenceLimitationsOmitted = 4,
            FindingsReply = new DiagnosticReplyOutcome { Status = DiagnosticReplyStatus.Valid, Problem = DiagnosticReplyProblem.None, DiscardedFindings = 1 },
        };
        var run = Run([role]);

        var json = JsonSerializer.Serialize(run, DelegationContractsJsonContext.Default.DelegationRun);
        var back = JsonSerializer.Deserialize(json, DelegationContractsJsonContext.Default.DelegationRun)!.Roles[0];

        Assert.Equal(role.EvidenceLimitations, back.EvidenceLimitations);
        Assert.Equal(4, back.EvidenceLimitationsOmitted);
        Assert.Equal(role.FindingsReply, back.FindingsReply);
        Assert.Contains("\"Outcome\":\"Failure\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordedEmptyList_StaysEmpty_AndIsNotConfusedWithNotRecorded()
    {
        var recorded = Run([Role() with { EvidenceLimitations = [] }]);
        var historical = Run([Role()]);

        var recordedBack = JsonSerializer.Deserialize(JsonSerializer.Serialize(recorded, DelegationContractsJsonContext.Default.DelegationRun), DelegationContractsJsonContext.Default.DelegationRun)!;
        var historicalJson = JsonSerializer.Serialize(historical, DelegationContractsJsonContext.Default.DelegationRun);
        var historicalBack = JsonSerializer.Deserialize(historicalJson, DelegationContractsJsonContext.Default.DelegationRun)!;

        Assert.NotNull(recordedBack.Roles[0].EvidenceLimitations);
        Assert.Empty(recordedBack.Roles[0].EvidenceLimitations!);
        Assert.Null(historicalBack.Roles[0].EvidenceLimitations);
        Assert.DoesNotContain("EvidenceLimitations", historicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("FindingsReply", historicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowStoredBeforeTheContract_Deserializes_AsNotRecorded()
    {
        // A role as the pre-ADR-0044 binary wrote it: no limitation members at all.
        var old = JsonNode.Parse(JsonSerializer.Serialize(Run([Role()]), DelegationContractsJsonContext.Default.DelegationRun))!.AsObject();
        foreach (var role in old["Roles"]!.AsArray())
        {
            Assert.False(role!.AsObject().ContainsKey("EvidenceLimitations"));
        }

        var back = JsonSerializer.Deserialize(old.ToJsonString(), DelegationContractsJsonContext.Default.DelegationRun)!;

        Assert.Null(back.Roles[0].EvidenceLimitations);
        Assert.Equal(0, back.Roles[0].EvidenceLimitationsOmitted);
        Assert.Null(back.Roles[0].FindingsReply);
    }

    // ---- one classification for text and type ----

    [Fact]
    public void TheTypedLimitations_AndTheDigest_ListTheSameSteps_WithTheSameFacts()
    {
        var rng = new DeterministicRandom(0x4811_0301);
        for (var i = 0; i < 200; i++)
        {
            var steps = RandomSteps(rng, rng.Next(0, 14));
            var (typed, omitted) = EvidenceLimitationsDigest.Typed(steps, AgentRoleKind.Discovery);
            var digest = EvidenceLimitationsDigest.Build(steps);

            Assert.Equal(0, omitted);
            if (digest is null)
            {
                Assert.Empty(typed);
                continue;
            }

            var lines = digest.Text.Split('\n').Where(line => line.StartsWith("- step ", StringComparison.Ordinal)).ToList();
            Assert.Equal(lines.Count, typed.Count);
            for (var j = 0; j < typed.Count; j++)
            {
                Assert.StartsWith($"- step {typed[j].StepIndex}: ", lines[j], StringComparison.Ordinal);
                Assert.Equal(typed[j].Completeness is ToolResultCompleteness.Partial or ToolResultCompleteness.Unavailable, lines[j].Contains("completeness", StringComparison.Ordinal));
                Assert.Equal(typed[j].ShortenedFromCharacters is not null, lines[j].Contains("shortened", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void TypedLimitations_KeepTheMostRecentSixtyFour_AndCountTheRest()
    {
        var steps = Enumerable.Range(0, 70)
            .Select(i => Step(i, "disk.read", ToolCallResult.Failure("x") with { FailureKind = ToolFailureKind.Environment }))
            .ToList();

        var (typed, omitted) = EvidenceLimitationsDigest.Typed(steps, AgentRoleKind.Diagnostic);

        Assert.Equal(64, typed.Count);
        Assert.Equal(6, omitted);
        Assert.Equal(6, typed[0].StepIndex);
        Assert.Equal(69, typed[^1].StepIndex);
    }

    [Fact]
    public void AnEvidenceIdIsSetOnlyForAStepThatProducedEvidence_AndAnUnknownToolHasNoName()
    {
        List<PlanStep> steps =
        [
            Step(0, "events.read", ToolCallResult.Success("x") with { Completeness = ToolResultCompleteness.Partial }),
            Step(1, "disk.read", ToolCallResult.Failure("x") with { FailureKind = ToolFailureKind.Environment }),
            Step(2, "made.up", ToolCallResult.Failure("x") with { FailureKind = ToolFailureKind.Validation }, description: "Denied"),
            Step(3, "big.read", ToolCallResult.Success(new string('a', 100)), observation: "aaa... [truncated] ...aaa"),
            Step(4, "bad name!", ToolCallResult.Success("x") with { Completeness = ToolResultCompleteness.Unavailable }),
        ];

        var (typed, _) = EvidenceLimitationsDigest.Typed(steps, AgentRoleKind.Diagnostic);

        Assert.Equal(["diagnostic-0", null, null, "diagnostic-3", "diagnostic-4"], typed.Select(l => l.EvidenceId));
        Assert.Equal(["events.read", "disk.read", null, "big.read", null], typed.Select(l => l.ToolName));
        Assert.True(typed[2].UnknownTool);
        Assert.Equal(100, typed[3].ShortenedFromCharacters);
        Assert.All(typed, l => Assert.Null(l.GetType().GetProperty("Output")));
    }

    // ---- fixtures ----

    private static DelegationRoleRun Role() => new()
    {
        Agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Diagnostic),
        Envelope = new AuthorityEnvelope(ActorIdentity.RuntimeSystem, 1, [], [], ["system.cpu"], RiskLevel.Read, BlastRadius.Single, ["local"], ["local"], new DelegationBudget(1, 1, T0)),
        Status = DelegationRoleStatus.Completed,
        Consumed = BudgetConsumption.Empty,
    };

    private static DelegationRun Run(IReadOnlyList<DelegationRoleRun> roles) => new()
    {
        Id = Guid.NewGuid(),
        Node = NodeId.Local,
        Actor = ActorIdentity.FromOperatingSystemUser("operator"),
        Objective = "o",
        Status = DelegationStatus.DiagnosisCompleted,
        RootEnvelope = roles[0].Envelope with { Depth = 0 },
        Roles = roles,
        Journal = [],
        CreatedAtUtc = T0,
        UpdatedAtUtc = T0,
    };

    private static PlanStep Step(int index, string tool, ToolCallResult result, string? description = null, string? observation = null) => new(
        index, description ?? tool, new ModelToolCall($"call-{index}", tool, ToolArguments.Empty), result, observation ?? result.Output ?? string.Empty);

    private static List<PlanStep> RandomSteps(DeterministicRandom rng, int count)
    {
        string[] tools = ["a.read", "b.read", "c.read"];
        var steps = new List<PlanStep>();
        for (var i = 0; i < count; i++)
        {
            var tool = tools[rng.Next(tools.Length)];
            var result = rng.Next(4) switch
            {
                0 => ToolCallResult.Failure("f") with { FailureKind = (ToolFailureKind)rng.Next(0, 3) },
                1 => ToolCallResult.Success("abcdef") with { Completeness = (ToolResultCompleteness)rng.Next(0, 4) },
                _ => ToolCallResult.Success("ok"),
            };
            var shortened = result.Succeeded && rng.Next(5) == 0;
            steps.Add(Step(i, tool, result, observation: shortened ? "ab..." : null));
        }

        return steps;
    }
}
