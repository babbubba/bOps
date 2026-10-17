// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA1031 // The export boundary reports closed codes and removes its own partial output.
using System.Text;
using System.Text.Json.Nodes;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// A heuristic, <b>informational</b> scan of a diagnosis for vocabulary that could tell a reviewer that Knowledge was used. It never alters
/// or removes anything (the diagnosis is exported exactly as produced); it only measures the residual limit of the blinding so the operator
/// can report it. Counts only, kept in the private export record, never shown to the reviewer.
/// </summary>
internal static class BlindingRisk
{
    private static readonly string[] Identifiers =
        ["acme.windows-event-runbooks", "windows-event-triage", "interpretation.md", "skill.md", "references/", "read_resource", "knowledge_read", "runtime.knowledge"];

    private static readonly string[] Vocabulary = ["knowledge", "conoscenza", "runbook", "skill", "pack", "indice runtime", "procedura autorizzata"];

    internal static JsonObject Scan(string text)
    {
        var identifiers = Identifiers.Sum(term => Count(text, term));
        var vocabulary = Vocabulary.Sum(term => Count(text, term));
        return new JsonObject { ["identifierMentions"] = identifiers, ["vocabularyMentions"] = vocabulary, ["suspected"] = identifiers + vocabulary > 0 };
    }

    private static int Count(string text, string term)
    {
        var count = 0;
        for (var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); index >= 0;
             index = text.IndexOf(term, index + term.Length, StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }
}

/// <summary>What a blind export produced. The package directory holds nothing the reviewer must not see.</summary>
internal sealed record BlindPackageResult(string PackageId, string Directory, IReadOnlyList<string> IncludedDiagnosisIds,
    IReadOnlyDictionary<string, string> Excluded, string ManifestSha256);

/// <summary>
/// D7: exports the blind review package, the only thing a reviewer receives. It contains the rubric, the verified minimized corpus and its
/// attestation (identical for every arm, so they say nothing about the condition), one card per reviewable diagnosis and a blank evaluation
/// per card. It never contains the private map, a condition, a run id, a repetition, a Knowledge metric, a result file, a timestamp of
/// execution or any ordering that follows execution order.
/// <para>Blinding measures: random opaque ids as the only file names; cards listed in id order (random with respect to arm and run order);
/// every file and directory stamped with one fixed timestamp; the diagnosis text exported byte-for-byte, never edited to hide Knowledge.</para>
/// </summary>
internal static class BlindReviewPackage
{
    internal const string CardsDirectory = "cards";
    internal const string EvaluationsDirectory = "evaluations";
    internal const string EvidenceDirectory = "evidence";
    internal const string ManifestFile = "package-manifest.json";

    // One neutral instant for every file and directory of the package: not a real execution time.
    internal static readonly DateTime NeutralTimestampUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static BlindPackageResult Export(DiagnosisArchive archive, string outputDirectory, string corpusPath, string corpusSha256,
        string attestationSha256, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!Path.IsPathFullyQualified(outputDirectory)) throw new ArchiveException("OutputNotAbsolute");
        var output = Path.GetFullPath(outputDirectory);
        if (IsUnder(archive.Root, output) || IsUnder(output, archive.Root)) throw new ArchiveException("PackageOverlapsArchive");
        if (Directory.Exists(output) || File.Exists(output)) throw new ArchiveException("LocationOccupied");

        // Corpus and attestation are verified exactly as the replay verifies them; the reviewer gets the verified bytes.
        byte[] corpusBytes, attestationBytes;
        try
        {
            _ = KnowledgeBenchmark.ReplayFrozen(corpusPath, corpusSha256, attestationSha256);
            corpusBytes = File.ReadAllBytes(corpusPath);
            attestationBytes = File.ReadAllBytes(CorpusAttestation.PathFor(corpusPath));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ArchiveException("CorpusRefused");
        }

        var (entries, unreadable) = archive.ReadMap();
        var excluded = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in unreadable) excluded[id] = "MapUnreadable";
        var cards = new SortedDictionary<string, (OpenedDiagnosis Opened, JsonObject Risk)>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Availability == DiagnosisAvailability.None) { excluded[entry.DiagnosisId] = "NoDiagnosis"; continue; }
            if (entry.Identity.CorpusSha256 != corpusSha256 || entry.Identity.AttestationSha256 != attestationSha256)
            {
                excluded[entry.DiagnosisId] = "CorpusMismatch";
                continue;
            }

            try
            {
                var opened = archive.ReadDiagnosis(entry.DiagnosisId);
                if (opened.TextSha256 != entry.TextSha256) throw new ArchiveException("IntegrityMismatch");
                cards[entry.DiagnosisId] = (opened, BlindingRisk.Scan(opened.Text));
            }
            catch (ArchiveException ex) { excluded[entry.DiagnosisId] = ex.Code; }
        }

        if (cards.Count == 0) throw new ArchiveException("NothingReviewable");

        var packageId = OpaqueIds.NewPackage();
        ArchiveAccess.CreateRestrictedDirectory(output);
        try
        {
            foreach (var name in new[] { CardsDirectory, EvaluationsDirectory, EvidenceDirectory })
                ArchiveAccess.CreateRestrictedDirectory(Path.Combine(output, name));
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["rubric.json"] = EvaluationFile.Encode(ReviewRubric.ToJson()),
                ["README-reviewer.md"] = Encoding.UTF8.GetBytes(ReviewerInstructions),
                [$"{EvidenceDirectory}/events-corpus.json"] = corpusBytes,
                [$"{EvidenceDirectory}/corpus-attestation.json"] = attestationBytes,
            };
            foreach (var (id, (opened, _)) in cards)
            {
                files[$"{CardsDirectory}/{id}.txt"] = new UTF8Encoding(false).GetBytes(opened.Text);
                var completeness = opened.Availability == DiagnosisAvailability.Complete ? "Complete" : "Incomplete";
                files[$"{EvaluationsDirectory}/{id}.evaluation.json"] =
                    EvaluationFile.Encode(EvaluationFile.Template(packageId, id, opened.TextSha256, completeness));
            }

            foreach (var (relative, bytes) in files)
                DiagnosisArchive.WriteRestrictedNew(Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar)), bytes);

            var manifest = new JsonObject
            {
                ["format"] = "bops.know-bench.review-package",
                ["version"] = 1,
                ["packageId"] = packageId,
                ["corpusSha256"] = corpusSha256,
                ["attestationSha256"] = attestationSha256,
                ["diagnosisIds"] = new JsonArray([.. cards.Keys.Select(id => (JsonNode)JsonValue.Create(id)!)]),
                ["files"] = new JsonArray([.. files.Select(file => (JsonNode)new JsonObject
                {
                    ["path"] = file.Key, ["sha256"] = DiagnosisArchive.Sha256(file.Value), ["bytes"] = file.Value.Length,
                })]),
            };
            var manifestBytes = EvaluationFile.Encode(manifest);
            DiagnosisArchive.WriteRestrictedNew(Path.Combine(output, ManifestFile), manifestBytes);
            ArchiveAccess.NormalizeTimestamps(output, NeutralTimestampUtc);

            archive.RecordExport(packageId, new JsonObject
            {
                ["packageId"] = packageId,
                ["manifestSha256"] = DiagnosisArchive.Sha256(manifestBytes),
                ["createdUtc"] = nowUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["corpusSha256"] = corpusSha256,
                ["attestationSha256"] = attestationSha256,
                ["included"] = new JsonArray([.. cards.Keys.Select(id => (JsonNode)JsonValue.Create(id)!)]),
                ["excluded"] = new JsonObject([.. excluded.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)JsonValue.Create(e.Value)))]),
                ["blindingRisk"] = new JsonObject([.. cards.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value.Risk.DeepClone()))]),
            });
            return new(packageId, output, [.. cards.Keys], excluded, DiagnosisArchive.Sha256(manifestBytes));
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException)
        {
            // A half-written package must not be handed to a reviewer.
            try { Directory.Delete(output, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw ex as ArchiveException ?? new ArchiveException("WriteFailed");
        }
    }

    private static bool IsUnder(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private const string ReviewerInstructions = """
        # Revisione cieca delle diagnosi KNOW-BENCH

        Questo pacchetto contiene solo cio che serve a giudicare la qualita di alcune diagnosi:

        - `cards/<id>.txt`: la diagnosi, esattamente come prodotta dall'agente (non modificata).
        - `evidence/events-corpus.json`: il corpus di eventi minimizzato su cui l'agente ha lavorato. E l'unica evidenza disponibile: un fatto
          che non compare qui non e verificabile e va annotato come tale.
        - `evidence/corpus-attestation.json`: l'attestazione dell'operatore su quel corpus.
        - `rubric.json`: la rubrica (cinque voci, punteggio 0, 1 o 2) e le categorie di annotazione dei claim.
        - `evaluations/<id>.evaluation.json`: il modulo da compilare, uno per diagnosi.

        ## Come compilare

        1. Leggere la diagnosi e verificare ogni affermazione nel corpus, non nella propria esperienza o nelle conoscenze generali.
        2. Assegnare i cinque punteggi (0 = errato/assente, 1 = parziale, 2 = corretto rispetto al corpus). Un punteggio non assegnato
           resta `null`: non e uno zero. Il completamento tecnico del task non implica un punteggio positivo.
        3. In `claims` annotare le affermazioni rilevanti con una categoria: fatto correttamente identificato, correlazione giustificata,
           inferenza causale non supportata, allucinazione o riferimento inesistente, fatto letto in modo errato, gestione dell'incertezza,
           verifica aggiuntiva pertinente. Elencare i claim non supportati anche quando il totale e alto.
        4. Quando tutti e cinque i punteggi sono assegnati, impostare `status` su `Final` e indicare `reviewer`.

        ## Regole del cieco

        - Non cercare di capire con quale condizione sia stata prodotta una diagnosi e non annotarlo nel modulo.
        - Una diagnosi `Incomplete` e stata interrotta o non e stata accettata come risposta finale: giudicare solo cio che c'e.
        - Non usare un LLM per valutare: la valutazione e umana.
        - Il pacchetto e i moduli compilati contengono testo diagnostico: trattarli come riservati e non copiarli fuori dal canale concordato.
        """;
}
