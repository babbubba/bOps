// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA2000 // Each world is owned by its export and disposed by the export boundary.
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge.Tests;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// D7: capture of the answer the agent actually produced, its archiving before the temporary world is deleted, and what reaches
/// <c>result.json</c>. Offline: a scripted model, invented diagnosis text, no provider, no Event Log.
/// </summary>
public sealed class KnowledgeDiagnosisCaptureTests
{
    private const string Answer = "FINAL-ANSWER-SENTINEL: Kernel-Power 41 followed by WHEA 18 at 10:12 UTC; cause not established.";
    private const string ToolOutput = "TOOL-OUTPUT-SENTINEL raw event listing";

    private static TaskState State(Guid id, AgentTaskStatus status, params PlanStep[] steps) =>
        new(id, NodeId.Local, "Offline synthetic task", status, steps, [], DateTimeOffset.UnixEpoch)
        {
            Accounting = new TaskAccounting(13, 1, 0),
            TerminalReason = status == AgentTaskStatus.Failed ? new TaskTerminalReason(TaskTerminalKind.AttemptDurationBudget) : null,
        };

    private static PlanStep ToolStep(string observation) => new(0, "Read events", null, null, observation);

    private static PlanStep FinalStep(string? text, int index = 1) => new(index, FinalResponse.Marker, null, null, text);

    private static DiagnosisArchiveSettings Settings(string root) => new(DiagnosisArchiveSettings.Authorized, root, KnowledgeDiagnosisArchiveTests.Protector);

    private static async Task<(KnowledgeBenchmarkExport Export, JsonElement Result, string WorldDirectory)> RunAsync(
        TempDirectory temporary, string condition, DiagnosisArchiveSettings settings, Func<Guid, TaskState> state,
        Exception? thenThrow = null, int repetition = 2)
    {
        var output = temporary.Sub("results-" + condition + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(output);
        KnowledgeBenchmarkExport? observed = null;
        string world = "";
        var execution = KnowledgeBenchmarkExport.RunAsync(output, condition, async export =>
        {
            observed = export;
            export.ArchiveSettings = settings;
            export.Repetition = repetition;
            export.CorpusSha256 = new string('1', 64);
            export.CorpusAttestationSha256 = new string('2', 64);
            export.World = new KnowledgeRunnerWorld();
            world = Path.GetDirectoryName(export.World.AuditFile)!;
            await export.ExecuteTaskAsync(() => Task.FromResult(state(export.RunId)));
            if (thenThrow is not null) throw thenThrow;
        });
        if (thenThrow is not null) Assert.Same(thenThrow, await Assert.ThrowsAsync(thenThrow.GetType(), () => execution));
        else await execution;
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, condition, "result.json")));
        return (observed!, document.RootElement.Clone(), world);
    }

    [Fact]
    public async Task ACompletedAnswer_IsArchivedBeforeTheWorldIsDeleted_AndResultJsonKeepsOnlyIdHashAndState()
    {
        using var temporary = new TempDirectory();
        var root = temporary.Sub("archive");
        var (export, result, world) = await RunAsync(temporary, "B", Settings(root),
            id => State(id, AgentTaskStatus.Completed, ToolStep(ToolOutput), FinalStep(Answer)));

        Assert.False(Directory.Exists(world), "the benchmark world must have been deleted");
        var block = result.GetProperty("diagnosis");
        Assert.Equal(export.DiagnosisId, block.GetProperty("diagnosisId").GetString());
        Assert.Equal("Complete", block.GetProperty("availability").GetString());
        Assert.Equal("PersistedFinalStep", block.GetProperty("source").GetString());
        Assert.Equal("Stored", block.GetProperty("archiveStatus").GetString());
        Assert.True(block.GetProperty("qualityMeasurable").GetBoolean());
        Assert.Equal("PendingBlindReview", block.GetProperty("qualityStatus").GetString());
        Assert.Equal(2, block.GetProperty("repetition").GetInt32());
        Assert.Equal(DiagnosisArchive.Sha256(System.Text.Encoding.UTF8.GetBytes(Answer)), block.GetProperty("textSha256").GetString());
        Assert.Equal(64, block.GetProperty("sealedSha256").GetString()!.Length);

        // The text survives the world, in the protected archive only; the ordinary export carries none of it.
        using var archive = DiagnosisArchive.Open(root, KnowledgeDiagnosisArchiveTests.Protector);
        Assert.Equal(Answer, archive.ReadDiagnosis(export.DiagnosisId).Text);
        var entry = Assert.Single(archive.ReadMap().Entries);
        Assert.Equal(("B", 2, export.RunId), (entry.Identity.Condition, entry.Identity.Repetition, entry.Identity.RunId));
        Assert.Equal(new string('1', 64), entry.Identity.CorpusSha256);
        Assert.Equal(new string('2', 64), entry.Identity.AttestationSha256);
        var shared = result.ToString() + string.Join("", Directory.GetFiles(Path.GetDirectoryName(root)!, "*.jsonl", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("FINAL-ANSWER-SENTINEL", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("Kernel-Power", shared, StringComparison.Ordinal);
        Assert.DoesNotContain(root, shared, StringComparison.OrdinalIgnoreCase);
        // The ordinary free-text redaction of D1-D3 is unchanged.
        Assert.Equal(KnowledgeBenchmarkExport.RedactedFreeText, result.GetProperty("finalObservation").GetString());
    }

    [Fact]
    public async Task AFailedOrTimedOutTask_StillKeepsTheAnswerItPersisted_AsPartial()
    {
        using var temporary = new TempDirectory();
        var root = temporary.Sub("archive");
        var (export, result, _) = await RunAsync(temporary, "C", Settings(root),
            id => State(id, AgentTaskStatus.Failed, ToolStep(ToolOutput), FinalStep(Answer)));

        var block = result.GetProperty("diagnosis");
        Assert.Equal("Partial", block.GetProperty("availability").GetString());
        Assert.Equal("TaskNotCompleted", block.GetProperty("reason").GetString());
        Assert.Equal("Stored", block.GetProperty("archiveStatus").GetString());
        Assert.True(block.GetProperty("qualityMeasurable").GetBoolean());
        Assert.Equal("Failed", result.GetProperty("status").GetString());
        using var archive = DiagnosisArchive.Open(root, KnowledgeDiagnosisArchiveTests.Protector);
        Assert.Equal(Answer, archive.ReadDiagnosis(export.DiagnosisId).Text);
    }

    [Fact]
    public async Task AnExceptionAfterTheTask_DoesNotLoseTheDiagnosis_AndIsStillRethrown()
    {
        using var temporary = new TempDirectory();
        var root = temporary.Sub("archive");
        var failure = new TimeoutException("controlled");
        var (export, result, _) = await RunAsync(temporary, "A", Settings(root),
            id => State(id, AgentTaskStatus.Completed, FinalStep(Answer)), thenThrow: failure);

        Assert.Equal("Stored", result.GetProperty("diagnosis").GetProperty("archiveStatus").GetString());
        Assert.Equal("Timeout", result.GetProperty("terminalCause").GetString());
        using var archive = DiagnosisArchive.Open(root, KnowledgeDiagnosisArchiveTests.Protector);
        Assert.Equal(Answer, archive.ReadDiagnosis(export.DiagnosisId).Text);
    }

    [Fact]
    public async Task NoAnswer_IsRecordedAsNoDiagnosis_AndAToolObservationIsNeverPromotedToOne()
    {
        using var temporary = new TempDirectory();
        var root = temporary.Sub("archive");
        var (export, result, _) = await RunAsync(temporary, "A", Settings(root),
            id => State(id, AgentTaskStatus.Failed, ToolStep(ToolOutput),
                // A tool step whose description happens to equal the marker is still a tool step; a blank final step is no answer.
                new PlanStep(1, FinalResponse.Marker, new ModelToolCall("events", "system.events", ToolArguments.FromJson(new JsonObject())), null, ToolOutput),
                FinalStep("   ", 2)));

        var block = result.GetProperty("diagnosis");
        Assert.Equal("None", block.GetProperty("availability").GetString());
        Assert.Equal("None", block.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, block.GetProperty("textSha256").ValueKind);
        Assert.False(block.GetProperty("qualityMeasurable").GetBoolean());
        Assert.Equal("NotMeasurable", block.GetProperty("qualityStatus").GetString());
        Assert.Equal("NoDiagnosis", block.GetProperty("qualityNotMeasurableReason").GetString());
        using var archive = DiagnosisArchive.Open(root, KnowledgeDiagnosisArchiveTests.Protector);
        Assert.Equal("RecordMissing", KnowledgeDiagnosisArchiveTests.ArchiveCode(() => archive.ReadDiagnosis(export.DiagnosisId)));
        Assert.DoesNotContain("TOOL-OUTPUT-SENTINEL", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnconfiguredOrUnauthorizedArchive_WritesNothing_AndIsReportedAsNotMeasurable()
    {
        using var temporary = new TempDirectory();
        foreach (var (settings, expected) in new[]
        {
            (DiagnosisArchiveSettings.Disabled, DiagnosisArchiveOutcome.NotConfigured),
            (new DiagnosisArchiveSettings(DiagnosisArchiveOutcome.NotAuthorized, null, null), DiagnosisArchiveOutcome.NotAuthorized),
        })
        {
            var (_, result, _) = await RunAsync(temporary, "B", settings,
                id => State(id, AgentTaskStatus.Completed, FinalStep(Answer)));
            var block = result.GetProperty("diagnosis");
            Assert.Equal(expected, block.GetProperty("archiveStatus").GetString());
            Assert.False(block.GetProperty("qualityMeasurable").GetBoolean());
            Assert.Equal("Complete", block.GetProperty("availability").GetString()); // observed, but not verifiable
            Assert.Equal(JsonValueKind.Null, block.GetProperty("textSha256").ValueKind);
            Assert.Equal("Archive" + expected, block.GetProperty("qualityNotMeasurableReason").GetString());
            Assert.DoesNotContain("FINAL-ANSWER-SENTINEL", result.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnArchiveFailure_IsLoud_ClosedCoded_AndDoesNotDestroyTheRunResult()
    {
        using var temporary = new TempDirectory();
        var squatter = temporary.Sub("archive");
        await File.WriteAllTextAsync(squatter, "a file occupies the archive root");
        var (export, result, _) = await RunAsync(temporary, "B", Settings(squatter),
            id => State(id, AgentTaskStatus.Completed, FinalStep(Answer)));

        var block = result.GetProperty("diagnosis");
        Assert.Equal("Failed", block.GetProperty("archiveStatus").GetString());
        Assert.Equal("LocationOccupied", block.GetProperty("archiveFailureCode").GetString());
        Assert.False(block.GetProperty("qualityMeasurable").GetBoolean());
        Assert.Equal("ArchiveFailed:LocationOccupied", block.GetProperty("qualityNotMeasurableReason").GetString());
        Assert.Equal("Completed", result.GetProperty("status").GetString()); // the run itself is preserved
        Assert.Equal("Failed", export.DiagnosisOutcome!.Status);
        Assert.DoesNotContain("FINAL-ANSWER-SENTINEL", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(squatter, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_PrefersThePersistedFinalStep_AndNeverInventsAnAnswer()
    {
        var capture = new DiagnosisCapture();
        Assert.Null(capture.Resolve(null));
        Assert.Null(capture.Resolve(State(Guid.NewGuid(), AgentTaskStatus.Failed, ToolStep(ToolOutput))));

        capture.Observe(Step(), new ModelResponse("an unpersisted reply", [], true, null));
        var observed = capture.Resolve(null)!;
        Assert.Equal((DiagnosisAvailability.Partial, DiagnosisSource.ObservedModelReply, DiagnosisReason.NotPersisted),
            (observed.Availability, observed.Source, observed.Reason));

        var persisted = capture.Resolve(State(Guid.NewGuid(), AgentTaskStatus.Completed, FinalStep(Answer)))!;
        Assert.Equal((Answer, DiagnosisAvailability.Complete, DiagnosisSource.PersistedFinalStep), (persisted.Text, persisted.Availability, persisted.Source));
    }

    [Fact]
    public void Observe_IgnoresPlansToolCallsEmptyRepliesAndTheGuardsCheckReplies_AndKeepsTruncatedPartialText()
    {
        var capture = new DiagnosisCapture();
        capture.Observe(Planning(), KnowledgeBenchmark.Plan); // plan JSON is not an answer
        capture.Observe(Step(), KnowledgeBenchmark.Events with { TextResponse = "calling a tool" }); // a tool step
        capture.Observe(Step(), new ModelResponse("   ", [], true, null));
        capture.Observe(Step(), new ModelResponse("""{"contradictions":[]}""", [], true, null));
        Assert.Null(capture.Resolve(null));

        var truncated = new ModelResponse(null, [], false, null)
        {
            Details = new ModelCallDetails(null, "length", null,
                """{"choices":[{"finish_reason":"length","message":{"content":"PARTIAL-SENTINEL cut mid","reasoning_content":"REASONING-NOT-AN-ANSWER"}}]}"""),
        };
        capture.Observe(Step(), truncated);
        var partial = capture.Resolve(null)!;
        Assert.Equal("PARTIAL-SENTINEL cut mid", partial.Text);
        Assert.Equal((DiagnosisAvailability.Partial, DiagnosisReason.TruncatedAtOutputLimit), (partial.Availability, partial.Reason));

        // Truncated with nothing readable in the raw body: still no invented answer, and the earlier candidate is kept.
        capture.Observe(Step(), truncated with { Details = new ModelCallDetails(null, "length", null, "not json") });
        Assert.Equal("PARTIAL-SENTINEL cut mid", capture.Resolve(null)!.Text);
        capture.Observe(Step(), new ModelResponse("the later, complete reply", [], true, null));
        Assert.Equal("the later, complete reply", capture.Resolve(null)!.Text);
    }

    [Fact]
    public async Task ThroughTheRealRunner_ACompleteAnswerAndAFrozenCorpus_AreArchivedWithTheirProvenance()
    {
        if (!OperatingSystem.IsWindows()) return; // the governed canary composition (system.events replay) exists on Windows only
        using var temporary = new TempDirectory();
        var (path, hash, attestation) = CorpusFixtures.Approved(temporary);
        var frozen = KnowledgeBenchmark.ReplayFrozen(path, hash, attestation);
        var root = temporary.Sub("archive");
        var output = temporary.Sub("results");
        Directory.CreateDirectory(output);
        var finalState = default(TaskState);
        await KnowledgeBenchmarkExport.RunAsync(output, "A", async export =>
        {
            export.World = new KnowledgeRunnerWorld();
            export.ArchiveSettings = Settings(root);
            export.CorpusSha256 = hash;
            export.CorpusAttestationSha256 = attestation;
            export.CorpusProvenance = frozen.Provenance;
            var model = new BenchmarkProbe([KnowledgeBenchmark.Plan, KnowledgeBenchmark.Events, Script.Final(Answer)]);
            var rig = KnowledgeBenchmark.ComposeReplay(export.World, model, "A", path, hash, observation: export.ModelObservation, frozen: frozen.Activation);
            export.Store = rig.Store;
            await export.ExecuteTaskAsync(() => rig.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA, export.RunId));
            finalState = export.State;
            Assert.Equal(AgentTaskStatus.Completed, export.State!.Status);
        });

        using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "A", "result.json")));
        var block = result.RootElement.GetProperty("diagnosis");
        Assert.Equal("Complete", block.GetProperty("availability").GetString());
        Assert.Equal("Stored", block.GetProperty("archiveStatus").GetString());
        // D1-D3 governance is untouched by D7: the corpus is still only operator-attested, and the free text is still withheld.
        Assert.Equal("OperatorAttested", result.RootElement.GetProperty("corpusProvenance").GetProperty("classification").GetString());
        Assert.Equal(attestation, result.RootElement.GetProperty("corpusProvenance").GetProperty("attestationSha256").GetString());
        Assert.Equal(KnowledgeBenchmarkExport.RedactedFreeText, result.RootElement.GetProperty("finalObservation").GetString());
        using var archive = DiagnosisArchive.Open(root, KnowledgeDiagnosisArchiveTests.Protector);
        var entry = Assert.Single(archive.ReadMap().Entries);
        var stored = archive.ReadDiagnosis(entry.DiagnosisId);
        Assert.Equal(finalState!.Steps.Last(FinalResponse.IsFinalStep).Observation, stored.Text);
        Assert.StartsWith(Answer, stored.Text, StringComparison.Ordinal); // the runtime may append its limitations section; the answer is the prefix
        Assert.Equal((hash, attestation), (entry.Identity.CorpusSha256, entry.Identity.AttestationSha256));
    }

    [Fact]
    public async Task ThroughTheRealRunner_ATruncatedFinalReply_IsKeptAsPartial_AndNoReplyAtAllIsNoDiagnosis()
    {
        if (!OperatingSystem.IsWindows()) return; // the governed canary composition (system.events replay) exists on Windows only
        using var temporary = new TempDirectory();
        var (path, hash, attestation) = CorpusFixtures.Approved(temporary);
        var frozen = KnowledgeBenchmark.ReplayFrozen(path, hash, attestation);
        var truncated = new ModelResponse(null, [], false, new ModelUsage(5, 5, null))
        {
            Details = new ModelCallDetails(null, "length", null, """{"choices":[{"message":{"content":"PARTIAL-SENTINEL the cut-off diagnosis"}}]}"""),
        };
        var empty = new ModelResponse(string.Empty, [], true, null);
        foreach (var (name, final, expectedAvailability) in new[]
        {
            ("truncated", truncated, "Partial"),
            ("empty", empty, "None"),
        })
        {
            var root = temporary.Sub("archive-" + name);
            var output = temporary.Sub("results-" + name);
            Directory.CreateDirectory(output);
            string? worldDirectory = null;
            await KnowledgeBenchmarkExport.RunAsync(output, "A", async export =>
            {
                export.World = new KnowledgeRunnerWorld();
                worldDirectory = Path.GetDirectoryName(export.World.AuditFile);
                export.ArchiveSettings = Settings(root);
                export.CorpusSha256 = hash;
                export.CorpusAttestationSha256 = attestation;
                export.CorpusProvenance = frozen.Provenance;
                var model = new BenchmarkProbe([KnowledgeBenchmark.Plan, KnowledgeBenchmark.Events, final, final, final, final, final, final]);
                var rig = KnowledgeBenchmark.ComposeReplay(export.World, model, "A", path, hash, observation: export.ModelObservation, frozen: frozen.Activation);
                export.Store = rig.Store;
                await export.ExecuteTaskAsync(() => rig.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA, export.RunId));
            });

            using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "A", "result.json")));
            Assert.Equal("Failed", result.RootElement.GetProperty("status").GetString());
            var block = result.RootElement.GetProperty("diagnosis");
            Assert.Equal(expectedAvailability, block.GetProperty("availability").GetString());
            Assert.Equal("Stored", block.GetProperty("archiveStatus").GetString());
            Assert.Equal(expectedAvailability != "None", block.GetProperty("qualityMeasurable").GetBoolean());
            Assert.NotNull(worldDirectory); // the archive is independent of the world's directory, which RunAsync has disposed
            using var archive = DiagnosisArchive.Open(root, KnowledgeDiagnosisArchiveTests.Protector);
            var entry = Assert.Single(archive.ReadMap().Entries);
            if (expectedAvailability == "Partial")
            {
                Assert.Equal("PARTIAL-SENTINEL the cut-off diagnosis", archive.ReadDiagnosis(entry.DiagnosisId).Text);
                Assert.Equal("TruncatedAtOutputLimit", block.GetProperty("reason").GetString());
            }
            else Assert.Empty(Directory.GetFiles(Path.Combine(root, "records")));
            Assert.DoesNotContain("PARTIAL-SENTINEL", result.RootElement.ToString(), StringComparison.Ordinal);
        }
    }

    private static ModelRequest Step() => new("You are an ops agent. Execute the current step.", [], []);

    private static ModelRequest Planning() => new("Plan the task; the tools listed are for later steps.", [], []);
}
