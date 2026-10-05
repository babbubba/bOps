// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0044 section 17: the Diagnostic reply is read strictly and within bounds, every failure is a typed outcome with zero
/// findings, nothing escapes, the existing first-<c>{</c>-to-last-<c>}</c> containment is kept exactly (no first-object rescue),
/// and every retained finding still cites only recorded evidence (ADR-0023).
/// </summary>
public sealed class DiagnosticReplyParserTests
{
    private static readonly IReadOnlySet<string> Recorded = new HashSet<string>(StringComparer.Ordinal) { "discovery-0", "discovery-1" };

    private static (List<Finding> Findings, DiagnosticReplyOutcome Outcome) Read(string? reply) => DelegationRoleData.ReadFindings(reply, Recorded);

    private static void AssertMalformed(string? reply, DiagnosticReplyProblem problem)
    {
        var (findings, outcome) = Read(reply);
        Assert.Empty(findings);
        Assert.Equal(DiagnosticReplyStatus.Malformed, outcome.Status);
        Assert.Equal(problem, outcome.Problem);
        Assert.Equal(0, outcome.DiscardedFindings);
    }

    private const string OneFinding = "{\"findings\":[{\"summary\":\"The service stopped.\",\"evidenceIds\":[\"discovery-0\"],\"severity\":\"high\"}]}";

    [Fact]
    public void AValidReply_YieldsItsFindings_AndAValidOutcome()
    {
        var (findings, outcome) = Read(OneFinding);

        var finding = Assert.Single(findings);
        Assert.Equal("finding-0", finding.Id);
        Assert.Equal(["discovery-0"], finding.EvidenceIds);
        Assert.Equal(RiskLevel.High, finding.Severity);
        Assert.Equal(new DiagnosticReplyOutcome { Status = DiagnosticReplyStatus.Valid, Problem = DiagnosticReplyProblem.None }, outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void AnEmptyReply_IsAbsent(string? reply)
    {
        var (findings, outcome) = Read(reply);

        Assert.Empty(findings);
        Assert.Equal(DiagnosticReplyStatus.Absent, outcome.Status);
        Assert.Equal(DiagnosticReplyProblem.None, outcome.Problem);
    }

    [Fact]
    public void ProseOrAFenceAroundOneObject_IsToleratedAsBefore()
    {
        Assert.Single(Read($"Here is my answer:\n```json\n{OneFinding}\n```").Findings);
    }

    [Theory]
    [InlineData("no braces at all")]
    [InlineData("} reversed {")]
    public void NoObjectSlice_IsNoJsonObject(string reply) => AssertMalformed(reply, DiagnosticReplyProblem.NoJsonObject);

    [Theory]
    [InlineData("{not json}")]
    [InlineData("{\"findings\":[}")]
    [InlineData("{\"findings\":[]} {\"findings\":[]}")]
    [InlineData("{\"findings\":[],}")]
    [InlineData("{\"findings\":[] /* a comment */}")]
    public void ASyntaxError_OrTwoTopLevelObjects_IsInvalidJson_NeverTheFirstObject(string reply) =>
        AssertMalformed(reply, DiagnosticReplyProblem.InvalidJson);

    [Theory]
    [InlineData("{\"findings\":[],\"findings\":[{\"summary\":\"x\",\"evidenceIds\":[\"discovery-0\"]}]}")]
    [InlineData("{\"findings\":[{\"summary\":\"a\",\"summary\":\"b\",\"evidenceIds\":[\"discovery-0\"]}]}")]
    [InlineData("{\"findings\":[{\"summary\":\"a\",\"evidenceIds\":[\"discovery-0\"],\"evidenceIds\":[\"discovery-1\"]}]}")]
    [InlineData("{\"findings\":[{\"summary\":\"a\",\"evidenceIds\":[\"discovery-0\"],\"severity\":\"low\",\"severity\":\"high\"}]}")]
    [InlineData("{\"findings\":[{\"summary\":\"a\",\"evidenceIds\":[\"discovery-0\"],\"extra\":{\"k\":1,\"k\":2}}]}")]
    [InlineData("{\"findings\":[{\"summary\":\"a\",\"evidenceIds\":[\"discovery-0\"]},{\"summary\":\"b\",\"summary\":\"c\",\"evidenceIds\":[\"discovery-1\"]}]}")]
    public void ADuplicatePropertyAtAnyDepth_IsDuplicateProperty_WithZeroFindings_NotFilteredPerFinding(string reply) =>
        AssertMalformed(reply, DiagnosticReplyProblem.DuplicateProperty);

    [Fact]
    public void DuplicateDetection_IsOrdinal_SoNamesDifferingOnlyInCaseAreNotDuplicates()
    {
        var (findings, outcome) = Read("{\"findings\":[{\"summary\":\"a\",\"Summary\":\"b\",\"evidenceIds\":[\"discovery-0\"]}]}");

        Assert.Equal(DiagnosticReplyStatus.Valid, outcome.Status);
        Assert.Single(findings);
    }

    [Fact]
    public void AReplyLongerThanTheBound_IsTooLarge_WithoutParsing_AndOneAtTheBoundIsRead()
    {
        var atBound = Padded(OneFinding, DelegationRoleData.MaxReplyCharacters);
        var overBound = Padded(OneFinding, DelegationRoleData.MaxReplyCharacters + 1);

        Assert.Equal(65_536, atBound.Length);
        Assert.Single(Read(atBound).Findings);
        AssertMalformed(overBound, DiagnosticReplyProblem.TooLarge);
    }

    [Fact]
    public void NestingDeeperThanSixteen_IsInvalidJson_AndSixteenIsRead()
    {
        // The root object is depth 1, its findings array 2, a finding 3, and each wrapper adds one level.
        Assert.Equal(DiagnosticReplyStatus.Valid, Read(Nested(16)).Outcome.Status);
        AssertMalformed(Nested(17), DiagnosticReplyProblem.InvalidJson);
        AssertMalformed(Nested(500), DiagnosticReplyProblem.InvalidJson);
    }

    [Fact]
    public void MoreThanSixtyFourFindings_IsTooManyFindings_WithZeroFindings_NeverTruncated()
    {
        Assert.Equal(64, Read(Findings(64)).Findings.Count);
        AssertMalformed(Findings(65), DiagnosticReplyProblem.TooManyFindings);
    }

    [Theory]
    [InlineData("{\"x\":[]}", DiagnosticReplyProblem.MissingFindingsArray)]
    [InlineData("{\"findings\":{}}", DiagnosticReplyProblem.MissingFindingsArray)]
    [InlineData("{\"findings\":null}", DiagnosticReplyProblem.MissingFindingsArray)]
    public void AnObjectWithoutAFindingsArray_IsMissingFindingsArray(string reply, DiagnosticReplyProblem problem) => AssertMalformed(reply, problem);

    [Fact]
    public void AQuotedObject_IsNotRescuedFromItsString()
    {
        // The slice runs from the first { to the last }, so a JSON string holding an object is cut into invalid JSON, never unquoted.
        AssertMalformed("\"{\\\"findings\\\":[]}\"", DiagnosticReplyProblem.InvalidJson);
    }

    [Fact]
    public void PerFindingRules_AreUnchanged_AndDroppedEntriesAreCounted()
    {
        var reply = "{\"findings\":["
            + "{\"summary\":\"kept\",\"evidenceIds\":[\"discovery-0\"]},"
            + "{\"summary\":\"\",\"evidenceIds\":[\"discovery-0\"]},"
            + "{\"summary\":\"cites nothing\",\"evidenceIds\":[]},"
            + "{\"summary\":\"half a citation\",\"evidenceIds\":[\"discovery-0\",\"invented-9\"]},"
            + "{\"summary\":\"not a string id\",\"evidenceIds\":[7]},"
            + "\"not an object\","
            + "{\"summary\":\"also kept\",\"evidenceIds\":[\"discovery-1\"],\"severity\":7}"
            + "]}";

        var (findings, outcome) = Read(reply);

        Assert.Equal(["kept", "also kept"], findings.Select(f => f.Summary));
        Assert.Equal(["finding-0", "finding-1"], findings.Select(f => f.Id));
        Assert.Null(findings[1].Severity);
        Assert.Equal(DiagnosticReplyStatus.Valid, outcome.Status);
        Assert.Equal(5, outcome.DiscardedFindings);
    }

    [Fact]
    public void EveryRetainedFinding_CitesOnlyRecordedEvidence_ForGeneratedReplies()
    {
        var rng = new DeterministicRandom(0x4811_0201);
        string[] ids = ["discovery-0", "discovery-1", "invented-1", "diagnostic-4"];
        for (var i = 0; i < 300; i++)
        {
            var count = rng.Next(0, 8);
            var builder = new StringBuilder("{\"findings\":[");
            for (var j = 0; j < count; j++)
            {
                var cited = Enumerable.Range(0, rng.Next(0, 3)).Select(_ => $"\"{ids[rng.Next(ids.Length)]}\"");
                builder.Append(j == 0 ? string.Empty : ",").Append("{\"summary\":\"s").Append(j).Append("\",\"evidenceIds\":[").Append(string.Join(',', cited)).Append("]}");
            }

            var (findings, _) = Read(builder.Append("]}").ToString());
            Assert.All(findings, finding =>
            {
                Assert.NotEmpty(finding.EvidenceIds);
                Assert.All(finding.EvidenceIds, id => Assert.Contains(id, Recorded));
            });
        }
    }

    [Fact]
    public void NoReplyOfAnyShape_ThrowsOutOfTheParser()
    {
        string[] hostile =
        [
            "{", "}", "{}", "{\"findings\":[{}]}", "{\"findings\":[{\"summary\":{\"a\":1},\"evidenceIds\":\"discovery-0\"}]}",
            "{\"\\ud800\":1}", "{\"findings\":[1,2,3]}", new string('{', 70_000), "{\"findings\":[" + new string('[', 40) + "]}",
        ];

        foreach (var reply in hostile)
        {
            var (_, outcome) = Read(reply);
            Assert.True(Enum.IsDefined(outcome.Status));
            Assert.True(Enum.IsDefined(outcome.Problem));
        }
    }

    [Fact]
    public void FindingsOf_IsTheSameReadingWithoutTheOutcome()
    {
        Assert.Equal(Read(OneFinding).Findings.Select(f => f.Summary), DelegationRoleData.FindingsOf(OneFinding, Recorded).Select(f => f.Summary));
    }

    private static string Padded(string json, int length) => json + new string(' ', length - json.Length);

    private static string Findings(int count) =>
        "{\"findings\":[" + string.Join(',', Enumerable.Range(0, count).Select(i => $"{{\"summary\":\"f{i}\",\"evidenceIds\":[\"discovery-0\"]}}")) + "]}";

    private static string Nested(int depth)
    {
        // depth 1: the root object; 2: the findings array; 3: a finding; deeper levels are an "extra" chain of objects.
        var extra = new StringBuilder();
        for (var i = 3; i < depth; i++)
        {
            extra.Append("{\"n\":");
        }

        extra.Append("null");
        for (var i = 3; i < depth; i++)
        {
            extra.Append('}');
        }

        return "{\"findings\":[{\"summary\":\"deep\",\"evidenceIds\":[\"discovery-0\"],\"extra\":" + extra + "}]}";
    }
}
