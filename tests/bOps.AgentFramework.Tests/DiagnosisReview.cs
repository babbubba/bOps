// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.AgentFramework.Tests;

/// <summary>One scored item of the human rubric.</summary>
internal sealed record RubricCriterion(string Key, string Title, string Guidance);

/// <summary>
/// The human review rubric of KNOW-BENCH. The five scored criteria and the 0/1/2 anchors are the ones in
/// <c>docs/benchmarks/know-bench-01a/README.md</c> (section "Metriche e giudizio"), unchanged in substance: only given stable keys so a
/// reviewer's file can be read back. A test pins this list to that README.
/// </summary>
internal static class ReviewRubric
{
    internal const int MinimumScore = 0;
    internal const int MaximumScore = 2;

    internal static IReadOnlyList<RubricCriterion> Criteria { get; } =
    [
        new("phenomenaClassification", "Classificazione dei fenomeni osservabili",
            "I fenomeni descritti esistono nel corpus e sono classificati correttamente (crash, riavvio, errore di servizio, disco, WHEA, ...)."),
        new("causeVersusClue", "Distinzione causa/indizio",
            "Un indizio non e presentato come causa accertata; una causa e dichiarata solo se il corpus la sostiene."),
        new("temporalCorrelation", "Correlazione temporale senza duplicare episodi",
            "Eventi vicini nel tempo sono correlati quando giustificato; lo stesso episodio non e contato piu volte."),
        new("verifiableCitations", "Citazioni verificabili",
            "Ogni conclusione cita timestamp UTC, canale, provider, eventId e dettaglio, e la citazione esiste nel corpus."),
        new("coverageAndUncertainty", "Rispetto di copertura e incertezza",
            "Dichiara i limiti del corpus (copertura, troncamenti, canali assenti) e non trae conclusioni di assenza da una copertura parziale."),
    ];

    internal static string ScaleText { get; } =
        "0 = errato/assente, 1 = parziale, 2 = corretto rispetto al corpus. Per le categorie assenti dal corpus si valuta se la diagnosi evita di inventarle; "
        + "non esiste una causa precostituita. Elencare i claim non supportati anche quando il totale e alto. Il completamento tecnico del task "
        + "non implica un punteggio diagnostico positivo.";

    internal static JsonObject ToJson() => new()
    {
        ["format"] = "bops.know-bench.rubric",
        ["version"] = 1,
        ["source"] = "docs/benchmarks/know-bench-01a/README.md#metriche-e-giudizio",
        ["scale"] = new JsonObject { ["minimum"] = MinimumScore, ["maximum"] = MaximumScore, ["anchors"] = ScaleText },
        ["criteria"] = new JsonArray([.. Criteria.Select(c => (JsonNode)new JsonObject
            { ["key"] = c.Key, ["title"] = c.Title, ["guidance"] = c.Guidance })]),
        ["claimCategories"] = new JsonArray([.. ClaimCategories.All.Select(c => (JsonNode)new JsonObject
            { ["key"] = c.Key, ["title"] = c.Title, ["supported"] = c.Supported })]),
    };
}

/// <summary>A reviewer's annotation category for one claim of a diagnosis. Annotations are not scores: they feed the claim ledger the README asks for.</summary>
internal sealed record ClaimCategory(string Key, string Title, bool Supported);

/// <summary>
/// The distinctions D7 requires the reviewer to be able to make. <see cref="IncorrectFact"/> is the one addition to the six asked for: a
/// fact that is present in the corpus but misread is neither a "correct fact" nor an invented reference, and a reviewer needs somewhere to record it.
/// It is an annotation only; the scored criteria are untouched.
/// </summary>
internal static class ClaimCategories
{
    internal const string CorrectFact = "CorrectFact";
    internal const string JustifiedCorrelation = "JustifiedCorrelation";
    internal const string UnsupportedCausalInference = "UnsupportedCausalInference";
    internal const string HallucinationOrNonexistentReference = "HallucinationOrNonexistentReference";
    internal const string UncertaintyHandling = "UncertaintyHandling";
    internal const string RelevantAdditionalCheck = "RelevantAdditionalCheck";
    internal const string IncorrectFact = "IncorrectFact";

    internal static IReadOnlyList<ClaimCategory> All { get; } =
    [
        new(CorrectFact, "Fatto correttamente identificato", true),
        new(JustifiedCorrelation, "Correlazione giustificata dal corpus", true),
        new(UnsupportedCausalInference, "Inferenza causale non supportata", false),
        new(HallucinationOrNonexistentReference, "Allucinazione o riferimento inesistente nel corpus", false),
        new(IncorrectFact, "Fatto presente nel corpus ma letto in modo errato", false),
        new(UncertaintyHandling, "Gestione dell'incertezza (adeguata o carente: specificare nel commento)", true),
        new(RelevantAdditionalCheck, "Verifica aggiuntiva pertinente", true),
    ];

    internal static bool IsKnown(string? key) => All.Any(c => c.Key == key);
}

/// <summary>One annotated claim. <see cref="Quote"/> and <see cref="Comment"/> may repeat diagnosis text: the evaluation file is as sensitive as the card.</summary>
internal sealed record EvaluationClaim(string Category, string? Quote, string? CorpusEvidence, string? Severity, string? Comment);

/// <summary>A reviewer's evaluation of one blind card. Never contains a condition, a run id or a Knowledge metric.</summary>
internal sealed record DiagnosisEvaluation(string PackageId, string DiagnosisId, string DiagnosisTextSha256, string Status, string? Reviewer,
    IReadOnlyDictionary<string, int?> Scores, IReadOnlyList<EvaluationClaim> Claims, string? Notes)
{
    internal bool IsFinal => Status == EvaluationFile.Final;

    internal int? Total => Scores.Values.All(v => v is not null) ? Scores.Values.Sum(v => v!.Value) : null;
}

/// <summary>Reads and writes the reviewer's evaluation files. Problems are closed codes: they never echo reviewer or diagnosis text.</summary>
internal static class EvaluationFile
{
    internal const string Format = "bops.know-bench.evaluation";
    internal const string Draft = "Draft";
    internal const string Final = "Final";
    internal const int MaximumBytes = 256 * 1024;
    private static readonly string[] Severities = ["Low", "Medium", "High"];

    /// <summary>A blank evaluation for the reviewer to fill in. Scores start as <c>null</c>: an unfilled score is not a zero.</summary>
    internal static JsonObject Template(string packageId, string diagnosisId, string textSha256, string completeness) => new()
    {
        ["format"] = Format,
        ["version"] = 1,
        ["packageId"] = packageId,
        ["diagnosisId"] = diagnosisId,
        ["diagnosisTextSha256"] = textSha256,
        ["diagnosisCompleteness"] = completeness,
        ["status"] = Draft,
        ["reviewer"] = "",
        ["scores"] = new JsonObject([.. ReviewRubric.Criteria.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)null))]),
        ["claims"] = new JsonArray(),
        ["notes"] = "",
        ["instructions"] = "Impostare status su Final quando tutti e cinque i punteggi (0, 1 o 2) sono assegnati. Aggiungere un elemento in claims per ogni "
            + "affermazione della diagnosi che si vuole annotare (category: " + string.Join(", ", ClaimCategories.All.Select(c => c.Key)) + ").",
    };

    internal static (DiagnosisEvaluation? Value, IReadOnlyList<string> Problems) Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var problems = new List<string>();
        if (bytes.Length is 0 or > MaximumBytes) return (null, ["InvalidSize"]);
        JsonObject root;
        try { root = JsonNode.Parse(bytes, nodeOptions: null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow })?.AsObject() ?? throw new JsonException(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return (null, ["InvalidJson"]); }
        if (Text(root, "format") != Format || Number(root, "version") != 1) problems.Add("WrongFormat");
        var packageId = Text(root, "packageId");
        var diagnosisId = Text(root, "diagnosisId");
        var textHash = Text(root, "diagnosisTextSha256");
        if (!OpaqueIds.IsPackage(packageId)) problems.Add("InvalidPackageId");
        if (!OpaqueIds.IsDiagnosis(diagnosisId)) problems.Add("InvalidDiagnosisId");
        if (textHash is not { Length: 64 } || !textHash.All(char.IsAsciiHexDigitLower)) problems.Add("InvalidTextHash");
        var status = Text(root, "status");
        if (status is not (Draft or Final)) problems.Add("InvalidStatus");

        var scores = new Dictionary<string, int?>(StringComparer.Ordinal);
        var scoreNode = root["scores"] as JsonObject;
        if (scoreNode is null) problems.Add("MissingScores");
        foreach (var criterion in ReviewRubric.Criteria)
        {
            JsonNode? node = scoreNode?[criterion.Key];
            if (node is null) { scores[criterion.Key] = null; continue; }
            if (node is JsonValue value && value.TryGetValue<int>(out var score) && score is >= ReviewRubric.MinimumScore and <= ReviewRubric.MaximumScore)
                scores[criterion.Key] = score;
            else { scores[criterion.Key] = null; problems.Add("ScoreOutOfRange:" + criterion.Key); }
        }

        if (scoreNode is not null)
            foreach (var property in scoreNode)
                if (ReviewRubric.Criteria.All(c => c.Key != property.Key)) problems.Add("UnknownCriterion");
        if (status == Final)
            foreach (var criterion in ReviewRubric.Criteria)
                if (scores[criterion.Key] is null && !problems.Contains("ScoreOutOfRange:" + criterion.Key)) problems.Add("MissingScore:" + criterion.Key);

        var claims = new List<EvaluationClaim>();
        if (root["claims"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject claim) { problems.Add("InvalidClaim"); continue; }
                var category = Text(claim, "category");
                if (!ClaimCategories.IsKnown(category)) { problems.Add("UnknownClaimCategory"); continue; }
                var severity = Text(claim, "severity");
                if (!string.IsNullOrEmpty(severity) && !Severities.Contains(severity)) problems.Add("InvalidSeverity");
                claims.Add(new(category!, Text(claim, "quote"), Text(claim, "corpusEvidence"), string.IsNullOrEmpty(severity) ? null : severity, Text(claim, "comment")));
            }
        }
        else if (root["claims"] is not null) problems.Add("InvalidClaims");

        if (problems.Count > 0) return (null, problems);
        return (new DiagnosisEvaluation(packageId!, diagnosisId!, textHash!, status!, Text(root, "reviewer"), scores, claims, Text(root, "notes")), []);
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    internal static byte[] Encode(JsonObject node) =>
        Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}
