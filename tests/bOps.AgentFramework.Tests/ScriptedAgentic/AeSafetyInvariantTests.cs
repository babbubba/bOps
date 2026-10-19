// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.AgentFramework.Tests.PromptAssembly;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Xunit.Sdk;

namespace bOps.AgentFramework.Tests.ScriptedAgentic;

/// <summary>
/// AE-1, kind S — SAFETY AND RUNTIME INVARIANTS. These are properties that must hold in every scenario, including the ones that reproduce a
/// prompt defect; they are not "expected defects" and no prompt change may loosen them. Each has a negative control (a deliberately
/// corrupted copy of a real run) that proves the check can fail.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed class AeSafetyInvariantTests
{
    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task EveryInvariant_HoldsInEveryScenario(string id)
    {
        var run = await AeCases.Get(id).Run();

        AeInvariants.AssertAll(run);
    }

    public static TheoryData<string> CaseIds => AeCases.Ids;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EveryInvariant_HoldsWhenTheRetryBudgetIsChanged(int retries)
    {
        var run = await AeScenarios.T05LengthThenGreaterThan(retries);

        AeInvariants.AssertAll(run, new AgentRunnerOptions { EmptyFinalResponseRetries = retries });
    }

    [Fact]
    public async Task NoInventedToolIsEverRegisteredOrExecuted_WhateverTheScenario()
    {
        foreach (var item in AeCases.All)
        {
            var run = await item.Run();
            Assert.DoesNotContain(AeScenarios.AbsentCapability, run.Pa.Tools.Keys);
            Assert.DoesNotContain(AeScenarios.AbsentCapability, run.Metrics.ToolsExecuted);
            Assert.DoesNotContain(run.Audit<ToolCallAuditEvent>(), evt => evt.Tool == AeScenarios.AbsentCapability && evt.EffectiveTimeout is not null);
        }
    }

    [Fact]
    public async Task NoCompletedTask_HasAnUnusableFinal_AndNoUnusableFinalIsPersistedAsAnAnswer()
    {
        foreach (var item in AeCases.All)
        {
            var run = await item.Run();
            if (run.State.Status == AgentTaskStatus.Completed)
            {
                Assert.True(AeObserver.HasAnswerContent(run.FinalText), $"{item.Id}: Completed with no usable final");
            }
            else
            {
                Assert.DoesNotContain(run.State.Steps, FinalResponse.IsFinalStep);
            }
        }
    }

    [Fact]
    public async Task FinishReason_UsabilityOfTheFinal_AndDiagnosticCorrectness_AreThreeSeparateFacts()
    {
        // The last reply of each run ended with finish_reason "stop"; whether it was usable, and whether the run answered the question, differ.
        var rows = new List<(string Id, string? Finish, bool Usable, AgentTaskStatus Status, AeOutcome Diagnostic)>();
        foreach (var id in new[] { "AE1-T05", "AE1-T05-CLEAN", "AE1-T08-A" })
        {
            var item = AeCases.Get(id);
            var run = await item.Run();
            var finish = run.State.Steps.Where(step => step.ModelCalls is { Count: > 0 }).SelectMany(step => step.ModelCalls!).Last().FinishReason;
            rows.Add((id, finish, AeObserver.HasAnswerContent(run.FinalText), run.State.Status, item.Evaluate(run).Outcome));
        }

        Assert.Equal(
        [
            ("AE1-T05", "stop", false, AgentTaskStatus.Failed, AeOutcome.Partial), // ended normally, answered nothing: not Completed
            ("AE1-T05-CLEAN", "stop", true, AgentTaskStatus.Completed, AeOutcome.Pass), // ended normally, answered, objective met
            ("AE1-T08-A", "stop", true, AgentTaskStatus.Completed, AeOutcome.BaselineDefect), // ended normally, usable, still wrong: objective unmet
        ], rows);
    }

    // ---- negative controls: each corrupted copy must be caught ----

    private static AeRun Corrupt(AeRun run, TaskState? state = null, AeMetrics? metrics = null, RecordingAuditSink? audit = null,
        IReadOnlyList<ModelRequest>? seen = null, IReadOnlyList<AeStep>? steps = null) => new()
        {
            Id = run.Id,
            Script = run.Script,
            Calls = run.Calls,
            Steps = steps ?? run.Steps,
            PlanDiffs = run.PlanDiffs,
            HostAudit = run.HostAudit,
            Metrics = metrics ?? run.Metrics,
            Pa = new PaRun
            {
                State = state ?? run.Pa.State,
                Bodies = run.Pa.Bodies,
                Seen = seen ?? run.Pa.Seen,
                Unconsumed = run.Pa.Unconsumed,
                Tools = run.Pa.Tools,
                Audit = audit ?? run.Pa.Audit,
            },
        };

    [Fact]
    public async Task Control_ACompletedTaskThatStillOwesPlannedSteps_IsCaught()
    {
        var loop = await AeScenarios.T02NoProgressLoop();
        var forged = Corrupt(loop, state: loop.State with { Status = AgentTaskStatus.Completed });

        Assert.ThrowsAny<XunitException>(() => AeInvariants.AssertCompletedMeansAnAnswerAndNoOwedStep(forged));
    }

    [Fact]
    public async Task Control_AContentFreeFinalPersistedAsTheAnswerOfACompletedTask_IsCaught()
    {
        var failed = await AeScenarios.T05LengthThenGreaterThan();
        var forgedSteps = new List<PlanStep>(failed.State.Steps.Take(1)) { new(1, FinalResponse.Marker, null, null, ">", 0) };
        var forged = Corrupt(failed, state: failed.State with { Status = AgentTaskStatus.Completed, Steps = forgedSteps });

        Assert.ThrowsAny<XunitException>(() => AeInvariants.AssertCompletedMeansAnAnswerAndNoOwedStep(forged));
    }

    [Fact]
    public async Task Control_AToolOutsideTheRegistryThatRan_IsCaught()
    {
        var run = await AeScenarios.T08ProposedInProse();
        var forged = Corrupt(run, metrics: run.Metrics with { ToolsExecuted = [.. run.Metrics.ToolsExecuted, AeScenarios.AbsentCapability] });

        Assert.ThrowsAny<XunitException>(() => AeInvariants.AssertOnlyGovernedRegisteredToolsExecuted(forged));
    }

    [Fact]
    public async Task Control_AnExecutedCallThatPolicyHadForbidden_IsCaught()
    {
        var policyRun = await AeScenarios.T02ContinuationUnderPolicy();
        var run = await AeScenarios.T02CleanRun();
        var executed = run.Audit<ToolCallAuditEvent>().First(evt => evt.Tool == "test.other");
        var forged = new RecordingAuditSink();
        foreach (var evt in run.Pa.Audit.Events)
        {
            await forged.WriteAsync(evt);
        }

        await forged.WriteAsync(policyRun.Audit<PolicyDecisionAuditEvent>().Single() with { Tool = executed.Tool, StepIndex = executed.StepIndex });

        Assert.ThrowsAny<XunitException>(() => AeInvariants.AssertOnlyGovernedRegisteredToolsExecuted(Corrupt(run, audit: forged)));
    }

    [Fact]
    public async Task Control_ToolOutputThatReachedASystemPrompt_IsCaught()
    {
        var run = await AeScenarios.T02CleanRun();
        var promoted = run.Pa.Seen.Select(request => request.SystemPrompt.Contains("Plan (revision", StringComparison.Ordinal)
            ? request with { SystemPrompt = request.SystemPrompt + "\nservice alpha: running; restarts 0; uptime 4d" }
            : request).ToList();

        Assert.ThrowsAny<XunitException>(() => AeInvariants.AssertNoToolOrKnowledgeTextBecameInstruction(Corrupt(run, seen: promoted)));
    }

    [Fact]
    public async Task Control_AReplanBudgetThatWasExceeded_IsCaught()
    {
        var loop = await AeScenarios.T02NoProgressLoop();

        Assert.ThrowsAny<XunitException>(() => AeInvariants.AssertBounded(loop, new AgentRunnerOptions { MaxReplans = 1 }));
    }

    [Fact]
    public async Task Control_ACursorThatDisagreesWithTheRequestThatOfferedTheStep_IsCaught()
    {
        var run = await AeScenarios.T02CleanRun();
        var shifted = run.Steps.Select(step => step.PlannedIndex == 1 ? step with { PlannedIndex = 2 } : step).ToList();

        Assert.ThrowsAny<XunitException>(() => AeInvariants.AssertCursorsAgree(Corrupt(run, steps: shifted)));
    }

    [Fact]
    public async Task Control_AStepThatWentMissingFromThePersistedState_IsCaught()
    {
        var run = await AeScenarios.T02CleanRun();
        var gap = run.State.Steps.Where(step => step.Index != 1).ToList();

        Assert.ThrowsAny<XunitException>(() => AeInvariants.AssertNothingPersistedWasLost(Corrupt(run, state: run.State with { Steps = gap })));
    }

    [Fact]
    public async Task Control_AHiddenModelCall_IsCaught()
    {
        var run = await AeScenarios.T02CleanRun();

        Assert.ThrowsAny<XunitException>(() => AeInvariants.AssertBounded(Corrupt(run, metrics: run.Metrics with { ModelCalls = run.Metrics.ModelCalls + 1 }), new AgentRunnerOptions()));
    }
}
