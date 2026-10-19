// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.PromptAssembly;

/// <summary>
/// PROMPT-ROUTING-FINALIZATION: the semantic properties of the prompts the runner assembles, after PRA-R2, PRA-R3, PRA-R4 and PRA-P1
/// were corrected. This file replaces <c>PromptBaselineDefectTests</c> of PA-1, which froze the same assembly as OBSERVED defects;
/// each test here is the rewritten counterpart of the pin of the same id. The tests assert what the runtime says and does, never what
/// a model answers: they hold the sentences that must be absent (an invitation the runtime does not honour), the sentences that must
/// be present (the rule as it is enforced) and the behaviour that did not change (the formal channels, the bounds, the safety nets).
/// PRA-V1 (grounding contradictions) is unchanged by this work and keeps its observed-behaviour pin; PRA-Q1 is not an assembly
/// property.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed class PromptRoutingFinalizationTests
{
    private const string FinalSentenceOfTheOldRouting = "a final answer makes the runtime re-evaluate the plan";

    // ---- PRA-R2 ----

    [Fact]
    public async Task PRA_R2_AtPlanEnd_TheTextStatesWhatTheRuntimeDoes_AndPromisesNothingItDoesNotHonour()
    {
        var run = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());
        var system = run.Wire(3).System;

        // Text is the final answer and ends the task; the limit of the evidence is reported in the answer, not deferred to a check.
        Assert.Contains("Any text you write is taken as your final answer and ends the task: it cannot request more work.", system, StringComparison.Ordinal);
        Assert.Contains("say plainly in the answer what remains unverified", system, StringComparison.Ordinal);
        // The only recognized channel for more work is named: the native call of a tool the plan or the task already names.
        Assert.Contains("native call to that tool by its exact name", system, StringComparison.Ordinal);
        // The old promise (a prose proposal makes the runtime ask for a revised plan) is gone: the runtime never did that.
        Assert.DoesNotContain("propose the one new check you need", system, StringComparison.Ordinal);
        Assert.Empty(run.Wire(3).OperationalToolNames); // D-043: no operational tool is offered at plan end
    }

    [Fact]
    public async Task PRA_R2_AProposalMadeAsProse_IsTheFinalAnswer_NothingRunsAndNoReplanIsRequested()
    {
        // Unchanged runtime guarantee, now stated truthfully in the prompt: prose is never executed, never parsed, never a replan.
        const string proposal = "The evidence is insufficient; please run test.other again with a larger limit before concluding.";
        var run = await PaCompose.RunAsync(new PaSetup(PaFixtures.Goal,
        [
            PaReply.Plan("Read the service.", "test.read"),
            PaReply.Call("call-1", "test.read"),
            PaReply.Text(proposal),
        ]));

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.Single(run.State.Plans); // no revision was requested
        Assert.Equal(proposal, run.State.Steps[^1].Observation); // the proposal is the persisted final answer
        Assert.Equal(3, run.Bodies.Count);
        Assert.DoesNotContain(run.State.Steps, step => step.ToolCall?.ToolName == "test.other"); // nothing was executed
    }

    [Fact]
    public async Task PRA_R2_OnlyANativeCallToAnUnofferedToolReplans_AndTheRuntimeThenExplainsWhy()
    {
        var run = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());

        Assert.Equal(2, run.State.Plans.Count);
        Assert.Contains(run.State.Steps, step => step.ToolCall is { ToolNameError: not null });
        Assert.Contains("It did not run because a completed plan offers no operational tool", run.Wire(4).Content(^1), StringComparison.Ordinal);
    }

    // ---- PRA-R3 ----

    [Fact]
    public async Task PRA_R3_TheStepPrompt_NoLongerInvitesAFinalAnswerWhileStepsRemain_WithOrWithoutKnowledge()
    {
        var run = await PaScenarios.Knowledge();
        var plain = (await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan())).Wire(1).System;
        var withKnowledge = run.Wire(1).System;

        foreach (var system in new[] { plain, withKnowledge })
        {
            Assert.DoesNotContain(FinalSentenceOfTheOldRouting, system, StringComparison.Ordinal);
            Assert.Contains(
                "while planned steps remain, a reply without a tool call is not taken as the final answer and only makes the runtime re-evaluate the plan.",
                system, StringComparison.Ordinal);
            Assert.DoesNotContain("or give your final answer", system, StringComparison.Ordinal);
        }

        // The knowledge instruction sends the next reply to the pending operational call, and keeps its own optionality.
        Assert.Contains("make the current step's operational call in your next reply", withKnowledge, StringComparison.Ordinal);
        Assert.Contains("Using it is never required", withKnowledge, StringComparison.Ordinal);
        Assert.Equal(["test.read", "test.other"], PaInvariants.PlanTools(withKnowledge)); // two steps pending, one tool offered
    }

    [Fact]
    public void PRA_R3_TheKnowledgeInstruction_SendsTheNextReplyToTheFinalAnswerOnlyWhenNoOperationalToolIsOffered()
    {
        var plan = new AgentPlan(0, "r", [new PlannedStep(0, "Read", "test.read")]);
        var stepTool = bOps.AgentFramework.KnowledgeReadBridge.Manifest with { Name = "test.read", Description = "Reads." };
        var function = bOps.AgentFramework.KnowledgeReadBridge.PublicToolName;

        var pending = AgentRunner.BuildStepSystemPrompt(plan, null, [stepTool], false, false, knowledgeFunction: function);
        var exhausted = AgentRunner.BuildStepSystemPrompt(plan, null, [], false, false, planExhausted: true, knowledgeFunction: function);

        Assert.Contains("make the current step's operational call in your next reply", pending, StringComparison.Ordinal);
        Assert.DoesNotContain("give your final answer in your next reply", pending, StringComparison.Ordinal);
        Assert.Contains("give your final answer in your next reply", exhausted, StringComparison.Ordinal);
        Assert.DoesNotContain("make the current step's operational call", exhausted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PRA_R3_TheEarlyFinalReplanLead_SaysWhyItRuns_AndAllowsAnEmptyPlan_WhileTheOrdinaryLeadIsUnchanged()
    {
        var early = await PaCompose.RunAsync(PaScenarios.EarlyFinalReplan());
        var ordinary = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());

        const string ordinaryLead = "Your previous plan no longer matches what you have learned. Revise it.";
        Assert.Contains(ordinaryLead, ordinary.Wire(4).System, StringComparison.Ordinal); // PlanDeviation: unchanged
        Assert.DoesNotContain(ordinaryLead, early.Wire(3).System, StringComparison.Ordinal);
        Assert.Contains("It was not accepted only because planned steps were still unresolved; the plan itself may be right.", early.Wire(3).System, StringComparison.Ordinal);
        Assert.Contains("Revise it.", early.Wire(3).System, StringComparison.Ordinal); // the marker the scripted models of the tests rely on
        Assert.Contains("return an empty \"steps\" array", early.Wire(3).System, StringComparison.Ordinal);
        Assert.Contains("add no check the goal does not need", early.Wire(3).System, StringComparison.Ordinal);
        Assert.Contains("Return an empty \"steps\" array if no further operational work is needed.", early.Wire(3).Content(^1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PRA_R3_TheContinuationTurnAndTheSystemPrompt_AgreeThatTheCallComesBeforeAnyFinalAnswer()
    {
        var run = await PaCompose.RunAsync(PaScenarios.EarlyFinalContinuation());
        var wire = run.Wire(4);

        Assert.Contains("Propose the call to test.other now, before giving any final answer.", wire.Content(^1), StringComparison.Ordinal);
        Assert.Contains("Propose the concrete tool call for the next unfinished step above;", wire.System, StringComparison.Ordinal);
        Assert.Contains("a reply without a tool call is not taken as the final answer", wire.System, StringComparison.Ordinal);
        Assert.DoesNotContain(FinalSentenceOfTheOldRouting, wire.System, StringComparison.Ordinal);
    }

    // ---- PRA-R4 ----

    [Fact]
    public async Task PRA_R4_TheRc2Text_FollowsTheToolSurface_TheStepCallWhileOneIsOffered_TheFinalAnswerAtPlanEnd()
    {
        var pending = await PaCompose.RunAsync(PaScenarios.OutputLimitedWhilePending());
        var complete = await PaCompose.RunAsync(PaScenarios.OutputLimitedWhenComplete());
        const string prefix = "Your last reply reached the output limit while reasoning and had no text and no tool call. Do not deliberate further: ";

        Assert.Equal(prefix + "call the operational tool offered for the current plan step now.", pending.Wire(3).Content(^1));
        Assert.NotEmpty(pending.Wire(3).OperationalToolNames);

        Assert.Empty(complete.Wire(3).OperationalToolNames);
        Assert.Equal(["runtime_evidence_read"], complete.Wire(3).ToolNames);
        Assert.Equal(prefix + "give your final answer to the operator's goal in plain text now, from the evidence already collected.", complete.Wire(3).Content(^1));
        Assert.DoesNotContain("call the", complete.Wire(3).Content(^1)!, StringComparison.OrdinalIgnoreCase); // it no longer asks for a tool at plan end
    }

    [Fact]
    public async Task PRA_R4_TheRc1Text_FollowsTheToolSurface_AndIsStillOneRetryWithTheEmptyTurnNotKept()
    {
        var pending = await PaCompose.RunAsync(PaScenarios.EmptyReply());
        var complete = await PaCompose.RunAsync(new PaSetup(PaFixtures.Goal,
        [
            PaReply.Plan("Read the service.", "test.read"),
            PaReply.Call("call-1", "test.read"),
            PaReply.Empty(),
            PaReply.Text(PaScenarios.Final),
        ]));
        const string prefix = "Your last reply was empty: it had no text and no tool call. ";

        Assert.Equal(prefix + "Call the operational tool offered for the current plan step now.", pending.Wire(3).Content(^1));
        Assert.Equal(prefix + "No operational tool is offered in this turn: give your final answer to the operator's goal in plain text now, from the evidence already collected.", complete.Wire(3).Content(^1));
        Assert.Equal(AgentTaskStatus.Completed, complete.State.Status);
        Assert.Equal(4, complete.Bodies.Count); // planning, step, step + the one retry, nothing more
    }

    [Fact]
    public async Task PRA_R4_ASatisfiedRecoveryInstruction_DoesNotOutliveItsPurpose_NotInTheContinuationAfterAnEvidenceRead()
    {
        const string rcOnePrefix = "Your last reply was empty";
        var run = await PaCompose.RunAsync(new PaSetup(PaFixtures.Goal,
        [
            PaReply.Plan("Read the service, then the disk.", "test.read", "test.other"),
            PaReply.Call("call-1", "test.read"),
            PaReply.Empty(),
            PaReply.EvidenceRead("read-1", 0),
            PaReply.Call("call-2", "test.other"),
            PaReply.Text(PaScenarios.Final),
        ]));

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.DoesNotContain(run.Wire(2).Texts, text => text.StartsWith(rcOnePrefix, StringComparison.Ordinal)); // the request that drew the empty reply
        Assert.Contains(run.Wire(3).Texts, text => text.StartsWith(rcOnePrefix, StringComparison.Ordinal)); // the retry
        Assert.DoesNotContain(run.Wire(4).Texts, text => text.StartsWith(rcOnePrefix, StringComparison.Ordinal)); // answered by the read: satisfied, gone
        Assert.DoesNotContain(run.Wire(5).Texts, text => text.StartsWith(rcOnePrefix, StringComparison.Ordinal)); // the next step starts clean
        Assert.Contains("tool", run.Wire(4).Roles); // the continuation still carries the read call and its answer
    }

    [Fact]
    public async Task PRA_R4_AnUnansweredRecoveryInstruction_IsStillPresentForTheLastRequest_AndTheTaskStillFails()
    {
        // F1 stays: a retry that is itself unusable keeps its instruction in the request that drew it, and ends the task.
        var run = await PaCompose.RunAsync(PaScenarios.EmptyReplyTwice());

        Assert.Equal(AgentTaskStatus.Failed, run.State.Status);
        Assert.Equal(4, run.Bodies.Count); // planning, step 0, step 1, the one retry: no third attempt
        Assert.Contains(run.Wire(3).Texts, text => text.StartsWith("Your last reply was empty", StringComparison.Ordinal));
    }

    // ---- PRA-P1 ----

    [Fact]
    public async Task PRA_P1_TheOneToolRouting_ExplainsWhyOnlyThisStepsToolIsOffered_AndWhatIsNotAvailable()
    {
        var run = await PaCompose.RunAsync(PaScenarios.PlanExhaustedThenOrdinaryReplan());
        var system = run.Wire(1).System;

        Assert.Contains(
            "The single offered native tool is the current plan step's tool. Invoke it at most once, with the concrete arguments this step requires.",
            system, StringComparison.Ordinal);
        Assert.Contains("the tools of the later planned steps are offered in the turns of their own steps, so do not call them early or treat them as available yet", system, StringComparison.Ordinal);
        Assert.Contains("Do not invent or assume any other tool, URL or resource.", system, StringComparison.Ordinal);
        Assert.Equal(["test.read", "test.other"], PaInvariants.PlanTools(system)); // D-043 unchanged: two steps shown, one tool offered
        Assert.Equal(["test_read"], run.Wire(1).ToolNames);
    }

    [Fact]
    public async Task PRA_P1_TheControlFunctionVariant_CarriesTheSameExplanation()
    {
        var run = await PaCompose.RunAsync(PaScenarios.EvidenceRead());
        var wire = run.Wire(2);

        Assert.Contains("Invoke the operational tool at most once, with the concrete arguments this step requires. Only this step's tool is offered now:", wire.System, StringComparison.Ordinal);
        Assert.Equal(["test_other", "runtime_evidence_read"], wire.ToolNames); // the surface is unchanged: control beside, never instead of, the step tool
    }

    [Fact]
    public async Task PRA_P1_ThePromptStillNamesToolsByTheirCanonicalName_WhileTheProviderIsOfferedAnAlias()
    {
        // Deliberately unchanged (alias reconciliation was not necessary): the exact-name correction turn already covers a wrong name.
        var run = await PaCompose.RunAsync(PaScenarios.EvidenceRead());
        var wire = run.Wire(2);

        Assert.Contains("[test.other]", wire.System, StringComparison.Ordinal);
        Assert.DoesNotContain("test_other", wire.System, StringComparison.Ordinal);
        Assert.Equal(["test_other", "runtime_evidence_read"], wire.ToolNames);
    }

    // ---- PRA-V1 (unchanged by this work) ----

    [Fact]
    public async Task PRA_V1_AnAnswerThatContradictsAFact_IsKeptWhenTheModelCitesNoContradiction()
    {
        const string contradictory = "No event of source alpha was observed, so there is nothing to report.";
        var setup = PaScenarios.Grounding() with
        {
            Script =
            [
                PaReply.Plan("Read the counters.", "test.events"),
                PaReply.Call("call-1", "test.events"),
                PaReply.Text(contradictory),
                PaReply.Text("{\"contradictions\":[]}"),
            ],
        };
        var run = await PaCompose.RunAsync(setup);

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.Equal(contradictory, run.State.Steps[^1].Observation); // the typed fact says 3; the answer says none; the verdict decides
        Assert.Equal("Final response; evidence grounding check cited no contradiction", run.State.Steps[^1].Description);
        Assert.Equal(4, run.Bodies.Count); // no deterministic comparison exists: one model call is the whole check
    }
}
