// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Knowledge.Tests;

#pragma warning disable CA2000 // Seeded owns the archive; every test disposes it through `using var archive = seeded.Archive`.
namespace bOps.AgentFramework.Tests;

/// <summary>
/// D7: the blind review package, the human evaluation format and the post-review join. Offline: invented diagnoses, a synthetic attested
/// corpus, no model and no Event Log. The reviewer-facing package is asserted from the reviewer's side: only what is in the directory.
/// </summary>
public sealed class KnowledgeBlindReviewTests
{
    private const string TextA = "A-RUN-SENTINEL: Kernel-Power 41 at 22:10 UTC; the cause is not established from this corpus.";
    private const string TextB = "B-RUN-SENTINEL: following the windows-event-triage runbook from the Knowledge, WHEA 18 precedes the reboot.";
    private const string TextC = "C-RUN-SENTINEL: partial diagnosis cut off before the limits of the corpus are";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20000);

    private sealed record Seeded(DiagnosisArchive Archive, string Root, string CorpusPath, string CorpusHash, string Attestation,
        IReadOnlyDictionary<string, DiagnosisMapEntry> ByCondition);

    private static Seeded Seed(TempDirectory temporary, bool withNoDiagnosis = true)
    {
        var (path, hash, attestation) = CorpusFixtures.Approved(temporary);
        var root = temporary.Sub("archive");
        var archive = DiagnosisArchive.OpenOrCreate(root, KnowledgeDiagnosisArchiveTests.Protector);
        void Store(string condition, int repetition, CapturedDiagnosis? diagnosis) =>
            Assert.Equal(DiagnosisArchiveOutcome.Stored, archive.Store(OpaqueIds.NewDiagnosis(),
                new DiagnosisRunIdentity(condition, repetition, Guid.NewGuid(), hash, attestation), diagnosis).Status);
        Store("A", 1, KnowledgeDiagnosisArchiveTests.Complete(TextA));
        Store("B", 1, KnowledgeDiagnosisArchiveTests.Complete(TextB));
        Store("C", 1, KnowledgeDiagnosisArchiveTests.Partial(TextC));
        if (withNoDiagnosis) Store("A", 2, null);
        var byCondition = archive.ReadMap().Entries.Where(e => e.Identity.Repetition == 1).ToDictionary(e => e.Identity.Condition);
        return new(archive, root, path, hash, attestation, byCondition);
    }

    private static BlindPackageResult Export(Seeded seeded, string directory) =>
        BlindReviewPackage.Export(seeded.Archive, directory, seeded.CorpusPath, seeded.CorpusHash, seeded.Attestation, Now);

    private static string[] Files(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    [Fact]
    public void ThePackage_ContainsOnlyWhatAReviewerNeeds_AndNothingThatNamesAnArm()
    {
        using var temporary = new TempDirectory();
        var seeded = Seed(temporary);
        using var archive = seeded.Archive;
        var package = Export(seeded, temporary.Sub("package"));

        Assert.Equal(3, package.IncludedDiagnosisIds.Count);
        Assert.Equal("NoDiagnosis", Assert.Single(package.Excluded).Value);
        var expected = new List<string> { "README-reviewer.md", "evidence/corpus-attestation.json", "evidence/events-corpus.json", "package-manifest.json", "rubric.json" };
        foreach (var id in package.IncludedDiagnosisIds) { expected.Add($"cards/{id}.txt"); expected.Add($"evaluations/{id}.evaluation.json"); }
        Assert.Equal(expected.Order(StringComparer.Ordinal), Files(package.Directory));

        // What the reviewer could read: no run id, no condition, no repetition, no archive path, no Knowledge metric, no map.
        var everything = string.Join("\n", Directory.EnumerateFiles(package.Directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var entry in seeded.ByCondition.Values)
            Assert.DoesNotContain(entry.Identity.RunId.ToString("D"), everything, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "\"condition\"", "repetition", "runId", "knowledgeUsed", "knowledgeDiscovery", "blindingRisk", seeded.Root, "private" })
            Assert.DoesNotContain(forbidden, everything, StringComparison.OrdinalIgnoreCase);
        Assert.All(package.IncludedDiagnosisIds, id => Assert.True(OpaqueIds.IsDiagnosis(id)));
        Assert.All(Files(package.Directory).Where(f => f.StartsWith("cards/", StringComparison.Ordinal) || f.StartsWith("evaluations/", StringComparison.Ordinal)),
            f => Assert.Matches("^(cards|evaluations)/dx-[2-9defghjkmnpqrstuvwxyz]{26}\\.(txt|evaluation\\.json)$", f));

        // The corpus is the verified one, byte for byte, with its attestation.
        Assert.Equal(File.ReadAllBytes(seeded.CorpusPath), File.ReadAllBytes(Path.Combine(package.Directory, "evidence", "events-corpus.json")));
        Assert.Equal(File.ReadAllBytes(CorpusAttestation.PathFor(seeded.CorpusPath)), File.ReadAllBytes(Path.Combine(package.Directory, "evidence", "corpus-attestation.json")));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(package.Directory, "package-manifest.json")))!.AsObject();
        Assert.Equal(package.PackageId, manifest["packageId"]!.GetValue<string>());
        Assert.Equal(seeded.CorpusHash, manifest["corpusSha256"]!.GetValue<string>());
        Assert.Equal(package.IncludedDiagnosisIds.Order(StringComparer.Ordinal), manifest["diagnosisIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        foreach (var file in manifest["files"]!.AsArray())
            Assert.Equal(DiagnosisArchive.Sha256(File.ReadAllBytes(Path.Combine(package.Directory, file!["path"]!.GetValue<string>()))), file["sha256"]!.GetValue<string>());
    }

    [Fact]
    public void Cards_AreTheDiagnosesExactly_NeverEditedToHideKnowledge()
    {
        using var temporary = new TempDirectory();
        var seeded = Seed(temporary);
        using var archive = seeded.Archive;
        var package = Export(seeded, temporary.Sub("package"));

        var texts = package.IncludedDiagnosisIds.Select(id => File.ReadAllBytes(Path.Combine(package.Directory, "cards", id + ".txt")))
            .Select(bytes => Encoding.UTF8.GetString(bytes)).ToArray();
        Assert.Equivalent(new[] { TextA, TextB, TextC }, texts);
        Assert.Contains("windows-event-triage runbook from the Knowledge", texts.Single(t => t.StartsWith("B-RUN", StringComparison.Ordinal)), StringComparison.Ordinal);
        foreach (var id in package.IncludedDiagnosisIds)
        {
            var bytes = File.ReadAllBytes(Path.Combine(package.Directory, "cards", id + ".txt"));
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
            Assert.Equal(archive.ReadMap().Entries.Single(e => e.DiagnosisId == id).TextSha256, DiagnosisArchive.Sha256(bytes));
        }
    }

    [Fact]
    public void FileTimestamps_AndOrder_CannotRevealWhichArmRanFirst()
    {
        using var temporary = new TempDirectory();
        var (path, hash, attestation) = CorpusFixtures.Approved(temporary);
        using var archive = DiagnosisArchive.OpenOrCreate(temporary.Sub("archive"), KnowledgeDiagnosisArchiveTests.Protector);
        var executionOrder = new List<string>();
        foreach (var condition in new[] { "A", "B", "C" })
            for (var repetition = 1; repetition <= 10; repetition++)
            {
                var id = OpaqueIds.NewDiagnosis();
                executionOrder.Add(id);
                archive.Store(id, new DiagnosisRunIdentity(condition, repetition, Guid.NewGuid(), hash, attestation),
                    KnowledgeDiagnosisArchiveTests.Complete($"{condition}{repetition} diagnosis"));
                Thread.Sleep(2); // distinct creation times in the archive, as in a real sequence of runs
            }

        var package = BlindReviewPackage.Export(archive, temporary.Sub("package"), path, hash, attestation, Now);

        // Every file and directory carries one neutral instant: not the run time, not the package time.
        var items = Directory.EnumerateFileSystemEntries(package.Directory, "*", SearchOption.AllDirectories).Append(package.Directory).ToArray();
        Assert.All(items, item =>
        {
            Assert.Equal(BlindReviewPackage.NeutralTimestampUtc, File.GetLastWriteTimeUtc(item));
            Assert.Equal(BlindReviewPackage.NeutralTimestampUtc, File.GetCreationTimeUtc(item));
        });
        // The listing order is the id order, and the id order is not the execution order (1/30! to coincide by chance).
        Assert.Equal(package.IncludedDiagnosisIds.Order(StringComparer.Ordinal), package.IncludedDiagnosisIds);
        Assert.NotEqual(executionOrder, package.IncludedDiagnosisIds);
        Assert.Equal(30, package.IncludedDiagnosisIds.Count);
    }

    [Fact]
    public void TheRubric_IsTheOneInTheBenchmarkReadme_AndTheClaimDistinctionsExist()
    {
        var readme = File.ReadAllText(Path.Combine(KnowledgeBenchmark.Root, "docs/benchmarks/know-bench-01a/README.md")).ToLowerInvariant();
        Assert.Equal(5, ReviewRubric.Criteria.Count);
        Assert.All(ReviewRubric.Criteria, criterion => Assert.Contains(criterion.Title.ToLowerInvariant(), readme, StringComparison.Ordinal));
        Assert.Contains("0 = errato/assente, 1 = parziale, 2 = corretto rispetto al corpus", readme, StringComparison.Ordinal);
        Assert.StartsWith("0 = errato/assente, 1 = parziale, 2 = corretto rispetto al corpus", ReviewRubric.ScaleText, StringComparison.Ordinal);
        Assert.Equal((0, 2), (ReviewRubric.MinimumScore, ReviewRubric.MaximumScore));
        Assert.Equal(ReviewRubric.Criteria.Select(c => c.Key).Distinct().Count(), ReviewRubric.Criteria.Count);

        // The six distinctions the reviewer must be able to make, plus the one documented addition.
        string[] required = [ClaimCategories.CorrectFact, ClaimCategories.JustifiedCorrelation, ClaimCategories.UnsupportedCausalInference,
            ClaimCategories.HallucinationOrNonexistentReference, ClaimCategories.UncertaintyHandling, ClaimCategories.RelevantAdditionalCheck];
        Assert.All(required, key => Assert.True(ClaimCategories.IsKnown(key)));
        Assert.Equal(required.Length + 1, ClaimCategories.All.Count);
        Assert.True(ClaimCategories.IsKnown(ClaimCategories.IncorrectFact));
    }

    [Fact]
    public void AnotherCorpus_OrABrokenAttestation_IsRefused_AndNothingIsCreated()
    {
        using var temporary = new TempDirectory();
        var seeded = Seed(temporary);
        using var archive = seeded.Archive;
        var output = temporary.Sub("package");

        Assert.Equal("CorpusRefused", KnowledgeDiagnosisArchiveTests.ArchiveCode(() =>
            BlindReviewPackage.Export(archive, output, seeded.CorpusPath, seeded.CorpusHash, new string('0', 64), Now)));
        Assert.Equal("CorpusRefused", KnowledgeDiagnosisArchiveTests.ArchiveCode(() =>
            BlindReviewPackage.Export(archive, output, seeded.CorpusPath, new string('0', 64), seeded.Attestation, Now)));
        Assert.False(Directory.Exists(output));

        // Runs recorded against a different corpus are not mixed into this package: they are excluded by id and reason.
        var (otherPath, otherHash, otherAttestation) = CorpusFixtures.Approved(temporary, "other");
        var foreign = OpaqueIds.NewDiagnosis();
        archive.Store(foreign, KnowledgeDiagnosisArchiveTests.Identity("B", 2, otherHash, otherAttestation), KnowledgeDiagnosisArchiveTests.Complete("FOREIGN-RUN-SENTINEL"));
        var package = Export(seeded, output);
        Assert.DoesNotContain(foreign, package.IncludedDiagnosisIds);
        Assert.Equal("CorpusMismatch", package.Excluded[foreign]);
        Assert.DoesNotContain("FOREIGN-RUN-SENTINEL", string.Concat(Directory.EnumerateFiles(package.Directory, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)), StringComparison.Ordinal);

        // The other corpus' package is the mirror image: only its own run is in it.
        var other = BlindReviewPackage.Export(archive, temporary.Sub("elsewhere"), otherPath, otherHash, otherAttestation, Now);
        Assert.Equal([foreign], other.IncludedDiagnosisIds);
    }

    [Fact]
    public void UnreadableOrAlteredRecords_AreExcludedByReason_NotSilentlyDropped_AndNothingReviewableCreatesNoPackage()
    {
        using var temporary = new TempDirectory();
        var seeded = Seed(temporary);
        using var archive = seeded.Archive;
        var tampered = seeded.ByCondition["B"];
        var file = Path.Combine(seeded.Root, "records", tampered.DiagnosisId + ".sealed");
        var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var tag = node["tag"]!.GetValue<string>();
        node["tag"] = (tag[0] == 'A' ? 'B' : 'A') + tag[1..];
        File.WriteAllText(file, node.ToJsonString());

        var package = Export(seeded, temporary.Sub("package"));

        Assert.Equal(2, package.IncludedDiagnosisIds.Count);
        Assert.DoesNotContain(tampered.DiagnosisId, package.IncludedDiagnosisIds);
        Assert.Equal("IntegrityMismatch", package.Excluded[tampered.DiagnosisId]);
        var record = archive.ReadExport(package.PackageId);
        Assert.Equal("IntegrityMismatch", record["excluded"]![tampered.DiagnosisId]!.GetValue<string>());
        Assert.DoesNotContain(tampered.DiagnosisId, string.Join("\n", Directory.EnumerateFiles(package.Directory, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)), StringComparison.Ordinal);

        // With nothing reviewable at all, no package directory is produced.
        using var empty = DiagnosisArchive.OpenOrCreate(temporary.Sub("empty"), KnowledgeDiagnosisArchiveTests.Protector);
        empty.Store(OpaqueIds.NewDiagnosis(), KnowledgeDiagnosisArchiveTests.Identity("A", 1, seeded.CorpusHash, seeded.Attestation), null);
        var none = temporary.Sub("none");
        Assert.Equal("NothingReviewable", KnowledgeDiagnosisArchiveTests.ArchiveCode(() =>
            BlindReviewPackage.Export(empty, none, seeded.CorpusPath, seeded.CorpusHash, seeded.Attestation, Now)));
        Assert.False(Directory.Exists(none));
    }

    [Fact]
    public void ExportRefusesToOverwrite_ToNestInTheArchive_OrToUseARelativePath_AndIsOwnerOnly()
    {
        using var temporary = new TempDirectory();
        var seeded = Seed(temporary);
        using var archive = seeded.Archive;
        var occupied = temporary.Sub("occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "do not touch");

        Assert.Equal("LocationOccupied", KnowledgeDiagnosisArchiveTests.ArchiveCode(() => Export(seeded, occupied)));
        Assert.Equal("keep.txt", Assert.Single(Directory.GetFiles(occupied)).Split(Path.DirectorySeparatorChar)[^1]);
        Assert.Equal("PackageOverlapsArchive", KnowledgeDiagnosisArchiveTests.ArchiveCode(() => Export(seeded, Path.Combine(seeded.Root, "package"))));
        Assert.Equal("OutputNotAbsolute", KnowledgeDiagnosisArchiveTests.ArchiveCode(() => Export(seeded, "relative-package")));

        var package = Export(seeded, temporary.Sub("package"));
        foreach (var location in new[] { package.Directory, Path.Combine(package.Directory, "cards"), Path.Combine(package.Directory, "evidence"),
            Path.Combine(package.Directory, "rubric.json"), Path.Combine(package.Directory, "cards", package.IncludedDiagnosisIds[0] + ".txt") })
            KnowledgeDiagnosisArchiveTests.AssertOwnerOnly(location);
        // A second export of the same archive is a new package with a new id; the first is untouched.
        var again = Export(seeded, temporary.Sub("package-2"));
        Assert.NotEqual(package.PackageId, again.PackageId);
        Assert.Equal(package.ManifestSha256, DiagnosisArchive.Sha256(File.ReadAllBytes(Path.Combine(package.Directory, "package-manifest.json"))));
    }

    [Fact]
    public void ResidualBlindingRisk_IsMeasuredPrivately_WithoutTouchingTheDiagnosis()
    {
        using var temporary = new TempDirectory();
        var seeded = Seed(temporary);
        using var archive = seeded.Archive;
        var package = Export(seeded, temporary.Sub("package"));

        var risk = archive.ReadExport(package.PackageId)["blindingRisk"]!.AsObject();
        Assert.True(risk[seeded.ByCondition["B"].DiagnosisId]!["suspected"]!.GetValue<bool>());
        Assert.True(risk[seeded.ByCondition["B"].DiagnosisId]!["identifierMentions"]!.GetValue<int>() >= 1);
        Assert.False(risk[seeded.ByCondition["A"].DiagnosisId]!["suspected"]!.GetValue<bool>());
        // The scan is informational only: the exported text is the stored text.
        Assert.Equal(TextB, File.ReadAllText(Path.Combine(package.Directory, "cards", seeded.ByCondition["B"].DiagnosisId + ".txt")));
        Assert.DoesNotContain("suspected", File.ReadAllText(Path.Combine(package.Directory, "package-manifest.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluationFiles_AreStrict_AndTheirProblemsNeverEchoReviewerOrDiagnosisText()
    {
        var pk = OpaqueIds.NewPackage();
        var dx = OpaqueIds.NewDiagnosis();
        var hash = new string('a', 64);
        JsonObject Filled(Action<JsonObject>? mutate = null)
        {
            var node = EvaluationFile.Template(pk, dx, hash, "Complete");
            node["status"] = "Final";
            node["reviewer"] = "R.R.";
            foreach (var criterion in ReviewRubric.Criteria) node["scores"]![criterion.Key] = 2;
            mutate?.Invoke(node);
            return node;
        }

        var draft = EvaluationFile.Parse(EvaluationFile.Encode(EvaluationFile.Template(pk, dx, hash, "Complete")));
        Assert.Empty(draft.Problems);
        Assert.False(draft.Value!.IsFinal);
        Assert.Null(draft.Value.Total); // an unfilled score is not a zero

        var good = EvaluationFile.Parse(EvaluationFile.Encode(Filled(n => n["claims"] = new JsonArray(
            new JsonObject { ["category"] = ClaimCategories.UnsupportedCausalInference, ["quote"] = "SECRET-QUOTE", ["severity"] = "High", ["comment"] = "no corpus basis" },
            new JsonObject { ["category"] = ClaimCategories.CorrectFact, ["corpusEvidence"] = "Kernel-Power 41" }))));
        Assert.Empty(good.Problems);
        Assert.True(good.Value!.IsFinal);
        Assert.Equal(10, good.Value.Total);
        Assert.Equal(2, good.Value.Claims.Count);

        (string Name, JsonObject Node, string Problem)[] bad =
        [
            ("range", Filled(n => n["scores"]!["causeVersusClue"] = 3), "ScoreOutOfRange:causeVersusClue"),
            ("negative", Filled(n => n["scores"]!["causeVersusClue"] = -1), "ScoreOutOfRange:causeVersusClue"),
            ("fraction", Filled(n => n["scores"]!["verifiableCitations"] = 1.5), "ScoreOutOfRange:verifiableCitations"),
            ("missing", Filled(n => n["scores"]!["coverageAndUncertainty"] = null), "MissingScore:coverageAndUncertainty"),
            ("category", Filled(n => n["claims"] = new JsonArray(new JsonObject { ["category"] = "Whatever", ["quote"] = "SECRET-QUOTE" })), "UnknownClaimCategory"),
            ("severity", Filled(n => n["claims"] = new JsonArray(new JsonObject { ["category"] = ClaimCategories.CorrectFact, ["severity"] = "Extreme" })), "InvalidSeverity"),
            ("status", Filled(n => n["status"] = "Done"), "InvalidStatus"),
            ("criterion", Filled(n => n["scores"]!["bonus"] = 2), "UnknownCriterion"),
            ("package", Filled(n => n["packageId"] = "pk-NOT-AN-ID"), "InvalidPackageId"),
            ("format", Filled(n => n["format"] = "other"), "WrongFormat"),
        ];
        foreach (var (name, node, problem) in bad)
        {
            var parsed = EvaluationFile.Parse(EvaluationFile.Encode(node));
            Assert.Null(parsed.Value);
            Assert.True(parsed.Problems.Contains(problem), $"{name}: expected {problem}, got {string.Join(",", parsed.Problems)}");
            Assert.DoesNotContain("SECRET-QUOTE", string.Join("|", parsed.Problems), StringComparison.Ordinal);
        }

        Assert.Equal("InvalidJson", Assert.Single(EvaluationFile.Parse(Encoding.UTF8.GetBytes("{ not json SECRET-QUOTE")).Problems));
        Assert.Equal("InvalidSize", Assert.Single(EvaluationFile.Parse([]).Problems));
        Assert.Equal("InvalidSize", Assert.Single(EvaluationFile.Parse(new byte[EvaluationFile.MaximumBytes + 1]).Problems));
    }

    [Fact]
    public void TheJoin_ReunitesEvaluationsAfterTheReview_KeepsQualitySeparateFromKnowledgeMetrics_AndNeverScoresWhatWasNotMeasured()
    {
        using var temporary = new TempDirectory();
        var seeded = Seed(temporary);
        using var archive = seeded.Archive;
        var package = Export(seeded, temporary.Sub("package"));
        var evaluations = Path.Combine(package.Directory, "evaluations");

        // The reviewer fills A (Final, 10/10), B (Final, mixed, with claims) and leaves C as a draft.
        Fill(evaluations, seeded.ByCondition["A"].DiagnosisId, 2, 2, 2, 2, 2, [], final: true);
        Fill(evaluations, seeded.ByCondition["B"].DiagnosisId, 2, 1, 0, 1, 2,
            [(ClaimCategories.UnsupportedCausalInference, "SECRET-QUOTE-B"), (ClaimCategories.HallucinationOrNonexistentReference, "SECRET-QUOTE-B2"), (ClaimCategories.CorrectFact, "ok")], final: true);
        Fill(evaluations, seeded.ByCondition["C"].DiagnosisId, 1, 1, 1, 1, 1, [], final: false);

        // Two result roots (two repetitions); A1 is consistent, B1 disagrees on the hash, C1 has no result file.
        var results1 = WriteResult(temporary, "rep1", seeded.ByCondition["A"], textSha256: null);
        WriteResult(temporary, "rep1", seeded.ByCondition["B"], textSha256: new string('9', 64));
        var results2 = temporary.Sub("rep2");
        Directory.CreateDirectory(results2);

        var joined = DiagnosisReviewJoin.Run(archive, package.PackageId, evaluations, [results1, results2], Now);

        var document = joined.Document;
        var byId = joined.Rows.ToDictionary(r => r.Entry.DiagnosisId);
        Assert.Equal(QualityStatus.Evaluated, byId[seeded.ByCondition["A"].DiagnosisId].Status);
        Assert.Equal(10, byId[seeded.ByCondition["A"].DiagnosisId].Evaluation!.Total);
        Assert.Equal(6, byId[seeded.ByCondition["B"].DiagnosisId].Evaluation!.Total);
        Assert.Equal("NotFinal", byId[seeded.ByCondition["C"].DiagnosisId].Problem);
        Assert.Equal(QualityStatus.EvaluationRejected, byId[seeded.ByCondition["C"].DiagnosisId].Status);
        var none = joined.Rows.Single(r => r.Entry.Availability == DiagnosisAvailability.None);
        Assert.Equal((QualityStatus.NotMeasurable, "NoDiagnosis"), (none.Status, none.Problem));
        Assert.Null(none.Evaluation); // no score, in particular no zero
        Assert.Equal("Consistent", byId[seeded.ByCondition["A"].DiagnosisId].ResultJson);
        Assert.Equal("Inconsistent", byId[seeded.ByCondition["B"].DiagnosisId].ResultJson);
        Assert.Equal("NotFound", byId[seeded.ByCondition["C"].DiagnosisId].ResultJson);

        var summary = document["byCondition"]!.AsObject();
        Assert.Equal(10.0, summary["A"]!["meanTotal"]!.GetValue<double>());
        Assert.Equal(1, summary["A"]!["evaluated"]!.GetValue<int>());
        Assert.Equal(1, summary["A"]!["notMeasurable"]!.GetValue<int>());
        Assert.Equal(2, summary["A"]!["runs"]!.GetValue<int>());
        Assert.Null(summary["C"]!["meanTotal"]); // nothing evaluated: no mean, not zero
        Assert.Equal(1, summary["B"]!["claimTotals"]![ClaimCategories.HallucinationOrNonexistentReference]!.GetValue<int>());

        // Quality and Knowledge access stay in separate records; the join copies no access metric and no text.
        var text = document.ToJsonString();
        Assert.Equal(package.ManifestSha256, document["packageManifestSha256"]!.GetValue<string>());
        Assert.Contains("Intentionally not merged", document["knowledgeAccessMetrics"]!.GetValue<string>(), StringComparison.Ordinal);
        foreach (var forbidden in new[] { "knowledgeUsed", "knowledgeReads", "knowledgeDiscovery", "readStage", "SECRET-QUOTE", "RUN-SENTINEL" })
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        // It is written create-new inside the private area, owner-only, and re-reading it gives the same document.
        Assert.StartsWith(Path.Combine(seeded.Root, "private", "quality"), joined.OutputPath, StringComparison.OrdinalIgnoreCase);
        KnowledgeDiagnosisArchiveTests.AssertOwnerOnly(joined.OutputPath);
        Assert.Equal(text, JsonNode.Parse(File.ReadAllText(joined.OutputPath))!.ToJsonString());
        var second = DiagnosisReviewJoin.Run(archive, package.PackageId, evaluations, [results1, results2], Now);
        Assert.NotEqual(joined.OutputPath, second.OutputPath); // a join never overwrites an earlier one
    }

    [Fact]
    public void TheJoin_RefusesEvaluationsThatDescribeAnotherPackageOrText_AndReportsStrays()
    {
        using var temporary = new TempDirectory();
        var seeded = Seed(temporary, withNoDiagnosis: false);
        using var archive = seeded.Archive;
        var package = Export(seeded, temporary.Sub("package"));
        var evaluations = Path.Combine(package.Directory, "evaluations");
        var a = seeded.ByCondition["A"].DiagnosisId;
        var b = seeded.ByCondition["B"].DiagnosisId;
        Fill(evaluations, a, 2, 2, 2, 2, 2, [], final: true);
        Fill(evaluations, b, 2, 2, 2, 2, 2, [], final: true);
        // A's form is pointed at a different text; B's form is a copy from another package.
        Edit(Path.Combine(evaluations, a + ".evaluation.json"), n => n["diagnosisTextSha256"] = new string('b', 64));
        Edit(Path.Combine(evaluations, b + ".evaluation.json"), n => n["packageId"] = OpaqueIds.NewPackage());
        var stray = OpaqueIds.NewDiagnosis();
        File.Copy(Path.Combine(evaluations, a + ".evaluation.json"), Path.Combine(evaluations, stray + ".evaluation.json"));
        File.WriteAllText(Path.Combine(evaluations, seeded.ByCondition["C"].DiagnosisId + ".evaluation.json"), "{ broken");

        var joined = DiagnosisReviewJoin.Run(archive, package.PackageId, evaluations, [], Now);

        var byId = joined.Rows.ToDictionary(r => r.Entry.DiagnosisId);
        Assert.Equal(("DescribesAnotherDiagnosis", QualityStatus.EvaluationRejected), (byId[a].Problem, byId[a].Status));
        Assert.Equal(("DescribesAnotherDiagnosis", QualityStatus.EvaluationRejected), (byId[b].Problem, byId[b].Status));
        Assert.Equal("InvalidJson", byId[seeded.ByCondition["C"].DiagnosisId].Problem);
        Assert.Equal(stray, Assert.Single(joined.Document["evaluationsWithoutRun"]!.AsArray())!.GetValue<string>());
        Assert.All(joined.Rows, r => Assert.Equal("NotFound", r.ResultJson));
        Assert.Equal("RecordMissing", KnowledgeDiagnosisArchiveTests.ArchiveCode(() =>
            DiagnosisReviewJoin.Run(archive, OpaqueIds.NewPackage(), evaluations, [], Now)));
    }

    [Fact]
    public void ADiagnosisThatWasExcludedFromThePackage_OrFailedItsIntegrity_IsNotMeasurable_NotZero()
    {
        using var temporary = new TempDirectory();
        var seeded = Seed(temporary);
        using var archive = seeded.Archive;
        var c = seeded.ByCondition["C"];
        File.Delete(Path.Combine(seeded.Root, "records", c.DiagnosisId + ".sealed")); // lost before the export
        var package = Export(seeded, temporary.Sub("package"));
        Assert.Equal("RecordMissing", package.Excluded[c.DiagnosisId]);

        var joined = DiagnosisReviewJoin.Run(archive, package.PackageId, Path.Combine(package.Directory, "evaluations"), [], Now);

        var row = joined.Rows.Single(r => r.Entry.DiagnosisId == c.DiagnosisId);
        Assert.Equal(QualityStatus.NotMeasurable, row.Status);
        Assert.Equal("NotInPackage", row.Problem);
        Assert.Null(row.Evaluation);
        Assert.Equal(0, joined.Document["byCondition"]!["C"]!["evaluated"]!.GetValue<int>());
    }

    private static void Fill(string evaluations, string id, int s1, int s2, int s3, int s4, int s5, (string Category, string Quote)[] claims, bool final) =>
        Edit(Path.Combine(evaluations, id + ".evaluation.json"), node =>
        {
            node["status"] = final ? "Final" : "Draft";
            node["reviewer"] = "R.R.";
            var scores = new[] { s1, s2, s3, s4, s5 };
            for (var i = 0; i < scores.Length; i++) node["scores"]![ReviewRubric.Criteria[i].Key] = scores[i];
            node["claims"] = new JsonArray([.. claims.Select(c => (JsonNode)new JsonObject { ["category"] = c.Category, ["quote"] = c.Quote })]);
        });

    private static void Edit(string file, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        change(node);
        File.WriteAllBytes(file, EvaluationFile.Encode(node));
    }

    private static string WriteResult(TempDirectory temporary, string repetition, DiagnosisMapEntry entry, string? textSha256)
    {
        var root = temporary.Sub(repetition);
        var directory = Path.Combine(root, entry.Identity.Condition);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "result.json"), new JsonObject
        {
            ["runId"] = entry.Identity.RunId.ToString("D"),
            ["knowledgeUsed"] = true, // present in the real file; the join must not copy it
            ["diagnosis"] = new JsonObject
            {
                ["diagnosisId"] = entry.DiagnosisId, ["textSha256"] = textSha256 ?? entry.TextSha256, ["availability"] = entry.Availability.ToString(),
            },
        }.ToJsonString());
        return root;
    }
}
