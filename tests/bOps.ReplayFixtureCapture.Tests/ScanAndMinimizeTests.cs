// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.ReplayFixtureCapture.Tests;

public sealed class ScanAndMinimizeTests
{
    // Credential-shaped samples are assembled at run time so this file never contains a string a secret scanner would flag.
    private static string Aws => "AK" + "IA" + "ABCDEFGHIJKLMNOP";
    private static string Github => "gh" + "p_" + new string('a', 36);
    private static string Jwt => "ey" + "Jabcdefghij.abcdefghijk.abcdefghijk";
    private static string Pem => "-----BEGIN " + "RSA PRIVATE KEY-----";

    [Theory]
    [InlineData("private-key-block")]
    [InlineData("aws-access-key-id")]
    [InlineData("github-token")]
    [InlineData("jwt")]
    [InlineData("bearer-credential")]
    [InlineData("password-assignment")]
    [InlineData("secret-or-key-assignment")]
    [InlineData("url-with-credentials")]
    public void Each_credential_shape_is_detected_and_the_hit_does_not_contain_the_matched_text(string pattern)
    {
        var sample = pattern switch
        {
            "private-key-block" => Pem,
            "aws-access-key-id" => "key " + Aws,
            "github-token" => "t=" + Github,
            "jwt" => "auth " + Jwt,
            "bearer-credential" => "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123",
            "password-assignment" => "connect password=" + "Hunter2Hunter2",
            "secret-or-key-assignment" => "client_secret: " + "abcdEFGH12345678",
            _ => "https://user:" + "pa55w0rd" + "@example.org/x",
        };
        var hits = SecretScanner.Scan("prefix " + sample + " suffix");
        var hit = Assert.Single(hits, h => h.PatternId == pattern);
        Assert.Equal(ScanCategory.Secret, hit.Category);
        Assert.Equal(1, hit.Count);
        Assert.All(hit.DistinctValueDigests, d => Assert.Equal(8, d.Length));
        var json = SecretScanner.ToJson(hits).ToJsonString();
        Assert.DoesNotContain("Hunter2Hunter2", json);
        Assert.DoesNotContain("pa55w0rd", json);
        Assert.DoesNotContain(Aws, json);
        Assert.DoesNotContain("abcdEFGH12345678", json);
    }

    [Fact]
    public void Ordinary_event_text_is_not_a_credential()
    {
        var text = "CancellationToken token Task a0d4fa7e-6c46-4295-83bf-4e8695338fd8 hash 04bc2e5a2ab042666c6a14931fa5468fa118f24011834cba5fb4dcd7268b40b0 "
            + "Password policy was applied; the password was changed. StatusCode: 0xC0000365 Bearer of bad news";
        Assert.DoesNotContain(SecretScanner.Scan(text), h => h.Category == ScanCategory.Secret);
    }

    [Fact]
    public void Identifying_strings_are_counted_in_their_own_category()
    {
        var text = @"S-1-5-21-1111111111-2222222222-3333333333-1001 at 10.0.0.12 mail bob@example.org C:\\Users\\bob\\x \\\\fileserver\\share host TESTBOX-01 TESTBOX-01";
        var hits = SecretScanner.Scan(text, ["TESTBOX-01", "bob", ""]);
        Assert.All(hits, h => Assert.Equal(ScanCategory.Identifier, h.Category));
        foreach (var id in new[] { "windows-sid", "ipv4-address", "email-address", "user-profile-path", "unc-path", "capture-identity-literal" })
        {
            Assert.Contains(hits, h => h.PatternId == id);
        }

        Assert.True(hits.Where(h => h.PatternId == "capture-identity-literal").Sum(h => h.Count) >= 3);
    }

    [Fact]
    public async Task A_captured_result_with_a_credential_shape_is_kept_unchanged_flagged_and_the_run_exits_with_the_review_code()
    {
        using var rig = new Rig();
        var leaky = "password=" + "Hunter2Hunter2";
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q02"
            ? FakeData.Make(spec, now, r => r["groups"] = new JsonArray(new JsonObject { ["sampleMessage"] = "connect " + leaky }))
            : FakeData.Result(spec, now);
        Assert.Equal(3, await rig.Capture());
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "fixtures", "FB-Q02-system.events.json")))!.AsObject();
        Assert.Contains(leaky, fixture["result"]!["Output"]!.GetValue<string>());
        Assert.Equal(["password-assignment"], fixture["secretScanHits"]!.AsArray().Select(h => h!.GetValue<string>()).ToArray());
        var index = File.ReadAllText(Path.Combine(rig.SnapshotDir, "index.json"));
        Assert.DoesNotContain("Hunter2Hunter2", index);
        Assert.Contains("need review", rig.Out.ToString());

        Assert.Equal(3, await rig.Run("scan", "--snapshot", rig.SnapshotDir));
        var reports = Directory.GetFiles(Path.Combine(rig.SnapshotDir, "reports"), "scan-*.json");
        var report = Assert.Single(reports);
        Assert.DoesNotContain("Hunter2Hunter2", File.ReadAllText(report));
    }

    [Fact]
    public async Task A_clean_snapshot_scans_clean_and_scanning_does_not_touch_the_payloads()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var before = Digest(rig);
        Assert.Equal(0, await rig.Run("scan", "--snapshot", rig.SnapshotDir));
        Assert.Equal(before, Digest(rig));
        var report = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(rig.SnapshotDir, "reports"))[0]))!.AsObject();
        Assert.Equal(0, report["credentialShapedHits"]!.GetValue<int>());
        // The fake message carries the machine, the account and a profile path: reported as identifiers.
        Assert.Contains("capture-identity-literal", report.ToJsonString());
        Assert.Contains("user-profile-path", report.ToJsonString());
    }

    private static string Digest(Rig rig) => string.Join("\n", Directory.GetFiles(Path.Combine(rig.SnapshotDir, "fixtures"), "F*.json").Order(StringComparer.Ordinal)
        .Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));

    // ---- minimization

    private const string Rules = """
        {"version":1,"rules":[
          {"id":"host","kind":"literal","value":"TESTBOX-01","replacement":"<HOST>","ignoreCase":true},
          {"id":"account","kind":"literal","value":"bob","replacement":"<USER>","ignoreCase":true}
        ]}
        """;

    private static async Task<(Rig Rig, string RulesFile)> Captured()
    {
        var rig = new Rig();
        Assert.Equal(0, await rig.Capture());
        var rules = Path.Combine(rig.Root.Path, "rules.json");
        File.WriteAllText(rules, Rules);
        return (rig, rules);
    }

    [Fact]
    public async Task Minimization_writes_a_derived_copy_and_leaves_the_originals_byte_identical_and_read_only()
    {
        var (rig, rules) = await Captured();
        using var _ = rig;
        var before = Digest(rig);
        Assert.Equal(0, await rig.Run("minimize", "--snapshot", rig.SnapshotDir, "--rules", rules));
        Assert.Equal(before, Digest(rig));
        Assert.All(Directory.GetFiles(Path.Combine(rig.SnapshotDir, "fixtures")), f => Assert.True(File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly)));
        Assert.Equal(0, await rig.Run("check-snapshot", "--snapshot", rig.SnapshotDir));

        var derived = Assert.Single(Directory.GetDirectories(Path.Combine(rig.SnapshotDir, "minimized")));
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(derived, "fixtures", "FB-Q01-system.events.json")))!.AsObject();
        var output = file["result"]!["Output"]!.GetValue<string>();
        Assert.DoesNotContain("TESTBOX-01", output);
        Assert.DoesNotContain("bob", output);
        Assert.Contains("<HOST>", output);
        Assert.Contains(@"C:\\Users\\<USER>\\AppData", output);
        Assert.Equal(@"C:\Users\<USER>\x", file["result"]!["Facts"]![0]!["Value"]!.GetValue<string>());
        Assert.Contains("not the captured evidence", file["provenance"]!["minimized"]!["statement"]!.GetValue<string>());
        var originalOutput = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "fixtures", "FB-Q01-system.events.json")))!["result"]!["Output"]!.GetValue<string>();
        Assert.True(Minimizer.SkeletonEqual(originalOutput, output));
        Assert.NotEqual(originalOutput, output);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(derived, "minimization-manifest.json")))!.AsObject();
        Assert.Equal(10, manifest["fixtures"]!.AsArray().Count);
        var entry = manifest["fixtures"]![0]!.AsObject();
        Assert.True(entry["structurePreserved"]!.GetValue<bool>());
        Assert.True(entry["replacements"]!["host"]!.GetValue<int>() >= 2);
        Assert.NotEqual(entry["originalOutputSha256"]!.GetValue<string>(), entry["minimizedOutputSha256"]!.GetValue<string>());
        Assert.DoesNotContain("TESTBOX-01", File.ReadAllText(Path.Combine(derived, "minimization-manifest.json")));
    }

    [Fact]
    public async Task A_minimized_copy_verifies_and_any_change_to_it_or_to_the_rules_is_detected()
    {
        var (rig, rules) = await Captured();
        using var _ = rig;
        Assert.Equal(0, await rig.Run("minimize", "--snapshot", rig.SnapshotDir, "--rules", rules));
        Assert.Equal(0, await rig.Run("minimize", "--snapshot", rig.SnapshotDir, "--rules", rules, "--verify"));
        Assert.Contains("MINIMIZATION VERIFIED", rig.Out.ToString());

        var derived = Directory.GetDirectories(Path.Combine(rig.SnapshotDir, "minimized"))[0];
        var file = Path.Combine(derived, "fixtures", "FB-Q03-system.events.json");
        File.AppendAllText(file, " ");
        Assert.Equal(2, await rig.Run("minimize", "--snapshot", rig.SnapshotDir, "--rules", rules, "--verify"));
        Assert.Contains("not what the rules produce", rig.Out.ToString());

        var otherRules = Path.Combine(rig.Root.Path, "other.json");
        File.WriteAllText(otherRules, Rules.Replace("<HOST>", "<H>"));
        Assert.Equal(2, await rig.Run("minimize", "--snapshot", rig.SnapshotDir, "--rules", otherRules, "--verify"));
    }

    [Fact]
    public async Task Minimization_is_deterministic_and_never_overwrites_a_derived_copy()
    {
        var (rig, rules) = await Captured();
        using var _ = rig;
        var parsed = MinimizationRules.Parse(File.ReadAllText(rules));
        var a = Minimizer.Build(rig.SnapshotDir, parsed);
        var b = Minimizer.Build(rig.SnapshotDir, parsed);
        Assert.Equal(a.Select(x => x.Text), b.Select(x => x.Text));
        Assert.Equal(0, await rig.Run("minimize", "--snapshot", rig.SnapshotDir, "--rules", rules));
        Assert.Equal(2, await rig.Run("minimize", "--snapshot", rig.SnapshotDir, "--rules", rules));
        Assert.Contains("never overwritten", rig.Err.ToString());
    }

    [Fact]
    public async Task A_rule_that_would_break_the_json_structure_writes_nothing()
    {
        var (rig, _) = await Captured();
        using var _r = rig;
        var breaking = MinimizationRules.Parse("""{"rules":[{"id":"colon","kind":"regex","pattern":"\"status\":","replacement":"x"}]}""");
        Assert.Throws<InvalidOperationException>(() => Minimizer.Build(rig.SnapshotDir, breaking));
        Assert.False(Directory.Exists(Path.Combine(rig.SnapshotDir, "minimized")));
    }

    [Theory]
    [InlineData("""{"rules":[{"id":"a","kind":"literal","value":"x","replacement":"has \"quote"}]}""")]
    [InlineData("""{"rules":[{"id":"a","kind":"literal","value":"x","replacement":"back\\slash"}]}""")]
    [InlineData("""{"rules":[{"id":"a","kind":"literal","value":"","replacement":"y"}]}""")]
    [InlineData("""{"rules":[{"id":"a","kind":"fuzzy","value":"x","replacement":"y"}]}""")]
    [InlineData("""{"rules":[{"id":"a","kind":"literal","value":"x","replacement":"y"},{"id":"a","kind":"literal","value":"z","replacement":"w"}]}""")]
    public void Unsafe_or_ambiguous_rule_sets_are_rejected(string json)
    {
        Assert.Throws<FormatException>(() => MinimizationRules.Parse(json));
    }

    [Fact]
    public async Task The_suggested_rules_come_from_the_snapshot_environment_and_are_valid()
    {
        var (rig, _) = await Captured();
        using var _r = rig;
        Assert.Equal(0, await rig.Run("suggest-rules", "--snapshot", rig.SnapshotDir));
        var text = rig.Out.ToString();
        var suggested = text[text.IndexOf('{')..];
        var rules = MinimizationRules.Parse(suggested);
        Assert.Equal(["machine-name", "account-name", "user-profile-path"], rules.Rules.Select(r => r.Id).ToArray());
        Assert.Contains("TESTBOX-01", suggested);
        Assert.Contains("bob", suggested);
        Assert.DoesNotContain(@"DOMAIN\\bob", suggested);

        var minimized = Minimizer.Build(rig.SnapshotDir, rules);
        Assert.All(minimized, m => Assert.DoesNotContain("TESTBOX-01", m.Text));
        Assert.All(minimized, m => Assert.DoesNotContain(@"C:\\\\Users\\\\bob", m.Text));
    }

    // ---- the output location

    [Fact]
    public void The_output_guard_accepts_a_new_directory_outside_git_and_names_every_problem_otherwise()
    {
        using var temp = new TempDir();
        Assert.Empty(OutputRootGuard.Problems(Path.Combine(temp.Path, "new"), "T02-B-x"));

        Directory.CreateDirectory(Path.Combine(temp.Path, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "repo", "x", "T02-B-x"));
        File.WriteAllText(Path.Combine(temp.Path, "repo", "x", "F01.json"), "{}");
        var problems = OutputRootGuard.Problems(Path.Combine(temp.Path, "repo", "x"), "T02-B-x");
        Assert.Equal(3, problems.Count);

        // A .git FILE (a linked worktree) counts too.
        Directory.CreateDirectory(Path.Combine(temp.Path, "wt"));
        File.WriteAllText(Path.Combine(temp.Path, "wt", ".git"), "gitdir: elsewhere");
        Assert.Single(OutputRootGuard.Problems(Path.Combine(temp.Path, "wt", "out"), "T02-B-x"));
    }
}
