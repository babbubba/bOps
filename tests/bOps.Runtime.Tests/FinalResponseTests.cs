// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using static bOps.Runtime.Tests.EvidenceScenario;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0042 §13 and §17 rows 21, 32, 33: the one shared final-step predicate (an exact marker and no tool call), the three
/// markers, and the reconstruction of a final step's logical model calls from the persisted order, <c>ModelAttempt</c> and
/// the marker — with no model-call kind stored anywhere.
/// </summary>
public sealed class FinalResponseTests
{
    private static ModelCallRecord Record(int? attempt, ModelCallOutcome outcome = ModelCallOutcome.Success) =>
        new("fake", "fake-model", null, DateTimeOffset.UnixEpoch, 1, outcome, null, null, null, null, null, false) { ModelAttempt = attempt };

    private static PlanStep FinalStep(string description, params ModelCallRecord[] calls) =>
        new(3, description, null, null, "answer") { ModelCalls = calls };

    // ---- markers and predicate ----

    [Fact]
    public void TheThreeMarkers_AreExactlyTheAcceptedStrings()
    {
        Assert.Equal("Final response", FinalResponse.Marker);
        Assert.Equal("Final response; evidence disclosure re-ask accepted", FinalResponse.ReAskAcceptedMarker);
        Assert.Equal("Final response; evidence disclosure re-ask result not used", FinalResponse.ReAskNotUsedMarker);
        Assert.Equal("Final response", FinalResponse.DescriptionFor(EvidenceDisclosureOutcome.NotAttempted));
        Assert.Equal(FinalResponse.ReAskAcceptedMarker, FinalResponse.DescriptionFor(EvidenceDisclosureOutcome.Accepted));
        Assert.Equal(FinalResponse.ReAskNotUsedMarker, FinalResponse.DescriptionFor(EvidenceDisclosureOutcome.ResultNotUsed));
    }

    [Theory]
    [InlineData("Final response")]
    [InlineData("Final response; evidence disclosure re-ask accepted")]
    [InlineData("Final response; evidence disclosure re-ask result not used")]
    public void IsFinalStep_AcceptsEachMarker_WithNoToolCall(string description)
    {
        Assert.True(FinalResponse.IsFinalStep(new PlanStep(1, description, null, null, "text")));
    }

    [Theory]
    [InlineData("Final response")]
    [InlineData("Final response; evidence disclosure re-ask accepted")]
    [InlineData("Final response; evidence disclosure re-ask result not used")]
    public void IsFinalStep_RejectsAToolStepWhoseDescriptionEqualsAMarker(string description)
    {
        var toolStep = new PlanStep(1, description, new ModelToolCall("c", "test.read", ToolArguments.Empty), ToolCallResult.Success("out"), "out");

        Assert.False(FinalResponse.IsFinalStep(toolStep));
    }

    [Theory]
    [InlineData("final response")]
    [InlineData("Final response ")]
    [InlineData("Final response; evidence disclosure")]
    [InlineData("Final response; evidence disclosure re-ask accepted!")]
    [InlineData("Final response; evidence disclosure re-ask")]
    [InlineData("Not a Final response")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Denied")]
    public void IsFinalStep_UsesNoSubstringPrefixOrCaseInsensitiveMatching(string? description)
    {
        Assert.False(FinalResponse.IsFinalStep(new PlanStep(1, description, null, null, "text")));
    }

    [Theory]
    [InlineData("Final response", 0)]
    [InlineData("Final response; evidence disclosure re-ask accepted", 1)]
    [InlineData("Final response; evidence disclosure re-ask result not used", 2)]
    [InlineData("Model failure", 0)]
    public void DisclosureOf_ReadsTheOutcomeFromTheMarker(string description, int expectedOutcome)
    {
        Assert.Equal((EvidenceDisclosureOutcome)expectedOutcome, FinalResponse.DisclosureOf(new PlanStep(1, description, null, null, "text")));
    }

    [Fact]
    public void FinalText_ReturnsThePersistedAnswer_ForAllThreeFinalDescriptions()
    {
        foreach (var description in new[] { FinalResponse.Marker, FinalResponse.ReAskAcceptedMarker, FinalResponse.ReAskNotUsedMarker })
        {
            var task = new TaskState(Guid.NewGuid(), NodeId.Local, "goal", AgentTaskStatus.Completed,
                [ToolStep(0, "test.read", Complete()), new PlanStep(1, description, null, null, $"answer for {description}")], [], DateTimeOffset.UnixEpoch);

            Assert.Equal($"answer for {description}", DelegationRoleData.FinalText(task));
        }
    }

    [Fact]
    public void FinalText_IgnoresAToolStepWhoseDescriptionEqualsAMarker_AndReturnsNullWithoutAFinalStep()
    {
        var spoofed = new PlanStep(1, FinalResponse.Marker, new ModelToolCall("c", "test.read", ToolArguments.Empty), ToolCallResult.Success("spoof"), "spoof");
        var task = new TaskState(Guid.NewGuid(), NodeId.Local, "goal", AgentTaskStatus.Completed,
            [new PlanStep(0, FinalResponse.Marker, null, null, "the answer"), spoofed], [], DateTimeOffset.UnixEpoch);

        Assert.Equal("the answer", DelegationRoleData.FinalText(task));
        Assert.Null(DelegationRoleData.FinalText(task with { Steps = [spoofed] }));
    }

    [Fact]
    public async Task TheRunnerWritesTheMarkerThePredicateReads()
    {
        var model = new FakeChatModel(Plan(), Final("Done."));
        var state = await Runner(model, Registry(), new RecordingAuditSink()).RunAsync("goal", Actor);

        Assert.True(FinalResponse.IsFinalStep(state.Steps[^1]));
        Assert.Equal("Done.", DelegationRoleData.FinalText(state));
    }

    // ---- logical model calls ----

    [Fact]
    public void LogicalCalls_OfANormalFinalAnswer_IsOneAnswerCall()
    {
        var calls = FinalResponse.LogicalCalls(FinalStep(FinalResponse.Marker, Record(1)));

        var only = Assert.Single(calls);
        Assert.Equal(LogicalModelCallRole.Answer, only.Role);
        Assert.Single(only.Attempts);
    }

    [Fact]
    public void LogicalCalls_StartsANewCallAtEveryAttemptOne_AndKeepsRetriedAttemptsTogether()
    {
        // answer needed 2 attempts; an empty-reply retry needed 3; no re-ask.
        var step = FinalStep(FinalResponse.Marker, Record(1, ModelCallOutcome.Failure), Record(2), Record(1, ModelCallOutcome.Failure), Record(2, ModelCallOutcome.Failure), Record(3));

        var calls = FinalResponse.LogicalCalls(step);

        Assert.Equal([LogicalModelCallRole.Answer, LogicalModelCallRole.EmptyAnswerRetry], calls.Select(c => c.Role));
        Assert.Equal([2, 3], calls.Select(c => c.Attempts.Count));
    }

    [Theory]
    [InlineData("Final response; evidence disclosure re-ask accepted")]
    [InlineData("Final response; evidence disclosure re-ask result not used")]
    public void LogicalCalls_TakesTheLastCallAsTheReAsk_WhenTheDescriptionCarriesAReAskMarker(string marker)
    {
        // answer, empty-reply retry, then the re-ask that needed three attempts.
        var step = FinalStep(marker, Record(1), Record(1), Record(1, ModelCallOutcome.Failure), Record(2, ModelCallOutcome.Failure), Record(3));

        var calls = FinalResponse.LogicalCalls(step);

        Assert.Equal(
            [LogicalModelCallRole.Answer, LogicalModelCallRole.EmptyAnswerRetry, LogicalModelCallRole.EvidenceDisclosureReAsk],
            calls.Select(c => c.Role));
        Assert.Equal([1, 1, 3], calls.Select(c => c.Attempts.Count));
    }

    [Fact]
    public void LogicalCalls_OfAReAskedAnswerWithNoEmptyReplies_IsAnswerThenReAsk()
    {
        var step = FinalStep(FinalResponse.ReAskAcceptedMarker, Record(1), Record(1));

        Assert.Equal([LogicalModelCallRole.Answer, LogicalModelCallRole.EvidenceDisclosureReAsk], FinalResponse.LogicalCalls(step).Select(c => c.Role));
    }

    [Fact]
    public void LogicalCalls_TreatsEveryLegacyRecordWithoutAnAttemptNumberAsItsOwnCall()
    {
        var step = FinalStep(FinalResponse.Marker, Record(null), Record(null));

        Assert.Equal([LogicalModelCallRole.Answer, LogicalModelCallRole.EmptyAnswerRetry], FinalResponse.LogicalCalls(step).Select(c => c.Role));
    }

    [Fact]
    public void LogicalCalls_DoesNotInventAReAsk_WhenOnlyOneLogicalCallWasRecorded()
    {
        var step = FinalStep(FinalResponse.ReAskNotUsedMarker, Record(1));

        Assert.Equal([LogicalModelCallRole.Answer], FinalResponse.LogicalCalls(step).Select(c => c.Role));
    }

    [Fact]
    public void LogicalCalls_OfAStepWithoutRecordedCalls_IsEmpty()
    {
        Assert.Empty(FinalResponse.LogicalCalls(new PlanStep(0, FinalResponse.Marker, null, null, "text")));
    }

    [Fact]
    public async Task TheRunnersRecordedModelCalls_ReconstructIntoNormalEmptyRetryAndReAsk()
    {
        // empty reply on the step call, then the answer; then a heading-less answer is restated by the re-ask.
        var tool = new ResultTool("test.partial", Partial());
        var model = new FakeChatModel(
            Plan(), Call("test.partial"),
            new ModelResponse(null, [], true, null), Final(OriginalAnswer), Final(DisclosedAnswer));

        var state = await Runner(model, Registry(tool), new RecordingAuditSink()).RunAsync("goal", Actor);

        var final = state.Steps[^1];
        Assert.Equal(FinalResponse.ReAskAcceptedMarker, final.Description);
        var calls = FinalResponse.LogicalCalls(final);
        Assert.Equal(
            [LogicalModelCallRole.Answer, LogicalModelCallRole.EmptyAnswerRetry, LogicalModelCallRole.EvidenceDisclosureReAsk],
            calls.Select(c => c.Role));
        Assert.All(calls, call => Assert.Single(call.Attempts));
        Assert.Equal(3, final.ModelCalls!.Count);
    }
}
