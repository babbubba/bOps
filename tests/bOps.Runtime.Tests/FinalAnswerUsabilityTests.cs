// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using static bOps.Runtime.Tests.EvidenceScenario;

namespace bOps.Runtime.Tests;

/// <summary>
/// LIVE-F1-FIX-01 (rule S3): a final reply made only of whitespace, punctuation, symbols or Markdown markers is not an answer,
/// whatever its finish reason. Live task T05 completed with the single character <c>&gt;</c> after its output-limit recovery had
/// spent the step's one retry; such a reply now takes the remaining retry of the shared budget, if any, and otherwise fails the
/// attempt as <see cref="TaskTerminalKind.EmptyResponse"/>. The guard acts before the premature-final check and before the
/// evidence disclosure, and never touches a reply that calls a tool.
/// </summary>
public sealed class FinalAnswerUsabilityTests
{
    private const string ReadTool = "test.read";
    private const string PartialTool = "test.partial";
    private const string EvidenceTool = "test.evidence";

    private static ModelResponse Reply(string? text, ModelUsage? usage = null, string finish = "stop") =>
        new(text, [], true, usage) { Details = Details(finish) };

    private static ModelCallDetails Details(string finish) =>
        new("vendor/actual-model", finish, """{"model":"fake-model","messages":[]}""", """{"model":"vendor/actual-model","choices":[]}""");

    private static async Task<(TaskState State, FakeChatModel Model, RecordingAuditSink Audit, InMemoryTaskStore Store)> RunAsync(
        ModelResponse[] responses, AgentRunnerOptions? options = null, params ITool[] tools)
    {
        var model = new FakeChatModel(responses);
        var audit = new RecordingAuditSink();
        var store = new InMemoryTaskStore();
        var state = await Runner(model, Registry(tools), audit, options, store: store).RunAsync("check", Actor);
        return (state, model, audit, store);
    }

    private static bool HistoryMentions(ModelRequest request, string text) =>
        request.History.Any(turn => turn.Content?.Contains(text, StringComparison.Ordinal) == true);

    private static void AssertFailedAsUnusable(TaskState state)
    {
        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.EmptyResponse, state.TerminalReason?.Kind);
        Assert.DoesNotContain(state.Steps, FinalResponse.IsFinalStep);
        Assert.Equal(TaskResumePolicy.ModelFailureStepDescription, state.Steps[^1].Description);
    }

    // --- The predicate -----------------------------------------------------------------------------------------------------

    public static TheoryData<string?> ContentFree => new()
    {
        null,
        "",
        " ",
        "\n\t \r\n",
        ">",
        "> ",
        ".",
        "...",
        "…",
        "?!",
        ";:,",
        "-",
        "—",
        "()[]{}",
        "**",
        "__",
        "#",
        "## ",
        "---",
        "* * *",
        "- [ ]",
        "|---|---|",
        "`",
        "``",
        "```",
        "```\n```",
        "```json\n```",
        "~~~\n~~~",
        "> \n> \n>",
        "→ • ✓",
        "👍",
    };

    [Theory]
    [MemberData(nameof(ContentFree))]
    public void AReplyOfOnlyWhitespacePunctuationSymbolsOrMarkdownMarkers_HasNoContent(string? text) =>
        Assert.False(FinalAnswerContent.HasContent(text));

    [Theory]
    [InlineData("OK")]
    [InlineData("No.")]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("-1")]
    [InlineData("5%")]
    [InlineData("Sì")]
    [InlineData("はい")]
    [InlineData("Ⅳ")]
    [InlineData("½")]
    [InlineData("> Yes")]
    [InlineData("**No**")]
    [InlineData("# Result\n---")]
    [InlineData("```OK```")]
    [InlineData("```\n0\n```")]
    [InlineData("```json\n{\"ok\":true}\n```")]
    [InlineData("✓ OK")]
    public void AShortRealAnswer_HasContent(string text) => Assert.True(FinalAnswerContent.HasContent(text));

    // --- Terminal failure when no retry is left ----------------------------------------------------------------------------

    [Theory]
    [InlineData(">")]
    [InlineData("...")]
    [InlineData("?!;")]
    [InlineData("**")]
    [InlineData("---")]
    [InlineData("```\n```")]
    [InlineData("# ")]
    public async Task AContentFreeFinal_WithNoRetryLeft_FailsAsEmptyResponse_NeverCompleted(string text)
    {
        var (state, model, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply(text, new ModelUsage(100, 1, null))],
            new AgentRunnerOptions { EmptyFinalResponseRetries = 0 });

        AssertFailedAsUnusable(state);
        Assert.Equal(2, model.Requests.Count); // the plan and the one step call
        var failure = state.Steps[^1];
        Assert.Contains("no answer content", failure.Observation, StringComparison.Ordinal);
        Assert.Contains("1 attempt(s)", failure.Observation, StringComparison.Ordinal);
        Assert.Contains("finish reason 'stop'", failure.Observation, StringComparison.Ordinal);
        Assert.Single(failure.ModelCalls!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public async Task AnEmptyFinal_KeepsItsEmptyResponseDescription(string? text)
    {
        var (state, _, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply(text)], new AgentRunnerOptions { EmptyFinalResponseRetries = 0 });

        AssertFailedAsUnusable(state);
        Assert.Contains("empty final response (no text and no tool call)", state.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.DoesNotContain("no answer content", state.Steps[^1].Observation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("No.")]
    [InlineData("0")]
    public async Task AShortValidFinal_CompletesUnchanged_WithNoRetry(string text)
    {
        var (state, model, _, _) = await RunAsync([PlanningTestSupport.PlanResponse(stepCount: 0), Reply(text)]);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(TaskTerminalKind.Completed, state.TerminalReason?.Kind);
        Assert.Equal(2, model.Requests.Count);
        var final = Assert.Single(state.Steps);
        Assert.Equal(FinalResponse.Marker, final.Description);
        Assert.Equal(text, final.Observation);
        Assert.Single(final.ModelCalls!);
    }

    // --- Retry available / exhausted -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AContentFreeFinal_WithARetryLeft_IsAskedAgainOnce_WithoutKeepingTheUnusableTurn()
    {
        var (state, model, _, _) = await RunAsync([PlanningTestSupport.PlanResponse(stepCount: 0), Reply(">"), Reply("OK")]);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(3, model.Requests.Count);
        var final = Assert.Single(state.Steps);
        Assert.Equal("OK", final.Observation);
        Assert.Equal(
            [LogicalModelCallRole.Answer, LogicalModelCallRole.EmptyAnswerRetry],
            FinalResponse.LogicalCalls(final).Select(call => call.Role));

        var retry = model.Requests[^1];
        Assert.Equal(ChatRole.User, retry.History[^1].Role);
        Assert.Contains("no answer content", retry.History[^1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(retry.History, turn => turn.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task AContentFreeFinal_ThatStaysContentFree_FailsAfterTheConfiguredRetries_AndIsPersistedAndAuditedAsFailed()
    {
        var (state, model, audit, store) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply(">"), Reply("...")]);

        AssertFailedAsUnusable(state);
        Assert.Equal(3, model.Requests.Count); // plan, answer, the single default retry: never a third answer call
        Assert.Contains("2 attempt(s)", state.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.Equal(2, state.Steps[^1].ModelCalls!.Count);

        var persisted = await store.LoadAsync(state.Id);
        Assert.NotNull(persisted);
        Assert.Equal(AgentTaskStatus.Failed, persisted.Status);
        Assert.Equal(TaskTerminalKind.EmptyResponse, persisted.TerminalReason?.Kind);
        Assert.DoesNotContain(persisted.Steps, FinalResponse.IsFinalStep);

        var terminal = Assert.Single(audit.Events.OfType<TaskLifecycleAuditEvent>(),
            evt => evt.Stage == TaskLifecycleStage.ExecutionTerminal);
        Assert.Equal((AgentTaskStatus.Failed, TaskTerminalKind.EmptyResponse), (terminal.Status, terminal.TerminalKind));
        Assert.Equal(3, audit.Events.OfType<ModelCallAuditEvent>().Count());
    }

    [Fact]
    public async Task EmptyAndContentFreeReplies_ShareOneRetryBudget()
    {
        var (state, model, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply(null), Reply(">"), Reply("OK")],
            new AgentRunnerOptions { EmptyFinalResponseRetries = 2 });

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(4, model.Requests.Count);
        Assert.Equal("OK", state.Steps[^1].Observation);
        Assert.Contains("empty", model.Requests[2].History[^1].Content, StringComparison.Ordinal);
        Assert.Contains("no answer content", model.Requests[3].History[^1].Content, StringComparison.Ordinal);
    }

    // --- The T05 sequence --------------------------------------------------------------------------------------------------

    private static ModelResponse EvidenceReadCall(int offset) => new(null,
        [new ModelToolCall(Guid.NewGuid().ToString("N"), EvidenceRead.ControlFunctionName, ToolArguments.FromJson(new JsonObject
        {
            ["step"] = 0,
            ["source"] = "result",
            ["offset"] = offset,
            ["length"] = 100,
        }))], false, null);

    private static ModelResponse OutputLimitedWithoutContent() =>
        new(null, [], false, new ModelUsage(900, 4096, null)) { Details = Details("length") };

    private static ModelResponse[] T05(params ModelResponse[] tail) =>
    [
        Plan(EvidenceTool),
        Call(EvidenceTool),
        EvidenceReadCall(0),
        OutputLimitedWithoutContent(),
        EvidenceReadCall(100),
        Reply(">", new ModelUsage(900, 1, null)),
        .. tail,
    ];

    private static ResultTool T05Tool() => new(EvidenceTool, Complete(new string('e', 600)));

    [Fact]
    public async Task T05_EvidenceRead_LengthWithoutContent_Recovery_EvidenceRead_Angle_IsNotCompleted()
    {
        var (state, model, audit, store) = await RunAsync(T05(), tools: T05Tool());

        AssertFailedAsUnusable(state);
        // plan, tool step, read, length, (output-limit recovery) read, '>': the recovery spent the one retry, none is added.
        Assert.Equal(6, model.Requests.Count);
        Assert.True(HistoryMentions(model.Requests[4], "reached the output limit"));
        Assert.DoesNotContain(model.Requests, request => HistoryMentions(request, "no answer content"));

        var failure = state.Steps[^1];
        Assert.Contains("no answer content", failure.Observation, StringComparison.Ordinal);
        Assert.Contains("finish reason 'stop'", failure.Observation, StringComparison.Ordinal);
        Assert.DoesNotContain('>', failure.Observation!); // the content-free text is not echoed; the recorded reply body holds it
        Assert.Equal(4, failure.ModelCalls!.Count);
        Assert.Equal("length", failure.ModelCalls[1].FinishReason);
        Assert.Equal(ModelFailureKind.MalformedResponse, failure.ModelCalls[1].FailureKind);
        Assert.Equal("stop", failure.ModelCalls[^1].FinishReason);
        Assert.Equal(2, audit.Events.OfType<EvidenceReadAuditEvent>().Count());

        var persisted = await store.LoadAsync(state.Id);
        Assert.Equal(AgentTaskStatus.Failed, persisted!.Status);
        Assert.Equal(TaskTerminalKind.EmptyResponse, persisted.TerminalReason?.Kind);
    }

    [Fact]
    public async Task T05_WithASecondConfiguredRetry_TheAngleTakesIt_AndARealAnswerCompletes()
    {
        var (state, model, _, _) = await RunAsync(
            T05(Reply("No WHEA events were found.")), new AgentRunnerOptions { EmptyFinalResponseRetries = 2 }, T05Tool());

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(7, model.Requests.Count);
        Assert.True(HistoryMentions(model.Requests[6], "no answer content"));
        Assert.Equal("No WHEA events were found.", state.Steps[^1].Observation);
    }

    // --- Evidence disclosure ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnderListedLimitations_AContentFreeFinal_IsNotMadeValidByTheAppendedSection()
    {
        var (state, model, _, _) = await RunAsync(
            [Plan(PartialTool), Call(PartialTool), Reply(">"), Reply("**")], tools: new ResultTool(PartialTool, Partial()));

        AssertFailedAsUnusable(state);
        Assert.Equal(4, model.Requests.Count);
        Assert.DoesNotContain(state.Steps, step => step.Description == FinalResponse.AppendedMarker);
        Assert.DoesNotContain(state.Steps, step => EvidenceDisclosure.HasHeading(step.Observation));
    }

    [Fact]
    public async Task UnderListedLimitations_ARealAnswerAfterTheRetry_GetsTheSectionAppended()
    {
        var (state, _, _, _) = await RunAsync(
            [Plan(PartialTool), Call(PartialTool), Reply(">"), Reply("OK")], tools: new ResultTool(PartialTool, Partial()));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        var final = state.Steps[^1];
        Assert.Equal(FinalResponse.AppendedMarker, final.Description);
        Assert.StartsWith("OK\n\n## Evidence limitations\n", final.Observation, StringComparison.Ordinal);
    }

    // --- Premature final and LIVE-05 ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AContentFreeFinal_BeforeAnUnresolvedStep_IsRetriedNotReplanned_AndThePlannedToolStillRuns()
    {
        var tool = new ResultTool(ReadTool, Complete());
        var (state, model, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(), Reply(">"), Call(ReadTool), Reply("Done.")], tools: tool);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(4, model.Requests.Count);
        Assert.DoesNotContain(state.Steps, PrematureFinal.IsStep);
        Assert.Single(state.Plans);
        Assert.Equal("Done.", state.Steps[^1].Observation);
    }

    [Fact]
    public async Task AContentFreeFinal_BeforeAnUnresolvedStep_WithNoRetryLeft_FailsWithoutAReplan()
    {
        var tool = new ResultTool(ReadTool, Complete());
        var (state, model, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(), Reply(">"), Reply("...")], tools: tool);

        AssertFailedAsUnusable(state);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal(3, model.Requests.Count); // no replan call was made
        Assert.DoesNotContain(state.Steps, PrematureFinal.IsStep);
        Assert.Single(state.Plans);
    }

    // --- Runtime-authored context echoed as the answer (MAF A/B campaign, 03-B02-A-r1) --------------------------------------------

    // The reply the model gave in the campaign run: the history compaction placeholder and a status bullet, finish reason 'stop'.
    private const string EchoedCompaction =
        "[Compacted tool interactions; persisted result data omitted]\n- runtime_evidence_read (step 2): result recorded";

    public static TheoryData<string> RuntimeContextEchoes => new()
    {
        RuntimeContextMarkers.CompactionHeader,
        EchoedCompaction,
        $"{RuntimeContextMarkers.CompactionHeader}\n- system.events: result recorded\n- system.crashes: not executed",
        $"{RuntimeContextMarkers.CompactionHeader}\r\n- runtime_evidence_read: result recorded\r\n",
        $"```\n{EchoedCompaction}\n```",
        $"> {RuntimeContextMarkers.CompactionHeader}",
        "[compacted tool interactions; persisted result data omitted]",
        "- runtime_evidence_read (step 4): result recorded",
        "* storage.health: not executed",
    };

    [Theory]
    [MemberData(nameof(RuntimeContextEchoes))]
    public void AReplyThatOnlyEchoesRuntimeContextMarkers_HasNoContent(string text)
    {
        Assert.False(FinalAnswerContent.HasContent(text));
        Assert.True(FinalAnswerContent.IsRuntimeContextEcho(text));
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("No.")]
    [InlineData("0")]
    [InlineData("Nessuna evidenza di instabilità nei dati disponibili.")]
    [InlineData("Non è possibile stabilirlo: le fonti sono parziali.")]
    [InlineData("[Compacted tool interactions; persisted result data omitted]\nNo crash was observed in the readable sources.")]
    [InlineData("- system.events: result recorded, but it matched no record")]
    [InlineData("- system.events: result recorded in 3 steps")]
    [InlineData("Result recorded.")]
    [InlineData("The step was not executed.")]
    public void ARealAnswer_IsNeverTakenForARuntimeContextEcho(string text)
    {
        Assert.True(FinalAnswerContent.HasContent(text));
        Assert.False(FinalAnswerContent.IsRuntimeContextEcho(text));
    }

    [Fact]
    public async Task AFinalThatOnlyEchoesTheCompactionPlaceholder_WithNoRetryLeft_FailsAsEmptyResponse_NeverCompleted()
    {
        var (state, model, audit, store) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply(EchoedCompaction, new ModelUsage(100, 28, null))],
            new AgentRunnerOptions { EmptyFinalResponseRetries = 0 });

        AssertFailedAsUnusable(state);
        Assert.Equal(2, model.Requests.Count); // the plan and the one step call: no grounding check was made on the placeholder
        var failure = state.Steps[^1];
        Assert.Contains("runtime-internal context markers", failure.Observation, StringComparison.Ordinal);
        Assert.DoesNotContain("Compacted", failure.Observation!, StringComparison.Ordinal);
        Assert.DoesNotContain(state.Steps, FinalResponse.IsFinalStep);
        Assert.Single(failure.ModelCalls!);

        var persisted = await store.LoadAsync(state.Id);
        Assert.Equal(AgentTaskStatus.Failed, persisted!.Status);
        Assert.Equal(TaskTerminalKind.EmptyResponse, persisted.TerminalReason?.Kind);
        var terminal = Assert.Single(audit.Events.OfType<TaskLifecycleAuditEvent>(),
            evt => evt.Stage == TaskLifecycleStage.ExecutionTerminal);
        Assert.Equal((AgentTaskStatus.Failed, TaskTerminalKind.EmptyResponse), (terminal.Status, terminal.TerminalKind));
        Assert.Equal(2, audit.Events.OfType<ModelCallAuditEvent>().Count());
    }

    [Fact]
    public async Task AFinalThatOnlyEchoesTheCompactionPlaceholder_IsAskedAgainOnce_AndARealAnswerCompletes()
    {
        var (state, model, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply(EchoedCompaction), Reply("No instability was observed in the readable sources.")]);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(3, model.Requests.Count);
        var final = Assert.Single(state.Steps);
        Assert.Equal("No instability was observed in the readable sources.", final.Observation);
        Assert.Equal(
            [LogicalModelCallRole.Answer, LogicalModelCallRole.EmptyAnswerRetry],
            FinalResponse.LogicalCalls(final).Select(call => call.Role));

        var retry = model.Requests[^1];
        Assert.Contains("internal context markers", retry.History[^1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(retry.History, turn => turn.Role == ChatRole.Assistant); // the echo is not kept in the conversation
    }

    [Fact]
    public async Task AnEchoedPlaceholder_ThatStaysAPlaceholder_FailsAfterTheConfiguredRetries()
    {
        var (state, model, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply(EchoedCompaction), Reply(EchoedCompaction)]);

        AssertFailedAsUnusable(state);
        Assert.Equal(3, model.Requests.Count); // plan, answer, the single default retry: never a third answer call
        Assert.Contains("2 attempt(s)", state.Steps[^1].Observation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnderListedLimitations_AnEchoedPlaceholder_IsNotMadeAnAnswerByTheAppendedSection()
    {
        var (state, model, _, _) = await RunAsync(
            [Plan(PartialTool), Call(PartialTool), Reply(EchoedCompaction), Reply(EchoedCompaction)], tools: new ResultTool(PartialTool, Partial()));

        AssertFailedAsUnusable(state);
        Assert.Equal(4, model.Requests.Count);
        Assert.DoesNotContain(state.Steps, step => step.Description == FinalResponse.AppendedMarker);
        Assert.DoesNotContain(state.Steps, step => EvidenceDisclosure.HasHeading(step.Observation));
    }

    [Fact]
    public async Task AnEchoedPlaceholder_ThatCrossesTheTokenBudget_StopsTheAttempt_InsteadOfBeingCompleted()
    {
        var (state, _, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply(EchoedCompaction, new ModelUsage(400, 700, null))],
            new AgentRunnerOptions { MaxTotalTokens = 1000 });

        Assert.Equal(AgentTaskStatus.BudgetExceeded, state.Status);
        Assert.DoesNotContain(state.Steps, FinalResponse.IsFinalStep);
    }

    // --- Tool calls are never judged by the final-answer guard ---------------------------------------------------------------

    [Theory]
    [InlineData(">")]
    [InlineData("...")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AToolCallWithContentFreeText_IsExecutedAsATool(string? text)
    {
        var tool = new ResultTool(ReadTool, Complete());
        var call = new ModelResponse(text, [new ModelToolCall("call-1", ReadTool, ToolArguments.Empty)], false, null);
        var (state, model, _, _) = await RunAsync([PlanningTestSupport.PlanResponse(), call, Reply("Done.")], tools: tool);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(3, model.Requests.Count);
        Assert.Single(state.Steps[0].ModelCalls!);
        Assert.DoesNotContain(model.Requests, request => HistoryMentions(request, "no answer content"));
    }

    // --- Token budget ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AContentFreeFinal_ThatCrossesTheTokenBudget_StopsTheAttempt_InsteadOfBeingRetriedOrCompleted()
    {
        var (state, model, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply(">", new ModelUsage(400, 700, null))],
            new AgentRunnerOptions { MaxTotalTokens = 1000 });

        Assert.Equal(AgentTaskStatus.BudgetExceeded, state.Status);
        Assert.Equal(TaskTerminalKind.TokenBudget, state.TerminalReason?.Kind);
        Assert.Equal(2, model.Requests.Count);
        Assert.DoesNotContain(state.Steps, FinalResponse.IsFinalStep);
    }

    [Fact]
    public async Task ARealFinal_ThatCrossesTheTokenBudget_IsStillKept()
    {
        var (state, _, _, _) = await RunAsync(
            [PlanningTestSupport.PlanResponse(stepCount: 0), Reply("OK", new ModelUsage(400, 700, null))],
            new AgentRunnerOptions { MaxTotalTokens = 1000 });

        Assert.Contains(state.Steps, FinalResponse.IsFinalStep);
        Assert.Equal("OK", state.Steps[^1].Observation);
    }
}
