// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.AgentFramework.Tests.PromptAssembly;
using bOps.AgentFramework.Tests.Regression;

namespace bOps.AgentFramework.Tests.ScriptedAgentic;

/// <summary>
/// AE-1, kind D — DIAGNOSTIC EVALUATION. A rubric reads a finished run and says whether the scenario's OBJECTIVE was met
/// (<c>PASS / PARTIAL / FAIL / BASELINE_DEFECT / NOT_RUN / NOT_COVERED</c>), independently of whether the characterization tests are green. Each
/// case pins the verdict <see cref="AeCase.Today"/> as observed; the verdicts marked <c>BaselineDefect</c> are the measurement that
/// <c>PROMPT-ROUTING-FINALIZATION</c> is expected to move (<see cref="AeCase.Desired"/>), and the pin is rewritten in that change.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed class AeDiagnosticTests
{
    public static TheoryData<string> CaseIds => AeCases.Ids;

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task TheVerdictOfEveryCase_IsTheObservedBaseline(string id)
    {
        var item = AeCases.Get(id);
        var run = await item.Run();

        var verdict = item.Evaluate(run);

        Assert.Equal(item.Today, verdict.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        Assert.Equal(verdict.Outcome == AeOutcome.BaselineDefect, verdict.Attributions.Count > 0); // a defect is always attributed to a layer
    }

    [Fact]
    public async Task AGreenCharacterizationTestIsNeverADiagnosticSuccess()
    {
        foreach (var item in AeCases.All.Where(item => item.Today == AeOutcome.BaselineDefect))
        {
            var verdict = item.Evaluate(await item.Run());
            Assert.NotEqual(AeOutcome.Pass, verdict.Outcome);
            Assert.NotEmpty(verdict.Attributions);
        }
    }

    [Fact]
    public async Task TheRubricsCanSayPass_WhenTheRunIsHealthy()
    {
        foreach (var item in AeCases.All.Where(item => item.IsControl))
        {
            var run = await item.Run();
            Assert.Equal(AeOutcome.Pass, item.Evaluate(run).Outcome);
            Assert.True(item.Evaluate(run).ObjectiveMet);
        }
    }

    [Fact]
    public async Task TheDefectsAreAttributedToTheirOwnLayer()
    {
        var layers = new Dictionary<string, string>
        {
            ["AE1-T02"] = "PRA-R3", ["AE1-T02-LOOP"] = "PRA-R3", ["AE1-T02-EMPTY"] = "PRA-R3", ["AE1-T02-POLICY"] = "PRA-R3", ["AE1-T10"] = "PRA-R3",
            ["AE1-T06-B"] = "PRA-R2", ["AE1-T08-A"] = "PRA-R2",
            ["AE1-P1"] = "PRA-P1",
        };
        foreach (var (id, layer) in layers)
        {
            var item = AeCases.Get(id);
            var verdict = item.Evaluate(await item.Run());
            Assert.Equal(AeOutcome.BaselineDefect, verdict.Outcome);
            Assert.Single(verdict.Attributions);
            Assert.StartsWith(layer, verdict.Attributions[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheObjectiveCanBeMetWhileTheBaselineDefectIsStillPresent()
    {
        // T02 and T10 deliver every planned check and a final, at the price of the rejected finals: the defect is cost, not a wrong answer.
        foreach (var id in new[] { "AE1-T02", "AE1-T10", "AE1-P1" })
        {
            var item = AeCases.Get(id);
            var verdict = item.Evaluate(await item.Run());
            Assert.True(verdict.ObjectiveMet, id);
            Assert.Equal(AeOutcome.BaselineDefect, verdict.Outcome);
        }

        // T02-EMPTY, T05, T06-B and T08-A end Completed or Failed with the objective unmet.
        foreach (var id in new[] { "AE1-T02-EMPTY", "AE1-T05", "AE1-T06-B", "AE1-T08-A", "AE1-T02-LOOP" })
        {
            var item = AeCases.Get(id);
            Assert.False(item.Evaluate(await item.Run()).ObjectiveMet, id);
        }
    }

    [Fact]
    public async Task TheOutcomesWithNoKnownDefectDoNotClaimTheObjective_WhenItIsUnreachable()
    {
        // T06-A and T08-B stay PARTIAL: the capability or permission is absent, nothing is hidden, and no prompt defect is blamed. T05 is PARTIAL since PRA-R4: the recovery text is right, the remaining cause is the scripted model's own reply.
        foreach (var id in new[] { "AE1-T05", "AE1-T06-A", "AE1-T08-B" })
        {
            var item = AeCases.Get(id);
            var verdict = item.Evaluate(await item.Run());
            Assert.Equal(AeOutcome.Partial, verdict.Outcome);
            Assert.False(verdict.ObjectiveMet);
            Assert.Empty(verdict.Attributions);
        }
    }

    [Fact]
    public void TheCatalogNamesEveryScenarioOfTheBrief()
    {
        var ids = AeCases.All.Select(item => item.Id).ToList();
        foreach (var required in new[] { "AE1-T02", "AE1-T05", "AE1-T06-A", "AE1-T06-B", "AE1-T08-A", "AE1-T08-B", "AE1-T10" })
        {
            Assert.Contains(required, ids);
        }

        Assert.All(AeCases.All, item => Assert.False(string.IsNullOrWhiteSpace(item.Desired)));
    }
}

/// <summary>AE-1: the same script gives the same sequence, the same counts and the same provider bodies (no reliance on time, order or chance).</summary>
[Collection("MAF summarization telemetry")]
public sealed class AeDeterminismTests
{
    public static TheoryData<string> CaseIds => AeCases.Ids;

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task TwoRunsOfTheSameScript_AreIdentical(string id)
    {
        var first = await AeCases.Get(id).Run();
        var second = await AeCases.Get(id).Run();

        Assert.Equal(first.Calls.Select(AeInvariants.CallLine), second.Calls.Select(AeInvariants.CallLine));
        Assert.Equal(first.Steps.Select(AeInvariants.StepLine), second.Steps.Select(AeInvariants.StepLine));
        Assert.Equal(AeObserver.RenderCalls(first), AeObserver.RenderCalls(second));
        Assert.Equal(first.Metrics.ToolsExecuted, second.Metrics.ToolsExecuted);
        Assert.Equal(
            (first.Metrics.ModelCalls, first.Metrics.PrematureFinals, first.Metrics.ReplansCharged, first.Metrics.ReplansSkipped, first.Metrics.RecoveryTurns,
                first.Metrics.EvidenceReads, first.Metrics.KnowledgeReads, first.Metrics.FinalStatus, first.Metrics.Terminal, first.Metrics.SyntheticTokens),
            (second.Metrics.ModelCalls, second.Metrics.PrematureFinals, second.Metrics.ReplansCharged, second.Metrics.ReplansSkipped, second.Metrics.RecoveryTurns,
                second.Metrics.EvidenceReads, second.Metrics.KnowledgeReads, second.Metrics.FinalStatus, second.Metrics.Terminal, second.Metrics.SyntheticTokens));
        Assert.Equal(first.Pa.Bodies.Select(PaGolden.Normalize), second.Pa.Bodies.Select(PaGolden.Normalize)); // byte-identical provider bodies
    }
}

/// <summary>
/// AE-1 and PA-1 share one request-assembly path; this ties the two by the PA-1 golden files themselves: the standing prompt, the routing
/// sentence and the plan-complete paragraph that the goldens froze are the ones the AE-1 requests carry.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed class AePa1GoldenLinkTests
{
    private static string GoldenSystem(string file, int pick)
    {
        var path = Path.Combine(PaGolden.Directory, file + ".json");
        var document = JsonNode.Parse(File.ReadAllText(path, new UTF8Encoding(false)))!;
        return document["requests"]![pick]!["body"]!["messages"]![0]!["content"]!.GetValue<string>();
    }

    private static string Standing(string systemPrompt) => systemPrompt[..systemPrompt.IndexOf("Plan (revision", StringComparison.Ordinal)];

    private const string RoutingSentence = "while planned steps remain, a reply without a tool call is not taken as the final answer and only makes the runtime re-evaluate the plan.";

    [Fact]
    public async Task EveryStepRequestOfAe1_StartsWithTheStandingPromptFrozenByPa1_AndCarriesItsRoutingSentenceWhileAStepIsPending()
    {
        var golden = Standing(GoldenSystem("pa1-03-step-pending-tool", 0).ReplaceLineEndings("\n"));
        foreach (var item in AeCases.All.Where(item => item.Id != "AE1-T10")) // the knowledge case adds its own control block, pinned by PA1-05
        {
            var run = await item.Run();
            foreach (var call in run.Calls.Where(call => call.Purpose is CallPurpose.Step or CallPurpose.EvidenceReadContinuation && call.Revision is not null))
            {
                var system = run.Pa.Seen[call.Index].SystemPrompt.ReplaceLineEndings("\n");
                Assert.StartsWith(golden, system, StringComparison.Ordinal);
                if (call.Operational.Count == 1)
                {
                    Assert.Contains(RoutingSentence, system, StringComparison.Ordinal); // PRA-R3: the rewritten sentence is on every pending step
                }
            }
        }
    }

    [Fact]
    public async Task ThePlanCompleteRequestsOfAe1_CarryTheParagraphThatPa1Froze()
    {
        var golden = GoldenSystem("pa1-06-plan-exhausted", 0).ReplaceLineEndings("\n");
        var start = golden.IndexOf("Every planned step above is complete", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var paragraph = golden[start..golden.IndexOf('\n', start)];

        foreach (var (run, index) in new[] { (await AeScenarios.T08ProposedInProse(), 2), (await AeScenarios.T06CheckOnlyDescribed(), 3), (await AeScenarios.T02CleanRun(), 4) })
        {
            Assert.Contains(paragraph, run.Pa.Seen[index].SystemPrompt.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Empty(run.Calls[index].Operational); // and no operational tool was offered, as in the golden
        }
    }

    [Fact]
    public async Task TheRecoveryTurnOfAe1T05_IsTheTextPa1Froze_AndSitsInTheSamePositionOfTheRequest()
    {
        var run = await AeScenarios.T05LengthThenGreaterThan();
        var golden = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(PaGolden.Directory, "pa1-08b-recovery-rc2-exhausted.json"), new UTF8Encoding(false)))!;
        var messages = golden["requests"]![1]!["body"]!["messages"]!.AsArray();
        var rc2 = messages[^1]!["content"]!.GetValue<string>();

        var request = run.Pa.Seen[4];
        Assert.Equal(rc2, request.History.Single(turn => turn.Content?.StartsWith("Your last reply reached the output limit", StringComparison.Ordinal) == true).Content);
    }
}
