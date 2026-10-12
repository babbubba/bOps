// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>
/// AF-05H regression fixtures equivalent to the three live runs (DETERMINISTIC FIXTURE on the real runner, broker and MAF projector;
/// synthetic data). LIVE-1 must stay cheap and simple, LIVE-2 must reach its later diagnostics and lose no fact to the disclosure,
/// LIVE-3 must never silently abandon its diagnostic suffix.
/// </summary>
// Shares a collection with GovernedSummarizationTests: its telemetry test listens globally for summary activities.
[Collection("MAF summarization telemetry")]
public sealed class LiveRegressionTests
{
    private static AgentRunnerOptions Unknown() => RegressionRunner.BaseOptions(null);

    [Fact]
    public async Task Live1_ShortDiagnostic_StaysCheapAndSimple()
    {
        var run = await LiveRegression.RunAsync(LiveRegression.ShortDiagnostic(), "unknown profile", Unknown());

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.Single(run.State.Plans);
        Assert.Equal(0, run.PrematureFinals);
        Assert.Equal(0, run.ReadsServed);
        Assert.Equal(0, run.Calls(CallPurpose.Replanning));
        Assert.Equal(0, run.Calls(CallPurpose.Summarization));
        Assert.Equal(0, run.Calls(CallPurpose.Disclosure));
        Assert.Equal(["system.cpu", "system.info", "system.memory"], run.SucceededTools);
        Assert.Equal(run.Script.FinalText, run.FinalText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(32768)]
    [InlineData(65536)]
    [InlineData(131072)]
    public async Task Live1_ShortDiagnostic_IsUnchangedAtAnyWindow(int? window)
    {
        var run = await LiveRegression.RunAsync(LiveRegression.ShortDiagnostic(), $"{window}", RegressionRunner.BaseOptions(window));

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.Equal(0, run.PrematureFinals);
        Assert.Equal(0, run.Calls(CallPurpose.Replanning));
        Assert.Equal(0, run.Calls(CallPurpose.Summarization));
        Assert.All(run.Projector.Projections, projection => Assert.Equal(0, projection.Stats.CompactedToolGroups));
    }

    [Fact]
    public async Task Live2_LongDiagnostic_ReachesTheLaterDiagnostics_ReplansThePrematureFinal_AndKeepsTheExactCount()
    {
        var run = await LiveRegression.RunAsync(LiveRegression.LongStabilityDiagnostic(), "unknown profile", Unknown());

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        // EvidenceRead bounded and soft; the later tools still ran.
        Assert.Equal(4, run.ReadsServed);
        Assert.True(run.Model.ReadsDeniedByExhaustion > 0);
        Assert.Contains("system.cpu", run.SucceededTools);
        Assert.Contains("system.memory", run.SucceededTools);
        Assert.Contains("storage.health", run.SucceededTools);
        // The superfluous last step is dropped by an empty replan, not by accepting the premature answer.
        Assert.Equal(1, run.PrematureFinals);
        Assert.Equal(1, run.Calls(CallPurpose.Replanning));
        Assert.Empty(run.State.Plans[^1].Steps);
        Assert.DoesNotContain("system.time", run.SucceededTools);
        var projected = ExecutionPlanProjector.Project(run.State, executing: false)!;
        Assert.Equal(ProjectedStepStatus.Superseded, projected.Revisions[0].Steps[^1].Status);
        Assert.DoesNotContain(projected.Revisions.SelectMany(revision => revision.Steps), step => step.Status == ProjectedStepStatus.Failed);
        // Deterministic disclosure: no restatement call, the exact original count survives, the section is appended.
        Assert.Equal(0, run.Calls(CallPurpose.Disclosure));
        Assert.StartsWith(run.Script.FinalText + "\n\n## Evidence limitations\n", run.FinalText, StringComparison.Ordinal);
        Assert.Contains("26 × 0x193", run.FinalText, StringComparison.Ordinal);
        Assert.DoesNotContain("27 × 0x193", run.FinalText, StringComparison.Ordinal);
        // The grounding check saw the disclosed final.
        var check = run.Model.Requests[run.Model.Log.FindIndex(call => call.Purpose == CallPurpose.GroundingCheck)];
        Assert.Equal(run.FinalText, check.History[^2].Content);
    }

    [Fact]
    public async Task Live3_ThePrematureFinalNeverAbandonsTheDiagnosticSuffix()
    {
        var run = await LiveRegression.RunAsync(LiveRegression.FocusedCrashDiagnosis(), "unknown profile", Unknown());

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        // The correction sequence is today's ADR-0050 one (independent budgets; the correction-conflict rule, H3, is deferred):
        // argument failure, semantic mismatch, semantic correction, mismatch again, replan; then the valid call.
        var crashSteps = run.State.Steps.Where(step => step.ToolCall?.ToolName == "system.crashes").ToList();
        Assert.Equal(
            [PlannedStepExecutionClassification.ArgumentValidationFailure, PlannedStepExecutionClassification.SemanticMismatch,
                PlannedStepExecutionClassification.SemanticMismatch, PlannedStepExecutionClassification.Matched],
            crashSteps.Select(step => step.ExecutionClassification));
        Assert.Equal((2, 1), (run.Tools["system.crashes"].Invocations, run.Tools["system.crashes"].Successes));
        // Two replans: after the second mismatch, then the H2 re-evaluation of the premature final.
        Assert.Equal(2, run.Calls(CallPurpose.Replanning));
        Assert.Equal(1, run.PrematureFinals);
        Assert.Equal(
            ["process.list", "system.cpu", "system.crashes", "system.events", "system.memory", "system.stability"],
            run.SucceededTools);
        Assert.Equal(run.Script.FinalText, run.FinalText[..run.Script.FinalText.Length]);
        Assert.DoesNotContain("cannot be determined", run.FinalText, StringComparison.Ordinal);
    }
}
