// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Policy;
using bOps.QwenLiveBaseline;

namespace bOps.ReplayFixtureCapture.Tests;

public sealed class CaptureFlowTests
{
    // ---- gates: nothing is read unless every one is satisfied

    [Fact]
    public async Task Capture_without_the_explicit_enabling_argument_is_refused_and_reads_nothing()
    {
        using var rig = new Rig();
        var code = await rig.Run("capture", "--plan-sha256", Rig.DefaultPlanHash, "--out-root", rig.OutRoot);
        Assert.Equal(2, code);
        Assert.Contains("--enable-capture", rig.Err.ToString());
        Assert.Equal(0, rig.CollectorFactoryCalls);
        Assert.Empty(rig.Collectors.Calls);
        Assert.False(Directory.Exists(rig.OutRoot));
    }

    [Fact]
    public async Task Capture_with_another_plan_hash_is_refused_and_reads_nothing()
    {
        using var rig = new Rig();
        Assert.Equal(2, await rig.Run("capture", "--enable-capture", "--plan-sha256", new string('0', 64), "--out-root", rig.OutRoot));
        Assert.Contains("does not match the plan", rig.Err.ToString());
        Assert.Empty(rig.Collectors.Calls);
        Assert.Equal(0, rig.Collectors.Created);
        Assert.False(Directory.Exists(rig.OutRoot));
    }

    [Fact]
    public async Task The_hash_of_the_default_plan_does_not_authorise_a_plan_with_an_optional_query()
    {
        using var rig = new Rig();
        Assert.Equal(2, await rig.Capture("--include-optional", "OPT1"));
        Assert.Empty(rig.Collectors.Calls);
    }

    [Fact]
    public async Task Capture_requires_the_plan_hash_and_the_output_root()
    {
        using var rig = new Rig();
        Assert.Equal(2, await rig.Run("capture", "--enable-capture", "--out-root", rig.OutRoot));
        Assert.Equal(2, await rig.Run("capture", "--enable-capture", "--plan-sha256", Rig.DefaultPlanHash));
        Assert.Empty(rig.Collectors.Calls);
    }

    [Fact]
    public async Task Capture_refuses_an_elevated_process()
    {
        using var rig = new Rig(elevated: true);
        Assert.Equal(2, await rig.Capture());
        Assert.Contains("elevated", rig.Err.ToString());
        Assert.Equal(0, rig.Collectors.Created);
        Assert.Empty(rig.Collectors.Calls);
    }

    [Fact]
    public async Task Capture_refuses_an_output_root_inside_a_git_working_tree_an_existing_pool_and_an_existing_snapshot()
    {
        using var rig = new Rig();
        var repo = Path.Combine(rig.Root.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Assert.Equal(2, await rig.Run("capture", "--enable-capture", "--plan-sha256", Rig.DefaultPlanHash, "--out-root", Path.Combine(repo, "docs")));
        Assert.Contains("Git working tree", rig.Err.ToString());

        var pool = Path.Combine(rig.Root.Path, "pool");
        Directory.CreateDirectory(Path.Combine(pool, "fixtures"));
        Assert.Equal(2, await rig.Run("capture", "--enable-capture", "--plan-sha256", Rig.DefaultPlanHash, "--out-root", pool));
        Assert.Contains("already is a fixture pool", rig.Err.ToString());

        Directory.CreateDirectory(rig.SnapshotDir);
        Assert.Equal(2, await rig.Capture());
        Assert.Contains("already exists", rig.Err.ToString());
        Assert.Empty(rig.Collectors.Calls);
    }

    [Fact]
    public async Task Capture_refuses_a_policy_that_does_not_allow_the_reads()
    {
        var forbidden = new PolicyEngine(PolicyConfigLoader.Load("defaults:\n  read: forbidden\n  low: forbidden\n  medium: forbidden\n  high: forbidden\n  critical: forbidden\n"));
        using var rig = new Rig(policy: forbidden);
        Assert.Equal(2, await rig.Capture());
        Assert.Equal(0, rig.Collectors.Created);
        Assert.Empty(rig.Collectors.Calls);
    }

    // ---- the clean run

    [Fact]
    public async Task A_clean_batch_runs_the_ten_queries_in_order_with_their_exact_arguments_and_promotes_the_fixtures()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        Assert.Equal(CapturePlan.Select([]).Select(q => q.Tool), rig.Collectors.Calls.Select(c => c.Tool));
        Assert.Equal(CapturePlan.Select([]).Select(q => CapturePlan.Canonical(q.Arguments)), rig.Collectors.Calls.Select(c => CapturePlan.Canonical(c.Arguments)));
        Assert.True(Directory.Exists(Path.Combine(rig.SnapshotDir, "fixtures")));
        Assert.False(Directory.Exists(Path.Combine(rig.SnapshotDir, "pending")));
        Assert.Equal(10, Directory.GetFiles(Path.Combine(rig.SnapshotDir, "fixtures"), "F*.json").Length);
        Assert.All(Directory.GetFiles(Path.Combine(rig.SnapshotDir, "fixtures")), f => Assert.True(File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly)));
        Assert.Equal(0, await rig.Run("check-snapshot", "--snapshot", rig.SnapshotDir));
    }

    [Fact]
    public async Task Optional_queries_run_only_when_named_and_the_matching_plan_hash_is_given()
    {
        using var rig = new Rig();
        var hash = CapturePlan.Hash(CapturePlan.Select(["OPT1", "OPT2"]));
        Assert.Equal(0, await rig.Run("capture", "--enable-capture", "--plan-sha256", hash, "--out-root", rig.OutRoot, "--include-optional", "OPT1,OPT2"));
        Assert.Equal(12, rig.Collectors.Calls.Count);
        Assert.Equal(12, Directory.GetFiles(Path.Combine(rig.SnapshotDir, "fixtures"), "F*.json").Length);
    }

    [Fact]
    public async Task Each_fixture_records_the_query_the_effective_arguments_the_window_the_times_the_completeness_and_the_digests()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "index.json")))!.AsObject();
        Assert.False(index["atomicSnapshot"]!.GetValue<bool>());
        Assert.Equal(Rig.DefaultPlanHash, index["planSha256"]!.GetValue<string>());
        Assert.Equal("T02-B-20261019T090000Z-ab12", index["snapshotId"]!.GetValue<string>());

        foreach (var entry in index["fixtures"]!.AsArray().Select(f => f!.AsObject()))
        {
            var path = Path.Combine(rig.SnapshotDir, "fixtures", entry["file"]!.GetValue<string>());
            var fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var provenance = fixture["provenance"]!.AsObject();
            var spec = CapturePlan.All.Single(q => q.Id == provenance["queryId"]!.GetValue<string>());

            Assert.Equal(CapturePlan.Canonical(spec.Arguments), CapturePlan.Canonical(fixture["arguments"]!.AsObject()));
            Assert.Equal(CapturePlan.Canonical(spec.EffectiveArguments), CapturePlan.Canonical(provenance["effectiveArguments"]!.AsObject()));
            Assert.Equal("T02", provenance["originScenario"]!.GetValue<string>());
            Assert.Equal("controlled-capture", provenance["kind"]!.GetValue<string>());
            Assert.Equal(rig.SnapshotDir.Split(Path.DirectorySeparatorChar)[^1], provenance["snapshotId"]!.GetValue<string>());
            Assert.False(provenance["identity"]!["elevated"]!.GetValue<bool>());
            Assert.Equal(@"DOMAIN\bob", provenance["identity"]!["user"]!.GetValue<string>());
            Assert.NotNull(provenance["binaries"]!["fake.dll"]);

            var from = DateTimeOffset.Parse(provenance["windowFromUtc"]!.GetValue<string>());
            var to = DateTimeOffset.Parse(provenance["windowToUtc"]!.GetValue<string>());
            Assert.Equal(spec.RequestedWindow, to - from);
            Assert.True(DateTimeOffset.Parse(provenance["startedAtUtc"]!.GetValue<string>()) <= DateTimeOffset.Parse(provenance["endedAtUtc"]!.GetValue<string>()));
            Assert.NotNull(provenance["status"]);
            Assert.NotNull(provenance["truncated"]);
            Assert.NotNull(provenance["toolCompleteness"]);
            Assert.False(provenance["atomicSnapshot"]!.GetValue<bool>());

            var output = fixture["result"]!["Output"]!.GetValue<string>();
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant(), fixture["outputSha256"]!.GetValue<string>());
            Assert.Equal(fixture["outputSha256"]!.GetValue<string>(), entry["outputSha256"]!.GetValue<string>());
            Assert.Equal(fixture["sha256"]!.GetValue<string>(), entry["sha256"]!.GetValue<string>());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), entry["fileSha256"]!.GetValue<string>());
            Assert.Equal(FixtureWriter.ContentSha256(spec.Tool, spec.Arguments, fixture["result"]!.AsObject()), fixture["sha256"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task The_payload_is_the_tool_result_unchanged()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var q05 = CapturePlan.All.Single(q => q.Id == "Q01");
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "fixtures", FixtureWriter.FixtureId(q05) + ".json")))!.AsObject();
        var expected = FakeData.Make(q05, FakeClockAt(rig, "Q01"), null);
        Assert.Equal(expected.Output, fixture["result"]!["Output"]!.GetValue<string>());
        Assert.Contains(@"C:\\Users\\bob", fixture["result"]!["Output"]!.GetValue<string>());
        Assert.Equal(@"C:\Users\bob\x", fixture["result"]!["Facts"]![0]!["Value"]!.GetValue<string>());
    }

    private static DateTimeOffset FakeClockAt(Rig rig, string id)
    {
        // The fake clock advanced 2 s before each call: query N (1-based) answered at Start + 2 s * N.
        var number = CapturePlan.Select([]).ToList().FindIndex(q => q.Id == id) + 1;
        return Rig.Start + TimeSpan.FromSeconds(2 * number);
    }

    [Fact]
    public async Task The_temporal_report_states_that_the_batch_is_not_atomic_and_measures_the_real_skew()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "temporal-report.json")))!.AsObject();
        Assert.False(report["atomicSnapshot"]!.GetValue<bool>());
        Assert.Equal(18, report["windowEndSpreadSeconds"]!.GetValue<double>());   // 10 queries x 2 s: first at +2 s, last at +20 s
        Assert.Equal(10, report["queries"]!.AsArray().Count);
        Assert.Equal(0, report["queries"]![0]!["windowEndOffsetFromEarliestSeconds"]!.GetValue<double>());
        Assert.Equal(18, report["queries"]![9]!["windowEndOffsetFromEarliestSeconds"]!.GetValue<double>());
        Assert.Contains("not a guarantee of simultaneity", report["statement"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_environment_record_has_the_identity_and_the_elevation_level()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var env = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "environment.json")))!.AsObject();
        Assert.Equal(@"DOMAIN\bob", env["user"]!.GetValue<string>());
        Assert.False(env["isElevated"]!.GetValue<bool>());
        Assert.False(env["isAdministratorRole"]!.GetValue<bool>());
        Assert.Equal("TESTBOX-01", env["machineName"]!.GetValue<string>());
        Assert.NotNull(env["binaries"]);
    }

    [Fact]
    public async Task The_ledger_chain_and_the_checksums_detect_tampering()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var ledger = Path.Combine(rig.SnapshotDir, "capture-ledger.jsonl");
        Assert.True(CaptureLedger.Verify(ledger));
        var lines = File.ReadAllLines(ledger);
        Assert.Contains(lines, l => l.Contains("\"event\":\"promoted\""));

        File.WriteAllText(ledger, string.Join("\n", lines.Select(l => l.Replace("queryStarted", "queryStartEd"))) + "\n");
        Assert.False(CaptureLedger.Verify(ledger));
        Assert.Equal(2, await rig.Run("check-snapshot", "--snapshot", rig.SnapshotDir));
    }

    [Fact]
    public async Task A_modified_fixture_is_detected_by_the_snapshot_check()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var file = Directory.GetFiles(Path.Combine(rig.SnapshotDir, "fixtures"), "F*.json")[0];
        File.SetAttributes(file, FileAttributes.Normal);
        File.AppendAllText(file, " ");
        Assert.Equal(2, await rig.Run("check-snapshot", "--snapshot", rig.SnapshotDir));
        Assert.Contains("missing or changed", rig.Out.ToString());
    }

    // ---- the produced fixtures are what the replay reads

    [Fact]
    public async Task The_captured_fixtures_load_with_the_replay_loader_and_the_matcher_applies_its_own_rules_to_them()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var fixtures = FixtureStore.Load(Path.Combine(rig.SnapshotDir, "fixtures"));
        Assert.Equal(10, fixtures.Count);
        Assert.All(fixtures, f => Assert.Equal("T02", f.OriginScenario));

        var broker = new ReplayBroker(fixtures, "T02");
        // Q06 {windowDays:1, limit:200} is untruncated with 7 observed groups: the stated limit rule serves the default (50) and 7, never 6.
        broker.Answer("system.stability", JsonNode.Parse("""{"windowDays":1}""")!.AsObject());
        broker.Answer("system.stability", JsonNode.Parse("""{"windowDays":1,"limit":7}""")!.AsObject());
        broker.Answer("system.stability", JsonNode.Parse("""{"windowDays":1,"limit":6}""")!.AsObject());
        broker.Answer("system.stability", JsonNode.Parse("""{"windowDays":1,"limit":200}""")!.AsObject());
        broker.Answer("system.stability", JsonNode.Parse("""{"windowDays":4}""")!.AsObject());
        Assert.Equal(["limit-not-binding", "limit-not-binding", null, "exact", null], broker.Ledger.Select(c => c.Rule).ToArray());
        Assert.Equal(["SERVED", "SERVED", "UNSUPPORTED_REPLAY", "SERVED", "UNSUPPORTED_REPLAY"], broker.Ledger.Select(c => c.Verdict).ToArray());
        Assert.StartsWith("FB-Q06", broker.Ledger[0].FixtureId);
    }

    [Fact]
    public async Task The_limit_coverage_of_a_truncated_result_states_no_smaller_limit()
    {
        var q05 = CapturePlan.All.Single(q => q.Id == "Q05");
        var truncated = FakeData.Make(q05, Rig.Start, null);
        var facts = ResultChecks.Read(q05, truncated)!;
        Assert.True(facts.Truncated);
        Assert.Null(ResultChecks.LimitCoverage(q05, facts)["smallestLimitReturningTheSameRecords"]);

        var q03 = CapturePlan.All.Single(q => q.Id == "Q03");
        var clean = ResultChecks.LimitCoverage(q03, ResultChecks.Read(q03, FakeData.Make(q03, Rig.Start, null))!);
        Assert.Equal(12, clean["smallestLimitReturningTheSameRecords"]!.GetValue<long>());
        Assert.Equal(500, clean["effectiveLimit"]!.GetValue<int>());
        Assert.Equal("partial", clean["messageDistribution"]!["availability"]!.GetValue<string>());
        await Task.CompletedTask;
    }

    // ---- unexpected results stop the batch; nothing is promoted

    private static async Task AssertAborted(Rig rig, int expectedCalls, int expectedExit, string expectedText)
    {
        var code = await rig.Capture();
        Assert.Equal(expectedExit, code);
        Assert.Contains(expectedText, rig.Err.ToString());
        Assert.Equal(expectedCalls, rig.Collectors.Calls.Count);
        Assert.False(Directory.Exists(Path.Combine(rig.SnapshotDir, "fixtures")));
        Assert.True(Directory.Exists(Path.Combine(rig.SnapshotDir, "aborted")));
        Assert.True(File.Exists(Path.Combine(rig.SnapshotDir, "ABORTED.json")));
        Assert.True(CaptureLedger.Verify(Path.Combine(rig.SnapshotDir, "capture-ledger.jsonl")));
    }

    [Fact]
    public async Task A_failed_tool_result_stops_the_batch_without_substituting_anything()
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q03"
            ? new ToolCallResult(ToolOutcome.Failure, null, "boom") { FailureKind = ToolFailureKind.Environment, Completeness = ToolResultCompleteness.Unavailable }
            : FakeData.Result(spec, now);
        await AssertAborted(rig, expectedCalls: 3, expectedExit: 4, expectedText: "Q03");
    }

    [Fact]
    public async Task An_unexpectedly_truncated_result_stops_the_batch()
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q01"
            ? FakeData.Make(spec, now, r => { r["truncated"] = true; r["complete"] = false; }, ToolResultCompleteness.Partial)
            : FakeData.Result(spec, now);
        await AssertAborted(rig, expectedCalls: 1, expectedExit: 4, expectedText: "truncated");
    }

    [Fact]
    public async Task A_truncation_that_the_plan_allows_is_kept_and_recorded()
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q02"
            ? FakeData.Make(spec, now, r => { r["truncated"] = true; r["complete"] = false; }, ToolResultCompleteness.Partial)
            : FakeData.Result(spec, now);
        Assert.Equal(0, await rig.Capture());
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "fixtures", "FB-Q02-system.events.json")))!.AsObject();
        Assert.True(fixture["provenance"]!["truncated"]!.GetValue<bool>());
        Assert.Null(fixture["provenance"]!["limitCoverage"]!["smallestLimitReturningTheSameRecords"]);
    }

    [Theory]
    [InlineData("incomplete-completeness")]
    [InlineData("status")]
    [InlineData("schema")]
    [InlineData("mode")]
    [InlineData("window")]
    [InlineData("sources")]
    [InlineData("counts")]
    [InlineData("not-json")]
    [InlineData("empty-output")]
    [InlineData("no-truncated")]
    public async Task Any_result_that_is_not_what_the_plan_expects_stops_the_batch(string defect)
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) =>
        {
            if (spec.Id != "Q01")
            {
                return FakeData.Result(spec, now);
            }

            return defect switch
            {
                "incomplete-completeness" => FakeData.Make(spec, now, null, ToolResultCompleteness.Partial),
                "status" => FakeData.Make(spec, now, r => r["status"] = "partial"),
                "schema" => FakeData.Make(spec, now, r => r["schemaVersion"] = 1),
                "mode" => FakeData.Make(spec, now, r => r["mode"] = "raw"),
                "window" => FakeData.Make(spec, now, r => r["window"]!["fromUtc"] = now.UtcDateTime.AddHours(-2).ToString("O")),
                "sources" => FakeData.Make(spec, now, r => r["sources"]![1]!["status"] = "unavailable"),
                "counts" => FakeData.Make(spec, now, r => r.Remove("observedGroups")),
                "not-json" => new ToolCallResult(ToolOutcome.Success, "not json", null) { Completeness = ToolResultCompleteness.Complete },
                "empty-output" => new ToolCallResult(ToolOutcome.Success, string.Empty, null) { Completeness = ToolResultCompleteness.Complete },
                "no-truncated" => FakeData.Make(spec, now, r => r.Remove("truncated")),
                _ => throw new InvalidOperationException(defect),
            };
        };
        await AssertAborted(rig, expectedCalls: 1, expectedExit: 4, expectedText: "unexpected result");
    }

    [Fact]
    public async Task An_output_over_the_budget_stops_the_batch()
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q01"
            ? FakeData.Make(spec, now, r => r["groups"] = new JsonArray(new JsonObject { ["m"] = new string('x', 40_000) }))
            : FakeData.Result(spec, now);
        await AssertAborted(rig, expectedCalls: 1, expectedExit: 4, expectedText: "budget");
    }

    [Fact]
    public async Task A_collector_exception_stops_the_batch_with_its_own_exit_code()
    {
        using var rig = new Rig();
        rig.Collectors.Throw = new UnauthorizedAccessException("denied");
        await AssertAborted(rig, expectedCalls: 1, expectedExit: 5, expectedText: "did not return a result");
    }

    [Fact]
    public async Task A_collector_that_never_returns_is_stopped_by_the_call_timeout()
    {
        using var rig = new Rig();
        rig.Collectors.Hang = true;
        var code = await rig.Capture("--call-timeout-seconds", "0.2");
        Assert.Equal(5, code);
        Assert.Single(rig.Collectors.Calls);
        Assert.False(Directory.Exists(Path.Combine(rig.SnapshotDir, "fixtures")));
    }

    [Fact]
    public async Task A_collector_whose_manifest_is_not_the_reviewed_one_is_refused_before_it_runs()
    {
        using var rig = new Rig();
        rig.Collectors.ManifestOverride = m => m with { Description = m.Description + " (changed)" };
        var code = await rig.Capture();
        Assert.Equal(4, code);
        Assert.Contains("differs from the reviewed one", rig.Err.ToString());
        Assert.Empty(rig.Collectors.Calls);
    }

    [Fact]
    public async Task A_collector_that_declares_a_higher_risk_is_refused_before_it_runs()
    {
        using var rig = new Rig();
        rig.Collectors.ManifestOverride = m => m with { Risk = RiskLevel.Medium };
        Assert.Equal(4, await rig.Capture());
        Assert.Empty(rig.Collectors.Calls);
    }

    [Fact]
    public async Task A_batch_whose_windows_end_too_far_apart_is_not_promoted()
    {
        using var rig = new Rig();
        rig.Collectors.AdvancePerCall = TimeSpan.FromSeconds(60);
        var code = await rig.Capture();
        Assert.Equal(6, code);
        Assert.Contains("not promoted", rig.Err.ToString());
        Assert.False(Directory.Exists(Path.Combine(rig.SnapshotDir, "fixtures")));
        Assert.True(File.Exists(Path.Combine(rig.SnapshotDir, "temporal-report.json")));
        Assert.Equal(10, rig.Collectors.Calls.Count);
    }

    [Fact]
    public async Task The_spread_limit_is_a_visible_setting()
    {
        using var rig = new Rig();
        rig.Collectors.AdvancePerCall = TimeSpan.FromSeconds(60);
        Assert.Equal(0, await rig.Capture("--max-window-end-spread-seconds", "600"));
    }

    [Fact]
    public async Task A_clock_that_disagrees_with_the_window_end_stops_the_batch()
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q01" ? FakeData.Make(spec, now + TimeSpan.FromMinutes(10), null) : FakeData.Result(spec, now);
        await AssertAborted(rig, expectedCalls: 1, expectedExit: 4, expectedText: "outside the call interval");
    }

    [Fact]
    public async Task Two_captures_never_share_a_directory_nor_overwrite_each_other()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var before = Directory.GetFiles(rig.SnapshotDir, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => File.ReadAllBytes(p));
        rig.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await rig.Capture());
        Assert.Equal(2, Directory.GetDirectories(rig.OutRoot).Length);
        foreach (var (path, bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        // The same instant and suffix would be the same directory: refused.
        rig.Clock.Set(Rig.Start);
        Assert.Equal(2, await rig.Capture());
    }
}
