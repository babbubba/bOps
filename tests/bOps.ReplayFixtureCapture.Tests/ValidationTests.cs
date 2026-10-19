// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.ReplayFixtureCapture.Tests;

public sealed class ValidationTests
{
    private static ValidationReport Validate(Rig rig, double maxSpreadSeconds = 300) =>
        SnapshotValidator.Validate(rig.SnapshotDir, TimeSpan.FromSeconds(maxSpreadSeconds));

    private static Check Find(ValidationReport report, string fragment) =>
        Assert.Single(report.Checks, c => c.Name.Contains(fragment, StringComparison.Ordinal));

    private static string Digest(Rig rig) => string.Join("\n", Directory.GetFiles(Path.Combine(rig.SnapshotDir, "fixtures"), "F*.json").Order(StringComparer.Ordinal)
        .Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));

    [Fact]
    public async Task A_clean_capture_validates_with_no_failure_and_the_report_states_the_decision_is_the_reviewers()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var report = Validate(rig);
        Assert.False(report.HasFail, report.ToText());
        Assert.False(report.HasCredentialHits);
        Assert.Equal("PASS-WITH-WARNINGS", report.ToJson()["verdict"]!.GetValue<string>());
        Assert.Contains("decision for the reviewer", report.ToJson()["note"]!.GetValue<string>());
        Assert.Contains(report.Checks, c => c.Area == "Completion" && c.Status == CheckStatus.Pass && c.Name.StartsWith("all 10 mandatory"));
        Assert.Equal("none captured (disabled, as approved)", Find(report, "optional queries").Detail);
        Assert.Equal(CheckStatus.Pass, Find(report, "Medium integrity, Limited").Status);
        Assert.Equal(CheckStatus.Pass, Find(report, "content, output and file SHA-256").Status);
        Assert.Equal(CheckStatus.Pass, Find(report, "every window is exactly the requested length").Status);
        Assert.Equal(CheckStatus.Pass, Find(report, "window ends span at most 300 s").Status);
        Assert.Equal(CheckStatus.Pass, Find(report, "every result meets the plan's expectations").Status);
        Assert.Equal(CheckStatus.Pass, Find(report, "pool is separate").Status);
        Assert.Equal(CheckStatus.Pass, Find(report, "kept outside any Git working tree").Status);
    }

    [Fact]
    public async Task Partial_truncated_and_non_available_outputs_are_reported_per_query()
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Tool == "system.stability"
            ? FakeData.Make(spec, now, r => r["sources"]!.AsArray().Add(new JsonObject { ["name"] = "windows.minidump", ["status"] = "unavailable" }), ToolResultCompleteness.Partial)
            : FakeData.Result(spec, now);
        Assert.Equal(0, await rig.Capture());
        var report = Validate(rig);
        Assert.False(report.HasFail, report.ToText());
        var outputs = report.ToJson()["details"]!["outputs"]!.AsArray();
        Assert.Equal(10, outputs.Count);
        var q06 = outputs.Single(o => o!["id"]!.GetValue<string>() == "Q06")!;
        Assert.True(q06["partialOrIncomplete"]!.GetValue<bool>());
        Assert.Equal("windows.minidump=unavailable", q06["nonAvailableSources"]![0]!.GetValue<string>());
        var q05 = outputs.Single(o => o!["id"]!.GetValue<string>() == "Q05")!;
        Assert.True(q05["truncated"]!.GetValue<bool>());
        Assert.True(q05["truncationAllowedByPlan"]!.GetValue<bool>());
        Assert.Equal(CheckStatus.Warn, Find(report, "Partial / incomplete").Status);
        Assert.Equal(CheckStatus.Warn, Find(report, "Truncated results").Status);
        Assert.Contains("1 of 10 are truncated", Find(report, "Truncated results").Detail);
        Assert.Contains("[Outputs]", report.ToText());
    }

    [Fact]
    public async Task Sensitive_data_is_counted_by_pattern_and_never_stored_and_credential_shapes_change_the_verdict()
    {
        using var rig = new Rig();
        var leaky = "password=" + "Hunter2Hunter2";
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q03"
            ? FakeData.Make(spec, now, r => r["groups"] = new JsonArray(new JsonObject { ["sampleMessage"] = "connect " + leaky }))
            : FakeData.Result(spec, now);
        Assert.Equal(3, await rig.Capture());
        var report = Validate(rig);
        Assert.False(report.HasFail, report.ToText());
        Assert.True(report.HasCredentialHits);
        Assert.Equal("REVIEW-CREDENTIAL-SHAPES", report.ToJson()["verdict"]!.GetValue<string>());
        var sensitive = report.ToJson()["details"]!["sensitive"]!;
        Assert.Equal(1, sensitive["credentialShapedHits"]!.GetValue<int>());
        Assert.True(sensitive["totalsByPattern"]!["capture-identity-literal"]!.GetValue<int>() > 0);
        Assert.True(sensitive["totalsByPattern"]!["user-profile-path"]!.GetValue<int>() > 0);
        Assert.DoesNotContain("Hunter2Hunter2", report.ToJson().ToJsonString());
        Assert.DoesNotContain("Hunter2Hunter2", report.ToText());

        Assert.Equal(3, await rig.Run("validate-snapshot", "--snapshot", rig.SnapshotDir));
    }

    [Fact]
    public async Task The_coverage_section_asks_the_unchanged_matcher_about_the_earlier_unsupported_calls()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var report = Validate(rig);
        var coverage = report.ToJson()["details"]!["coverage"]!.AsArray().ToDictionary(r => r!["label"]!.GetValue<string>(), r => r!.AsObject());
        Assert.Equal(12, coverage.Count);
        Assert.False(coverage["O2"]["served"]!.GetValue<bool>());         // raw warning at the default limit: only OPT1 covers it
        Assert.False(coverage["O2"]["expectedByDesign"]!.GetValue<bool>());
        foreach (var label in coverage.Keys.Where(k => k != "O2"))
        {
            Assert.True(coverage[label]["served"]!.GetValue<bool>(), label + " " + coverage[label]["result"]);
        }

        Assert.StartsWith("FB-Q03-system.events via exact", coverage["R1"]["result"]!.GetValue<string>());
        Assert.StartsWith("FB-Q05-system.crashes via default-equivalent", coverage["O4"]["result"]!.GetValue<string>());
        Assert.StartsWith("FB-Q06-system.stability via limit-not-binding", coverage["O6"]["result"]!.GetValue<string>());
        Assert.Contains("limit-not-binding", coverage["O5"]["result"]!.GetValue<string>());
        Assert.StartsWith("FB-Q10-system.stability via limit-not-binding", coverage["R2"]["result"]!.GetValue<string>());
        Assert.StartsWith("FB-Q04-system.events via limit-not-binding", coverage["R3"]["result"]!.GetValue<string>());
        Assert.Equal(CheckStatus.Pass, Find(report, "11 of 12 earlier calls").Status);
    }

    [Fact]
    public async Task A_truncated_capture_does_not_cover_a_smaller_limit_and_the_coverage_says_so()
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q02"
            ? FakeData.Make(spec, now, r => { r["truncated"] = true; r["complete"] = false; }, ToolResultCompleteness.Partial)
            : FakeData.Result(spec, now);
        Assert.Equal(0, await rig.Capture());
        var report = Validate(rig);
        var o1 = report.ToJson()["details"]!["coverage"]!.AsArray().Single(r => r!["label"]!.GetValue<string>() == "O1")!;
        Assert.False(o1["served"]!.GetValue<bool>());
        Assert.Equal(CheckStatus.Warn, Find(report, "earlier calls would be served").Status);
        Assert.Contains("O1", Find(report, "earlier calls would be served").Detail);
    }

    [Fact]
    public async Task The_optional_queries_when_captured_are_reported_and_extend_the_coverage()
    {
        using var rig = new Rig();
        var hash = CapturePlan.Hash(CapturePlan.Select(["OPT1", "OPT2"]));
        Assert.Equal(0, await rig.Run("capture", "--enable-capture", "--plan-sha256", hash, "--out-root", rig.OutRoot, "--include-optional", "OPT1,OPT2"));
        var report = Validate(rig);
        Assert.False(report.HasFail, report.ToText());
        Assert.Equal(CheckStatus.Warn, Find(report, "optional queries").Status);
        Assert.Contains("OPT1, OPT2", Find(report, "optional queries").Detail);
        var o2 = report.ToJson()["details"]!["coverage"]!.AsArray().Single(r => r!["label"]!.GetValue<string>() == "O2")!;
        Assert.True(o2["served"]!.GetValue<bool>());
    }

    // ---- what the validator must catch

    [Fact]
    public async Task A_batch_that_did_not_complete_is_not_a_pool()
    {
        using var rig = new Rig();
        rig.Collectors.Throw = new InvalidOperationException("boom");
        Assert.Equal(5, await rig.Capture());
        var report = Validate(rig);
        Assert.True(report.HasFail);
        Assert.Equal(CheckStatus.Fail, report.Checks[0].Status);
        Assert.Equal(2, await rig.Run("validate-snapshot", "--snapshot", rig.SnapshotDir));
    }

    [Fact]
    public async Task A_missing_acquisition_a_modified_fixture_and_a_foreign_file_are_caught()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var fixtures = Path.Combine(rig.SnapshotDir, "fixtures");

        var q04 = Path.Combine(fixtures, "FB-Q04-system.events.json");
        File.SetAttributes(q04, FileAttributes.Normal);
        File.Delete(q04);
        var missing = Validate(rig);
        Assert.Equal(CheckStatus.Fail, Find(missing, "mandatory acquisitions").Status);
        Assert.Contains("Q04", Find(missing, "mandatory acquisitions").Detail);
    }

    [Fact]
    public async Task A_changed_payload_fails_the_integrity_and_hash_checks()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var q05 = Path.Combine(rig.SnapshotDir, "fixtures", "FB-Q05-system.crashes.json");
        File.SetAttributes(q05, FileAttributes.Normal);
        File.WriteAllText(q05, File.ReadAllText(q05).Replace("\"observedItems\\\":54", "\"observedItems\\\":55"));
        var report = Validate(rig);
        Assert.True(report.HasFail);
        Assert.Equal(CheckStatus.Fail, Find(report, "SHA256SUMS").Status);
        Assert.Equal(CheckStatus.Fail, Find(report, "content, output and file SHA-256").Status);
    }

    [Fact]
    public async Task A_historical_fixture_inside_the_pool_is_caught()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        File.Copy(Path.Combine(rig.SnapshotDir, "fixtures", "FB-Q01-system.events.json"), Path.Combine(rig.SnapshotDir, "fixtures", "F02-system.events.json"));
        var report = Validate(rig);
        Assert.Equal(CheckStatus.Fail, Find(report, "pool is separate").Status);
    }

    [Fact]
    public async Task A_spread_above_the_limit_chosen_at_validation_time_fails()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var report = Validate(rig, maxSpreadSeconds: 10);
        Assert.Equal(CheckStatus.Fail, Find(report, "window ends span at most 10 s").Status);
        Assert.Contains("18.0 s", Find(report, "window ends span at most 10 s").Detail);
    }

    [Fact]
    public async Task A_token_that_is_not_limited_fails_the_validation_even_if_the_capture_ran()
    {
        using var rig = new Rig(environment: new FakeEnvironment(elevationType: "Default"));
        Assert.Equal(0, await rig.Capture());
        var report = Validate(rig);
        Assert.Equal(CheckStatus.Fail, Find(report, "Medium integrity, Limited").Status);
        Assert.Contains("Default", Find(report, "Medium integrity, Limited").Detail);
    }

    [Fact]
    public async Task The_required_elevation_type_is_enforced_by_verify_and_by_capture_before_any_collector()
    {
        using var rig = new Rig(environment: new FakeEnvironment(elevationType: "Default")) { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(2, await rig.Run("verify", "--require-elevation-type", "Limited"));
        Assert.Contains("elevation type is 'Default', 'Limited' was required", rig.Out.ToString());
        Assert.Equal(2, await rig.Run("capture", "--enable-capture", "--plan-sha256", Rig.DefaultPlanHash, "--out-root", rig.OutRoot, "--require-elevation-type", "Limited"));
        Assert.Contains("'Limited' was required", rig.Err.ToString());
        Assert.Equal(0, rig.CollectorFactoryCalls);
        Assert.False(Directory.Exists(rig.OutRoot));

        using var ok = new Rig();
        Assert.Equal(0, await ok.Capture("--require-elevation-type", "Limited"));
        using var verify = new Rig { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(0, await verify.Run("verify", "--require-elevation-type", "Limited"));
        Assert.Contains("--require-elevation-type Limited", verify.Out.ToString());
    }

    [Fact]
    public async Task Validation_changes_no_payload_and_only_adds_its_report()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var before = Digest(rig);
        Assert.Equal(0, await rig.Run("validate-snapshot", "--snapshot", rig.SnapshotDir));
        Assert.Equal(before, Digest(rig));
        var report = Assert.Single(Directory.GetFiles(Path.Combine(rig.SnapshotDir, "reports"), "validation-*.json"));
        Assert.Equal("PASS-WITH-WARNINGS", JsonNode.Parse(File.ReadAllText(report))!["verdict"]!.GetValue<string>());
        Assert.Contains("VERDICT: PASS-WITH-WARNINGS", rig.Out.ToString());
        // The read-only fixtures and the original sums stay intact: a new report is not a change to the snapshot's evidence.
        Assert.Equal(0, await rig.Run("check-snapshot", "--snapshot", rig.SnapshotDir));
    }
}
