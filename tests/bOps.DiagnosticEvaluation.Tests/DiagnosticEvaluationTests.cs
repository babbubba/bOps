// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace bOps.DiagnosticEvaluation.Tests;

/// <summary>BOPS-0126 P1: each known diagnostic defect is found by the deterministic scorers and reported through the MAF checks.</summary>
public sealed class DiagnosticEvaluationTests
{
    private static bool? Check(JsonObject result, string name) =>
        result["checks"]!.AsArray().OfType<JsonObject>().Single(c => (string?)c["name"] == name)["passed"]?.GetValue<bool>();

    private static int Int(JsonNode? node) => node!.GetValue<int>();

    [Fact]
    public async Task ACorrectAnswer_PassesEveryCheck()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer:
            "Sono presenti 17 BSOD reali (bugcheck) e 27 live dump, che non sono BSOD. Arresti inattesi: 60. " +
            "Kernel-Power 41: 30 eventi (2026-09-16 → 2026-10-25) negli ultimi 180 giorni.");

        var result = await campaign.EvaluateAsync("R01");

        Assert.Equal(0, Int(result["checksFailed"]));
        Assert.Equal(0, Int(result["m2Counts"]!["wrongFamily"]));
        Assert.Equal(0, Int(result["m2Counts"]!["unverified"]));
        Assert.True(Int(result["m2Counts"]!["exact"]) >= 4);
        Assert.Equal(1.0, result["m2Counts"]!["accuracy"]!.GetValue<double>());
    }

    [Fact]
    public async Task ALiveDumpCountGivenAsBsods_IsAWrongFamilyAndFailsTheClassificationCheck()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer: "Il PC ha avuto 27 BSOD negli ultimi mesi.");

        var result = await campaign.EvaluateAsync("R01");

        Assert.Equal(1, Int(result["m1Classification"]!["liveDumpCountAsCrash"]));
        Assert.Equal(1, Int(result["m2Counts"]!["wrongFamily"]));
        Assert.False(Check(result, "live-dump-kept-distinct"));
        Assert.False(Check(result, "counts-consistent"));
    }

    [Fact]
    public async Task ASentenceThatCallsLiveDumpsCrashes_IsFlagged_ButANegatedOneIsNot()
    {
        using var wrong = new SyntheticCampaign();
        wrong.AddRun("R01", answer: "I live dump 0x193 sono BSOD che hanno causato i riavvii del PC.");
        var flagged = await wrong.EvaluateAsync("R01");
        Assert.Single(flagged["m1Classification"]!["liveDumpSentencesAsCrash"]!.AsArray());
        Assert.False(Check(flagged, "live-dump-kept-distinct"));

        using var right = new SyntheticCampaign();
        right.AddRun("R01", answer: "I live dump 0x193 non sono BSOD e non causano riavvii.");
        var clean = await right.EvaluateAsync("R01");
        Assert.Empty(clean["m1Classification"]!["liveDumpSentencesAsCrash"]!.AsArray());
        Assert.True(Check(clean, "live-dump-kept-distinct"));
    }

    [Fact]
    public async Task ACountTheEvidenceDoesNotHold_IsUnverified()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer: "Sono stati registrati 23 BSOD.");

        var result = await campaign.EvaluateAsync("R01");

        Assert.Equal(1, Int(result["m2Counts"]!["unverified"]));
        Assert.Equal(0.0, result["m2Counts"]!["accuracy"]!.GetValue<double>());
        Assert.False(Check(result, "counts-consistent"));
    }

    [Fact]
    public async Task IdentifiersDatesDurationsAndSeries_AreNotCounted()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer:
            "Evento Kernel-Power 41 dal 2026-09-16 al 2026-10-25, event id 193, burst di ~2 secondi, " +
            "arresti inattesi per mese 2, 2, 10, 22, 14, 10.");

        var result = await campaign.EvaluateAsync("R01");

        Assert.Empty(result["m2Counts"]!["findings"]!.AsArray());
        Assert.Equal(0, Int(result["checksFailed"]));
    }

    [Fact]
    public async Task ANumberFromTheToolOutputOrASumOfIt_IsInEvidence_NotUnverified()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", output: "{\"groups\":[{\"source\":\"WER\",\"count\":442},{\"source\":\"WER queue\",\"count\":44}]}",
            answer: "Report WER: 486 applicazioni in totale (442 archiviati, 44 in coda).");

        var result = await campaign.EvaluateAsync("R01");

        var findings = result["m2Counts"]!["findings"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(0, Int(result["m2Counts"]!["unverified"]));
        Assert.Equal("derived", (string?)findings.Single(f => Int(f["value"]) == 486)["verdict"]);
        Assert.Equal("inEvidence", (string?)findings.Single(f => Int(f["value"]) == 442)["verdict"]);
    }

    [Fact]
    public async Task AnEntryCitedButNeverLoaded_IsReported_AndFailsTheKnowledgeCheck()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer: "Secondo K3 i live dump non sono BSOD: 27 live dump.",
            audit: [SyntheticCampaign.KnowledgeIndex(("K3", "windows-crash-classification"), ("K6", "windows-event-triage"))]);

        var result = await campaign.EvaluateAsync("R01");
        var knowledge = result["m4Knowledge"]!;

        Assert.Equal("windows-crash-classification", (string?)knowledge["citedNotLoaded"]!.AsArray().Single());
        Assert.Equal(0, Int(knowledge["readsServed"]));
        Assert.False(Check(result, "knowledge-pertinent"));
    }

    [Fact]
    public async Task ApplicableReadsInTheAnswerTurn_GivePrecisionAndRecall()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer: "27 live dump, 17 BSOD.",
            audit:
            [
                SyntheticCampaign.KnowledgeIndex(("K3", "windows-crash-classification"), ("K1", "windows-bugcheck-codes-core")),
                SyntheticCampaign.KnowledgeRead("windows-crash-classification", stepIndex: 1),
                SyntheticCampaign.KnowledgeRead("windows-bugcheck-codes-core", stepIndex: 0),
            ]);

        var result = await campaign.EvaluateAsync("R01");
        var knowledge = result["m4Knowledge"]!;

        // 0x1e is not one of the codes windows-bugcheck-codes-core explains, so that read is not applicable to this run's evidence.
        Assert.Equal(0.5, knowledge["precision"]!.GetValue<double>());
        Assert.Equal(1.0, knowledge["recallListed"]!.GetValue<double>());
        Assert.Equal(1, Int(knowledge["readsInAnswerTurn"]));
        Assert.True(Check(result, "knowledge-pertinent"));
    }

    [Fact]
    public async Task AKnowledgeReadAnsweredNotExecuted_IsCountedOnce()
    {
        var request = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject { ["id"] = "e1", ["function"] = new JsonObject { ["name"] = "runtime_evidence_read", ["arguments"] = "{}" } },
                    new JsonObject { ["id"] = "k1", ["function"] = new JsonObject { ["name"] = "runtime_knowledge_read", ["arguments"] = "{}" } },
                },
            },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "e1", ["content"] = "EvidenceRead/v1 Success" },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "k1", ["content"] = "Not executed: a runtime evidence request in the same reply was served first." },
        };
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer: "17 BSOD.", answerTurnMessages: request,
            audit: [SyntheticCampaign.KnowledgeIndex(("K3", "windows-crash-classification"))]);

        var result = await campaign.EvaluateAsync("R01");

        Assert.Equal(1, Int(result["m4Knowledge"]!["readsNotExecuted"]));
    }

    [Fact]
    public async Task ARunThatNeverAnswered_GetsCompletionOnly_NeverZeroDiagnosisScores()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer: null, status: 2, plannedSteps: 17, maxSteps: 15);

        var result = await campaign.EvaluateAsync("R01");

        Assert.Equal("no final answer", (string?)result["notApplicable"]);
        Assert.Null(result["m1Classification"]);
        Assert.Null(result["m2Counts"]);
        Assert.Equal("MaxStepsReached", (string?)result["m5Completion"]!["status"]);
        Assert.False(Check(result, "finalized"));
        Assert.False(Check(result, "plan-within-budget"));
        Assert.DoesNotContain(result["checks"]!.AsArray().OfType<JsonObject>(), c => (string?)c["evaluator"] == MafEvaluation.DiagnosisEvaluator);
    }

    [Fact]
    public async Task KnowledgeNotOffered_HasNoKnowledgeScore_AndPassesThatCheck()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer: "17 BSOD.");

        var result = await campaign.EvaluateAsync("R01");

        Assert.Null(result["m4Knowledge"]);
        Assert.True(Check(result, "knowledge-pertinent"));
    }

    [Fact]
    public async Task PartialSourcesWithoutLimitationWording_FailTheDisclosureCheck()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer: "17 BSOD.", completeness: 2);

        var result = await campaign.EvaluateAsync("R01");

        Assert.True(result["m3Support"]!["partialSourcesUndisclosed"]!.GetValue<bool>());
        Assert.False(Check(result, "limitations-disclosed"));
    }

    [Fact]
    public async Task TheGroundingOutcome_IsReadFromTheFinalMarker()
    {
        using var campaign = new SyntheticCampaign();
        campaign.AddRun("R01", answer: "17 BSOD.", finalMarker: "Final response; evidence limitations appended by runtime; evidence claims corrected");

        var result = await campaign.EvaluateAsync("R01");

        Assert.Equal("claimsCorrected", (string?)result["m5Completion"]!["grounding"]);
        Assert.Equal("appended", (string?)result["m5Completion"]!["disclosure"]);
    }

    [Fact]
    public async Task TheEvaluation_IsDeterministic_LeavesTheCampaignUntouched_AndCopiesNoAnswerText()
    {
        using var campaign = new SyntheticCampaign();
        const string Marker = "Frase-segreta-di-prova";
        campaign.AddRun("R01", answer: $"{Marker}: 27 BSOD e 17 bugcheck.");
        campaign.AddRun("R02", answer: null, status: 6);
        campaign.WriteManifest();
        var before = Snapshot(campaign.Directory);

        await CampaignEvaluator.RunAsync(campaign.Directory, SyntheticCampaign.KeysDirectory, CancellationToken.None);
        var first = Outputs(campaign.Directory);
        await CampaignEvaluator.RunAsync(campaign.Directory, SyntheticCampaign.KeysDirectory, CancellationToken.None);
        var second = Outputs(campaign.Directory);

        Assert.Equal(before, Snapshot(campaign.Directory));
        Assert.Equal(first, second);
        Assert.Equal(["R01.json", "R02.json", "manifest.json"], first.Keys.Order(StringComparer.Ordinal));
        Assert.All(first.Values, text => Assert.DoesNotContain(Marker.ToLowerInvariant(), text.ToLowerInvariant(), StringComparison.Ordinal));
        Assert.All(first.Values, text => Assert.DoesNotContain("\"variant\"", text, StringComparison.Ordinal));
    }

    [Fact]
    public void TheShippedKeys_LoadAndCoverTheBenchmarkScenariosOnce()
    {
        var keys = ScenarioKey.LoadDirectory(SyntheticCampaign.KeysDirectory);

        Assert.Equal(["B01", "B02", "B03"], keys.Keys.Order(StringComparer.Ordinal));
        Assert.All(keys.Values, k => Assert.Equal(64, k.Sha256.Length));
        Assert.Same(keys["B02"], keys["B03"]);
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).StartsWith(CampaignEvaluator.OutputDirectoryName, StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static Dictionary<string, string> Outputs(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, CampaignEvaluator.OutputDirectoryName))
            .ToDictionary(f => Path.GetFileName(f), File.ReadAllText);
}
