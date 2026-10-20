// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA2000 // Each world is disposed by its using scope.
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// Offline checks of provenance for a frozen corpus (D1): a saved file is attested by the operator, never "Captured", and only the
/// benchmark replay path accepts that. Nothing here runs a model, a Windows collector or the Event Log.
/// </summary>
[Collection("Benchmark capture environment")]
public sealed class KnowledgeCorpusAttestationTests
{
    private static string Flip(string text) => text[..^1] + (text[^1] == '0' ? '1' : '0');

    [Fact]
    public void ACapturedCorpus_ThenApproved_IsReplayableAsOperatorAttestedOnly()
    {
        using var temporary = new TempDirectory();
        var (corpus, hash, quality) = CorpusFixtures.Captured();
        Assert.True(quality.Suitable, quality.Describe());
        // In the capturing process the observation is Captured; the very same bytes reloaded from disk can never be.
        Assert.Equal("Captured", CanaryEventsCorpus.Provenance(corpus, hash, capturedHere: true)["classification"]!.GetValue<string>());
        var path = CorpusFixtures.Write(temporary, corpus);
        Assert.Equal("Unverified", CanaryEventsCorpus.Provenance(CanaryEventsCorpus.Read(path, hash).Corpus, hash)["classification"]!.GetValue<string>());

        var attestationHash = CorpusAttestation.Approve(path, hash, hash, CorpusFixtures.Now.AddHours(1));
        var frozen = KnowledgeBenchmark.ReplayFrozen(path, hash, attestationHash);

        var provenance = frozen.Provenance;
        Assert.Equal("OperatorAttested", provenance["classification"]!.GetValue<string>());
        Assert.Equal("OperatorAttested", provenance["acquisitionMetadataStatus"]!.GetValue<string>());
        Assert.Equal(CorpusAttestation.Guarantee, provenance["guarantee"]!.GetValue<string>());
        Assert.Contains("not independently provable", provenance["guarantee"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(attestationHash, provenance["attestationSha256"]!.GetValue<string>());
        Assert.Equal(hash, provenance["corpusSha256"]!.GetValue<string>());
        Assert.Equal(corpus["capture"]!["captureId"]!.GetValue<string>(), provenance["captureId"]!.GetValue<string>());
        Assert.Equal(corpus["capture"]!["manifestSha256"]!.GetValue<string>(), provenance["manifestSha256"]!.GetValue<string>());
        Assert.NotNull(frozen.Activation.Attestation);
        // The runtime bounds a string fact to 1024 characters; the provenance travels as one.
        Assert.InRange(CanaryEventsCorpus.Canonical(provenance).Length, 1, 1024);
        CanaryEventsCorpus.RequireBenchmarkProvenance(provenance);

        // The global guards are untouched: an attested file is still not "real"/"Captured" for them.
        Assert.Throws<InvalidOperationException>(() => CanaryEventsCorpus.RequireRealCorpus(provenance));
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.Replay(path, hash, requireCaptured: true));
        Assert.Throws<InvalidOperationException>(() =>
            CanaryEventsCorpus.RequireBenchmarkProvenance(CanaryEventsCorpus.Provenance(corpus, hash)));
        CanaryEventsCorpus.RequireBenchmarkProvenance(CanaryEventsCorpus.Provenance(corpus, hash, capturedHere: true));
    }

    [Fact]
    public void AnUnattestedFile_IsRefusedByTheBenchmarkReplay()
    {
        using var temporary = new TempDirectory();
        var (corpus, hash, _) = CorpusFixtures.Captured();
        var path = CorpusFixtures.Write(temporary, corpus);

        // No sidecar at all, and no pin: both must refuse; neither downgrades to an "Unverified" replay.
        Assert.Contains("no operator attestation", Assert.Throws<InvalidOperationException>(() =>
            KnowledgeBenchmark.ReplayFrozen(path, hash, new string('a', 64))).Message, StringComparison.Ordinal);
        Assert.Contains("attestation SHA-256 is required", Assert.Throws<InvalidOperationException>(() =>
            KnowledgeBenchmark.ReplayFrozen(path, hash, null)).Message, StringComparison.Ordinal);

        // The plain (non-benchmark) replay still works for what it was for, but it is "Unverified" and fails the benchmark guard.
        var plain = KnowledgeBenchmark.Replay(path, hash);
        Assert.Null(plain.Attestation);
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.Replay(path, hash, requireCaptured: true));
    }

    [Fact]
    public void WrongHashesAndAlteredFiles_AreRefused()
    {
        using var temporary = new TempDirectory();
        var (path, hash, attestation) = CorpusFixtures.Approved(temporary);

        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ReplayFrozen(path, new string('0', 64), attestation));
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ReplayFrozen(path, null, attestation));
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ReplayFrozen(path, hash, Flip(attestation)));
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ReplayFrozen(path, hash, "short"));
        _ = KnowledgeBenchmark.ReplayFrozen(path, hash, attestation); // Control: the untouched pair works.

        // Altered corpus bytes, even with a recomputed embedded hash, cannot match the separately pinned hash.
        var original = File.ReadAllBytes(path);
        var altered = JsonNode.Parse(original)!.AsObject();
        altered["corpus"]!["output"] = "altered";
        File.WriteAllText(path, altered.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ReplayFrozen(path, hash, attestation));
        File.WriteAllBytes(path, original);
        _ = KnowledgeBenchmark.ReplayFrozen(path, hash, attestation);

        // An altered attestation (any byte) fails its own pin.
        var sidecar = CorpusAttestation.PathFor(path);
        var sidecarBytes = File.ReadAllBytes(sidecar);
        var edited = System.Text.Encoding.UTF8.GetString(sidecarBytes).Replace("1970-01-02T01:00:00", "1970-01-02T02:00:00", StringComparison.Ordinal);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetString(sidecarBytes), edited);
        File.WriteAllText(sidecar, edited);
        Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ReplayFrozen(path, hash, attestation));
        File.WriteAllBytes(sidecar, sidecarBytes);
        _ = KnowledgeBenchmark.ReplayFrozen(path, hash, attestation);
    }

    [Fact]
    public void AForgedButSelfConsistentAttestation_DoesNotDescribeTheCorpus()
    {
        using var temporary = new TempDirectory();
        var (path, hash, _) = CorpusFixtures.Approved(temporary);
        var sidecar = CorpusAttestation.PathFor(path);
        var envelope = JsonNode.Parse(File.ReadAllBytes(sidecar))!.AsObject();
        var body = envelope["attestation"]!.AsObject();

        // Re-hashed on purpose: the attacker also controls the file, but not the facts it must match.
        string Reseal(Action<JsonObject> edit)
        {
            var copy = (JsonObject)body.DeepClone();
            edit(copy);
            var sha = CanaryEventsCorpus.JsonHash(copy);
            File.WriteAllText(sidecar, CanaryEventsCorpus.Canonical(new JsonObject { ["attestation"] = copy, ["sha256"] = sha }));
            return sha;
        }

        foreach (var edit in new Action<JsonObject>[]
                 {
                     b => b["captureId"] = Guid.NewGuid().ToString("D"),
                     b => b["corpusSha256"] = new string('b', 64),
                     b => b["approval"] = "self-approved",
                     b => b["minimization"] = "none",
                     b => b["quality"]!["usableEvents"] = 99,
                     b => b["arguments"] = "{}",
                     b => b["approvedUtc"] = "1969-12-31T00:00:00.0000000+00:00",
                 })
        {
            var pin = Reseal(edit);
            Assert.Throws<InvalidOperationException>(() => KnowledgeBenchmark.ReplayFrozen(path, hash, pin));
        }
    }

    [Fact]
    public void AnUnsuitableOrMismatchedCorpus_CannotBeApproved_AndLeavesNoAttestation()
    {
        using var temporary = new TempDirectory();
        var (empty, emptyHash, emptyQuality) = CorpusFixtures.Captured(CorpusFixtures.Output([]));
        Assert.False(emptyQuality.Suitable);
        var emptyPath = CorpusFixtures.Write(temporary, empty, "empty");
        var refusal = Assert.Throws<InvalidOperationException>(() => CorpusAttestation.Approve(emptyPath, emptyHash, emptyHash, CorpusFixtures.Now.AddHours(1)));
        Assert.Equal("Corpus quality gate failed: NoUsableEvents", refusal.Message);
        Assert.False(File.Exists(CorpusAttestation.PathFor(emptyPath)));

        var (corpus, hash, _) = CorpusFixtures.Captured();
        var path = CorpusFixtures.Write(temporary, corpus, "good");
        // The operator must repeat the exact reviewed hash; a different one (or an absent confirmation) approves nothing.
        Assert.Throws<InvalidOperationException>(() => CorpusAttestation.Approve(path, hash, new string('c', 64), CorpusFixtures.Now.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => CorpusAttestation.Approve(path, new string('c', 64), new string('c', 64), CorpusFixtures.Now.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => CorpusAttestation.Approve(path, hash, hash, CorpusFixtures.Now.AddHours(-1))); // Before the capture.
        Assert.False(File.Exists(CorpusAttestation.PathFor(path)));

        // A controlled (synthetic) source is not a native capture and can never be attested.
        var synthetic = CanaryEventsCorpus.Build(KnowledgeBenchmark.Arguments, CorpusFixtures.Output(CorpusFixtures.DiagnosticEvents()), "synthetic", CorpusFixtures.Now);
        var syntheticPath = CorpusFixtures.Write(temporary, synthetic, "synthetic");
        Assert.Contains("NotNativeCapture", Assert.Throws<InvalidOperationException>(() =>
            CorpusAttestation.Approve(syntheticPath, CanaryEventsCorpus.Hash(synthetic), CanaryEventsCorpus.Hash(synthetic), CorpusFixtures.Now.AddHours(1))).Message, StringComparison.Ordinal);

        // Approval is create-new: a recorded approval is never overwritten.
        _ = CorpusAttestation.Approve(path, hash, hash, CorpusFixtures.Now.AddHours(1));
        Assert.Throws<IOException>(() => CorpusAttestation.Approve(path, hash, hash, CorpusFixtures.Now.AddHours(2)));
    }

    [WindowsOnlyFact]
    public async Task ThreeConsecutiveReplays_GiveIdenticalDiagnosticBytes_InA_B_AndC()
    {
        using var temporary = new TempDirectory();
        var (path, hash, attestation) = CorpusFixtures.Approved(temporary);
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        var outputs = new List<string>();

        for (var repetition = 0; repetition < 3; repetition++)
        {
            foreach (var condition in new[] { "A", "B", "C" })
            {
                var frozen = KnowledgeBenchmark.ReplayFrozen(path, hash, attestation);
                using var world = new KnowledgeRunnerWorld();
                var knowledge = KnowledgeBenchmark.UsesKnowledge(condition);
                var probe = new BenchmarkProbe([]);
                var rig = KnowledgeBenchmark.ComposeReplay(world, probe, condition, path, hash,
                    knowledge ? raw : null, knowledge ? trust : null,
                    knowledge ? KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span) : null,
                    knowledge ? KnowledgeBenchmark.GrantAuthorization : null, frozen: frozen.Activation);

                var report = await rig.Runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), KnowledgeRunnerWorld.ActorA,
                    new ExecutionPlan("benchmark.events", "1", "Attested replay", [new ExecutionPlanStep(0, "system.events", KnowledgeBenchmark.Arguments, "replay")]), null);

                outputs.Add(Assert.IsType<string>(Assert.Single(report.Evidence).Data));
                var call = Assert.Single(world.Events.OfType<ToolCallAuditEvent>());
                Assert.Equal(ToolOutcome.Success, call.Outcome);
                Assert.Equal("replay", call.Summary!["executionMode"]!.GetValue<string>());
                Assert.Equal(hash, call.Summary["corpusSha256"]!.GetValue<string>());
                Assert.Equal(CanaryEventsCorpus.Canonical(frozen.Provenance), CanaryEventsCorpus.Canonical(call.Summary["corpusProvenance"]!));
                Assert.Equal("OperatorAttested", call.Summary["corpusProvenance"]!["classification"]!.GetValue<string>());
                Assert.Empty(probe.Requests);
            }
        }

        Assert.Equal(9, outputs.Count);
        Assert.All(outputs, output => Assert.Equal(outputs[0], output));
        var seen = JsonNode.Parse(outputs[0])!.AsObject();
        Assert.Equal("operator-attested", seen["benchmarkCorpus"]!["origin"]!.GetValue<string>());
        Assert.Equal(6, seen["events"]!.AsArray().Count(record => CorpusMinimizer.IsRetainedProvider(record!["source"]!.GetValue<string>())));
        Assert.DoesNotContain("jdoe", outputs[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Contoso-Internal-Agent", outputs[0], StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public async Task TheGovernedReplayRefusesAnAttestedActivationWhoseCorpusOrAttestationChanged()
    {
        using var temporary = new TempDirectory();
        var (path, hash, attestation) = CorpusFixtures.Approved(temporary);
        var frozen = KnowledgeBenchmark.ReplayFrozen(path, hash, attestation);
        using var world = new KnowledgeRunnerWorld();
        var process = world.Boot([]);

        async Task<string> RunAsync(CanaryEventsActivation activation)
        {
            var rig = process.ComposeCanary(new BenchmarkProbe([]), withHost: false, withWindowsSystemEvents: true, eventsCorpus: activation);
            var report = await rig.Runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), KnowledgeRunnerWorld.ActorA,
                new ExecutionPlan("benchmark.events", "1", "Attested replay", [new ExecutionPlanStep(0, "system.events", KnowledgeBenchmark.Arguments, "replay")]), null);
            return Assert.IsType<string>(Assert.Single(report.Evidence).Data);
        }

        Assert.DoesNotContain("ERROR", await RunAsync(frozen.Activation), StringComparison.Ordinal);
        // An attestation of another corpus, or a provenance expectation that no longer matches, is refused at dispatch.
        var other = CorpusFixtures.Approved(temporary, "other");
        var foreign = KnowledgeBenchmark.ReplayFrozen(other.Path, other.Hash, other.Attestation).Activation.Attestation;
        Assert.StartsWith("ERROR", await RunAsync(frozen.Activation with { Attestation = foreign }), StringComparison.Ordinal);
        Assert.StartsWith("ERROR", await RunAsync(frozen.Activation with { Attestation = null }), StringComparison.Ordinal);
        Assert.StartsWith("ERROR", await RunAsync(frozen.Activation with { Capture = true }), StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public async Task TheOperatorEntryPoints_ApproveThenReplayOnlyWithTheExplicitPins()
    {
        using var temporary = new TempDirectory();
        var (corpus, hash, _) = CorpusFixtures.Captured();
        var path = CorpusFixtures.Write(temporary, corpus);
        var names = new[] { "BOPS_EVENTS_CORPUS_MODE", "BOPS_EVENTS_CORPUS_PROFILE", "BOPS_EVENTS_CORPUS_PATH", "BOPS_EVENTS_CORPUS_SHA256",
            "BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION", "BOPS_EVENTS_CORPUS_APPROVE_SHA256", "BOPS_EVENTS_CORPUS_ATTESTATION_SHA256", "BOPS_RUN_KNOW_BENCH_01D" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        static Task Entry() => new KnowledgeEventsCorpusTests().SyntheticCorpus_TwoGovernedReplaysAreIdentical();
        void Set(string mode, string? approve = null, string? attestation = null)
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("BOPS_EVENTS_CORPUS_MODE", mode);
            Environment.SetEnvironmentVariable("BOPS_EVENTS_CORPUS_PROFILE", "01D");
            Environment.SetEnvironmentVariable("BOPS_EVENTS_CORPUS_PATH", path);
            Environment.SetEnvironmentVariable("BOPS_EVENTS_CORPUS_SHA256", hash);
            Environment.SetEnvironmentVariable("BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION", "1");
            Environment.SetEnvironmentVariable("BOPS_EVENTS_CORPUS_APPROVE_SHA256", approve);
            Environment.SetEnvironmentVariable("BOPS_EVENTS_CORPUS_ATTESTATION_SHA256", attestation);
        }

        try
        {
            // Replay before any approval, and approval without repeating the hash: both refuse and leave no sidecar.
            Set("replay", attestation: new string('a', 64));
            await Assert.ThrowsAsync<InvalidOperationException>(Entry);
            Set("approve");
            await Assert.ThrowsAsync<InvalidOperationException>(Entry);
            Set("approve", approve: new string('d', 64));
            await Assert.ThrowsAsync<InvalidOperationException>(Entry);
            Assert.False(File.Exists(CorpusAttestation.PathFor(path)));

            Set("approve", approve: hash);
            await Entry();
            var attestation = JsonNode.Parse(await File.ReadAllTextAsync(CorpusAttestation.PathFor(path)))!["sha256"]!.GetValue<string>();

            Set("replay"); // No attestation pin.
            await Assert.ThrowsAsync<InvalidOperationException>(Entry);
            Set("replay", attestation: attestation);
            await Entry(); // The governed replay of the attested file succeeds with exactly the pinned pair.
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
