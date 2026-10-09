// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using static bOps.Runtime.Tests.EvidenceScenario;

namespace bOps.Runtime.Tests;

/// <summary>
/// PRE-5 (ADR-0042 amendment §5–§9): a final answer that the bounded check finds contradicting a persisted typed fact is
/// never persisted as a completed answer; one correction may replace it, otherwise the attempt fails. The scenario is the
/// real one semantically — an event-reading tool persisted a positive count for an event group, and the first final answer
/// denied it — but the runtime sees only opaque fact identifiers.
/// </summary>
public sealed class EvidenceGroundingLoopTests
{
    private const string EventsTool = "test.events";

    // The package-side shape of the real evidence (System.Core's system.events facts); opaque to the runtime.
    private const string FactType = "system.events.matched-count";
    private const string FactKey = "source=Microsoft-Windows-WHEA-Logger;eventId=19";

    private const string Contradiction = "No WHEA 18/19/29 events were observed in the System log";

    private const string ContradictoryAnswer =
        "The machine froze because of a driver issue. " + Contradiction + ", so hardware errors are ruled out.";

    private const string CorrectedAnswer =
        "WHEA Event 19 (a corrected hardware error) was observed 3 times in the requested window. It is a hypothesis, not an " +
        "established cause, that these errors relate to the freezes.";

    private const string Artifact =
        "<tool_call>\n<function=runtime_evidence_read>\n<parameter=step>0</parameter>\n</function>\n</tool_call>";

    private static readonly EvidenceFact WheaFact = new(FactType, FactKey, ToolParameterType.Integer, JsonValue.Create(3));

    /// <summary>Replays a script of responses and exceptions, records every request, and fails non-transiently once exhausted.</summary>
    private sealed class ScriptedModel(params object[] script) : IChatModel
    {
        private int _next;

        public List<ModelRequest> Requests { get; } = [];

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (_next >= script.Length)
            {
                _next++;
                return Task.FromException<ModelResponse>(
                    new ModelProtocolException("provider unavailable") { FailureKind = ModelFailureKind.Authentication });
            }

            var item = script[_next++];
            return item is Exception failure ? Task.FromException<ModelResponse>(failure) : Task.FromResult((ModelResponse)item);
        }
    }

    private static ToolCallResult Evidence(ToolResultCompleteness completeness = ToolResultCompleteness.Complete) =>
        ToolCallResult.Success("{\"groups\":[]}") with { Completeness = completeness, Facts = [WheaFact] };

    private static AgentRunnerOptions Options() =>
        new() { ModelCallMaxAttempts = 1 };

    private static async Task<(TaskState State, ScriptedModel Model, ResultTool Tool)> RunAsync(
        ToolCallResult result, AgentRunnerOptions options, params object[] afterTool)
    {
        var tool = new ResultTool(EventsTool, result);
        var model = new ScriptedModel([Plan(EventsTool), Call(EventsTool), .. afterTool]);
        var state = await Runner(model, Registry(tool), new RecordingAuditSink(), options).RunAsync("why does this PC freeze?", Actor);
        return (state, model, tool);
    }

    private static Task<(TaskState State, ScriptedModel Model, ResultTool Tool)> RunAsync(params object[] afterTool) =>
        RunAsync(Evidence(), Options(), afterTool);

    private static ModelResponse Verdict(params (string Fact, string Quote)[] contradictions) =>
        Final(new JsonObject
        {
            ["contradictions"] = new JsonArray([.. contradictions.Select(item => (JsonNode)new JsonObject { ["fact"] = item.Fact, ["quote"] = item.Quote })]),
        }.ToJsonString());

    private static ModelResponse Flagged => Verdict(("F0.1", Contradiction));

    private static ModelProtocolException Failure(ModelFailureKind kind) => new("model call failed") { FailureKind = kind };

    private static void AssertNeverPersistedAsAnswer(TaskState state, string text) =>
        Assert.DoesNotContain(state.Steps, step => FinalResponse.IsFinalStep(step) && step.Observation?.Contains(text, StringComparison.Ordinal) == true);

    private static string LastUserTurn(ModelRequest request) =>
        request.History[^1].Content ?? string.Empty;

    [Fact]
    public async Task G7_WheaStylePositiveFact_ContradictoryFirstAnswer_IsNotPersistedUnchanged()
    {
        var (state, model, _) = await RunAsync(Final(ContradictoryAnswer), Flagged, Final(CorrectedAnswer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        AssertNeverPersistedAsAnswer(state, Contradiction);
        Assert.Equal(CorrectedAnswer, state.Steps[^1].Observation);
        // The grounding block put the persisted fact in front of the final synthesis.
        Assert.Contains("- F0.1 step 0 test.events: type \"system.events.matched-count\" key \"source=Microsoft-Windows-WHEA-Logger;eventId=19\" = Integer 3",
            model.Requests[2].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task G7_WheaStyle_WhenTheContradictionCannotBeCorrected_TheTaskNeverCompletes()
    {
        var (state, _, _) = await RunAsync(Final(ContradictoryAnswer), Flagged, Final(ContradictoryAnswer));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, state.TerminalReason?.Kind);
        AssertNeverPersistedAsAnswer(state, Contradiction);
        Assert.DoesNotContain(state.Steps, step => step.Observation?.Contains(Contradiction, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task G8_ValidCorrection_IsAccepted_WithTheCorrectedMarker_AndIdentifiableCalls()
    {
        var (state, model, tool) = await RunAsync(Final(ContradictoryAnswer), Flagged, Final(CorrectedAnswer));

        var final = state.Steps[^1];
        Assert.Equal("Final response; evidence grounding corrected", final.Description);
        Assert.Equal(EvidenceGroundingOutcome.Corrected, FinalResponse.GroundingOf(final));
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(5, model.Requests.Count);

        var check = model.Requests[3];
        Assert.Equal(EvidenceGroundingLedger.CheckInstruction, LastUserTurn(check));
        Assert.Equal(ContradictoryAnswer, check.History[^2].Content);

        var correction = model.Requests[4];
        Assert.StartsWith("EvidenceGroundingCorrection/v1.", LastUserTurn(correction), StringComparison.Ordinal);
        Assert.Contains("F0.1", LastUserTurn(correction), StringComparison.Ordinal);
        Assert.DoesNotContain(Contradiction, LastUserTurn(correction), StringComparison.Ordinal);

        Assert.Equal(
            [LogicalModelCallRole.Answer, LogicalModelCallRole.EvidenceGroundingCheck, LogicalModelCallRole.EvidenceGroundingCorrection],
            FinalResponse.LogicalCalls(final).Select(call => call.Role));
    }

    [Fact]
    public async Task G8_NoContradictionCited_KeepsTheAnswer_WithOneExtraCall()
    {
        var (state, model, _) = await RunAsync(Final(CorrectedAnswer), Verdict());

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(CorrectedAnswer, state.Steps[^1].Observation);
        Assert.Equal("Final response; evidence grounding check cited no contradiction", state.Steps[^1].Description);
        Assert.Equal(4, model.Requests.Count);
    }

    [Theory]
    [InlineData(Contradiction + ".")]
    [InlineData("Summary.\nNo WHEA 18/19/29   events were observed\nin the System log, as before.")]
    public async Task G9_RepeatedContradiction_FailsBounded_WithoutAThirdCall(string correction)
    {
        var (state, model, _) = await RunAsync(Final(ContradictoryAnswer), Flagged, Final(correction), Final(CorrectedAnswer));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, state.TerminalReason?.Kind);
        Assert.Equal(5, model.Requests.Count);
        AssertNeverPersistedAsAnswer(state, Contradiction);
    }

    [Fact]
    public async Task G9_CorrectionThatIsAToolCallOrAnArtifact_FailsAndExecutesNothing()
    {
        var (toolCall, _, tool) = await RunAsync(Final(ContradictoryAnswer), Flagged, Call(EventsTool, "call-2"));
        var (artifact, _, _) = await RunAsync(Final(ContradictoryAnswer), Flagged, Final(Artifact));

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, toolCall.TerminalReason?.Kind);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, artifact.TerminalReason?.Kind);
        Assert.Equal(AgentTaskStatus.Failed, toolCall.Status);
        Assert.Equal(AgentTaskStatus.Failed, artifact.Status);
    }

    [Fact]
    public async Task G9_EmptyCorrection_FailsAsEmptyResponse()
    {
        var (state, _, _) = await RunAsync(Final(ContradictoryAnswer), Flagged, Final("   "));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.EmptyResponse, state.TerminalReason?.Kind);
    }

    public static TheoryData<object> UnusableChecks => new()
    {
        Final("I think the answer is fine."),
        Final("{\"contradictions\":[{\"fact\":\"F7.1\",\"quote\":\"driver issue\"}]}"),
        Final("{\"contradictions\":[{\"fact\":\"F0.1\",\"quote\":\"a sentence the answer never contained\"}]}"),
        Final(""),
        Call(EventsTool, "call-2"),
        Failure(ModelFailureKind.Timeout),
        Failure(ModelFailureKind.Unknown),
    };

    [Theory]
    [MemberData(nameof(UnusableChecks))]
    public async Task G10_MalformedOrTimedOutCheck_KeepsTheUnclassifiedAnswer_WithTheUnavailableMarker(object check)
    {
        var (state, model, tool) = await RunAsync(Final(ContradictoryAnswer), check);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(ContradictoryAnswer, state.Steps[^1].Observation);
        Assert.Equal("Final response; evidence grounding check unavailable", state.Steps[^1].Description);
        Assert.Equal(4, model.Requests.Count);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Theory]
    [InlineData(ModelFailureKind.Timeout)]
    [InlineData(ModelFailureKind.Unknown)]
    public async Task G10_CorrectionTimeoutOrFailure_FailsWithTheModelFailureKind(ModelFailureKind kind)
    {
        var (state, _, _) = await RunAsync(Final(ContradictoryAnswer), Flagged, Failure(kind));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.ModelFailure, state.TerminalReason?.Kind);
        Assert.Equal(kind, state.TerminalReason?.FailureKind);
        AssertNeverPersistedAsAnswer(state, Contradiction);
    }

    [Fact]
    public async Task G11_ProtocolArtifact_StaysInvalid_AndIsNeverChecked()
    {
        var (state, model, _) = await RunAsync(Final(Artifact), Final(Artifact));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, state.TerminalReason?.Kind);
        Assert.Equal(4, model.Requests.Count);
        Assert.DoesNotContain(model.Requests, request => LastUserTurn(request) == EvidenceGroundingLedger.CheckInstruction);
    }

    [Fact]
    public async Task G11_ArtifactCorrectedFirst_ThenTheGroundingGuardRunsOnTheCorrectedAnswer()
    {
        var (state, model, _) = await RunAsync(Final(Artifact), Final(ContradictoryAnswer), Flagged, Final(CorrectedAnswer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(CorrectedAnswer, state.Steps[^1].Observation);
        Assert.Equal(ContradictoryAnswer, model.Requests[4].History[^2].Content);
    }

    [Fact]
    public async Task G11_EmptyCandidate_IsHandledBeforeTheGuard()
    {
        var (state, model, _) = await RunAsync(Evidence(), new AgentRunnerOptions { ModelCallMaxAttempts = 1, EmptyFinalResponseRetries = 0 },
            Final("  "));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.EmptyResponse, state.TerminalReason?.Kind);
        Assert.Equal(3, model.Requests.Count);
    }

    [Fact]
    public async Task G12_DisclosureReAskRunsFirst_ThenTheCheck_AndTheCorrectionKeepsTheHeading()
    {
        const string disclosed = ContradictoryAnswer + "\n\nEvidence limitations\n- test.events returned a partial result.";
        const string correctedDisclosed = CorrectedAnswer + "\n\nEvidence limitations\n- test.events returned a partial result.";

        var (state, model, _) = await RunAsync(Evidence(ToolResultCompleteness.Partial), Options(),
            Final(ContradictoryAnswer), Final(disclosed), Flagged, Final(correctedDisclosed));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(correctedDisclosed, state.Steps[^1].Observation);
        Assert.Equal("Final response; evidence disclosure re-ask accepted; evidence grounding corrected", state.Steps[^1].Description);
        Assert.Equal(EvidenceDisclosure.Instruction, LastUserTurn(model.Requests[3]));
        Assert.Equal(EvidenceGroundingLedger.CheckInstruction, LastUserTurn(model.Requests[4]));
        Assert.Equal(disclosed, model.Requests[4].History[^2].Content);
        Assert.Contains("(from partial evidence)", model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(
            [LogicalModelCallRole.Answer, LogicalModelCallRole.EvidenceDisclosureReAsk, LogicalModelCallRole.EvidenceGroundingCheck,
                LogicalModelCallRole.EvidenceGroundingCorrection],
            FinalResponse.LogicalCalls(state.Steps[^1]).Select(call => call.Role));
    }

    [Fact]
    public async Task G12_ACorrectionThatDropsTheDisclosure_IsNotAccepted()
    {
        const string disclosed = ContradictoryAnswer + "\n\nEvidence limitations\n- test.events returned a partial result.";

        var (state, _, _) = await RunAsync(Evidence(ToolResultCompleteness.Partial), Options(),
            Final(disclosed), Flagged, Final(CorrectedAnswer));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, state.TerminalReason?.Kind);
    }

    [Fact]
    public async Task G13_NoFacts_NoBlock_NoExtraCall()
    {
        var tool = new ResultTool(EventsTool, Complete());
        var model = new ScriptedModel(Plan(EventsTool), Call(EventsTool), Final(ContradictoryAnswer));

        var state = await Runner(model, Registry(tool), new RecordingAuditSink(), Options()).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(FinalResponse.Marker, state.Steps[^1].Description);
        Assert.DoesNotContain(model.Requests, request => request.SystemPrompt.Contains(EvidenceGroundingLedger.OpenMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task G10_AValidCitation_BesideAnEntryTheCheckerGotWrong_StillForcesTheCorrection()
    {
        // A cited contradiction never degrades to "check unavailable" because a sibling entry paraphrased its quote.
        var check = Verdict(("F0.1", "WHEA events were absent"), ("F0.1", Contradiction));

        var (state, model, _) = await RunAsync(Final(ContradictoryAnswer), check, Final(ContradictoryAnswer));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, state.TerminalReason?.Kind);
        Assert.Equal(5, model.Requests.Count);
        AssertNeverPersistedAsAnswer(state, Contradiction);
    }

    [Theory]
    [InlineData("{\"contradictions\":[]}")]
    [InlineData("```json\n{\"contradictions\":[]}\n```")]
    public async Task G9_ACorrectionShapedLikeACheckReply_IsNeverPersistedAsTheAnswer(string correction)
    {
        var (state, _, _) = await RunAsync(Final(ContradictoryAnswer), Flagged, Final(correction));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, state.TerminalReason?.Kind);
        Assert.DoesNotContain(state.Steps, step => FinalResponse.IsFinalStep(step));
    }

    [Fact]
    public async Task G6_PersistReopenResume_GivesTheSameGroundingBlock()
    {
        var store = new InMemoryTaskStore();
        var liveModel = new ScriptedModel(Plan(EventsTool), Call(EventsTool));
        var interrupted = await Runner(liveModel, Registry(new ResultTool(EventsTool, Evidence())), new RecordingAuditSink(), Options(), store: store)
            .RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);
        var liveBlock = BlockOf(liveModel.Requests[2].SystemPrompt);

        var stored = await store.LoadAsync(interrupted.Id);
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(stored))!;
        Assert.Equal(liveBlock, EvidenceGroundingLedger.Delimit(EvidenceGroundingLedger.Build(reloaded.Steps, reloaded.Plans)!));

        var resumeStore = new InMemoryTaskStore();
        resumeStore.Seed(reloaded);
        var resumedModel = new ScriptedModel(Final(CorrectedAnswer), Verdict());
        var resumed = await Runner(resumedModel, Registry(new ResultTool(EventsTool, Evidence())), new RecordingAuditSink(), Options(), store: resumeStore)
            .ResumeAsync(reloaded, new ActorIdentity("api-user", "resumer", "Resumer"));

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Equal(liveBlock, BlockOf(resumedModel.Requests[0].SystemPrompt));
        Assert.Equal("Final response; evidence grounding check cited no contradiction", resumed.Steps[^1].Description);
    }

    private static string BlockOf(string systemPrompt)
    {
        var start = systemPrompt.IndexOf(EvidenceGroundingLedger.OpenMarker, StringComparison.Ordinal);
        var end = systemPrompt.IndexOf(EvidenceGroundingLedger.CloseMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the grounding block is missing");
        return systemPrompt[start..(end + EvidenceGroundingLedger.CloseMarker.Length)];
    }
}
