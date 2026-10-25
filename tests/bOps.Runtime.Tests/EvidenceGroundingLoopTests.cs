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

    // T02-GROUNDING-01: sanitized typed values from T02-r3, not a Windows capture.
    private static ToolCallResult T02Evidence(ToolResultCompleteness completeness = ToolResultCompleteness.Partial) =>
        ToolCallResult.Success("{}") with
        {
            Completeness = completeness,
            Facts =
            [
                new("system.crashes.observed-count", "all", ToolParameterType.Integer, JsonValue.Create(43)),
                new("system.crashes.group-count", "groups", ToolParameterType.Integer, JsonValue.Create(10)),
                new("system.crashes.kind-count", "kernel-live-dump", ToolParameterType.Integer, JsonValue.Create(27)),
                new("system.crashes.kind-count", "kernel-bugcheck", ToolParameterType.Integer, JsonValue.Create(15)),
                new("system.crashes.kind-count", "wer", ToolParameterType.Integer, JsonValue.Create(1)),
                new("system.crashes.kernel-code-count", "kind=kernel-live-dump;code=0x193", ToolParameterType.Integer, JsonValue.Create(26)),
                new("system.crashes.kernel-code-count", "kind=kernel-bugcheck;code=0x1e", ToolParameterType.Integer, JsonValue.Create(7)),
            ],
        };

    private const string T02ObservedQuote =
        "Il codice `0x193` (NTFS_FILE_SYSTEM) è predominante, apparendo in 26 dump live e 7 bugcheck.";

    private const string T02SupportedAnswer =
        "Osservati 43 elementi: 27 live dump, 15 bugcheck e 1 WER. Sono 42 segnalazioni kernel, non 42 incidenti dimostrati. " +
        "26 live dump hanno codice 0x193; 7 bugcheck hanno codice 0x1e. La causa resta incerta.\n\n" +
        "Evidence limitations\nLa raccolta è parziale; i conteggi osservati sono minimi.";

    [Fact]
    public void T02_LedgerReproduction_ShowsCategorySplitButOmitsDumpReferenceCount()
    {
        static EvidenceFact Integer(string type, string key, int value) =>
            new(type, key, ToolParameterType.Integer, JsonValue.Create(value));

        var crashes = T02Evidence() with
        {
            Facts =
            [
                .. T02Evidence().Facts,
                Integer("system.crashes.kernel-code-count", "kind=kernel-bugcheck;code=0x7f", 2),
                Integer("system.crashes.kernel-code-count", "kind=kernel-bugcheck;code=0xa", 2),
                Integer("system.crashes.dump-reference-count", "all", 42),
                // Sanitized: the path's text is immaterial to the selection, and is omitted anyway.
                new("system.crashes.kernel-dump-path", "newest", ToolParameterType.Path, JsonValue.Create(@"C:\sanitized\dump.dmp")),
            ],
        };
        var stability = Partial() with
        {
            Facts =
            [
                Integer("system.stability.category-count", "hardwareError", 8),
                Integer("system.stability.category-count", "displayFault", 26),
                Integer("system.stability.window-days", "window", 1),
            ],
        };
        var events = Complete() with
        {
            Facts =
            [
                Integer("system.events.matched-count", "source=.NET Runtime;eventId=1026", 2),
                Integer("system.events.matched-count", "source=.NET Runtime;eventId=1025", 1),
            ],
        };
        var grounding = EvidenceGroundingLedger.Build(
        [
            ToolStep(0, "system.info", Failed(ToolFailureKind.Environment)),
            ToolStep(1, "system.crashes", crashes),
            ToolStep(2, "system.stability", stability),
            ToolStep(3, "system.events", events),
        ], [])!;

        Assert.Equal(["F1.1", "F1.2", "F1.3", "F1.4", "F1.5", "F1.6", "F1.7",
            "F2.1", "F2.2", "F2.3", "F3.1", "F3.2"], grounding.Facts.Select(fact => fact.Id));
        Assert.Equal(4, grounding.OmittedCount);
        Assert.DoesNotContain("dump-reference-count", grounding.Text, StringComparison.Ordinal);
        Assert.Contains("(from partial evidence)", grounding.Text, StringComparison.Ordinal);
        Assert.Contains("Unknown or limited (never zero or absence; see the evidence limitations): steps 0, 1, 2.",
            grounding.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sono stati osservati 43 dump kernel.", "F0.1")]
    [InlineData("Sono stati osservati 28 kernel-live-dump.", "F0.3")]
    [InlineData(T02ObservedQuote, "F0.7")]
    public async Task T02_CitedCountOrCategoryContradiction_UsesExistingBoundedCorrection(string answer, string factId)
    {
        var (state, model, tool) = await RunAsync(T02Evidence(), Options(),
            Final(answer), Verdict((factId, answer)), Final(T02SupportedAnswer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(T02SupportedAnswer, state.Steps[^1].Observation);
        Assert.Equal(EvidenceGroundingOutcome.Corrected, FinalResponse.GroundingOf(state.Steps[^1]));
        Assert.Equal(5, model.Requests.Count); // plan, tool, final, check, correction; no extra check or tool.
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Theory]
    [InlineData(T02SupportedAnswer)]
    [InlineData("Osservati 43 elementi, non 43 dump kernel; 10 gruppi non significano 10 crash effettivi.")]
    [InlineData("Potrebbero esserci altri crash nelle fonti non leggibili; il totale reale è sconosciuto.")]
    [InlineData("Se ci fossero 43 bugcheck, occorrerebbe distinguerli dai report; è un esempio ipotetico.")]
    [InlineData("L'ipotesi storage è da verificare; la correlazione non dimostra una causa.")]
    public async Task T02_NegativeControls_NoCitationDoesNotRejectLawfulOrUncertainText(string answer)
    {
        // Scripted checker: this proves orchestration, not the ability of Qwen to classify these sentences.
        var (state, model, _) = await RunAsync(T02Evidence(), Options(), Final(answer), Verdict());

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.StartsWith(answer, state.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.Equal(EvidenceGroundingOutcome.NoContradictionCited, FinalResponse.GroundingOf(state.Steps[^1]));
        Assert.Equal(4, model.Requests.Count);
    }

    [Fact]
    public async Task T02_KnownDefect_PartialCorrectionLeavesWrongCodeAndKernelTotalCompleted()
    {
        const string residual = "Osservati 43 dump kernel. Il codice `0x193` (NTFS_FILE_SYSTEM) compare in 26 live dump; " +
            "7 bugcheck hanno codice 0x1e.\n\nEvidence limitations\nLa raccolta è parziale.";
        var (state, model, _) = await RunAsync(T02Evidence(), Options(),
            Final("Osservati 43 dump kernel. " + T02ObservedQuote),
            Verdict(("F0.7", T02ObservedQuote)), Final(residual));

        // Characterization of the observed defect, explicitly NOT a diagnostic acceptance criterion.
        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(residual, state.Steps[^1].Observation);
        Assert.Equal(EvidenceGroundingOutcome.Corrected, FinalResponse.GroundingOf(state.Steps[^1]));
        Assert.Equal(5, model.Requests.Count);
    }

    [Fact]
    public async Task T02_KnownDefect_DisclosureDoesNotValidateCompletenessClaims()
    {
        const string answer = "La raccolta è completa: esattamente 43 crash totali e nessun altro evento possibile.";
        var (state, _, _) = await RunAsync(T02Evidence(), Options(), Final(answer), Verdict());

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.StartsWith(answer, state.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.True(EvidenceDisclosure.HasHeading(state.Steps[^1].Observation));
        Assert.Equal(EvidenceGroundingOutcome.NoContradictionCited, FinalResponse.GroundingOf(state.Steps[^1]));
    }

    [Fact]
    public async Task T02_KnownDefect_ShortenedObservationDoesNotValidateCompletenessClaims()
    {
        const string answer = "Tutti i dati sono visibili integralmente; il totale reale è esattamente 43.";
        var result = T02Evidence(ToolResultCompleteness.Complete) with { Output = new string('x', 9000) };
        var (state, model, _) = await RunAsync(result, Options(), Final(answer), Verdict());

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.StartsWith(answer, state.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.True(EvidenceDisclosure.HasHeading(state.Steps[^1].Observation));
        Assert.Contains("observation shortened from 9000 characters", model.Requests[3].SystemPrompt, StringComparison.Ordinal);
    }

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

    // LIVE-F1-FIX-01: the correction becomes the persisted answer, so a content-free one is no correction.
    [Theory]
    [InlineData(">")]
    [InlineData("**")]
    [InlineData("...")]
    public async Task G9b_ContentFreeCorrection_FailsAsEmptyResponse_NeverCompleted(string correction)
    {
        var (state, _, _) = await RunAsync(Final(ContradictoryAnswer), Flagged, Final(correction));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.EmptyResponse, state.TerminalReason?.Kind);
        Assert.DoesNotContain(state.Steps, FinalResponse.IsFinalStep);
        Assert.Contains("The correction had no answer content.", state.Steps[^1].Observation, StringComparison.Ordinal);
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
    public async Task G12_TheDeterministicDisclosureComesFirst_ThenTheCheckSeesTheDisclosedFinal_AndTheCorrectionKeepsTheHeading()
    {
        // AF-05H: no model call between the answer and the check; the check reads the original answer plus the runtime section.
        const string correctedDisclosed = CorrectedAnswer + "\n\nEvidence limitations\n- test.events returned a partial result.";

        var (state, model, _) = await RunAsync(Evidence(ToolResultCompleteness.Partial), Options(),
            Final(ContradictoryAnswer), Flagged, Final(correctedDisclosed));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(correctedDisclosed, state.Steps[^1].Observation);
        Assert.Equal("Final response; evidence limitations appended by runtime; evidence grounding corrected", state.Steps[^1].Description);
        Assert.Equal(EvidenceGroundingLedger.CheckInstruction, LastUserTurn(model.Requests[3]));
        var checkedCandidate = model.Requests[3].History[^2].Content!;
        Assert.StartsWith(ContradictoryAnswer + "\n\n## Evidence limitations\n" + EvidenceDisclosure.AppendedLead, checkedCandidate, StringComparison.Ordinal);
        Assert.True(EvidenceDisclosure.HasHeading(checkedCandidate));
        Assert.Contains("(from partial evidence)", model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(
            [LogicalModelCallRole.Answer, LogicalModelCallRole.EvidenceGroundingCheck, LogicalModelCallRole.EvidenceGroundingCorrection],
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

    [Fact]
    public async Task G2T5_PersistReopenResume_GivesTheSameStepDiverseSelection()
    {
        // AF-05G: three dense steps, none referenced. The selection is one fact per step per pass, live and after reopen.
        ToolCallResult Dense(string prefix) => ToolCallResult.Success("{}") with
        {
            Completeness = ToolResultCompleteness.Complete,
            Facts = [.. Enumerable.Range(0, 8).Select(i => new EvidenceFact(FactType, $"{prefix}{i}", ToolParameterType.Integer, JsonValue.Create(i)))],
        };
        ToolRegistry DenseRegistry() => Registry(
            new ResultTool("test.d1", Dense("a")), new ResultTool("test.d2", Dense("b")), new ResultTool("test.d3", Dense("c")));

        var store = new InMemoryTaskStore();
        var liveModel = new ScriptedModel(Plan("test.d1", "test.d2", "test.d3"), Call("test.d1", "c1"), Call("test.d2", "c2"), Call("test.d3", "c3"));
        var interrupted = await Runner(liveModel, DenseRegistry(), new RecordingAuditSink(), Options(), store: store).RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);
        var liveBlock = BlockOf(liveModel.Requests[4].SystemPrompt);
        foreach (var step in new[] { 0, 1, 2 })
        {
            Assert.Contains($"- F{step}.1 step {step} ", liveBlock, StringComparison.Ordinal);
        }

        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(await store.LoadAsync(interrupted.Id)))!;
        Assert.Equal(liveBlock, EvidenceGroundingLedger.Delimit(EvidenceGroundingLedger.Build(reloaded.Steps, reloaded.Plans)!));

        var resumeStore = new InMemoryTaskStore();
        resumeStore.Seed(reloaded);
        var resumedModel = new ScriptedModel(Final(CorrectedAnswer), Verdict());
        var resumed = await Runner(resumedModel, DenseRegistry(), new RecordingAuditSink(), Options(), store: resumeStore)
            .ResumeAsync(reloaded, new ActorIdentity("api-user", "resumer", "Resumer"));

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Equal(liveBlock, BlockOf(resumedModel.Requests[0].SystemPrompt));
    }

    // --- MAF A/B campaign 09-B02-B-r2: counts and dates invented for an empty query ------------------------------------------------
    // The tool returned an empty window in 2026 and, by contract, no typed fact (absence is never a fact), so the grounding check has
    // nothing to contradict. The answer attributes 196 events and 2025 dates to that very step.

    private const string EmptyQueryOutput =
        """{"schemaVersion":2,"mode":"raw","status":"complete","complete":true,"window":{"fromUtc":"2026-10-21T23:34:57Z","toUtc":"2026-10-24T23:34:57Z"},"observedEvents":0,"returnedEvents":0,"events":[]}""";

    private const string InventedAnswer =
        "Lo step 0 (test.events) ha trovato 196 eventi WHEA tra il 28/10/2025 e il 02/11/2025: l'hardware è la causa.";

    private const string HonestAnswer =
        "Lo step 0 (test.events) ha restituito 0 eventi nella finestra richiesta; zero corrispondenze non dimostra che il sistema non " +
        "abbia mai registrato eventi WHEA. La causa resta da stabilire.";

    private static ToolCallResult EmptyQuery() =>
        ToolCallResult.Success(EmptyQueryOutput) with { Completeness = ToolResultCompleteness.Complete };

    [Fact]
    public async Task EmptyQuery_InventedCountAndDates_AreCorrectedOnce_WithoutACheckCall_AndNeverPersistedAsTheAnswer()
    {
        var (state, model, tool) = await RunAsync(EmptyQuery(), Options(),
            Final(InventedAnswer, new ModelUsage(100, 10, null)), Final(HonestAnswer, new ModelUsage(120, 20, null)));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(4, model.Requests.Count); // plan, tool, final, correction: no typed fact, so no check call
        var final = state.Steps[^1];
        Assert.Equal(HonestAnswer, final.Observation);
        Assert.Equal("Final response; evidence claims corrected", final.Description);
        Assert.Equal(EvidenceGroundingOutcome.ClaimsCorrected, FinalResponse.GroundingOf(final));
        AssertNeverPersistedAsAnswer(state, "196 eventi");
        Assert.Equal(
            [LogicalModelCallRole.Answer, LogicalModelCallRole.EvidenceGroundingCorrection],
            FinalResponse.LogicalCalls(final).Select(call => call.Role));

        var correction = LastUserTurn(model.Requests[3]);
        Assert.StartsWith("EvidenceProvenanceCorrection/v1.", correction, StringComparison.Ordinal);
        Assert.Contains("196", correction, StringComparison.Ordinal);
        Assert.Contains("28/10/2025", correction, StringComparison.Ordinal);
        Assert.Contains("step 0 test.events", correction, StringComparison.Ordinal);
        Assert.DoesNotContain("l'hardware è la causa", correction, StringComparison.Ordinal); // the model's words are never echoed back
    }

    [Fact]
    public async Task EmptyQuery_CorrectionAccounting_CountsBothAnswerCalls()
    {
        var (state, _, _) = await RunAsync(EmptyQuery(), Options(),
            Final(InventedAnswer, new ModelUsage(100, 10, null)), Final(HonestAnswer, new ModelUsage(120, 20, null)));

        Assert.Equal(250, state.Accounting!.TokensUsed);
        Assert.Equal(2, state.Steps[^1].ModelCalls!.Count);
    }

    [Fact]
    public async Task EmptyQuery_WhenTheCorrectionKeepsTheAttribution_TheTaskNeverCompletes()
    {
        var (state, model, _) = await RunAsync(EmptyQuery(), Options(), Final(InventedAnswer), Final(InventedAnswer));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, state.TerminalReason?.Kind);
        Assert.Contains("attributed counts or dates to tool results that do not contain them", state.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.Equal(4, model.Requests.Count); // exactly one correction: never a second
        AssertNeverPersistedAsAnswer(state, "196");
        Assert.DoesNotContain(state.Steps, FinalResponse.IsFinalStep);
    }

    [Fact]
    public async Task EmptyQuery_WhenTheCorrectionKeepsTheCountWithoutAnAttribution_ItIsStillRefused()
    {
        var (state, _, _) = await RunAsync(EmptyQuery(), Options(),
            Final(InventedAnswer), Final("Nel complesso sono stati rilevati 196 eventi WHEA, quindi la causa è l'hardware."));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, state.TerminalReason?.Kind);
        AssertNeverPersistedAsAnswer(state, "196");
    }

    [Fact]
    public async Task EmptyQuery_AnHonestAnswer_IsNotQuestioned_AndCostsNoExtraCall()
    {
        var (state, model, _) = await RunAsync(EmptyQuery(), Options(), Final(HonestAnswer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(3, model.Requests.Count); // plan, tool, final
        Assert.Equal(HonestAnswer, state.Steps[^1].Observation);
        Assert.Equal(FinalResponse.Marker, state.Steps[^1].Description);
        Assert.Equal(EvidenceGroundingOutcome.NotChecked, FinalResponse.GroundingOf(state.Steps[^1]));
    }

    [Theory]
    [InlineData("Le evidenze raccolte non bastano per indicare una causa: l'ipotesi più probabile è un problema di alimentazione, da confermare con misure elettriche e l'analisi dei dump.")]
    [InlineData("Nessun evento è stato osservato dallo step 0 nella finestra richiesta; questo non esclude eventi in altre fonti o periodi.")]
    [InlineData("Lo step 0 non ha dati: la finestra copre gli ultimi 3 giorni e 0 eventi corrispondono al filtro.")]
    public async Task EmptyQuery_UncertainOrNegativeAnswers_AreAcceptedAsGiven(string answer)
    {
        var (state, model, _) = await RunAsync(EmptyQuery(), Options(), Final(answer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(answer, state.Steps[^1].Observation);
    }

    [Fact]
    public async Task WithTypedFacts_TheProvenanceFindingJoinsTheCheck_InTheSameSingleCorrection()
    {
        // A positive typed fact exists (3 WHEA events), so the check runs; the model's attribution of 196 to the same step is unsupported.
        const string answer = "Lo step 0 ha trovato 196 eventi WHEA, il filtro ne mostra 3.";
        var (state, model, _) = await RunAsync(Final(answer), Verdict(), Final(CorrectedAnswer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(5, model.Requests.Count); // plan, tool, final, check, one correction
        Assert.Equal(EvidenceGroundingOutcome.Corrected, FinalResponse.GroundingOf(state.Steps[^1]));
        Assert.Equal(
            [LogicalModelCallRole.Answer, LogicalModelCallRole.EvidenceGroundingCheck, LogicalModelCallRole.EvidenceGroundingCorrection],
            FinalResponse.LogicalCalls(state.Steps[^1]).Select(call => call.Role));
        Assert.StartsWith("EvidenceProvenanceCorrection/v1.", LastUserTurn(model.Requests[4]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTypedFacts_ASupportedAttribution_NeedsNoCorrection()
    {
        var (state, model, _) = await RunAsync(Final("Lo step 0 ha osservato 3 eventi WHEA corretti; è un'ipotesi, non una causa."), Verdict());

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(4, model.Requests.Count); // plan, tool, final, check
        Assert.Equal(EvidenceGroundingOutcome.NoContradictionCited, FinalResponse.GroundingOf(state.Steps[^1]));
    }

    [Fact]
    public async Task EmptyQuery_WithNoBudgetLeftForTheCorrection_FailsInsteadOfPersistingTheInventedAnswer()
    {
        var (state, model, _) = await RunAsync(EmptyQuery(), new AgentRunnerOptions { ModelCallMaxAttempts = 1, MaxTotalTokens = 1000 },
            Final(InventedAnswer, new ModelUsage(900, 200, null)));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, state.TerminalReason?.Kind);
        Assert.Contains("No budget remained for the correction", state.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.Equal(3, model.Requests.Count); // no paid correction past the budget
        AssertNeverPersistedAsAnswer(state, "196");
    }

    private static string BlockOf(string systemPrompt)
    {
        var start = systemPrompt.IndexOf(EvidenceGroundingLedger.OpenMarker, StringComparison.Ordinal);
        var end = systemPrompt.IndexOf(EvidenceGroundingLedger.CloseMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the grounding block is missing");
        return systemPrompt[start..(end + EvidenceGroundingLedger.CloseMarker.Length)];
    }
}
