// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0047: an argument-validation failure on the exact tool the current planned step expects keeps that planned step, and
/// its single native tool, for exactly one corrective turn; a second validation failure replans through the existing
/// transactional path. The cursor and the spent correction are derived from persisted steps, so a resumed attempt neither
/// regains the correction nor consumes the step.
/// </summary>
public sealed class PlannedStepValidationRetryTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly ActorIdentity Resumer = new("api-user", "resumer", "Resumer");

    private const string ToolA = "test.tool-a";
    private const string ToolB = "test.tool-b";
    private const string ToolC = "test.tool-c";
    private const string ValidationObservation = "ERROR (validation):";
    private const string CorrectionInstruction = "failed argument validation and did not run";

    private static RecordingReadTool Tool(string name) =>
        new(name, [new ToolParameter("mode", ToolParameterType.String, "Collection mode.")]);

    private static AgentRunner Runner(IChatModel model, ITaskStore store, params ITool[] tools) =>
        Runner(model, store, new RecordingAuditSink(), tools);

    private static AgentRunner Runner(IChatModel model, ITaskStore store, RecordingAuditSink audit, params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit, store, TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());
    }

    private static ModelResponse Malformed() => new("not a plan", [], false, null);

    /// <summary>The shape of the real incident: an argument that belongs to another mode of the same tool.</summary>
    private static ModelResponse Invalid(string tool) => Call(tool, new JsonObject { ["sinceDays"] = 2, ["mode"] = "raw" });

    private static ModelResponse Valid(string tool) => Call(tool, new JsonObject { ["mode"] = "raw" });

    private static ModelResponse Call(string tool, JsonObject arguments) =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), tool, ToolArguments.FromJson(arguments))], false, null);

    private static ModelResponse Final() => new("done", [], true, null);

    private static IEnumerable<string> Offered(ModelRequest request) => request.AvailableTools.Select(tool => tool.Name);

    private static bool SeesValidationFailure(ModelRequest request) =>
        request.History.Any(turn => turn.Role == ChatRole.Tool
            && turn.Content!.Contains(ValidationObservation, StringComparison.Ordinal));

    /// <summary>A persisted, resumable copy of what an interrupted attempt stored, through a JSON round trip.</summary>
    private static async Task<(InMemoryTaskStore Store, TaskState Task)> PersistedAsync(InMemoryTaskStore store, Guid taskId)
    {
        var stored = await store.LoadAsync(taskId);
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(stored))!;
        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        return (fresh, reloaded);
    }

    // ---- A: the first validation failure keeps the planned tool ----

    [Fact]
    public async Task AFirstValidationFailure_KeepsTheCurrentPlannedTool_ForOneCorrectiveTurn()
    {
        var (toolA, toolB) = (Tool(ToolA), Tool(ToolB));
        var model = new FakeChatModel(PlanningTestSupport.PlanResponseFor(ToolA, ToolB), Invalid(ToolA), Final());

        var result = await Runner(model, new InMemoryTaskStore(), toolA, toolB).RunAsync("diagnose", Actor);

        var failed = result.Steps[0];
        Assert.Equal(ToolA, failed.ToolCall!.ToolName);
        Assert.Equal((ToolOutcome.Failure, ToolFailureKind.Validation), (failed.Result!.Outcome, failed.Result.FailureKind));
        Assert.Equal(0, toolA.ExecutionCount);

        var correction = model.Requests[2];
        Assert.Equal([ToolA], Offered(correction));
        Assert.DoesNotContain(ToolB, Offered(correction));
        Assert.True(SeesValidationFailure(correction));
        Assert.Contains(CorrectionInstruction, correction.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(CorrectionInstruction, model.Requests[1].SystemPrompt, StringComparison.Ordinal);

        Assert.Single(result.Plans);
        Assert.Equal(0, result.Accounting!.LifetimeReplans);
    }

    // ---- B: a corrected call consumes the planned step ----

    [Fact]
    public async Task ACorrectedCall_ExecutesNormally_AndConsumesThePlannedStep()
    {
        var (toolA, toolB) = (Tool(ToolA), Tool(ToolB));
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponseFor(ToolA, ToolB), Invalid(ToolA), Valid(ToolA), Valid(ToolB), Final());

        var result = await Runner(model, new InMemoryTaskStore(), toolA, toolB).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal((1, 1), (toolA.ExecutionCount, toolB.ExecutionCount));
        Assert.Equal(ToolOutcome.Success, result.Steps[1].Result!.Outcome);
        Assert.Equal([ToolB], Offered(model.Requests[3]));
        Assert.DoesNotContain(CorrectionInstruction, model.Requests[3].SystemPrompt, StringComparison.Ordinal);
        Assert.Single(result.Plans);
        Assert.Equal(0, result.Accounting!.LifetimeReplans);
    }

    // ---- C: a second validation failure replans ----

    [Fact]
    public async Task ASecondValidationFailure_Replans_WithNoNativeTool_InsteadOfAThirdAttempt()
    {
        var (toolA, toolB) = (Tool(ToolA), Tool(ToolB));
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponseFor(ToolA, ToolB),
            Invalid(ToolA),
            Invalid(ToolA),
            PlanningTestSupport.PlanResponseFor(ToolB),
            Valid(ToolB),
            Final());

        var result = await Runner(model, new InMemoryTaskStore(), toolA, toolB).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, toolA.ExecutionCount);
        Assert.Equal(2, result.Steps.Count(step => step.ToolCall?.ToolName == ToolA));
        Assert.All(result.Steps.Take(2), step => Assert.Equal(ToolFailureKind.Validation, step.Result!.FailureKind));

        // The one correction offered the same single tool; the next request is the replan, never a third attempt.
        Assert.Equal([ToolA], Offered(model.Requests[2]));
        Assert.Empty(model.Requests[3].AvailableTools);
        Assert.Equal([ToolB], Offered(model.Requests[4]));

        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
        Assert.Equal([0, 0, 1, 1], result.Steps.Select(step => step.PlanRevision));
    }

    // ---- D: resume reconstructs the cursor and the spent correction ----

    [Fact]
    public async Task AResumeAfterTheFirstValidationFailure_StaysOnThePlannedTool_AndRemembersTheSpentCorrection()
    {
        var store = new InMemoryTaskStore();
        var interrupted = await Runner(
                new FailingAfterModel(PlanningTestSupport.PlanResponseFor(ToolA, ToolB), Invalid(ToolA)),
                store, Tool(ToolA), Tool(ToolB))
            .RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);

        var (resumeStore, persisted) = await PersistedAsync(store, interrupted.Id);
        var (toolA, toolB) = (Tool(ToolA), Tool(ToolB));
        var model = new FakeChatModel(Invalid(ToolA), PlanningTestSupport.PlanResponseFor(ToolB), Valid(ToolB), Final());

        var result = await Runner(model, resumeStore, toolA, toolB).ResumeAsync(persisted, Resumer);

        var resumedCorrection = model.Requests[0];
        Assert.Equal([ToolA], Offered(resumedCorrection));
        Assert.True(SeesValidationFailure(resumedCorrection));
        Assert.Contains(CorrectionInstruction, resumedCorrection.SystemPrompt, StringComparison.Ordinal);

        // The resumed attempt's validation failure is the second one for that planned step: it replans.
        Assert.Empty(model.Requests[1].AvailableTools);
        Assert.Equal([ToolB], Offered(model.Requests[2]));
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, toolA.ExecutionCount);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
    }

    [Fact]
    public async Task AResumeAfterASuccessfulCorrection_ContinuesOnTheFollowingPlannedStep()
    {
        var store = new InMemoryTaskStore();
        var interrupted = await Runner(
                new FailingAfterModel(PlanningTestSupport.PlanResponseFor(ToolA, ToolB, ToolC), Invalid(ToolA), Valid(ToolA)),
                store, Tool(ToolA), Tool(ToolB), Tool(ToolC))
            .RunAsync("diagnose", Actor);

        var (resumeStore, persisted) = await PersistedAsync(store, interrupted.Id);
        var model = new FakeChatModel(Valid(ToolB), Final());

        var result = await Runner(model, resumeStore, Tool(ToolA), Tool(ToolB), Tool(ToolC)).ResumeAsync(persisted, Resumer);

        Assert.Equal([ToolB], Offered(model.Requests[0]));
        Assert.DoesNotContain(CorrectionInstruction, model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal([ToolC], Offered(model.Requests[1]));
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
    }

    [Fact]
    public async Task AResumeAfterAnExhaustedCorrectionAndAMalformedReplan_ReplansBeforeOfferingAnyTool()
    {
        var store = new InMemoryTaskStore();
        var toolA = Tool(ToolA);
        var interrupted = await Runner(
                new FakeChatModel(
                    PlanningTestSupport.PlanResponseFor(ToolA, ToolB),
                    Invalid(ToolA),
                    Invalid(ToolA),
                    new ModelResponse("not a plan", [], false, null),
                    new ModelResponse("still not a plan", [], false, null)),
                store, toolA, Tool(ToolB))
            .RunAsync("diagnose", Actor);

        // ADR-0046 §4: the malformed replan is transactional — the last accepted plan stays, the attempt fails.
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);
        Assert.Equal((TaskTerminalKind.ModelFailure, ModelFailureKind.MalformedResponse),
            (interrupted.TerminalReason!.Kind, interrupted.TerminalReason.FailureKind));
        Assert.Single(interrupted.Plans);
        Assert.Equal(0, interrupted.Accounting!.LifetimeReplans);

        var (resumeStore, persisted) = await PersistedAsync(store, interrupted.Id);
        var toolB = Tool(ToolB);
        var model = new FakeChatModel(PlanningTestSupport.PlanResponseFor(ToolB), Valid(ToolB), Final());

        var result = await Runner(model, resumeStore, Tool(ToolA), toolB).ResumeAsync(persisted, Resumer);

        Assert.Empty(model.Requests[0].AvailableTools);
        Assert.Equal([ToolB], Offered(model.Requests[1]));
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1, toolB.ExecutionCount);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
    }

    [Fact]
    public async Task AResumeAfterASpentCorrectionThenANotOfferedToolAndAMalformedReplan_ReplansBeforeOfferingAnyTool()
    {
        var store = new InMemoryTaskStore();
        var interrupted = await Runner(
                new FakeChatModel(
                    PlanningTestSupport.PlanResponseFor(ToolA, ToolB, ToolC),
                    Invalid(ToolA),
                    Valid(ToolB),
                    Malformed(),
                    Malformed()),
                store, Tool(ToolA), Tool(ToolB), Tool(ToolC))
            .RunAsync("diagnose", Actor);

        // The not-offered call is a typed rejection, not an executed call; its replan did not commit (ADR-0046 §4).
        var rejected = interrupted.Steps[1];
        Assert.Equal(ToolB, rejected.ToolCall!.ToolName);
        Assert.NotNull(rejected.ToolCall.ToolNameError);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);
        Assert.Equal((TaskTerminalKind.ModelFailure, ModelFailureKind.MalformedResponse),
            (interrupted.TerminalReason!.Kind, interrupted.TerminalReason.FailureKind));
        Assert.Single(interrupted.Plans);
        Assert.Null(interrupted.Steps[^1].PlanRevision);

        var (resumeStore, persisted) = await PersistedAsync(store, interrupted.Id);
        var (toolA, toolB, toolC) = (Tool(ToolA), Tool(ToolB), Tool(ToolC));
        var audit = new RecordingAuditSink();
        var model = new FakeChatModel(PlanningTestSupport.PlanResponseFor(ToolC), Valid(ToolC), Final());

        var result = await Runner(model, resumeStore, audit, toolA, toolB, toolC).ResumeAsync(persisted, Resumer);

        // The first resumed request is the replan: no native tool, so neither A, nor B, nor anything else, before it commits.
        Assert.Empty(model.Requests[0].AvailableTools);
        Assert.Equal([ToolC], Offered(model.Requests[1]));
        Assert.Equal((0, 0, 1), (toolA.ExecutionCount, toolB.ExecutionCount, toolC.ExecutionCount));
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);

        // The resumed replan is triggered by the rejected step of the stale plan, not by the later synthetic failure step.
        Assert.Equal(rejected.Index, audit.Events.OfType<ModelCallAuditEvent>().First().StepIndex);
    }

    [Fact]
    public async Task ALegacyRecordThatConsumedTheFailedStep_FailsClosed_ByReplanningBeforeOfferingAnyTool()
    {
        // Written by the runtime before ADR-0047: the validation failure on A advanced the cursor, and B ran next.
        var plan = new AgentPlan(0, "plan",
            [new PlannedStep(0, "a", ToolA), new PlannedStep(1, "b", ToolB), new PlannedStep(2, "c", ToolC)]);
        var invalid = new ModelToolCall("call-0", ToolA, ToolArguments.FromJson(new JsonObject { ["sinceDays"] = 2 }));
        var steps = new List<PlanStep>
        {
            new(0, ToolA, invalid, ToolCallResult.Failure("Unknown argument 'sinceDays'.") with { FailureKind = ToolFailureKind.Validation },
                "ERROR (validation): Unknown argument 'sinceDays'.", 0) { ExecutionAttempt = 1 },
            new(1, ToolB, new ModelToolCall("call-1", ToolB, ToolArguments.Empty), ToolCallResult.Success("ok"), "ok", 0)
            {
                ExecutionAttempt = 1,
            },
        };
        var stored = new TaskState(Guid.NewGuid(), NodeId.Local, "diagnose", AgentTaskStatus.Failed, steps, [plan], DateTimeOffset.UtcNow)
        {
            Origin = TaskOrigin.Ordinary,
            Accounting = new TaskAccounting(100, 2, 0),
        };
        var store = new InMemoryTaskStore();
        store.Seed(stored);
        var model = new FakeChatModel(PlanningTestSupport.PlanResponseFor(ToolC), Valid(ToolC), Final());

        var result = await Runner(model, store, Tool(ToolA), Tool(ToolB), Tool(ToolC)).ResumeAsync(stored, Resumer);

        Assert.Empty(model.Requests[0].AvailableTools);
        Assert.Equal([ToolC], Offered(model.Requests[1]));
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans.Count);
    }

    // ---- E: exact-name enforcement is unchanged during the correction ----

    [Theory]
    [InlineData("test_tool-a")]
    [InlineData(ToolB)]
    public async Task ACorrectionNamingAnyOtherTool_IsRejectedUnchanged_AndReplans(string otherName)
    {
        var (toolA, toolB) = (Tool(ToolA), Tool(ToolB));
        var audit = new RecordingAuditSink();
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponseFor(ToolA, ToolB),
            Invalid(ToolA),
            Valid(otherName),
            PlanningTestSupport.PlanResponseFor(ToolB),
            Final());
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), toolA);
        registry.Register(new PackageId("test.package"), toolB);
        var runner = new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit, new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal([ToolA], Offered(model.Requests[2]));
        var rejected = result.Steps[1];
        Assert.Equal(otherName, rejected.ToolCall!.ToolName);
        Assert.NotNull(rejected.ToolCall.ToolNameError);
        Assert.Equal(RuntimeStepTokens.Denied, rejected.Description);
        Assert.Equal((0, 0), (toolA.ExecutionCount, toolB.ExecutionCount));
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.UnknownTool, Outcome: ToolOutcome.Denied });
        Assert.Empty(model.Requests[3].AvailableTools);
        Assert.Equal(2, result.Plans.Count);
    }

    // ---- F: what is not an argument-validation failure on the planned tool is unchanged ----

    [Fact]
    public async Task AnOrdinaryToolFailure_StillConsumesThePlannedStep_WithoutAReplan()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponseFor("test.throws", ToolB), Call("test.throws", new JsonObject()), Final());

        var result = await Runner(model, new InMemoryTaskStore(), new ThrowingTool(), Tool(ToolB)).RunAsync("diagnose", Actor);

        Assert.Equal(ToolOutcome.Failure, result.Steps[0].Result!.Outcome);
        Assert.NotEqual(ToolFailureKind.Validation, result.Steps[0].Result!.FailureKind);
        Assert.Equal([ToolB], Offered(model.Requests[2]));
        Assert.Single(result.Plans);
    }

    [Fact]
    public void AnUnknownToolRejectionOfTheExpectedName_IsNotAnArgumentValidationFailure()
    {
        var plan = new AgentPlan(0, "plan", [new PlannedStep(0, "a", ToolA), new PlannedStep(1, "b", ToolB)]);
        var rejection = new PlanStep(0, RuntimeStepTokens.Denied,
            new ModelToolCall("call-0", ToolA, ToolArguments.Empty) { ToolNameError = "not offered" },
            ToolCallResult.Failure("Unknown tool.") with { FailureKind = ToolFailureKind.Validation }, "ERROR (validation): Unknown tool.", 0);

        Assert.Equal(new PlannedStepPosition(1, false, false, false), PlannedStepPosition.Derive(plan, [rejection]));
    }

    public static TheoryData<string> DeviationsAfterASpentCorrection => ["not-offered", "policy-denied", "timeout", "refuted"];

    [Theory]
    [MemberData(nameof(DeviationsAfterASpentCorrection))]
    public void ATypedDeviationAfterASpentCorrection_RequiresAReplan(string deviation)
    {
        var plan = new AgentPlan(0, "plan", [new PlannedStep(0, "a", ToolA), new PlannedStep(1, "b", ToolB)]);
        var invalid = new PlanStep(0, ToolA, new ModelToolCall("call-0", ToolA, ToolArguments.Empty),
            ToolCallResult.Failure("Invalid.") with { FailureKind = ToolFailureKind.Validation }, "ERROR (validation): Invalid.", 0);
        var call = new ModelToolCall("call-1", ToolA, ToolArguments.Empty);
        var next = deviation switch
        {
            "not-offered" => new PlanStep(1, RuntimeStepTokens.Denied, call with { ToolName = ToolB, ToolNameError = "not offered" },
                ToolCallResult.Failure("x") with { FailureKind = ToolFailureKind.Validation }, "x", 0),
            "policy-denied" => new PlanStep(1, RuntimeStepTokens.Denied, call,
                ToolCallResult.Failure("x") with { FailureKind = ToolFailureKind.Authorization }, "x", 0),
            "timeout" => new PlanStep(1, ToolA, call,
                new ToolCallResult(ToolOutcome.Timeout, null, "x") { FailureKind = ToolFailureKind.Timeout }, "x", 0),
            _ => new PlanStep(1, ToolA, call, ToolCallResult.Success("ok"), "ok", 0) { VerificationStatus = VerificationStatus.Refuted },
        };

        Assert.Equal(new PlannedStepPosition(0, false, true, true), PlannedStepPosition.Derive(plan, [invalid, next]));
        // The same outcome without a spent correction consumes the step, as before ADR-0047 (the live loop still replans).
        Assert.Equal(1, PlannedStepPosition.Derive(plan, [next with { Index = 0 }]).Cursor);
    }

    [Fact]
    public void MalformedArgumentsOnThePlannedTool_AreAnArgumentValidationFailure_AndOtherRevisionsAreIgnored()
    {
        var plan = new AgentPlan(1, "plan", [new PlannedStep(0, "a", ToolA), new PlannedStep(1, "b", ToolB)]);
        var malformed = new PlanStep(3, ToolA, new ModelToolCall("call-3", ToolA, ToolArguments.Empty) { ArgumentsError = "not an object" },
            ToolCallResult.Failure("Invalid arguments.") with { FailureKind = ToolFailureKind.Validation }, "ERROR (validation): Invalid arguments.", 1);
        var earlierRevision = malformed with { Index = 0, PlanRevision = 0 };
        var synthetic = new PlanStep(4, TaskResumePolicy.ModelFailureStepDescription, null, null, "The provider failed.");

        Assert.Equal(new PlannedStepPosition(0, false, true, false), PlannedStepPosition.Derive(plan, [earlierRevision, malformed, synthetic]));
        Assert.Equal(new PlannedStepPosition(0, false, true, true), PlannedStepPosition.Derive(plan, [malformed, malformed with { Index = 5 }]));
    }

    /// <summary>Replays <c>responses</c>, then fails every later call terminally, as a provider outage would.</summary>
    private sealed class FailingAfterModel(params ModelResponse[] responses) : IChatModel
    {
        private int _calls;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            _calls < responses.Length
                ? Task.FromResult(responses[_calls++])
                : throw new ModelProtocolException("The provider rejected the credentials.") { FailureKind = ModelFailureKind.Authentication };
    }
}
