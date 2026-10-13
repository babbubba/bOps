// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge.Tests;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>Two offline checks; the first is also the separately opted-in manual capture/replay entry point.</summary>
public sealed class KnowledgeEventsCorpusTests
{
    private static ToolArguments Request => ToolArguments.FromJson(new JsonObject
        { ["channel"] = "System", ["mode"] = "raw", ["windowMinutes"] = 5, ["limit"] = 10, ["maxOutputBytes"] = 8192 });

    [Fact]
    public async Task SyntheticCorpus_TwoGovernedReplaysAreIdentical()
    {
        // No model invocation, no Knowledge admission/signing, no benchmark A/B.
        // This environment switch exists solely in the test project. Default execution is offline.
        if (Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_MODE") is { } manualMode)
        {
            await ManualAsync(manualMode);
            return;
        }
        using var temporary = new TempDirectory();
        var path = CorpusPath(temporary);
        var corpus = Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var encoded = CanaryEventsCorpus.Encode(corpus, hash);
        Assert.Equal(encoded, CanaryEventsCorpus.Encode(corpus, hash));
        await File.WriteAllBytesAsync(path, encoded);
        var verified = CanaryEventsCorpus.Read(path, hash);
        Assert.Equal(hash, verified.Hash);
        Assert.Equal("synthetic", verified.Corpus["origin"]!.GetValue<string>());
        var expected = corpus["output"]!.GetValue<string>();
        Assert.DoesNotContain("harmless synthetic message", expected, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-provider", expected, StringComparison.Ordinal);
        using var world = new KnowledgeRunnerWorld();
        var process = world.Boot([]);
        // Two independent canary compositions pin the same immutable file; no fake live collector exists.
        foreach (var unused in Enumerable.Range(0, 2))
        {
            var rig = process.ComposeCanary(new ScriptedRunnerModel([]), withHost: false,
                withWindowsSystemEvents: true, eventsCorpus: Replay(path, hash));
            var manifest = Assert.Single(rig.Registry.GetAvailableManifests());
            Assert.Null(SemanticExpectedArguments.Validate(manifest, Request));
            Assert.True(SemanticExpectedArguments.Matches(manifest, Request, new ModelToolCall("offline", "system.events", Request)));
            var report = await ExecuteAsync(rig, Request, "replay");
            Assert.Equal(expected, Assert.Single(report.Evidence).Data);
            Assert.Contains("replay", Assert.Single(report.Evidence).Description, StringComparison.Ordinal);
        }
        Assert.Equal(encoded, await File.ReadAllBytesAsync(path));
        Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
        Assert.Empty(world.Events.OfType<KnowledgePackAdmissionAuditEvent>());
        Assert.All(world.Events.OfType<ToolCallAuditEvent>(), call =>
        {
            Assert.Equal(ToolOutcome.Success, call.Outcome);
            Assert.Equal(RiskLevel.Read, call.Risk);
            Assert.Equal("replay", call.Summary!["executionMode"]!.GetValue<string>());
            Assert.Equal(hash, call.Summary["corpusSha256"]!.GetValue<string>());
            Assert.DoesNotContain("harmless synthetic message", call.Summary.ToJsonString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task InvalidCorpusOrUnauthorizedTool_FailsClosedThroughGovernedDispatch()
    {
        using var temporary = new TempDirectory();
        var path = CorpusPath(temporary);
        var corpus = Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var good = CanaryEventsCorpus.Encode(corpus, hash);
        using var world = new KnowledgeRunnerWorld();
        var process = world.Boot([]);
        var rig = process.ComposeCanary(new ScriptedRunnerModel([]), withHost: false,
            withWindowsSystemEvents: true, eventsCorpus: Replay(path, hash));

        async Task RefusedAsync(ToolArguments request)
        {
            var report = await ExecuteAsync(rig, request, "replay");
            Assert.StartsWith("ERROR", Assert.Single(report.Evidence).Data);
            Assert.Equal(ToolOutcome.Failure, world.Events.OfType<ToolCallAuditEvent>().Last().Outcome);
            Assert.Equal(ToolFailureKind.Validation, world.Events.OfType<ToolCallAuditEvent>().Last().FailureKind);
        }
        await RefusedAsync(Request); // Missing.
        await File.WriteAllBytesAsync(path, good);
        var incompatible = Request.ToJson();
        incompatible["limit"] = 11;
        await RefusedAsync(ToolArguments.FromJson(incompatible));
        var forbiddenChannel = Request.ToJson();
        forbiddenChannel["channel"] = "Security";
        await RefusedAsync(ToolArguments.FromJson(forbiddenChannel));
        var invalid = Request.ToJson();
        invalid["limit"] = 501;
        Assert.NotNull(SemanticExpectedArguments.Validate(Assert.Single(rig.Registry.GetAvailableManifests()),
            ToolArguments.FromJson(invalid)));
        await RefusedAsync(ToolArguments.FromJson(invalid)); // Normal schema validation before the adapter.

        var changed = (JsonObject)corpus.DeepClone();
        changed["output"] = "altered";
        await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(changed, hash));
        await RefusedAsync(Request);
        await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(corpus, new string('0', 64)));
        await RefusedAsync(Request);
        // Recomputing the embedded hash cannot evade the separately pinned hash.
        changed["format"] = "unsupported";
        await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(changed, CanaryEventsCorpus.Hash(changed)));
        await RefusedAsync(Request);
        var unsupportedRig = process.ComposeCanary(new ScriptedRunnerModel([]), withHost: false,
            withWindowsSystemEvents: true, eventsCorpus: Replay(path, CanaryEventsCorpus.Hash(changed)));
        Assert.StartsWith("ERROR", Assert.Single((await ExecuteAsync(unsupportedRig, Request, "replay")).Evidence).Data);
        await File.WriteAllTextAsync(path, "{}");
        await RefusedAsync(Request);
        await File.WriteAllBytesAsync(path, good);

        var disabledRig = process.ComposeCanary(new ScriptedRunnerModel([]), withHost: false,
            withWindowsSystemEvents: true, eventsCorpus: Replay(path, hash) with { Enabled = false });
        Assert.StartsWith("ERROR", Assert.Single((await ExecuteAsync(disabledRig, Request, "replay")).Evidence).Data);
        var noSanitizationRig = process.ComposeCanary(new ScriptedRunnerModel([]), withHost: false,
            withWindowsSystemEvents: true, eventsCorpus: Replay(path, hash) with { AcceptMinimization = false });
        Assert.StartsWith("ERROR", Assert.Single((await ExecuteAsync(noSanitizationRig, Request, "replay")).Evidence).Data);
        // Even an opted-in capture must refuse an existing file before the real collector is called.
        var overwriteRig = process.ComposeCanary(new ScriptedRunnerModel([]), withHost: false,
            withWindowsSystemEvents: true, eventsCorpus: Replay(path, hash) with { Capture = true });
        Assert.StartsWith("ERROR", Assert.Single((await ExecuteAsync(overwriteRig, Request, "capture")).Evidence).Data);
        rig.Registry.SetEnabled(new PackageId("bops.packages.system.windows"), false);
        Assert.Empty(rig.Registry.GetAvailableManifests());
        await ExecuteAsync(rig, Request, "replay");
        var denied = world.Events.OfType<ToolCallAuditEvent>().Last();
        Assert.Equal(ToolOutcome.Denied, denied.Outcome);
        Assert.Equal(AuthorizationKind.UnknownTool, denied.Authorization);
        Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
        Assert.Equal(good, await File.ReadAllBytesAsync(path));
    }

    private static async Task ManualAsync(string mode)
    {
        if (mode is not ("capture" or "replay")) throw new InvalidOperationException("Unknown corpus mode.");
        var profile = Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_PROFILE");
        if (profile is not (null or "01C" or "01D")) throw new InvalidOperationException("Unknown benchmark corpus profile.");
        if (Environment.GetEnvironmentVariable("BOPS_RUN_KNOW_BENCH_01D") is not null)
            throw new InvalidOperationException("Corpus preparation must be separate from A/B execution.");
        var arguments = profile == "01D" ? KnowledgeBenchmark.Arguments : Request;
        var path = Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_PATH")
            ?? throw new InvalidOperationException("An explicit corpus path is required.");
        var hash = Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_SHA256");
        if (Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION") != "1")
            throw new InvalidOperationException("Explicit minimization acknowledgement is required.");
        if (mode == "replay" && profile == "01D")
            _ = KnowledgeBenchmark.Replay(path, hash ?? throw new InvalidOperationException("Explicit corpus pin required."));
        using var world = new KnowledgeRunnerWorld();
        var process = world.Boot([]);
        var rig = process.ComposeCanary(new ScriptedRunnerModel([]), withHost: false, withWindowsSystemEvents: true,
            eventsCorpus: new CanaryEventsActivation(true, mode == "capture", path, hash, true));
        await ExecuteAsync(rig, arguments, mode);
        var call = Assert.Single(world.Events.OfType<ToolCallAuditEvent>());
        // No event data is printed even on failure. Hash is available in the snapshot's sha256 member.
        Assert.True(call.Outcome == ToolOutcome.Success, "Manual event corpus operation refused; no payload logged.");
        Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
    }

    private static CanaryEventsActivation Replay(string path, string hash) => new(true, false, path, hash, true);
    private static Task<SkillReport> ExecuteAsync(CanaryRig rig, ToolArguments arguments, string mode) =>
        rig.Runner.ExecuteExecutionPlanAsync(Guid.NewGuid(), KnowledgeRunnerWorld.ActorA,
            new ExecutionPlan("benchmark.events", "1", "Explicit isolated corpus operation",
                [new ExecutionPlanStep(0, "system.events", arguments, "system.events " + mode + " of an unattested corpus")]), null);

    private static string CorpusPath(TempDirectory temporary)
    {
        var directory = temporary.Sub(".bops-event-corpus");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "synthetic.bops-events.snapshot.json");
    }

    private static JsonObject Synthetic()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        bOps.Packages.Sys.Core.SystemEventsArguments.TryRead(Request, now, out var query, out var mode, out var limit, out var bytes, out _);
        var snapshot = new bOps.Packages.Sys.Core.SystemEventSnapshot(
            [new(now.AddMinutes(-1), bOps.Packages.Sys.Core.SystemEventSeverity.Warning, "synthetic-provider", "1", "System",
                "harmless synthetic message", null, null)],
            [new("windows.channel.System", bOps.Packages.Sys.Core.InventorySourceStatus.Partial, "synthetic coverage gap")], true);
        return CanaryEventsCorpus.Build(Request,
            bOps.Packages.Sys.Core.SystemEventFormatting.Format(snapshot, query!, mode, limit, bytes), "synthetic", now);
    }
}
