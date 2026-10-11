// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>PRE-3A: the typed <c>runtime.evidence_read</c> control function (ADR-0046 amendment, ADR-0042).</summary>
public sealed class EvidenceControlPlaneTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private const string Big = "diag.big";
    private const string Next = "diag.next";
    private const string Wrong = "diag.wrong";
    private const string Marker = "SEGMENT-BEYOND-4000";

    // Clearly above the 4000-character normal observation limit; the marker sits past it.
    private static readonly string BigOutput = new string('x', 4_000) + Marker + new string('y', 2_400);

    private sealed class FixedTool(string name, string output) : ITool
    {
        public int ExecutionCount { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Fixed read tool.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult(ToolCallResult.Success(output));
        }
    }

    private static (AgentRunner Runner, RecordingAuditSink Audit, FixedTool Next, FixedTool Wrong) Build(IChatModel model)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        var next = new FixedTool(Next, "next-done");
        var wrong = new FixedTool(Wrong, "wrong");
        registry.Register(new PackageId("test.package"), new FixedTool(Big, BigOutput));
        registry.Register(new PackageId("test.package"), next);
        registry.Register(new PackageId("test.package"), wrong);
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit, new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions { MaxSteps = 20, MaxLifetimeSteps = 40 });
        return (runner, audit, next, wrong);
    }

    private static ModelResponse TwoStepPlan() => new(new JsonObject
    {
        ["rationale"] = "test",
        ["steps"] = new JsonArray(
            new JsonObject { ["description"] = "big", ["expectedTool"] = Big },
            new JsonObject { ["description"] = "next", ["expectedTool"] = Next }),
    }.ToJsonString(), [], false, null);

    private static ModelToolCall NativeCall(string name, JsonObject arguments) =>
        new(Guid.NewGuid().ToString("N"), name, ToolArguments.FromJson(arguments));

    private static ModelResponse Calls(params ModelToolCall[] calls) => new(null, calls, false, null);

    private static ModelToolCall Control(int step = 0, int offset = 4_000, int length = 200, string source = "result") =>
        NativeCall(EvidenceRead.ControlFunctionName, new JsonObject
        {
            ["step"] = step,
            ["source"] = source,
            ["offset"] = offset,
            ["length"] = length,
        });

    private static ModelResponse Tool(string name) => Calls(NativeCall(name, new JsonObject()));

    private static ModelResponse Final() => new("done", [], true, null);

    private static ModelResponse EmptyPlan() =>
        new(new JsonObject { ["rationale"] = "nothing left", ["steps"] = new JsonArray() }.ToJsonString(), [], false, null);

    private static string[] Offered(ModelRequest request) => [.. request.AvailableTools.Select(tool => tool.Name)];

    private static bool HistoryMentions(ModelRequest request, string text) =>
        request.History.Any(turn => turn.Content?.Contains(text, StringComparison.Ordinal) == true);

    // Requests: 0 plan, 1 step 0, 2 step 1 (control offered), 3 after the control read, ...
    [Fact]
    public async Task T1_T2_T5_TypedRead_ReturnsSegment_KeepsStep_ThenExpectedToolCompletes()
    {
        var model = new FakeChatModel(TwoStepPlan(), Tool(Big), Calls(Control()), Tool(Next), Final());
        var (runner, audit, next, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1, next.ExecutionCount);
        Assert.Equal([Big, Next], result.Steps.Where(step => step.ToolCall is not null).Select(step => step.ToolCall!.ToolName));
        // The read is not a step: only the two tool steps and the final answer exist, so the planned cursor never moved.
        Assert.Equal([0, 1, 2], result.Steps.Select(step => step.Index));

        // T5: before oversized evidence only the step tool is offered; afterwards the control function joins it, nothing else.
        Assert.Equal([Big], Offered(model.Requests[1]));
        Assert.Equal([Next, EvidenceRead.ControlFunctionName], Offered(model.Requests[2]));
        Assert.Equal([Next, EvidenceRead.ControlFunctionName], Offered(model.Requests[3]));

        // The segment past the 4000-character observation limit reaches the model through the control reply only.
        Assert.False(HistoryMentions(model.Requests[2], Marker));
        Assert.True(HistoryMentions(model.Requests[3], Marker));
        Assert.Equal(4_000, new AgentRunnerOptions().MaxObservationCharacters);

        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal((EvidenceReadResultCode.Success, 200, 1), (read.ResultCode, read.ReturnedLength, read.StepIndex));
    }

    [Fact]
    public async Task T3_ReadWithExpectedToolInSameTurn_ServesReadOnly_AndAsksAgain()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), Calls(Control(), NativeCall(Next, new JsonObject())), Tool(Next), Final());
        var (runner, _, next, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(1, next.ExecutionCount);
        Assert.Equal(2, result.Steps.Count(step => step.ToolCall is not null));
        Assert.Contains(model.Requests[3].History, turn => turn.Role == ChatRole.Tool
            && turn.Content!.StartsWith("Not executed", StringComparison.Ordinal));
        Assert.True(HistoryMentions(model.Requests[3], Marker));
    }

    [Fact]
    public async Task T4_ReadWithWrongToolInSameTurn_DoesNotMakeTheWrongToolValid()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), Calls(Control(), NativeCall(Wrong, new JsonObject())), Tool(Wrong), Final(), Final());
        var (runner, _, next, wrong) = Build(model);

        _ = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(0, wrong.ExecutionCount);
        Assert.Equal(0, next.ExecutionCount);
    }

    [Fact]
    public async Task T6_TaskIsolation_ModelCannotNameAnotherTask()
    {
        var foreignTyped = NativeCall(EvidenceRead.ControlFunctionName, new JsonObject
        {
            ["step"] = 0,
            ["evidenceId"] = EvidenceRead.EvidenceId(Guid.NewGuid(), 0),
            ["source"] = "result",
            ["offset"] = 4_000,
            ["length"] = 200,
        });
        var foreignLegacy = new ModelResponse(
            "{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"" + EvidenceRead.EvidenceId(Guid.NewGuid(), 0)
            + "\",\"source\":\"result\",\"offset\":4000,\"length\":200}", [], false, null);
        var model = new FakeChatModel(TwoStepPlan(), Tool(Big), Calls(foreignTyped), foreignLegacy, Tool(Next), Final());
        var (runner, audit, _, _) = Build(model);

        _ = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(
            [EvidenceReadResultCode.Malformed, EvidenceReadResultCode.CrossTaskRejected],
            audit.Events.OfType<EvidenceReadAuditEvent>().Select(read => read.ResultCode));
        Assert.False(HistoryMentions(model.Requests[4], Marker));
    }

    [Fact]
    public async Task T7_BoundedRead_RejectsOversizedLength_AndNeverWidensTheObservation()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), Calls(Control(length: 4_001)), Calls(Control(offset: 4_000, length: 4_000)),
            Tool(Next), Final());
        var (runner, audit, _, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(
            [EvidenceReadResultCode.OutOfRange, EvidenceReadResultCode.Success],
            audit.Events.OfType<EvidenceReadAuditEvent>().Select(read => read.ResultCode));
        Assert.Equal(BigOutput, result.Steps[0].Result!.Output);
        Assert.True(result.Steps[0].Observation!.Length < BigOutput.Length);
    }

    [Fact]
    public async Task T8_LegacyTextDirective_StillWorks_ThroughTheSameHandler()
    {
        var taskId = Guid.NewGuid();
        var legacy = new ModelResponse(
            "{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"" + EvidenceRead.EvidenceId(taskId, 0)
            + "\",\"source\":\"result\",\"offset\":4000,\"length\":200}", [], false, null);
        var model = new FakeChatModel(TwoStepPlan(), Tool(Big), legacy, Tool(Next), Final());
        var (runner, audit, _, _) = Build(model);

        _ = await runner.RunAsync("diagnose", Actor, taskId);

        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal((EvidenceReadResultCode.Success, 200), (read.ResultCode, read.ReturnedLength));
        Assert.True(HistoryMentions(model.Requests[3], Marker));
    }

    [Fact]
    public async Task T9_UnknownRuntimeControlFunction_IsAControlError_NotATool()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), Calls(NativeCall("runtime.something_else", new JsonObject())), Tool(Next), Final());
        var (runner, audit, next, wrong) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(1, next.ExecutionCount);
        Assert.Equal(0, wrong.ExecutionCount);
        Assert.DoesNotContain(result.Steps, step => step.ToolCall?.ToolName == "runtime.something_else");
        Assert.Equal(EvidenceReadResultCode.Malformed, Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>()).ResultCode);
    }

    [Fact]
    public void ControlFunction_IsNotARegistrableTool()
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());

        Assert.Throws<ToolRegistrationException>(() =>
            registry.Register(new PackageId("test.package"), new FixedTool(EvidenceRead.ControlFunctionName, "x")));
        Assert.Empty(registry.GetAvailableManifests());
    }

    // ---- ADR-0052 AF-04: soft exhaustion, separated counters, cursor metadata ----

    private static ModelResponse ReadAt(int slot) => Calls(Control(offset: 4_000 + (slot * 200)));

    private static ModelResponse MalformedControl() =>
        Calls(NativeCall(EvidenceRead.ControlFunctionName, new JsonObject { ["step"] = "not-an-integer" }));

    private static bool ReplyMentions(ModelRequest request, string text) =>
        request.History.Any(turn => turn.Role == ChatRole.Tool && turn.Content?.Contains(text, StringComparison.Ordinal) == true);

    private static ModelResponse ThreeStepPlan() => new(new JsonObject
    {
        ["rationale"] = "test",
        ["steps"] = new JsonArray(
            new JsonObject { ["description"] = "big", ["expectedTool"] = Big },
            new JsonObject { ["description"] = "next", ["expectedTool"] = Next },
            new JsonObject { ["description"] = "wrong", ["expectedTool"] = Wrong }),
    }.ToJsonString(), [], false, null);

    // Requests: 0 plan, 1 step 0, 2..5 the four served reads' replies, 6 after the fourth read (exhausted).
    [Fact]
    public async Task AF04_FourReadsServed_FifthRefusedWithoutRawRead_ThenExpectedToolRunsAndTaskCompletes()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), ReadAt(0), ReadAt(1), ReadAt(2), ReadAt(3), ReadAt(4), Tool(Next), Final());
        var (runner, audit, next, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1, next.ExecutionCount);
        Assert.Equal(
            [.. Enumerable.Repeat(EvidenceReadResultCode.Success, 4), EvidenceReadResultCode.LimitExceeded],
            audit.Events.OfType<EvidenceReadAuditEvent>().Select(read => read.ResultCode));
        Assert.Equal(0, audit.Events.OfType<EvidenceReadAuditEvent>().Last().ReturnedLength);
        Assert.DoesNotContain(result.Steps, step => step.Observation?.Contains("limit exceeded", StringComparison.OrdinalIgnoreCase) == true);

        // The surface never grows: control offered while read slots remain, then only the step's tool; never the full catalog.
        for (var index = 2; index <= 5; index++)
        {
            Assert.Equal([Next, EvidenceRead.ControlFunctionName], Offered(model.Requests[index]));
        }

        Assert.Equal([Next], Offered(model.Requests[6]));
        Assert.Equal([Next], Offered(model.Requests[7]));
        Assert.Contains(EvidenceRead.ExhaustedFeedback, model.Requests[6].SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"runtime\":\"EvidenceRead/v1\"", model.Requests[6].SystemPrompt, StringComparison.Ordinal);
        Assert.True(ReplyMentions(model.Requests[6], "readsRemaining=0"));
        Assert.True(ReplyMentions(model.Requests[7], "rejected: LimitExceeded"));
    }

    [Fact]
    public async Task R2_NormalStep_MaxFourServedReads_FifthNotServed_ExpectedToolStillOffered_TaskProceeds()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), ReadAt(0), ReadAt(1), ReadAt(2), ReadAt(3), ReadAt(4), Tool(Next), Final());
        var (runner, audit, next, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1, next.ExecutionCount);
        var codes = audit.Events.OfType<EvidenceReadAuditEvent>().Select(read => read.ResultCode).ToList();
        Assert.Equal(4, codes.Count(code => code == EvidenceReadResultCode.Success));
        Assert.Equal(EvidenceReadResultCode.LimitExceeded, codes[^1]);
        Assert.Equal([Next], Offered(model.Requests[6]));
        Assert.DoesNotContain("\"runtime\":\"EvidenceRead/v1\"", model.Requests[6].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(EvidenceRead.ExhaustedFeedback, model.Requests[6].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AF04_AfterExhaustion_FinalAnswerCompletesTheTask()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), ReadAt(0), ReadAt(1), ReadAt(2), ReadAt(3), ReadAt(4), Final(),
            // AF-05H: the second planned step is still unresolved, so that final is re-evaluated by a replan, which drops it.
            EmptyPlan(), Final());
        var (runner, _, _, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal("done", result.Steps[^1].Observation);
        Assert.True(PrematureFinal.IsStep(result.Steps[^2]));
    }

    [Fact]
    public async Task AF04_ReadReply_CarriesCursorMetadata_OnlyWhereKnown()
    {
        var lastOffset = BigOutput.Length - 100;
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), Calls(Control(offset: 4_000, length: 200)), Calls(Control(offset: lastOffset, length: 500)),
            Calls(Control(offset: BigOutput.Length, length: 10)), Tool(Next), Final());
        var (runner, _, _, _) = Build(model);

        _ = await runner.RunAsync("diagnose", Actor);

        Assert.True(ReplyMentions(model.Requests[3],
            $"returned=200; sourceLength={BigOutput.Length}; nextOffset=4200; endOfSource=false; readsRemaining=3."));
        Assert.True(ReplyMentions(model.Requests[4],
            $"returned=100; sourceLength={BigOutput.Length}; nextOffset={BigOutput.Length}; endOfSource=true; readsRemaining=2."));
        Assert.True(ReplyMentions(model.Requests[5], $"EndOfEvidence; returned=0; sourceLength={BigOutput.Length}; endOfSource=true"));
    }

    [Fact]
    public async Task AF04_MalformedControlRequests_DoNotConsumeReadSlots_ButStayBounded()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), MalformedControl(), MalformedControl(), MalformedControl(),
            ReadAt(0), ReadAt(1), ReadAt(2), ReadAt(3), Tool(Next), Final());
        var (runner, audit, next, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1, next.ExecutionCount);
        Assert.Equal(
            [.. Enumerable.Repeat(EvidenceReadResultCode.Malformed, 3), .. Enumerable.Repeat(EvidenceReadResultCode.Success, 4)],
            audit.Events.OfType<EvidenceReadAuditEvent>().Select(read => read.ResultCode));
    }

    [Fact]
    public async Task AF04_InvalidEvidenceRequests_AreNotSuccessfulReads()
    {
        var foreign = new ModelResponse(
            "{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"" + EvidenceRead.EvidenceId(Guid.NewGuid(), 0)
            + "\",\"source\":\"result\",\"offset\":4000,\"length\":200}", [], false, null);
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big),
            Calls(Control(step: 99)), Calls(Control(source: "bogus")), Calls(Control(offset: 999_999)), foreign,
            ReadAt(0), ReadAt(1), ReadAt(2), ReadAt(3), Tool(Next), Final());
        var (runner, audit, next, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1, next.ExecutionCount);
        Assert.Equal(
            [
                EvidenceReadResultCode.MissingStep, EvidenceReadResultCode.UnavailableSource,
                EvidenceReadResultCode.OutOfRange, EvidenceReadResultCode.CrossTaskRejected,
                .. Enumerable.Repeat(EvidenceReadResultCode.Success, 4),
            ],
            audit.Events.OfType<EvidenceReadAuditEvent>().Select(read => read.ResultCode));
    }

    [Fact]
    public async Task AF04_RepeatedMalformedControl_IsABoundedProtocolFailure_NotAReadExhaustion()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), MalformedControl(), MalformedControl(), MalformedControl(), MalformedControl(), MalformedControl());
        var (runner, audit, next, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal((AgentTaskStatus.Failed, TaskTerminalKind.RuntimeFailure), (result.Status, result.TerminalReason!.Kind));
        Assert.Equal(0, next.ExecutionCount);
        Assert.Equal(5, audit.Events.OfType<EvidenceReadAuditEvent>().Count());
        Assert.DoesNotContain(result.Steps, step => step.Observation == "EvidenceRead/v1 limit exceeded");
        Assert.Equal(7, model.Requests.Count);
    }

    [Fact]
    public async Task AF04_ModelThatKeepsReadingAfterExhaustion_IsBounded()
    {
        var model = new FakeChatModel(
            TwoStepPlan(), Tool(Big), ReadAt(0), ReadAt(1), ReadAt(2), ReadAt(3),
            ReadAt(4), ReadAt(4), ReadAt(4), ReadAt(4), ReadAt(4));
        var (runner, audit, next, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal((AgentTaskStatus.Failed, TaskTerminalKind.RuntimeFailure), (result.Status, result.TerminalReason!.Kind));
        Assert.Equal(0, next.ExecutionCount);
        Assert.Equal(4, audit.Events.OfType<EvidenceReadAuditEvent>().Count(read => read.ResultCode == EvidenceReadResultCode.Success));
        Assert.All(audit.Events.OfType<EvidenceReadAuditEvent>().Where(read => read.ResultCode != EvidenceReadResultCode.Success),
            read => Assert.Equal(EvidenceReadResultCode.LimitExceeded, read.ResultCode));
    }

    [Fact]
    public async Task AF04_ReadBudget_IsPerLogicalCall_SoARebuiltCallStartsDeterministicallyFull()
    {
        // Step 1's logical call spends all four slots; step 2's logical call (also what resume rebuilds) offers the control again.
        var model = new FakeChatModel(
            ThreeStepPlan(), Tool(Big), ReadAt(0), ReadAt(1), ReadAt(2), ReadAt(3), Tool(Next), Tool(Wrong), Final());
        var (runner, _, next, wrong) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal((1, 1), (next.ExecutionCount, wrong.ExecutionCount));
        Assert.Equal([Next], Offered(model.Requests[6]));
        Assert.Equal([Wrong, EvidenceRead.ControlFunctionName], Offered(model.Requests[7]));
    }

    [Fact]
    public void AF04_Budget_SeparatesServedReadsFromRejections()
    {
        var budget = new EvidenceReadBudget();
        for (var i = 0; i < EvidenceRead.MaxControlRejections; i++)
        {
            budget.RecordRejection();
        }

        Assert.False(budget.Exhausted);
        Assert.False(budget.ProtocolBoundExceeded);
        Assert.Equal(EvidenceRead.MaxAttempts, budget.ReadsRemaining);

        budget.RecordRejection();
        Assert.True(budget.ProtocolBoundExceeded);
        Assert.False(budget.Exhausted);

        for (var i = 0; i < EvidenceRead.MaxAttempts; i++)
        {
            budget.RecordServedRead();
        }

        Assert.True(budget.Exhausted);
        Assert.Equal(0, budget.ReadsRemaining);
        Assert.Equal(4, EvidenceRead.MaxAttempts);
    }
}
