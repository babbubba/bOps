// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// MAF A/B campaign, 09-B02-B-r2: the final answer attributed 196 WHEA events, 1319 Kernel-Power events and 2025 dates to
/// <c>system.events</c> steps whose persisted results were empty queries in 2026, and the grounding check (which only knows
/// typed facts, and by contract has none for an empty query) found no contradiction. The provenance check judges only a count or a
/// date that the answer attributes to a persisted step (by step number or by tool name), against what that step's own result and
/// arguments contain. It never judges an unattributed number, a unit, an identifier, a derived value or a hedged statement.
/// The fixtures are synthetic and anonymized: the shapes and magnitudes of the campaign evidence, none of its payloads.
/// </summary>
public sealed class AnswerClaimProvenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 23, 32, 56, TimeSpan.Zero);

    private const string EmptyEventsWindow =
        """{"schemaVersion":2,"mode":"raw","status":"complete","complete":true,"truncated":false,"window":{"fromUtc":"2026-10-21T23:34:57Z","toUtc":"2026-10-24T23:34:57Z"},"sourceFilter":{"requested":"Kernel-Power","registrationStatus":"notRegistered","observedMatch":false,"caution":"noMatchSourceNotRegistered"},"observedEvents":0,"returnedEvents":0,"events":[]}""";

    private const string EmptyWheaAggregate =
        """{"schemaVersion":2,"mode":"aggregate","status":"complete","complete":false,"window":{"fromUtc":"2026-09-24T23:35:06Z","toUtc":"2026-10-24T23:35:06Z"},"sourceFilter":{"requested":"WHEA-Logger","registrationStatus":"notRegistered","observedMatch":false},"observedEvents":0,"observedGroups":0,"returnedGroups":0,"groups":[]}""";

    private static ToolArguments Args(string json) => ToolArguments.FromJson((JsonObject)JsonNode.Parse(json)!);

    private static PlanStep Step(int index, string tool, ToolCallResult result, string args = "{}") =>
        new(index, tool, new ModelToolCall($"call-{index}", tool, Args(args)), result, result.Output ?? result.ErrorMessage, 0)
        {
            PlannedStepIndex = index,
            ExecutionClassification = PlannedStepExecutionClassification.Matched,
        };

    private static ToolCallResult Done(string output, ToolResultCompleteness completeness = ToolResultCompleteness.Complete) =>
        ToolCallResult.Success(output) with { Completeness = completeness };

    private static ToolCallResult Rejected(string message) =>
        ToolCallResult.Failure(message) with { FailureKind = ToolFailureKind.Validation };

    /// <summary>The campaign's tool sequence, reduced: info, stability, crashes, three failed event queries, three empty ones.</summary>
    private static List<PlanStep> CampaignSteps() =>
    [
        Step(0, "system.info", Done("OS: Microsoft Windows 10.0.28000\nHost: TEST-HOST\nUptime: 0d 1h 49m\nHardware model: TEST")),
        Step(1, "system.stability", Done(
            """{"schemaVersion":1,"status":"partial","window":{"fromUtc":"2026-04-27T23:33:28Z","toUtc":"2026-10-24T23:33:28Z"},"categories":{"unexpectedShutdown":60,"kernelCrash":10,"hardwareError":390,"displayFault":26}}""",
            ToolResultCompleteness.Partial), """{"windowDays":180}"""),
        Step(2, "system.crashes", Done(
            """{"schemaVersion":2,"mode":"aggregate","window":{"fromUtc":"2026-04-27T23:33:36Z","toUtc":"2026-10-24T23:33:36Z"},"observedItems":110,"observedGroups":43}""",
            ToolResultCompleteness.Partial), """{"mode":"aggregate","sinceDays":180}"""),
        Step(3, "system.events", Rejected("The offered tool's arguments did not match the current planned step; the call was not executed."),
            """{"source":"WHEA-Logger","minSeverity":"warning","windowDays":30,"mode":"aggregate","limit":50}"""),
        Step(4, "system.events", Rejected("windowDays applies only to mode aggregate; raw mode is limited to windowMinutes up to 10080 (7 days)."),
            """{"source":"Kernel-Power","minSeverity":"critical","windowDays":30,"mode":"raw","limit":50}"""),
        Step(5, "system.events", Rejected("The offered tool's arguments did not match the current planned step; the call was not executed."),
            """{"source":"Kernel-Power","minSeverity":"critical","windowMinutes":4320,"mode":"raw","limit":50}"""),
        Step(6, "system.events", Done(EmptyEventsWindow),
            """{"source":"Kernel-Power","minSeverity":"critical","windowMinutes":4320,"mode":"raw","limit":50}"""),
        Step(7, "system.events", Done(EmptyWheaAggregate, ToolResultCompleteness.Partial),
            """{"source":"WHEA-Logger","minSeverity":"warning","windowDays":30,"mode":"aggregate","limit":50}"""),
        Step(8, "system.events", Done(EmptyWheaAggregate.Replace("WHEA-Logger", "Disk", StringComparison.Ordinal), ToolResultCompleteness.Partial),
            """{"source":"Disk","minSeverity":"warning","windowDays":30,"mode":"aggregate","limit":50}"""),
    ];

    private static IReadOnlyList<UnsupportedClaim> Find(string answer, IReadOnlyList<PlanStep>? steps = null) =>
        AnswerClaimProvenance.Find(answer, steps ?? CampaignSteps(), Now);

    // The structure of the campaign answer 09-B02-B-r2: headed sections naming steps, facts and hypotheses under bold labels.
    private const string InventedCampaignAnswer = """
        ### 2. Stabilità e Crash (Step 1-2)
        **Fatti osservati:**
        *   **Spegnimenti inaspettati (unexpectedShutdown):** 60 eventi rilevati.
        *   **Errori hardware (hardwareError):** 390 eventi rilevati.
        *   **Crash totali osservati (all):** 110.
        *   **Finestra di analisi:** 180 giorni (dal 06/05/2025 al 06/11/2025).

        ### 3. Event Log: Kernel-Power e Riavvii (Step 3-5 falliti, Step 6 recuperato)
        **Fatti osservati:**
        *   Gli steps 3-5 non hanno restituito dati grezzi (0 caratteri).
        *   **Evento 1074 (Kernel-Power):** 1319 occorrenze nel periodo 27/10/2025 - 02/11/2025.
        *   **WHEA-Logger (Hardware Error):** 196 occorrenze nel periodo 28/10/2025 - 02/11/2025.

        ### 4. Errori Disco e WHEA (Steps 7-8)
        *   **Disk Errors:** 12 occorrenze nel periodo 10/10/2025 - 02/11/2025.
        *   **WHEA-Logger (riepilogo):** 196 occorrenze totali.
        """;

    [Fact]
    public void CountsAndDatesAttributedToEmptyOrRejectedQueries_AreUnsupported_WhileRealCountsAreNot()
    {
        var claims = Find(InventedCampaignAnswer);

        var counts = claims.Where(claim => claim.Kind == UnsupportedClaimKind.Count).ToList();
        Assert.Equal(["1319", "196", "12", "196"], counts.Select(claim => claim.Token)); // in order of appearance
        Assert.DoesNotContain(counts, claim => claim.Token is "60" or "390" or "110" or "180" or "1074" or "0");

        var dates = claims.Where(claim => claim.Kind == UnsupportedClaimKind.Date).Select(claim => claim.Token).ToList();
        Assert.Contains("06/05/2025", dates);
        Assert.Contains("27/10/2025", dates);
        Assert.Contains("10/10/2025", dates);
        Assert.All(claims, claim => Assert.False(string.IsNullOrWhiteSpace(claim.Quote)));
    }

    [Fact]
    public void AnAnswerThatStatesWhatTheEmptyQueriesReturned_IsNotQuestioned()
    {
        const string honest = """
            ### 3. Event Log (Step 3-5 falliti, Step 6-8)
            Gli step 3-5 sono stati rifiutati dalla validazione e non hanno prodotto dati. Lo step 6 (Kernel-Power, finestra di 3 giorni)
            ha restituito 0 eventi, ma la fonte risulta non registrata: zero corrispondenze non dimostra che il sistema non abbia mai
            registrato quegli eventi. Gli step 7 e 8 (WHEA-Logger e Disk, 30 giorni) hanno restituito 0 gruppi e la copertura è parziale.
            I 390 errori hardware storici vengono da system.stability (step 1), un dato distinto; sono un'ipotesi di instabilità, non
            una causa confermata.
            """;

        Assert.Empty(Find(honest));
    }

    [Fact]
    public void AnUncertainDiagnosis_WithNoAttributedNumbers_IsNotQuestioned()
    {
        const string uncertain = """
            Le evidenze non permettono di stabilire la causa. Il sospetto principale è un problema di alimentazione o di driver della GPU
            (ipotesi), ma mancano misure elettriche e l'analisi dei dump. Si consiglia di raccogliere i dump completi e di ripetere la
            verifica con l'evento 41 e i codici di arresto. Con 5 test mirati la causa potrebbe essere confermata.
            """;

        Assert.Empty(Find(uncertain));
    }

    [Theory]
    [InlineData("Sistema: Windows 11 Pro, build 26200.6974 (step 0).")]
    [InlineData("Memoria: 22770 MB usati su 32475 MB, pari al 70.1% (step 1).")]
    [InlineData("Analizzati gli ultimi 30 giorni e 7 settimane di log (step 6).")]
    [InlineData("L'evento 1074 e l'evento 41 hanno 0 occorrenze nello step 6.")]
    [InlineData("Lo step 6 ha usato limit 50 e windowMinutes 4320, senza risultati.")]
    [InlineData("Alle 12:27:39 del 24/10/2026 (step 0) l'uptime era 1h 49m.")]
    public void NumbersThatAreNotCounts_UnitsIdentifiersVersionsAndWindows_AreNeverQuestioned(string answer) =>
        Assert.Empty(Find(answer));

    [Fact]
    public void ADerivedValue_WithAnExplicitDerivation_IsNotQuestioned()
    {
        Assert.Empty(Find("Step 1: 60 + 10 + 26 = 96 eventi gravi in totale (somma delle categorie)."));
        Assert.Empty(Find("Step 1: in totale 96 eventi gravi sommando le categorie."));
    }

    [Fact]
    public void AnOffByOneStepNumber_IsToleratedWhenTheNumberExistsInANeighbouringStep()
    {
        // The model numbered the stability step 2 (one-based); the persisted index is 1.
        Assert.Empty(Find("Allo step 2 risultano 60 spegnimenti inattesi e 390 errori hardware."));
    }

    [Fact]
    public void AToolName_AttributesACount_WithoutAStepNumber()
    {
        var invented = Find("system.events ha trovato 196 eventi WHEA negli ultimi 7 giorni.");
        var claim = Assert.Single(invented);
        Assert.Equal((UnsupportedClaimKind.Count, "196"), (claim.Kind, claim.Token));
        Assert.Equal([3, 4, 5, 6, 7, 8], claim.Steps.Order());

        Assert.Empty(Find("system.crashes ha trovato 110 crash in 43 gruppi."));
    }

    [Fact]
    public void ACountWithNoStepAndNoToolAttribution_IsNotJudged_ButIsRememberedAcrossACorrection()
    {
        const string unattributed = "Sono stati rilevati 196 eventi WHEA nell'ultima settimana.";
        Assert.Empty(Find(unattributed));

        // After a correction asked to drop 196, any later count claim of it that no step carries is still unsupported.
        var recheck = AnswerClaimProvenance.Find(unattributed, CampaignSteps(), Now, new HashSet<string>(["196"], StringComparer.Ordinal));
        Assert.Equal("196", Assert.Single(recheck).Token);
        Assert.Empty(AnswerClaimProvenance.Find("Sono stati rilevati 60 spegnimenti inattesi.", CampaignSteps(), Now, new HashSet<string>(["60"], StringComparer.Ordinal)));
    }

    [Fact]
    public void AFailedStep_CarriesNoCount()
    {
        var claim = Assert.Single(Find("Lo step 4 ha trovato 52 eventi Kernel-Power."));
        Assert.Equal("52", claim.Token);
    }

    [Fact]
    public void ADateWhoseYearNoCitedStepAndNoClockCarries_IsUnsupported_AndOneThatMatchesIsNot()
    {
        var invented = Find("Ultimo avvio il 02/11/2025 12:27:39 (step 0).");
        Assert.Equal("02/11/2025", Assert.Single(invented).Token);

        Assert.Empty(Find("Finestra di analisi dal 27/04/2026 al 24/10/2026 (step 1)."));
        Assert.Empty(Find("Finestra ISO 2026-04-27 - 2026-10-24 (step 1)."));
        Assert.Equal("2025-04-27", Assert.Single(Find("Finestra ISO 2025-04-27 - 2026-10-24 (step 1).")).Token);
    }

    [Fact]
    public void AnAnswerWithoutAnyTools_OrAnEmptyAnswer_HasNothingToJudge()
    {
        Assert.Empty(AnswerClaimProvenance.Find("Step 6 ha trovato 196 eventi.", [], Now));
        Assert.Empty(Find(string.Empty));
        Assert.Empty(Find("   \n  "));
    }

    [Fact]
    public void EvidenceControlSteps_AreNotEvidenceAndNeverSupportANumber()
    {
        var control = Step(9, EvidenceRead.ControlFunctionName, Done("196 eventi"));
        var steps = CampaignSteps();
        steps.Add(control);

        Assert.Equal("196", Assert.Single(AnswerClaimProvenance.Find("Lo step 9 ha trovato 196 eventi.", steps, Now)).Token);
    }

    [Fact]
    public void TheCorrectionInstruction_NamesTheStepsAndTokens_NeverTheModelsWords_AndKeepsZeroResultsInContext()
    {
        var claims = Find(InventedCampaignAnswer);

        var instruction = AnswerClaimProvenance.CorrectionInstruction(claims, CampaignSteps());

        Assert.StartsWith("EvidenceProvenanceCorrection/v1.", instruction, StringComparison.Ordinal);
        Assert.Contains("steps 3, 4, 5, 6 system.events", instruction, StringComparison.Ordinal);
        Assert.Contains("steps 7, 8 system.events", instruction, StringComparison.Ordinal);
        Assert.Contains("196", instruction, StringComparison.Ordinal);
        Assert.Contains("1319", instruction, StringComparison.Ordinal);
        Assert.Contains("06/05/2025", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("occorrenze", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Fatti osservati", instruction, StringComparison.Ordinal);
        Assert.Contains("does not show that the system never logged", instruction, StringComparison.Ordinal);
        Assert.True(instruction.Length < 2400, "the instruction is bounded");
    }

    [Fact]
    public void TheResult_IsDeterministic_AndBounded()
    {
        var first = Find(InventedCampaignAnswer);
        var second = Find(InventedCampaignAnswer);
        Assert.Equal(first.Select(claim => (claim.Kind, claim.Token, claim.Quote)), second.Select(claim => (claim.Kind, claim.Token, claim.Quote)));

        var flood = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"Step 6 ha trovato {1000 + i} eventi nel giorno 01/01/2025."));
        Assert.True(Find(flood).Count <= AnswerClaimProvenance.MaxClaims);
    }
}
