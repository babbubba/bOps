// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.Json;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>HARDEN-8 deterministic contract, history, retrieval, overflow and budget tests.</summary>
public sealed class HardenEightTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("harden-eight");
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Options_ShipAcceptedDefaults_AndRejectInvalidValues()
    {
        var options = new AgentRunnerOptions();
        Assert.Equal(3, options.VerbatimHistorySteps);
        Assert.Equal(350_000, options.MaxTotalTokens);
        Assert.Equal(TimeSpan.FromHours(1), options.MaxAttemptDuration);

        Assert.Throws<InvalidOperationException>(() => (options with { VerbatimHistorySteps = -1 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (options with { VerbatimHistorySteps = 16 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (options with { MaxTotalTokens = 0 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (options with { MaxAttemptDuration = TimeSpan.Zero }).Validate());
        Assert.Throws<InvalidOperationException>(() => (options with { MaxAttemptDuration = TimeSpan.FromHours(24) + TimeSpan.FromTicks(1) }).Validate());
        (options with { MaxTotalTokens = null, MaxAttemptDuration = null }).Validate();
    }

    [Fact]
    public void BoundedHistory_EnforcesK_M_ArchiveAndPlateau_WithoutCopyingOldOutput()
    {
        var taskId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var steps20 = Enumerable.Range(0, 20).Select(index => Step(index, $"secret-{index}-" + new string('x', 4300),
            index == 0 ? VerificationStatus.Refuted : null)).ToList();
        var steps40 = Enumerable.Range(0, 40).Select(index => Step(index, $"secret-{index}-" + new string('x', 4300),
            index == 0 ? VerificationStatus.Refuted : null)).ToList();

        var at20 = BoundedHistory.Build(taskId, "goal", steps20, verbatimSteps: 3, RegisteredName);
        var at40 = BoundedHistory.Build(taskId, "goal", steps40, verbatimSteps: 3, RegisteredName);

        Assert.Equal((5, 12, 3), (at20.ArchiveSteps, at20.CompactSteps, at20.VerbatimSteps));
        Assert.Equal((25, 12, 3), (at40.ArchiveSteps, at40.CompactSteps, at40.VerbatimSteps));
        Assert.DoesNotContain("secret-0", at20.BoundedBlock, StringComparison.Ordinal);
        Assert.Contains("Refuted=1", BoundedHistory.ArchiveSummary(steps20.Take(5).ToList()), StringComparison.Ordinal);
        Assert.Contains("v=Refuted;", BoundedHistory.CompactRecord(taskId, steps20[0], "test.read"), StringComparison.Ordinal);
        Assert.Contains("v=Confirmed;", BoundedHistory.CompactRecord(taskId,
            steps20[1] with { VerificationStatus = VerificationStatus.Confirmed }, "test.read"), StringComparison.Ordinal);
        Assert.Contains("v=-;", BoundedHistory.CompactRecord(taskId,
            steps20[2] with { VerificationStatus = null }, "test.read"), StringComparison.Ordinal);
        Assert.All(steps20.Skip(5).Take(12), step =>
            Assert.InRange(BoundedHistory.CompactRecord(taskId, step, "test.read").Length, 1, BoundedHistory.CompactRecordMaxCharacters));
        Assert.InRange(BoundedHistory.ArchiveSummary(steps40.Take(25).ToList()).Length, 1, BoundedHistory.ArchiveSummaryMaxCharacters);
        Assert.True(at20.HistoricalCharacters <= BoundedHistory.FixtureHistoricalMaxCharacters);
        Assert.True(at40.HistoricalCharacters <= BoundedHistory.FixtureHistoricalMaxCharacters);
        Assert.Equal(
            JsonSerializer.Serialize(at40.Turns),
            JsonSerializer.Serialize(BoundedHistory.Build(taskId, "goal", steps40, 3, RegisteredName).Turns));

        var zero = BoundedHistory.Build(taskId, "goal", steps20, verbatimSteps: 0, RegisteredName);
        Assert.Equal((8, 12, 0), (zero.ArchiveSteps, zero.CompactSteps, zero.VerbatimSteps));

        var replanZero = BoundedHistory.Build(taskId, "goal", steps20, verbatimSteps: 0, RegisteredName, requiredVerbatimStep: 19);
        Assert.Equal(1, replanZero.VerbatimSteps);
        Assert.Contains("secret-19", string.Join('\n', replanZero.Turns.Select(turn => turn.Content)), StringComparison.Ordinal);
    }

    private static string? RegisteredName(PlanStep step) => step.ToolCall?.ToolName;

    [Fact]
    public void EvidenceIdsAndRanges_AreCurrentTaskOnly_ExactAndUtf16Bounded()
    {
        var taskId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var output = new string('a', 4001) + "SENTINEL";
        var steps = new List<PlanStep>
        {
            Step(7, output, VerificationStatus.Refuted),
            Step(8, "duplicate"),
            Step(8, "duplicate-two"),
            new(9, "synthetic", null, null, "not evidence"),
        };
        var id = BoundedHistory.EvidenceId(taskId, 7);

        var result = EvidenceRead.Read(taskId, steps, new EvidenceReadDirective(id, "result", 4000, 100));
        Assert.Equal(EvidenceReadResultCode.Success, result.Code);
        Assert.Equal("aSENTINEL", result.Fragment);
        Assert.Equal(EvidenceReadResultCode.EndOfEvidence,
            EvidenceRead.Read(taskId, steps, new EvidenceReadDirective(id, "result", output.Length, 1)).Code);
        Assert.Equal(EvidenceReadResultCode.OutOfRange,
            EvidenceRead.Read(taskId, steps, new EvidenceReadDirective(id, "result", output.Length + 1, 1)).Code);
        Assert.Equal(EvidenceReadResultCode.OutOfRange,
            EvidenceRead.Read(taskId, steps, new EvidenceReadDirective(id, "result", -1, 1)).Code);
        Assert.Equal(EvidenceReadResultCode.OutOfRange,
            EvidenceRead.Read(taskId, steps, new EvidenceReadDirective(id, "result", 0, 0)).Code);
        Assert.Equal(EvidenceReadResultCode.OutOfRange,
            EvidenceRead.Read(taskId, steps, new EvidenceReadDirective(id, "result", 0, 4001)).Code);
        Assert.Equal(EvidenceReadResultCode.CrossTaskRejected,
            EvidenceRead.Read(taskId, steps, new EvidenceReadDirective(BoundedHistory.EvidenceId(Guid.NewGuid(), 7), "result", 0, 1)).Code);
        Assert.Equal(EvidenceReadResultCode.MissingStep,
            EvidenceRead.Read(taskId, steps, new EvidenceReadDirective(BoundedHistory.EvidenceId(taskId, 9), "observation", 0, 1)).Code);
        Assert.Equal(EvidenceReadResultCode.InvalidId,
            EvidenceRead.Read(taskId, steps, new EvidenceReadDirective(BoundedHistory.EvidenceId(taskId, 8), "result", 0, 1)).Code);
        Assert.Equal(EvidenceReadResultCode.InvalidId,
            EvidenceRead.Read(taskId, steps, new EvidenceReadDirective($"ev1:{taskId:N}:07", "result", 0, 1)).Code);
        Assert.Contains("Verification: Refuted", EvidenceRead.Read(taskId, steps,
            new EvidenceReadDirective(id, "observation", output.Length, 100)).Fragment, StringComparison.Ordinal);

        var surrogateStep = Step(10, "a😀b");
        var surrogateId = BoundedHistory.EvidenceId(taskId, 10);
        Assert.Equal(EvidenceReadResultCode.OutOfRange,
            EvidenceRead.Read(taskId, [surrogateStep], new EvidenceReadDirective(surrogateId, "result", 2, 1)).Code);
        Assert.Equal(EvidenceReadResultCode.OutOfRange,
            EvidenceRead.Read(taskId, [surrogateStep], new EvidenceReadDirective(surrogateId, "result", 1, 1)).Code);

        var empty = Step(11, string.Empty);
        Assert.Equal(EvidenceReadResultCode.EndOfEvidence, EvidenceRead.Read(taskId, [empty],
            new EvidenceReadDirective(BoundedHistory.EvidenceId(taskId, 11), "result", 0, 1)).Code);
        var unavailable = Step(12, "x") with { Result = ToolCallResult.Success(null) };
        Assert.Equal(EvidenceReadResultCode.UnavailableSource, EvidenceRead.Read(taskId, [unavailable],
            new EvidenceReadDirective(BoundedHistory.EvidenceId(taskId, 12), "result", 0, 1)).Code);
    }

    [Theory]
    [InlineData("```json\n{\"runtime\":\"EvidenceRead/v1\"}\n```")]
    [InlineData("before {\"runtime\":\"EvidenceRead/v1\"} after")]
    [InlineData("{\"runtime\":\"Other/v1\"}")]
    [InlineData("{\"runtime\":\"EvidenceRead/v1\",")]
    public void EvidenceReadRecognition_RejectsMalformedClaims(string text)
    {
        Assert.Equal(RuntimeDirectiveRecognitionKind.Malformed,
            EvidenceRead.Recognize(new ModelResponse(text, [], false, null)).Kind);
    }

    [Fact]
    public void EvidenceReadRecognition_DoesNotFuzzilyClaimAQuotedWordWithoutAnObject()
    {
        Assert.Equal(RuntimeDirectiveRecognitionKind.None,
            EvidenceRead.Recognize(new ModelResponse("The literal word \"runtime\" is not a directive.", [], true, null)).Kind);
    }

    [Fact]
    public async Task EvidenceReadPrompt_SaysTheDirectiveIsNeitherAUrlNorAToolCall()
    {
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(), Final());

        var state = await Runner(model, new RecordingAuditSink()).RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        var prompt = model.Requests[1].SystemPrompt;
        Assert.Contains("not a URL", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not call web.fetch, web.search, or any other tool", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvidenceRead_ResultBeyond4000_IsReturnedAndPersistedEvidenceStaysComplete()
    {
        var taskId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var output = new string('x', 4000) + "OLD-EVIDENCE-SENTINEL" + new string('y', 100);
        var directive = ReadDirective(taskId, 0, "result", 4000, 100);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            Call("test.read"),
            new ModelResponse(directive, [], false, null),
            Final());
        var audit = new RecordingAuditSink();

        var state = await Runner(model, audit, new AgentRunnerOptions { MaxModelPayloadCharacters = 0 },
            tools: [new FakeReadTool(output: output)]).RunAsync("goal", Actor, taskId);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(output, state.Steps[0].Result!.Output);
        Assert.Contains("OLD-EVIDENCE-SENTINEL",
            string.Join('\n', model.Requests[^1].History.Select(turn => turn.Content)), StringComparison.Ordinal);
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal((EvidenceReadResultCode.Success, 100, 1, (int?)null),
            (read.ResultCode, read.ReturnedLength, read.StepIndex, read.PlanRevision));
    }

    [Fact]
    public async Task EvidenceRead_CrossTask_IsRejectedAndAuditedWithoutLeakingEvidence()
    {
        var taskId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var foreignId = BoundedHistory.EvidenceId(Guid.Parse("aaaaaaaa-1234-1234-1234-123456789abc"), 0);
        var directive =
            $"{{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"{foreignId}\",\"source\":\"result\",\"offset\":0,\"length\":100}}";
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Call("test.read"), new ModelResponse(directive, [], false, null), Final());
        var audit = new RecordingAuditSink();

        var state = await Runner(model, audit, tools: [new FakeReadTool(output: "private-evidence")])
            .RunAsync("goal", Actor, taskId);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal(EvidenceReadResultCode.CrossTaskRejected, read.ResultCode);
        Assert.DoesNotContain("private-evidence", JsonSerializer.Serialize(read), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FifthEvidenceRead_AuditsOnce_DoesNoRead_AndFailsWithOneSyntheticStep()
    {
        var taskId = Guid.Parse("87654321-4321-4321-4321-cba987654321");
        var directive = new ModelResponse(ReadDirective(taskId, 0, "result", 0, 1), [], false, null);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(), Call("test.read"), directive, directive, directive, directive, directive);
        var audit = new RecordingAuditSink();

        var state = await Runner(model, audit, tools: [new FakeReadTool(output: "abc")]).RunAsync("goal", Actor, taskId);

        Assert.Equal((AgentTaskStatus.Failed, TaskTerminalKind.RuntimeFailure), (state.Status, state.TerminalReason!.Kind));
        Assert.Equal("EvidenceRead/v1 limit exceeded", state.Steps[^1].Observation);
        Assert.Null(state.Steps[^1].ToolCall);
        Assert.Equal(5, audit.Events.OfType<EvidenceReadAuditEvent>().Count());
        Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>(), item => item.ResultCode == EvidenceReadResultCode.LimitExceeded);
    }

    [Theory]
    [InlineData(false, EvidenceReadResultCode.NotAllowedInPhase)]
    [InlineData(true, EvidenceReadResultCode.Malformed)]
    public async Task InitialPlanning_ReadClaim_UsesTheOneCorrectiveReask(bool malformed, EvidenceReadResultCode expected)
    {
        var taskId = Guid.Parse("99999999-8888-7777-6666-555555555555");
        var claim = malformed
            ? "```json\n{\"runtime\":\"EvidenceRead/v1\"}\n```"
            : ReadDirective(taskId, 0, "result", 0, 1);
        var model = new FakeChatModel(new ModelResponse(claim, [], false, null), PlanningTestSupport.PlanResponse(), Final());
        var audit = new RecordingAuditSink();

        var state = await Runner(model, audit).RunAsync("goal", Actor, taskId);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(3, model.Requests.Count);
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal(expected, read.ResultCode);
        Assert.Null(read.StepIndex);
        Assert.Null(read.PlanRevision);
    }

    [Fact]
    public async Task ReplanEvidenceRead_KeepsTriggerContextAndAuditsPlanRevision()
    {
        var taskId = Guid.Parse("11223344-1234-1234-1234-123456789abc");
        var output = new string('r', 4_000) + "REPLAN-SENTINEL";
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(stepCount: 1),
            Call("test.read", "c0"),
            Call("test.read", "c1"),
            new ModelResponse(ReadDirective(taskId, 0, "result", 4_000, 100), [], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
            Final());
        var audit = new RecordingAuditSink();

        var state = await Runner(model, audit, tools: [new FakeReadTool(output: output)])
            .RunAsync("goal", Actor, taskId);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(2, state.Plans.Count);
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal((EvidenceReadResultCode.Success, 1, (int?)1),
            (read.ResultCode, read.StepIndex, read.PlanRevision));
        Assert.Contains("REPLAN-SENTINEL",
            string.Join('\n', model.Requests[4].History.Select(turn => turn.Content)), StringComparison.Ordinal);
        Assert.Contains(output[..100],
            string.Join('\n', model.Requests[3].History.Select(turn => turn.Content)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextOverflow_RetriesOnceAtK0_AndNextLogicalCallRestoresConfiguredK()
    {
        var model = new ObjectSequenceModel(
            PlanningTestSupport.PlanResponseIndexed("test.read", 2),
            PlanningTestSupport.IndexedCall("test.read", 0, "c0"),
            ContextOverflow(),
            PlanningTestSupport.IndexedCall("test.read", 1, "c1"),
            Final());

        var state = await Runner(model, new RecordingAuditSink(), tools: [new FakeReadTool(output: "history-sentinel", parameters: [PlanningTestSupport.CallIndexParameter])])
            .RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(5, model.Requests.Count);
        var aggressive = string.Join('\n', model.Requests[3].History.Select(turn => turn.Content));
        Assert.Contains("BOPS_HISTORY/v1", aggressive, StringComparison.Ordinal);
        Assert.DoesNotContain("history-sentinel", aggressive, StringComparison.Ordinal);
        var nextLogical = string.Join('\n', model.Requests[4].History.Select(turn => turn.Content));
        Assert.Contains("history-sentinel", nextLogical, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextOverflow_SecondOverflowStopsWithoutAThirdAttempt()
    {
        var model = new ObjectSequenceModel(
            PlanningTestSupport.PlanResponse(stepCount: 1),
            Call("test.read", "c0"),
            ContextOverflow(),
            ContextOverflow());

        var state = await Runner(model, new RecordingAuditSink(), tools: [new FakeReadTool(output: "history")])
            .RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(ModelFailureKind.ContextOverflow, state.TerminalReason!.FailureKind);
        Assert.Equal(4, model.Requests.Count);
        Assert.Equal(2, state.Steps[^1].ModelCalls!.Count);
    }

    [Fact]
    public async Task EvidenceReadLimit_IsNotResetByProviderRetry()
    {
        var taskId = Guid.Parse("13572468-1234-1234-1234-123456789abc");
        var read = new ModelResponse(ReadDirective(taskId, 0, "result", 0, 1), [], false, null);
        var model = new ObjectSequenceModel(
            PlanningTestSupport.PlanResponse(), Call("test.read"), read, read, read, read, Transient(), read);
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions
        {
            ModelCallMaxAttempts = 2,
            ModelRetryBaseDelay = TimeSpan.Zero,
            ModelRetryMaxDelay = TimeSpan.Zero,
        };
        var runner = Runner(model, audit, options, tools: [new FakeReadTool(output: "abc")]);

        var state = await runner.RunAsync("goal", Actor, taskId);

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(5, audit.Events.OfType<EvidenceReadAuditEvent>().Count());
        Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>(), item =>
            item.ResultCode == EvidenceReadResultCode.LimitExceeded);
        Assert.Equal(8, model.Requests.Count);
    }

    [Fact]
    public async Task EvidenceReadLimit_IsNotResetByContextOverflowRecovery()
    {
        var taskId = Guid.Parse("24681357-1234-1234-1234-123456789abc");
        var read = new ModelResponse(ReadDirective(taskId, 0, "result", 0, 1), [], false, null);
        var model = new ObjectSequenceModel(
            PlanningTestSupport.PlanResponse(), Call("test.read"), read, read, read, read, ContextOverflow(), read);
        var audit = new RecordingAuditSink();

        var state = await Runner(model, audit, tools: [new FakeReadTool(output: "abc")]).RunAsync("goal", Actor, taskId);

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(5, audit.Events.OfType<EvidenceReadAuditEvent>().Count());
        Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>(), item =>
            item.ResultCode == EvidenceReadResultCode.LimitExceeded);
        Assert.Equal(8, model.Requests.Count);
    }

    [Fact]
    public async Task InitialPlanContextOverflow_IsNotRetriedWithAnIdenticalRequest()
    {
        var model = new ObjectSequenceModel(ContextOverflow());
        var state = await Runner(model, new RecordingAuditSink()).RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(ModelFailureKind.ContextOverflow, state.TerminalReason!.FailureKind);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task NonFinalTokenCrossing_PersistsCallOnce_AndDoesNotExecuteTool()
    {
        var tool = new CountingReadTool();
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [new ModelToolCall("c", "test.count", ToolArguments.Empty)], false, new ModelUsage(6, 0, null)));
        var state = await Runner(model, new RecordingAuditSink(), new AgentRunnerOptions { MaxTotalTokens = 5 }, tools: [tool])
            .RunAsync("goal", Actor);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.TokenBudget), (state.Status, state.TerminalReason!.Kind));
        Assert.Equal(0, tool.Executions);
        Assert.Equal(TaskResumePolicy.TokenBudgetStepDescription, state.Steps[^1].Description);
        Assert.Single(state.Steps[^1].ModelCalls!);
    }

    [Fact]
    public async Task ExactTokenCap_IsAllowed()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [new ModelToolCall("c", "test.read", ToolArguments.Empty)], false,
                new ModelUsage(5, 0, null)),
            Final());

        var state = await Runner(model, new RecordingAuditSink(), new AgentRunnerOptions { MaxTotalTokens = 5 },
            tools: [new FakeReadTool()]).RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Single(state.Steps, step => step.ToolCall is not null);
    }

    [Fact]
    public async Task PlanningTokenCrossing_CreatesOneSyntheticStep()
    {
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse() with
        {
            Usage = new ModelUsage(6, 0, null),
        });

        var state = await Runner(model, new RecordingAuditSink(), new AgentRunnerOptions { MaxTotalTokens = 5 })
            .RunAsync("goal", Actor);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.TokenBudget),
            (state.Status, state.TerminalReason!.Kind));
        Assert.Single(state.Steps);
        Assert.Equal(TaskResumePolicy.TokenBudgetStepDescription, state.Steps[0].Description);
        Assert.Single(state.Steps[0].ModelCalls!);
        Assert.Empty(state.Plans);
    }

    [Fact]
    public async Task ReplanTokenCrossing_CreatesOneSyntheticStepAndStops()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(stepCount: 1),
            Call("test.read", "c0"),
            Call("test.read", "c1"),
            PlanningTestSupport.PlanResponse(revision: 1) with { Usage = new ModelUsage(6, 0, null) });

        var state = await Runner(model, new RecordingAuditSink(), new AgentRunnerOptions { MaxTotalTokens = 5 },
            tools: [new FakeReadTool()]).RunAsync("goal", Actor);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.TokenBudget),
            (state.Status, state.TerminalReason!.Kind));
        Assert.Equal(3, state.Steps.Count);
        Assert.Equal(TaskResumePolicy.TokenBudgetStepDescription, state.Steps[^1].Description);
        Assert.Single(state.Steps[^1].ModelCalls!);
        Assert.Single(state.Plans);
    }

    [Fact]
    public async Task OriginalFinalAnswerMayCrossTokenCap_AndStillCompletes()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            new ModelResponse("done", [], true, new ModelUsage(6, 0, null)));
        var state = await Runner(model, new RecordingAuditSink(), new AgentRunnerOptions { MaxTotalTokens = 5 })
            .RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(TaskTerminalKind.Completed, state.TerminalReason!.Kind);
        Assert.DoesNotContain(state.Steps, step => step.Description == TaskResumePolicy.TokenBudgetStepDescription);
    }

    [Fact]
    public async Task AttemptDuration_CancelsModelCallWithoutProviderTimeoutRetry()
    {
        var clock = new FakeTimeProvider(Now);
        var model = new DurationModel(clock);
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { MaxAttemptDuration = TimeSpan.FromMinutes(1) };

        var state = await Runner(model, audit, options, clock: clock).RunAsync("goal", Actor);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.AttemptDurationBudget),
            (state.Status, state.TerminalReason!.Kind));
        Assert.Equal(2, model.Requests.Count);
        var failed = Assert.Single(audit.Events.OfType<ModelCallAuditEvent>(), item => item.Outcome == ModelCallOutcome.Failure);
        Assert.Equal(ModelRetryDecision.NotRetryable, failed.RetryDecision);
    }

    [Fact]
    public async Task AttemptDuration_ExcludesHumanApprovalWaiting()
    {
        var clock = new FakeTimeProvider(Now);
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(expectedTool: "test.approval"), Call("test.approval"), Final());
        var approval = new AdvancingApprovalProvider(clock, TimeSpan.FromHours(2));
        var options = new AgentRunnerOptions { MaxAttemptDuration = TimeSpan.FromMinutes(1) };

        var state = await Runner(model, new RecordingAuditSink(), options, clock: clock,
            policy: new StubPolicyEngine(PolicyMode.Approval), approval: approval,
            tools: [new ApprovalTool()]).RunAsync("goal", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.True(approval.WasCalled);
    }

    [Fact]
    public async Task AttemptDuration_CancelsTool_PersistsInterruptedResult_AndDoesNotReplan()
    {
        var clock = new FakeTimeProvider(Now);
        var model = new FakeChatModel(PlanningTestSupport.PlanResponse(expectedTool: "test.slow"), Call("test.slow"));
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions
        {
            MaxAttemptDuration = TimeSpan.FromMinutes(1),
            DefaultToolTimeout = TimeSpan.FromMinutes(10),
        };

        var state = await Runner(model, audit, options, clock: clock, tools: [new DurationTool(clock)])
            .RunAsync("goal", Actor);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.AttemptDurationBudget),
            (state.Status, state.TerminalReason!.Kind));
        Assert.Single(state.Steps);
        Assert.Equal(ToolOutcome.Timeout, state.Steps[0].Result!.Outcome);
        Assert.Contains("attempt duration budget exceeded", state.Steps[0].Result!.ErrorMessage!, StringComparison.Ordinal);
        Assert.Single(state.Plans);
        Assert.Contains(audit.Events, item => item is ToolCallAuditEvent { Outcome: ToolOutcome.Timeout });
    }

    [Fact]
    public async Task AttemptDuration_DuringEvidenceRead_AuditsInterruptionAndDoesNotContinue()
    {
        var clock = new FakeTimeProvider(Now);
        var taskId = Guid.Parse("abcdefab-1234-1234-1234-123456789abc");
        var model = new EvidenceDurationModel(clock, ReadDirective(taskId, 0, "result", 0, 1));
        var audit = new RecordingAuditSink();

        var state = await Runner(model, audit,
            new AgentRunnerOptions { MaxAttemptDuration = TimeSpan.FromMinutes(1) }, clock: clock)
            .RunAsync("goal", Actor, taskId);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.AttemptDurationBudget),
            (state.Status, state.TerminalReason!.Kind));
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal(EvidenceReadResultCode.AttemptBudgetInterrupted, read.ResultCode);
        Assert.Equal(2, model.Calls);
    }

    private static AgentRunner Runner(
        IChatModel model,
        IAuditSink audit,
        AgentRunnerOptions? options = null,
        ITaskStore? store = null,
        TimeProvider? clock = null,
        IPolicyEngine? policy = null,
        IApprovalProvider? approval = null,
        params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return new AgentRunner(model, registry, policy ?? new DefaultTestPolicyEngine(), approval ?? new NeverCalledApprovalProvider(),
            audit, store ?? new InMemoryTaskStore(), clock ?? TimeProvider.System,
            NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());
    }

    private static PlanStep Step(int index, string output, VerificationStatus? verification = null)
    {
        var observation = output + (verification is { } status ? $"\nVerification: {status}." : string.Empty);
        return new PlanStep(index, "test.read", new ModelToolCall($"c-{index}", "test.read", ToolArguments.Empty),
            ToolCallResult.Success(output), observation, 0)
        {
            VerificationStatus = verification,
        };
    }

    private static ModelResponse Call(string tool, string id = "call") =>
        new(null, [new ModelToolCall(id, tool, ToolArguments.Empty)], false, null);

    private static ModelResponse Final() => new("done", [], true, null);

    private static string ReadDirective(Guid taskId, int index, string source, int offset, int length) =>
        $"{{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"{BoundedHistory.EvidenceId(taskId, index)}\",\"source\":\"{source}\",\"offset\":{offset},\"length\":{length}}}";

    private static ModelProtocolException ContextOverflow() =>
        new("context overflow") { FailureKind = ModelFailureKind.ContextOverflow };

    private static ModelProtocolException Transient() =>
        new("transient") { FailureKind = ModelFailureKind.Transient };

    private sealed class ObjectSequenceModel(params object[] items) : IChatModel
    {
        private int index;
        internal List<ModelRequest> Requests { get; } = [];
        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var item = items[index++];
            return item is Exception exception
                ? Task.FromException<ModelResponse>(exception)
                : Task.FromResult((ModelResponse)item);
        }
    }

    private sealed class CountingReadTool : ITool
    {
        public int Executions { get; private set; }
        public ToolManifest Manifest { get; } = new()
        {
            Name = "test.count",
            Description = "Counts executions.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };
        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            Executions++;
            return Task.FromResult(ToolCallResult.Success("ran"));
        }
    }

    private sealed class DurationModel(FakeTimeProvider clock) : IChatModel
    {
        private int calls;
        internal List<ModelRequest> Requests { get; } = [];
        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            calls++;
            if (calls == 1)
            {
                return PlanningTestSupport.PlanResponse();
            }

            clock.Advance(TimeSpan.FromMinutes(2));
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class EvidenceDurationModel(FakeTimeProvider clock, string directive) : IChatModel
    {
        internal int Calls { get; private set; }
        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (Calls == 1)
            {
                return Task.FromResult(PlanningTestSupport.PlanResponse());
            }

            clock.Advance(TimeSpan.FromMinutes(2));
            return Task.FromResult(new ModelResponse(directive, [], false, null));
        }
    }

    private sealed class AdvancingApprovalProvider(FakeTimeProvider clock, TimeSpan wait) : IApprovalProvider
    {
        internal bool WasCalled { get; private set; }

        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest,
            ToolArguments arguments,
            VerificationSpec? verification,
            string reason,
            CancellationToken ct = default)
        {
            WasCalled = true;
            clock.Advance(wait);
            return Task.FromResult(new ApprovalDecision(true, Actor, null));
        }
    }

    private sealed class ApprovalTool : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "test.approval",
            Description = "Requires approval.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success("approved"));
    }

    private sealed class DurationTool(FakeTimeProvider clock) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "test.slow",
            Description = "Runs until cancelled.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
