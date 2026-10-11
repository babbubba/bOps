// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>
/// AF-05 regression gate (ADR-0052). DETERMINISTIC FIXTURE evidence only: the real runner, broker and MAF projector run against a
/// scripted model and fixed tool results with the real package fact builders. These tests make no claim about any live provider.
/// </summary>
// Shares a collection with GovernedSummarizationTests: its telemetry test listens globally for summary activities.
[Collection("MAF summarization telemetry")]
public sealed class MafRegressionTests
{
    private static SummarizationOptions SummaryOn { get; } = new() { Enabled = true };

    private static RegressionConfiguration Maf(string name, int window, SummarizationOptions? summary = null, bool dedicated = false) =>
        new(name, RegressionRunner.BaseOptions(window, summarization: summary), DedicatedSummarizer: dedicated);

    [Theory]
    [InlineData(null)]
    [InlineData(12288)]
    [InlineData(16384)]
    [InlineData(32768)]
    public async Task C508_EvidenceReadExhaustionIsSoft_AndTheLaterDiagnosticsRun(int? window)
    {
        var configuration = window is null
            ? new RegressionConfiguration("unknown-profile", RegressionRunner.BaseOptions(null))
            : Maf($"maf-{window}", window.Value);

        var run = await RegressionRunner.RunAsync(RegressionScenarios.C508Equivalent(), configuration);

        // Four served reads is the per-call allowance; the scripted model wanted six. The task must not die from that.
        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.Equal(4, run.EvidenceReadServed);
        Assert.True(run.Model.ReadsDeniedByExhaustion > 0, "the scripted model must have wanted more reads than the allowance");
        Assert.Equal(7, run.OperationalSteps);
        foreach (var tool in new[] { "system.cpu", "system.memory", "storage.health" })
        {
            Assert.Equal(1, run.Tools.Single(t => t.Manifest.Name == tool).ExecutionCount);
        }

        Assert.DoesNotContain(run.State.Steps, step => step.Observation?.Contains("Evidence read limit exceeded", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(10240)]
    [InlineData(12288)]
    [InlineData(16384)]
    [InlineData(32768)]
    public async Task C508_ConfiguredProfile_KeepsEveryAuthoritativeFactAndLimitationVisible(int window)
    {
        var run = await RegressionRunner.RunAsync(RegressionScenarios.C508Equivalent(), Maf($"maf-{window}", window));

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.All(run.Model.FinalFactVisibility, kv => Assert.True(kv.Value, $"expected fact not visible to the final call: {kv.Key}"));
        Assert.All(run.Model.FinalLimitationVisibility, kv => Assert.True(kv.Value, $"expected limitation not visible: {kv.Key}"));
        Assert.Contains("Evidence limitations", run.State.Steps[^1].Observation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10240)]
    [InlineData(16384)]
    [InlineData(32768)]
    public async Task C508_WithSummarization_KeepsLimitationsAndAGroundedAnswer_AndNeverGainsAuthority(int window)
    {
        var run = await RegressionRunner.RunAsync(RegressionScenarios.C508Equivalent(), Maf($"maf-{window}-summary", window, SummaryOn));

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.All(run.Model.FinalLimitationVisibility, kv => Assert.True(kv.Value, $"expected limitation not visible: {kv.Key}"));
        Assert.Contains("Evidence limitations", run.State.Steps[^1].Observation, StringComparison.Ordinal);
        // Facts stay owned by the persisted ledger: the grounding block of the final call still lists the newest facts, whatever a
        // summary says, and no summary text is ever persisted as a step.
        Assert.Contains("EvidenceGrounding/v1", run.Model.FinalRequest!.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(run.State.Steps, step => step.Observation?.Contains("Earlier tool results were reviewed.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task C508_ProjectionNeverChangesRawPersistedEvidenceOrTheToolSurface()
    {
        var scenario = RegressionScenarios.C508Equivalent();
        var run = await RegressionRunner.RunAsync(scenario, Maf("maf-10k", 10240, SummaryOn));

        foreach (var tool in scenario.Tools)
        {
            var persisted = run.State.Steps.Single(step => step.ToolCall?.ToolName == tool.Name);
            Assert.Equal(tool.Result.Output, persisted.Result!.Output);
            Assert.Equal(tool.Result.Facts, persisted.Result.Facts);
        }

        // Exact step-scoped routing: a step request offers at most the one planned tool plus the runtime control function.
        var planned = scenario.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var request in run.Model.Requests.Where(r => r.AvailableTools.Count > 0))
        {
            Assert.InRange(request.AvailableTools.Count, 1, 2);
            Assert.True(request.AvailableTools.Count(tool => planned.Contains(tool.Name)) <= 1);
            Assert.All(request.AvailableTools, tool => Assert.True(planned.Contains(tool.Name) || tool.Name == "runtime.evidence_read"));
        }
    }

    [Fact]
    public async Task ShortDiagnostic_UnknownProfile_KeepsEveryFact_AndNeedsNoInventedCapacity()
    {
        // The shipped default for an unprofiled model (for example openrouter/free): the structural bound alone, no window invented.
        var run = await RegressionRunner.RunAsync(
            RegressionScenarios.ShortDiagnostic(), new RegressionConfiguration("unknown", RegressionRunner.BaseOptions(null, summarization: SummaryOn)));

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.Empty(run.SummaryCalls);
        Assert.All(run.Projector!.Projections, p => Assert.Null(p.Stats.MaxContextTokens));
        Assert.All(run.Projector.Projections, p => Assert.Equal("unknown-structural", p.Stats.ProfileSource));
        Assert.All(run.Model.FinalFactVisibility, kv => Assert.True(kv.Value, kv.Key));
    }

    [Theory]
    [InlineData(16384)]
    [InlineData(32768)]
    public async Task ShortDiagnostic_PaysNoSummaryAndNoCompaction(int window)
    {
        var withSummary = await RegressionRunner.RunAsync(RegressionScenarios.ShortDiagnostic(), Maf("maf-summary-on", window, SummaryOn));
        var without = await RegressionRunner.RunAsync(RegressionScenarios.ShortDiagnostic(), Maf("maf-summary-off", window));

        Assert.Equal(AgentTaskStatus.Completed, withSummary.State.Status);
        Assert.Empty(withSummary.SummaryCalls);
        Assert.All(withSummary.Projector!.Projections, p => Assert.Equal(0, p.Stats.CompactedToolGroups));
        Assert.All(withSummary.Projector.Projections, p => Assert.Equal(p.Stats.HistoricalGroupsBefore, p.Stats.HistoricalGroupsAfter));
        Assert.Equal(without.AllInTokens, withSummary.AllInTokens);
        Assert.All(withSummary.Model.FinalFactVisibility, kv => Assert.True(kv.Value, kv.Key));
        Assert.Equal(0, withSummary.EvidenceReadServed);
    }

    [Fact]
    public async Task C508_ProfileTooSmallForTheProtectedReadsFailsSafelyAsClassifiedContextOverflow()
    {
        var run = await RegressionRunner.RunAsync(RegressionScenarios.C508Equivalent(), Maf("maf-8k", 8192));

        Assert.Equal(AgentTaskStatus.Failed, run.State.Status);
        Assert.Equal(TaskTerminalKind.ModelFailure, run.State.TerminalReason?.Kind);
        Assert.Equal(ModelFailureKind.ContextOverflow, run.State.TerminalReason?.FailureKind);
        Assert.Contains("exceed the selected model profile's input capacity", run.State.Steps[^1].Observation ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedResult_IsOfferedWithItsStableEvidenceIdAfterMafCompaction()
    {
        // AF-05: the limitations digest names the step of a shortened result, but only the runtime-authored evidence index carries
        // the stable id and lengths the EvidenceRead/v1 text directive (the only read a replan has) needs once the HARDEN-8
        // compact records are gone.
        var big = new ScenarioTool("system.big", ToolCallResult.Success(new string('x', 12_000)) with { Completeness = ToolResultCompleteness.Complete });
        var cpu = new ScenarioTool("system.cpu", ToolCallResult.Success("{\"usagePercent\":4.7}") with { Completeness = ToolResultCompleteness.Complete });
        var scenario = new RegressionScenario(
            "complete-truncated", "Check the machine.", [big, cpu],
            new Dictionary<string, IReadOnlyList<string>> { ["system.cpu"] = ["system.big"] }, [], []);

        var run = await RegressionRunner.RunAsync(scenario, Maf("maf-16k", 16384));

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.Equal(0, run.Model.UnaddressableReads);
        Assert.Equal(1, run.EvidenceReadServed);
        var stepPrompt = run.Model.Requests.First(request => request.AvailableTools.Any(tool => tool.Name == "system.cpu")).SystemPrompt;
        Assert.Contains("<<<BOPS_EVIDENCE_INDEX>>>", stepPrompt, StringComparison.Ordinal);
        Assert.Contains("step 0 system.big: id ev1:" + run.State.Id.ToString("N") + ":0", stepPrompt, StringComparison.Ordinal);
    }
}
