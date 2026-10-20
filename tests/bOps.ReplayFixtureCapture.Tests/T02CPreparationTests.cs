// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.QwenLiveBaseline;

namespace bOps.ReplayFixtureCapture.Tests;

public sealed class T02CPreparationTests
{
    private static readonly IReadOnlyList<QuerySpec> Plan = CapturePlan.Select([], "T02-C");
    private static string Hash => CapturePlan.Hash(Plan, "T02-C");
    private static string Snapshot(Rig rig) => Path.Combine(rig.OutRoot, "T02-C-20261019T090000Z-ab12");
    private static Task<int> SyntheticCapture(Rig rig, params string[] extra) => rig.Run(
        ["capture", "--plan", "T02-C", "--enable-capture", "--plan-sha256", Hash, "--out-root", rig.OutRoot, .. extra]);
    private static ValidationReport Validate(Rig rig) => SnapshotValidator.Validate(Snapshot(rig), TimeSpan.FromSeconds(300));
    private static void Edit(string path, Action<JsonObject> change)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(json);
        File.SetAttributes(path, FileAttributes.Normal);
        CaptureRunner.WriteJson(path, json);
        File.SetAttributes(path, FileAttributes.ReadOnly);
    }

    [Fact]
    public void Version_two_reuses_ten_original_queries_and_has_three_unique_mandatory_additions()
    {
        Assert.Equal(13, Plan.Count);
        Assert.Equal(CapturePlan.Select([]), Plan.Take(10));
        Assert.Equal(["C1", "C2", "C3"], Plan.Skip(10).Select(q => q.Id));
        Assert.All(Plan, q => Assert.False(q.Optional));
        Assert.Equal(13, Plan.Select(q => q.Tool + ReplayArguments.Literal(q.EffectiveArguments)).Distinct().Count());
        Assert.Equal(2, CapturePlan.VersionOf("T02-C"));
        Assert.Equal("a9bf0ddf37602050c9dd18bcd0ad4ae4f1ff563ecaf4ccd5414694992ca27508", Rig.DefaultPlanHash);
        Assert.Equal("46758334aa173b15c5aaf800f99851749918c5963e37d6e6ab106b412ed15386", Hash);
        Assert.NotEqual(Rig.DefaultPlanHash, Hash);
        Assert.NotEqual(CapturePlan.Hash(Plan), Hash); // version is part of the digest
        Assert.Throws<ArgumentException>(() => CapturePlan.Select([], "T02-D"));
        Assert.Throws<ArgumentException>(() => CapturePlan.Select(["OPT1"], "T02-C"));
        Assert.Throws<ArgumentException>(() => CapturePlan.Select(["OPT2"], "T02-C"));
        Assert.Equal("""{"windowMinutes":1440,"mode":"aggregate","limit":50}""", Plan[10].ArgumentsJson);
        Assert.Equal("""{"windowMinutes":1440,"minSeverity":"warning","mode":"raw","limit":50}""", Plan[11].ArgumentsJson);
        Assert.Equal("""{"windowDays":30,"limit":100}""", Plan[12].ArgumentsJson);
    }

    [Fact]
    public async Task Verify_checks_all_thirteen_contracts_without_constructing_collectors_or_creating_output()
    {
        using var rig = new Rig { CollectorFactoryMustNotBeCalled = true };
        Assert.All(Preflight.Run(Plan, new RealManifestSource(), ReadOnlyPolicy.Create()), p => Assert.True(p.Ok));
        Assert.Equal(0, await rig.Run("verify", "--plan", "T02-C", "--out-root", rig.OutRoot, "--json"));
        var json = JsonNode.Parse(rig.Out.ToString())!;
        Assert.Equal(13, json["queries"]!.AsArray().Count);
        Assert.Equal(Hash, json["planSha256"]!.GetValue<string>());
        Assert.Equal(2, json["planVersion"]!.GetValue<int>());
        Assert.Contains("--plan T02-C", json["captureCommand"]!.GetValue<string>());
        Assert.Contains("--require-elevation-type Limited", json["captureCommand"]!.GetValue<string>());
        Assert.Equal(0, rig.CollectorFactoryCalls);
        Assert.Empty(rig.Collectors.Calls);
        Assert.False(Directory.Exists(rig.OutRoot));
    }

    [Theory]
    [InlineData("Default", false, 8192)]
    [InlineData("Full", true, 12288)]
    [InlineData("Limited", true, 8192)]
    [InlineData("Limited", false, 12288)]
    public async Task T02C_identity_gate_cannot_be_relaxed(string elevation, bool elevated, int integrity)
    {
        using var rig = new Rig(environment: new FakeEnvironment(elevationType: elevation, elevated: elevated, integrityRid: integrity))
            { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(2, await rig.Run("verify", "--plan", "T02-C", "--require-elevation-type", "Default"));
        Assert.Equal(2, await SyntheticCapture(rig, "--require-elevation-type", "Default"));
        Assert.Equal(0, rig.CollectorFactoryCalls);
        Assert.False(Directory.Exists(rig.OutRoot));
    }

    [Fact]
    public async Task Prior_plan_hash_cannot_authorise_C_and_explicit_enabling_is_still_required()
    {
        using var rig = new Rig { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(2, await rig.Run("capture", "--plan", "T02-C", "--plan-sha256", Hash, "--out-root", rig.OutRoot));
        Assert.Equal(2, await rig.Run("capture", "--plan", "T02-C", "--enable-capture", "--plan-sha256", Rig.DefaultPlanHash, "--out-root", rig.OutRoot));
        Assert.Equal(0, rig.CollectorFactoryCalls);
        Assert.False(Directory.Exists(rig.OutRoot));
    }

    [Fact]
    public async Task Independent_synthetic_session_validates_and_potentially_serves_all_twelve_calls()
    {
        using var rig = new Rig();
        // Reproduce the two B limitations without concealing them. The new exact queries resolve those gaps.
        rig.Collectors.Respond = (spec, now) => spec.Id is "Q02" or "Q10"
            ? FakeData.Make(spec, now, r => { r["truncated"] = true; r["complete"] = false; }, ToolResultCompleteness.Partial)
            : FakeData.Result(spec, now);
        Assert.Equal(0, await SyntheticCapture(rig));
        var report = Validate(rig);
        Assert.False(report.HasFail, report.ToText());
        Assert.Equal(13, rig.Collectors.Calls.Count);
        Assert.Equal(12, report.ToJson()["details"]!["summary"]!["probesServed"]!.GetValue<int>());
        Assert.Contains(report.Checks, c => c.Name.StartsWith("all 13 mandatory") && c.Status == CheckStatus.Pass);
        Assert.All(Directory.GetFiles(Path.Combine(Snapshot(rig), "fixtures")), f => Assert.StartsWith("FC-", Path.GetFileName(f)));
        var coverage = report.ToJson()["details"]!["coverage"]!.AsArray();
        foreach (var (label, id) in new[] { ("O1", "C1"), ("O2", "C2"), ("R2", "C3") })
            Assert.StartsWith("FC-" + id + "-", coverage.Single(r => r!["label"]!.GetValue<string>() == label)!["result"]!.GetValue<string>());
        Assert.Equal(3, report.ToJson()["details"]!["summary"]!["truncated"]!.GetValue<int>());
        rig.Clock.Set(Rig.Start);
        Assert.Equal(2, await SyntheticCapture(rig)); // same id: no overwrite
    }

    [Fact]
    public async Task Thirteen_fixtures_do_not_guarantee_twelve_served_calls_when_a_limit_is_binding()
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q01"
            ? FakeData.Make(spec, now, r => { r["observedGroups"] = 53; r["returnedGroups"] = 53; })
            : FakeData.Result(spec, now);
        Assert.Equal(0, await SyntheticCapture(rig));
        var report = Validate(rig);
        Assert.False(report.HasFail, report.ToText());
        Assert.True(report.ToJson()["details"]!["summary"]!["probesServed"]!.GetValue<int>() < 12);
        Assert.False(report.ToJson()["details"]!["coverage"]!.AsArray().Single(r => r!["label"]!.GetValue<string>() == "O5")!["served"]!.GetValue<bool>());
        Assert.Contains(report.Checks, c => c.Area == "Coverage" && c.Status == CheckStatus.Warn);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("mixed-session")]
    [InlineData("historical")]
    [InlineData("plan-version")]
    [InlineData("plan-hash")]
    [InlineData("recorded-queries")]
    [InlineData("time")]
    public async Task Damaged_or_mixed_snapshots_are_rejected(string fault)
    {
        using var rig = new Rig();
        Assert.Equal(0, await SyntheticCapture(rig));
        var file = Path.Combine(Snapshot(rig), "fixtures", "FC-C2-system.events.json");
        switch (fault)
        {
            case "missing": File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); break;
            case "duplicate": File.Copy(file, Path.Combine(Path.GetDirectoryName(file)!, "FC-copy.json")); break;
            case "unknown": Edit(file, f => f["provenance"]!["queryId"] = "OPT2"); break;
            case "mixed-session": Edit(file, f => f["provenance"]!["snapshotId"] = "T02-C-another-session"); break;
            case "historical": File.Copy(file, Path.Combine(Path.GetDirectoryName(file)!, "FB-OPT1-system.events.json")); break;
            case "plan-version": Edit(Path.Combine(Snapshot(rig), "plan.json"), f => f["planVersion"] = 1); break;
            case "plan-hash": Edit(Path.Combine(Snapshot(rig), "plan.json"), f => f["planSha256"] = Rig.DefaultPlanHash); break;
            case "recorded-queries": Edit(Path.Combine(Snapshot(rig), "plan.json"), f => f["queries"]![10]!["arguments"]!["limit"] = 500); break;
            case "time": Edit(file, f => f["provenance"]!["windowToUtc"] = Rig.Start.AddHours(1).ToString("O")); break;
        }
        var report = Validate(rig);
        Assert.True(report.HasFail, report.ToText());
        if (fault == "time") Assert.Contains(report.Checks, c => c.Area == "Temporal" && c.Status == CheckStatus.Fail);
        if (fault == "mixed-session") Assert.Contains(report.Checks, c => c.Name.StartsWith("snapshot identity") && c.Status == CheckStatus.Fail);
        if (fault == "unknown") Assert.Contains(report.Checks, c => c.Name.StartsWith("only authorised") && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task Longer_session_still_aborts_at_the_original_300_second_threshold()
    {
        using var rig = new Rig();
        rig.Collectors.AdvancePerCall = TimeSpan.FromSeconds(26);
        Assert.Equal(6, await SyntheticCapture(rig));
        Assert.False(Directory.Exists(Path.Combine(Snapshot(rig), "fixtures")));
        Assert.True(Validate(rig).HasFail);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(500)]
    public void Truncated_fixtures_only_serve_the_same_limit(int requestedLimit)
    {
        foreach (var id in new[] { "C1", "C2", "C3", "Q02", "Q10" })
        {
            var spec = Plan.Single(q => q.Id == id);
            var result = FakeData.Make(spec, Rig.Start, r => { r["truncated"] = true; r["complete"] = false; }, ToolResultCompleteness.Partial);
            var fixture = new Fixture("synthetic", spec.Tool, spec.Arguments, result, "T02", "test", "digest");
            var broker = new ReplayBroker([fixture], "T02");
            var args = spec.Arguments;
            args["limit"] = requestedLimit;
            broker.Answer(spec.Tool, args);
            Assert.Equal(requestedLimit == spec.EffectiveArguments["limit"]!.GetValue<int>() ? "SERVED" : "UNSUPPORTED_REPLAY", broker.Ledger.Single().Verdict);
            var facts = ResultChecks.Read(spec, result)!;
            Assert.Null(ResultChecks.LimitCoverage(spec, facts)["smallestLimitReturningTheSameRecords"]);
        }
    }
}
