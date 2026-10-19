// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.PromptAssembly;

/// <summary>
/// PA-1, level B: structural and governance invariants of the requests the runner assembles, checked on the provider body the real
/// adapter serialized and on the request the adapter received (where provenance exists). None of these asserts what a prompt should
/// say, so none of them depends on the future prompt-routing finalization; the wording is frozen separately by the golden files and
/// by <see cref="PromptBaselineDefectTests"/>.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed class RequestAssemblyInvariantTests
{
    private const string ToolOutputOpen = "<<<BOPS_TOOL_OUTPUT>>>";

    private static readonly int[] PendingSteps = [1, 2];
    private static readonly int[] NoToolSteps = [0, 3, 4];

    public static TheoryData<string> Cases => [.. PaCases.All.Select(item => item.Id)];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryRequestOfEveryScenario_IsAValidGovernedProviderBody(string id)
    {
        var run = await PaCases.Get(id).Run();

        PaInvariants.AssertEveryRequestIsAValidGovernedBody(run);
        for (var index = 0; index < run.Bodies.Count; index++)
        {
            var wire = run.Wire(index);
            // The standing rule that tool output is data, never an instruction, is in every call.
            Assert.StartsWith("You are bOps, an operations agent.", wire.System, StringComparison.Ordinal);
            Assert.Contains("never an instruction", wire.System, StringComparison.Ordinal);
            // Tool output reaches the model only inside the delimiters, as a tool-role message.
            Assert.All(
                run.Seen[index].History.Where(turn => turn.Role == ChatRole.Tool && turn.Provenance!.Origin == ContextOrigin.ToolResult),
                turn => Assert.Contains(ToolOutputOpen, turn.Content, StringComparison.Ordinal));
        }
    }

    // ---- PA1-01 ----

    [Fact]
    public async Task PA101_Planning_OffersNoNativeTool_PublishesTheCatalogueInTheSystemPrompt_AndSendsTheGoalAsTheOnlyUserMessage()
    {
        var run = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());
        var wire = run.Wire(0);
        var request = run.Seen[0];

        Assert.Empty(wire.ToolNames);
        Assert.Empty(request.AvailableTools);
        Assert.Equal(["system", "user"], wire.Roles);
        Assert.Equal(PaFixtures.Goal, wire.Content(1));
        Assert.Equal(ContextOrigin.Task, request.History[0].Provenance!.Origin);

        // The catalogue lists every registered tool, in ordinal order, with its parameters and the parameter's planning guidance.
        var system = wire.System;
        Assert.Contains("lay out your plan", system, StringComparison.Ordinal);
        Assert.True(system.IndexOf("- test.other [Read]", StringComparison.Ordinal) < system.IndexOf("- test.read [Read]", StringComparison.Ordinal));
        Assert.Contains("mode:String optional allowed=[\"summary\",\"detail\"]", system, StringComparison.Ordinal);
        Assert.Contains(PaFixtures.Guidance, system, StringComparison.Ordinal);
        Assert.Contains("limit:Integer optional minimum=1 maximum=100", system, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime.", system, StringComparison.Ordinal); // no control function exists before there is anything to control
    }

    // ---- PA1-02 ----

    [Fact]
    public async Task PA102A_AnOrdinaryReplan_CarriesThePreviousPlan_TheCompletedChecksAndTheTriggerAsTheLastProtectedTurn()
    {
        var run = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());
        var wire = run.Wire(4);
        var request = run.Seen[4];

        Assert.Empty(wire.ToolNames); // a replan offers no native tool
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "tool", "assistant", "assistant", "tool", "user"], wire.Roles);
        Assert.Contains("Revise it.", wire.System, StringComparison.Ordinal);
        Assert.Contains("- task step 0 test.read {}", wire.System, StringComparison.Ordinal);
        Assert.Contains("- task step 1 test.other {}", wire.System, StringComparison.Ordinal);

        // Completed steps, in order, then the previous plan, then the proposal that triggered the replan and its refusal, then the trigger text.
        Assert.StartsWith("Plan (revision 0):", wire.Content(6), StringComparison.Ordinal);
        Assert.Equal(["call-1", "call-2", "call-3"],
            wire.Messages.Where(message => message["tool_calls"] is not null).Select(message => message["tool_calls"]![0]!["id"]!.GetValue<string>()));
        Assert.StartsWith("Every step of plan revision 0 was complete", wire.Content(9), StringComparison.Ordinal);
        Assert.Equal(ContextOrigin.Runtime, request.History[^1].Provenance!.Origin);
        Assert.Equal(ContextOrigin.Model, request.History[5].Provenance!.Origin); // the previous plan is the model's own output
        Assert.Equal(2, run.State.Plans.Count);
        Assert.Empty(run.State.Plans[1].Steps);
    }

    [Fact]
    public async Task PA102B_AnEarlyFinalReplan_NamesTheUnresolvedStepsButNeverRepeatsThePrematureAnswer()
    {
        var run = await PaCompose.RunAsync(PaScenarios.EarlyFinalReplan());
        var wire = run.Wire(3);
        var request = run.Seen[3];

        Assert.Empty(wire.ToolNames);
        Assert.Contains("Revise it.", wire.System, StringComparison.Ordinal);
        Assert.Contains("- task step 0 test.read {}", wire.System, StringComparison.Ordinal);
        Assert.StartsWith("Plan (revision 0):", wire.Content(4), StringComparison.Ordinal);
        Assert.Equal("user", wire.Role(5));
        Assert.StartsWith(
            "Final answer not accepted: plan revision 0 still had 1 unresolved planned step(s): 2 [test.other];",
            wire.Content(5), StringComparison.Ordinal);
        Assert.Equal(ContextOrigin.Runtime, request.History[^1].Provenance!.Origin);
        Assert.All(run.Bodies.Skip(3).Take(1), body => Assert.DoesNotContain(PaScenarios.Premature, body, StringComparison.Ordinal));

        // The step that drew the premature answer is bookkeeping: it carries no tool call and is not a final answer.
        var bookkeeping = Assert.Single(run.State.Steps, step => step.Description == "Premature final response; plan re-evaluation required");
        Assert.Null(bookkeeping.ToolCall);
        Assert.Equal(2, run.State.Plans.Count);
        Assert.Equal(["test.other"], run.State.Plans[1].Steps.Select(step => step.ExpectedTool!));

        // The step request after the replan offers exactly the tool of the revised plan.
        Assert.Equal(["test_other"], run.Wire(4).OperationalToolNames);
    }

    [Fact]
    public async Task PA102_TheTwoReplanKinds_ShareTheOrdinaryLeadOnlyForPlanDeviation_AndDifferInTheirLastProtectedTurn()
    {
        var ordinary = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());
        var earlyFinal = await PaCompose.RunAsync(PaScenarios.EarlyFinalReplan());

        // PRA-R3: the early-final replan has a lead of its own (the generic "no longer matches" is not why it runs); both keep the
        // "Revise it." sentence that identifies a replan request, and the rest of the system prompt is the same assembly.
        const string ordinaryLead = "Your previous plan no longer matches what you have learned. Revise it.";
        Assert.Contains(ordinaryLead, ordinary.Wire(4).System, StringComparison.Ordinal);
        Assert.DoesNotContain(ordinaryLead, earlyFinal.Wire(3).System, StringComparison.Ordinal);
        Assert.Contains("Revise it.", earlyFinal.Wire(3).System, StringComparison.Ordinal);
        Assert.NotEqual(ordinary.Wire(4).Messages[^1]["content"]!.GetValue<string>(), earlyFinal.Wire(3).Messages[^1]["content"]!.GetValue<string>());
    }

    // ---- PA1-03 ----

    [Fact]
    public async Task PA103_AStepWithAPendingTool_OffersExactlyThatTool_UnderOneAlias_WithItsSchema_AndNeverTheNextOne()
    {
        var run = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());
        var step0 = run.Wire(1);

        Assert.Equal(["test_read"], step0.ToolNames);
        Assert.Equal(["test.read"], PaInvariants.Operational(run.Seen[1])); // canonical name in the runtime, alias on the wire
        Assert.Empty(step0.ControlToolNames); // nothing persisted and no knowledge yet: no control function
        Assert.Equal(["system", "user"], step0.Roles);
        Assert.Equal(["test.read", "test.other"], PaInvariants.PlanTools(step0.System)); // the whole plan is shown, the second tool is not offered
        Assert.DoesNotContain("test_other", step0.ToolNames);

        var function = step0.Function("test_read");
        Assert.Equal("Reads the synthetic service status. Read-only.", function["description"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"type":"object","properties":{"mode":{"type":"string","description":"Output mode.","enum":["summary","detail"]}},"required":[],"additionalProperties":false}"""),
            function["parameters"]));

        // D-043: one operational tool while a planned step is pending; none while planning, replanning, or once the plan is complete.
        Assert.All(PendingSteps, index => Assert.Single(run.Wire(index).OperationalToolNames));
        Assert.All(NoToolSteps, index => Assert.Empty(run.Wire(index).OperationalToolNames));
        Assert.Equal(["test_other"], run.Wire(2).OperationalToolNames);
    }

    // ---- PA1-04 ----

    [Fact]
    public async Task PA104_PersistedEvidence_IsIndexed_AddressableThroughOneControlFunction_AndReadBackAfterTheObservationIsShortened()
    {
        var run = await PaCompose.RunAsync(PaScenarios.EvidenceRead());
        var withIndex = run.Wire(2);
        var continuation = run.Wire(3);
        var id = $"ev1:{PaCompose.TaskId:N}:0";

        // No evidence yet: no index and no control function.
        Assert.Empty(run.Wire(1).ControlToolNames);
        Assert.DoesNotContain("BOPS_EVIDENCE_INDEX", run.Wire(1).System, StringComparison.Ordinal);

        Assert.Equal(["runtime_evidence_read"], withIndex.ControlToolNames);
        Assert.Equal(["test_other"], withIndex.OperationalToolNames);
        Assert.Equal(["test_other", "runtime_evidence_read"], withIndex.ToolNames); // the control function is offered beside, after, the step's tool
        var schema = withIndex.Function("runtime_evidence_read")["parameters"]!;
        Assert.Equal(["step", "source", "offset", "length"], schema["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(["result", "observation"], schema["properties"]!["source"]!["enum"]!.AsArray().Select(node => node!.GetValue<string>()));

        var index = PaInvariants.Block(withIndex.System, "<<<BOPS_EVIDENCE_INDEX>>>", "<<<END_BOPS_EVIDENCE_INDEX>>>");
        Assert.Contains($"step 0 test.read: id {id};", index, StringComparison.Ordinal);
        Assert.Equal(["system", "user", "assistant", "tool"], withIndex.Roles);

        // The observation is shortened in the conversation (the index says by how much); the full result stays addressable.
        var observation = withIndex.Content(3)!;
        Assert.StartsWith(ToolOutputOpen, observation, StringComparison.Ordinal);
        Assert.Contains("[truncated", observation, StringComparison.Ordinal);
        Assert.Contains("observation shortened from", withIndex.System, StringComparison.Ordinal);

        // After the read: the model's own read call and the runtime's answer are the last two messages, matched by id, before anything else.
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "tool"], continuation.Roles);
        Assert.Equal("read-1", continuation.Messages[4]["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("read-1", continuation.Messages[5]["tool_call_id"]!.GetValue<string>());
        Assert.StartsWith("EvidenceRead/v1 Success;", continuation.Content(5), StringComparison.Ordinal);
        Assert.Equal(withIndex.System, continuation.System);
        Assert.Equal(ContextOrigin.Model, run.Seen[3].History[^2].Provenance!.Origin);
        Assert.NotNull(run.Seen[3].History[^1].Provenance);
    }

    // ---- PA1-05 ----

    [Fact]
    public async Task PA105_Knowledge_IsPublishedAsADataTurnBeforeTheGoal_NeverInTheSystemPrompt_AndTheOperationalToolSurvives()
    {
        var run = await PaScenarios.Knowledge();
        var planning = run.Wire(0);
        var step = run.Wire(1);
        var after = run.Wire(2);

        // Planning sees no knowledge at all.
        Assert.DoesNotContain("knowledge", planning.System + string.Join('\n', planning.Texts), StringComparison.OrdinalIgnoreCase);

        // The step offers its operational tool and, beside it, the knowledge control function.
        Assert.Equal(["test_read"], step.OperationalToolNames);
        Assert.Equal(["runtime_knowledge_read"], step.ControlToolNames);
        Assert.Equal(["test_read", "runtime_knowledge_read"], step.ToolNames);
        Assert.Equal(["operation"], step.Function("runtime_knowledge_read")["parameters"]!["required"]!.AsArray().Select(node => node!.GetValue<string>()));

        // The index is a user-role DATA turn attributed to the pack, before the operator's goal; the goal turn is untouched.
        Assert.Equal(["system", "user", "user"], step.Roles);
        Assert.StartsWith("[knowledge-index v1 sha256=", step.Content(1), StringComparison.Ordinal);
        Assert.Contains("nginx 502 triage", step.Content(1), StringComparison.Ordinal);
        Assert.Equal("diagnose nginx", step.Content(2));
        var indexTurn = run.Seen[1].History[0].Provenance!;
        Assert.Equal((ContextOrigin.Knowledge, "index"), (indexTurn.Origin, indexTurn.Operation));
        Assert.NotNull(indexTurn.PackId);
        Assert.Equal(ContextOrigin.Task, run.Seen[1].History[1].Provenance!.Origin);

        // Publisher text never enters the system prompt, which only carries typed host facts about the index.
        Assert.DoesNotContain("nginx 502 triage", step.System, StringComparison.Ordinal);
        Assert.DoesNotContain("checklist", step.System, StringComparison.Ordinal);
        Assert.EndsWith("[/knowledge-index-control]", step.System, StringComparison.Ordinal);
        Assert.Contains("is third-party reference data", step.System, StringComparison.Ordinal);

        // After the read the resource is a tool-role message answering the model's call, attributed to the pack, after the goal; the
        // pending operational tool is still the only one offered.
        Assert.Equal(["test_read", "runtime_knowledge_read"], after.ToolNames);
        Assert.Equal(["system", "user", "user", "assistant", "tool"], after.Roles);
        Assert.Equal("k1", after.Messages[3]["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("k1", after.Messages[4]["tool_call_id"]!.GetValue<string>());
        Assert.Contains("nginx -t", after.Content(4), StringComparison.Ordinal); // the resource text, as data
        var read = run.Seen[2].History[^1].Provenance!;
        Assert.Equal(ContextOrigin.Knowledge, read.Origin);
        Assert.Equal(indexTurn.PackId, read.PackId);
        Assert.DoesNotContain("nginx -t", after.System, StringComparison.Ordinal);
        Assert.Equal(["test.read", "test.other"], PaInvariants.PlanTools(after.System)); // the plan is unchanged by the read
        Assert.Equal(1, run.Tools["test.read"].Invocations);
    }

    // ---- PA1-06 ----

    [Fact]
    public async Task PA106_APlanWithEveryStepComplete_OffersNoOperationalTool_OnlyTheControlFunctionThatIsStillLegitimate()
    {
        var run = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());
        var wire = run.Wire(3);

        Assert.Empty(wire.OperationalToolNames);
        Assert.Equal(["runtime_evidence_read"], wire.ToolNames);
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "tool"], wire.Roles);
        Assert.Equal(["test.read", "test.other"], PaInvariants.PlanTools(wire.System)); // the completed plan stays visible
        Assert.Contains("Every planned step above is complete and no operational tool is offered.", wire.System, StringComparison.Ordinal);
        Assert.DoesNotContain("The single offered native tool", wire.System, StringComparison.Ordinal);
        Assert.DoesNotContain("only operational tool offered", wire.System, StringComparison.Ordinal);

        // Both observations are in the history, in execution order, answered by their own call ids.
        Assert.Equal(["call-1", "call-2"],
            wire.Messages.Where(message => message["role"]!.GetValue<string>() == "tool").Select(message => message["tool_call_id"]!.GetValue<string>()));
        Assert.Contains("service alpha: running", wire.Content(3), StringComparison.Ordinal);
        Assert.Contains("volume data: 61% used", wire.Content(5), StringComparison.Ordinal);
        Assert.Equal(2, run.State.Steps.Count(step => step.Result is { Outcome: ToolOutcome.Success }));
    }

    // ---- PA1-07 ----

    [Fact]
    public async Task PA107_AnEmptyReply_IsAnsweredByOneRuntimeInstruction_AppendedToTheUnchangedRequest_AndTheEmptyTurnIsNotKept()
    {
        var run = await PaCompose.RunAsync(PaScenarios.EmptyReply());

        PaInvariants.AssertExtendsBy(run, 2, 3, "user");
        var instruction = run.Wire(3).Messages[^1];
        Assert.StartsWith("Your last reply was empty: it had no text and no tool call.", instruction["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(ContextOrigin.Runtime, run.Seen[3].History[^1].Provenance!.Origin);
        Assert.DoesNotContain(run.Wire(3).Messages, message => message["role"]!.GetValue<string>() == "assistant" && string.IsNullOrEmpty(message["content"]?.GetValue<string>()) && message["tool_calls"] is null);
        Assert.Equal(["test_other"], run.Wire(3).OperationalToolNames); // the step's tool is still the one offered
        Assert.Equal(["test.read", "test.other"], PaInvariants.PlanTools(run.Wire(3).System)); // the plan state is unchanged

        // One logical call: both provider calls are accounted on the one step that finally ran the tool.
        var step = run.State.Steps.Single(item => item.ToolCall?.ToolName == "test.other");
        Assert.Equal(2, step.ModelCalls!.Count);
        Assert.Equal(1, run.Bodies.Count(body => body.Contains("Your last reply was empty", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PA107_TheRetryBudgetIsOne_ASecondEmptyReplyFailsTheTaskWithoutAThirdAttempt()
    {
        var run = await PaCompose.RunAsync(PaScenarios.EmptyReplyTwice());

        Assert.Equal(AgentTaskStatus.Failed, run.State.Status);
        Assert.Equal(TaskTerminalKind.EmptyResponse, run.State.TerminalReason!.Kind);
        Assert.Equal(4, run.Bodies.Count); // planning, step 0, the empty step, its single retry
        Assert.Equal(0, run.Unconsumed);
        PaInvariants.AssertExtendsBy(run, 2, 3, "user");
        Assert.Equal(new AgentRunnerOptions().EmptyFinalResponseRetries, run.Bodies.Count(body => body.Contains("Your last reply was empty", StringComparison.Ordinal)));
    }

    // ---- PA1-08 ----

    [Fact]
    public async Task PA108A_AnOutputLimitedReply_WithAToolPending_IsAnsweredByOneRuntimeInstruction_AndTheOfferedToolIsKept()
    {
        var run = await PaCompose.RunAsync(PaScenarios.OutputLimitedWhilePending());

        PaInvariants.AssertExtendsBy(run, 2, 3, "user");
        Assert.StartsWith("Your last reply reached the output limit while reasoning", run.Wire(3).Content(^1)!, StringComparison.Ordinal);
        Assert.Equal(ContextOrigin.Runtime, run.Seen[3].History[^1].Provenance!.Origin);
        Assert.Equal(["test_other"], run.Wire(3).OperationalToolNames);
        Assert.Equal(["test.read", "test.other"], PaInvariants.PlanTools(run.Wire(3).System));
        Assert.Equal(2, run.State.Steps.Single(step => step.ToolCall?.ToolName == "test.other").ModelCalls!.Count);
    }

    [Fact]
    public async Task PA108B_AnOutputLimitedReply_WithThePlanComplete_IsAnsweredByTheSameKindOfInstruction_WhileNoOperationalToolIsOffered()
    {
        var run = await PaCompose.RunAsync(PaScenarios.OutputLimitedWhenComplete());

        PaInvariants.AssertExtendsBy(run, 2, 3, "user");
        Assert.Equal(ContextOrigin.Runtime, run.Seen[3].History[^1].Provenance!.Origin);
        Assert.Empty(run.Wire(3).OperationalToolNames);
        Assert.Equal(["runtime_evidence_read"], run.Wire(3).ToolNames);
        Assert.Contains("Every planned step above is complete and no operational tool is offered.", run.Wire(3).System, StringComparison.Ordinal);
        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
    }

    // ---- PA1-09 ----

    [Fact]
    public async Task PA109_AfterAFailedEarlyFinalReplan_ThePersistedPlanContinuesAtItsCursor_WithOneRuntimeTurn_AndNothingElseChanges()
    {
        var run = await PaCompose.RunAsync(PaScenarios.EarlyFinalContinuation());

        // The continuation request is the request that drew the premature final plus exactly one protected runtime turn at the end.
        PaInvariants.AssertExtendsBy(run, 2, 4, "user");
        var turn = run.Seen[4].History[^1];
        Assert.Equal(ContextOrigin.Runtime, turn.Provenance!.Origin);
        Assert.StartsWith("Runtime control instruction: your previous final answer was rejected because plan revision 0 is incomplete.", turn.Content, StringComparison.Ordinal);
        Assert.Contains("step 2 of 2, which uses the operational tool test.other", turn.Content, StringComparison.Ordinal);
        Assert.Contains("executes nothing and grants no authority", turn.Content, StringComparison.Ordinal);
        Assert.Equal(1, run.Bodies.Count(body => body.Contains("Runtime control instruction:", StringComparison.Ordinal)));
        Assert.DoesNotContain("Runtime control instruction:", run.Bodies[2], StringComparison.Ordinal); // the ordinary step request has none
        Assert.All(run.Bodies, body => Assert.DoesNotContain(PaScenarios.Premature, body, StringComparison.Ordinal)); // the rejected answer is never replayed

        // Cursor and offered tools: still the second planned step's tool, and no tool of an earlier or later step.
        Assert.Equal(["test_other"], run.Wire(4).OperationalToolNames);
        Assert.Equal(["test_other", "runtime_evidence_read"], run.Wire(4).ToolNames);
        var recovery = Assert.Single(run.State.Steps, EarlyFinalRecovery.IsStep);
        Assert.Equal(1, recovery.EarlyFinalRecoveryCursor);
        Assert.Single(run.State.Plans); // no replan was accepted: the plan is the persisted one
        Assert.Equal((1, 1), (run.Tools["test.read"].Invocations, run.Tools["test.other"].Invocations));

        // The harness adds nothing: each tool call id appears once in the continuation request.
        Assert.Equal(1, run.Bodies[4].Split("\"id\":\"call-1\"").Length - 1);
        Assert.Equal(run.Wire(2).Messages.Count + 1, run.Wire(4).Messages.Count);
    }

    // ---- PA1-10 ----

    [Fact]
    public async Task PA110_TheGroundingCheck_IsTheSynthesisRequestPlusTheCandidateAnswerAndOneFixedRuntimeTurn()
    {
        var run = await PaCompose.RunAsync(PaScenarios.Grounding());
        var synthesis = run.Wire(2);
        var check = run.Wire(3);

        // The collected facts are typed, delimited runtime text in the system prompt of the synthesis, with their values.
        var ledger = PaInvariants.Block(synthesis.System, EvidenceGroundingLedger.OpenMarker, EvidenceGroundingLedger.CloseMarker);
        Assert.Contains("- F0.1 step 0 test.events: type \"synthetic.events.matched-count\" key \"source=alpha;id=19\" = Integer 3", ledger, StringComparison.Ordinal);
        Assert.Contains("- F0.2 step 0 test.events: type \"synthetic.events.any-critical\" key \"source=alpha\" = Boolean false", ledger, StringComparison.Ordinal);
        Assert.DoesNotContain(EvidenceGroundingLedger.OpenMarker, run.Wire(0).System, StringComparison.Ordinal); // planning has no ledger

        // The check extends that request by the candidate (assistant) and the fixed instruction (user); nothing else moves.
        PaInvariants.AssertExtendsBy(run, 2, 3, "assistant", "user");
        Assert.Equal(PaScenarios.GroundedAnswer, check.Content(^2));
        Assert.Equal(EvidenceGroundingLedger.CheckInstruction, check.Content(^1));
        Assert.Contains("\"contradictions\"", check.Content(^1), StringComparison.Ordinal);
        Assert.Equal(ContextOrigin.Model, run.Seen[3].History[^2].Provenance!.Origin);
        Assert.Equal(ContextOrigin.Runtime, run.Seen[3].History[^1].Provenance!.Origin);

        // The control function is still offered on the check request (it is the synthesis request's tool list).
        Assert.Equal(["runtime_evidence_read"], check.ToolNames);
        Assert.Equal("Final response; evidence grounding check cited no contradiction", run.State.Steps[^1].Description);
        Assert.Equal(PaScenarios.GroundedAnswer, run.State.Steps[^1].Observation);
    }

    // ---- agreement with the existing d3da032 baseline ----

    [Fact]
    public async Task TheAssembledPlanningAndEarlyFinalReplanRequests_AgreeWithTheD3da032BaselineWhereTheFixturesAreComparable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "d3da032", "planning-replan-bound.json");
        var lines = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var planningBaseline = JsonNode.Parse(lines[0])!;
        var replanBaseline = JsonNode.Parse(lines[1])!;
        var planning = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());
        var replan = await PaCompose.RunAsync(PaScenarios.EarlyFinalReplan());

        // Planning: same standing prompt and planning instructions up to the catalogue (the catalogue lists this scenario's own tools).
        const string catalogue = "Tools available in later steps.";
        Assert.Equal(Before(planningBaseline["SystemPrompt"]!.GetValue<string>(), catalogue), Before(planning.Wire(0).System, catalogue));
        Assert.Equal(["User"], planningBaseline["History"]!.AsArray().Select(turn => turn!["Role"]!.GetValue<string>()));
        Assert.Equal(["system", "user"], planning.Wire(0).Roles);

        // Early-final replan: same prompt up to the completed checks, same turn shape, and the same runtime-authored last turn.
        const string completed = "Completed checks of this task";
        Assert.Equal(Before(replanBaseline["SystemPrompt"]!.GetValue<string>(), completed), Before(replan.Wire(3).System, completed));
        Assert.Equal(["User", "Assistant", "Tool", "Assistant", "User"], replanBaseline["History"]!.AsArray().Select(turn => turn!["Role"]!.GetValue<string>()));
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user"], replan.Wire(3).Roles);
        Assert.Equal(replanBaseline["History"]![4]!["Content"]!.GetValue<string>(), replan.Wire(3).Content(^1));
    }

    private static string Before(string text, string marker) => text[..text.IndexOf(marker, StringComparison.Ordinal)];

    // ---- the path itself ----

    [Theory]
    [InlineData("plan-exhausted")]
    [InlineData("early-final-continuation")]
    [InlineData("recovery-rc1")]
    public async Task TheRequestJsonThatTheRuntimePersists_IsExactlyTheBodyThatWasSent(string scenario)
    {
        var run = await PaCompose.RunAsync(scenario switch
        {
            "plan-exhausted" => PaScenarios.PlanExhaustedThenOrdinaryReplan(),
            "early-final-continuation" => PaScenarios.EarlyFinalContinuation(),
            _ => PaScenarios.EmptyReply(),
        });

        var persisted = run.State.Plans.SelectMany(plan => plan.ModelCalls ?? [])
            .Concat(run.State.Steps.SelectMany(step => (step.ModelCalls ?? []).Concat(step.EarlyFinalReplanCalls ?? [])))
            .ToList();
        Assert.Equal(run.Bodies.Count, persisted.Count);
        Assert.All(persisted, call => Assert.False(call.PayloadTruncated));
        Assert.Equal(run.Bodies.Order(StringComparer.Ordinal), persisted.Select(call => call.RequestJson!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AConfiguredOutputLimit_ReachesEveryProviderBodyAsMaxTokens()
    {
        var setup = PaScenarios.EmptyReply() with { Options = new AgentRunnerOptions { MaxOutputTokens = 2048 } };
        var run = await PaCompose.RunAsync(setup);

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.All(Enumerable.Range(0, run.Bodies.Count), index => Assert.Equal(2048, run.Wire(index).MaxTokens));
    }

    [Theory]
    [InlineData("plan-exhausted")]
    [InlineData("early-final-continuation")]
    [InlineData("evidence-read")]
    [InlineData("knowledge")]
    public async Task TheGovernedMafTurnPath_SendsTheSameBytesAsTheProductionComposition(string scenario)
    {
        var (production, governed) = scenario switch
        {
            "plan-exhausted" => (await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan()),
                await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan() with { ViaMafTurn = true })),
            "early-final-continuation" => (await PaCompose.RunAsync(PaScenarios.EarlyFinalContinuation()),
                await PaCompose.RunAsync(PaScenarios.EarlyFinalContinuation() with { ViaMafTurn = true })),
            "evidence-read" => (await PaCompose.RunAsync(PaScenarios.EvidenceRead()),
                await PaCompose.RunAsync(PaScenarios.EvidenceRead() with { ViaMafTurn = true })),
            _ => (await PaScenarios.Knowledge(), await PaScenarios.Knowledge(viaMafTurn: true)),
        };

        Assert.Equal(AgentTaskStatus.Completed, governed.State.Status);
        Assert.Equal(production.Bodies, governed.Bodies);
    }
}
