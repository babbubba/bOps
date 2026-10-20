// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Principal;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.ReplayFixtureCapture.Tests;

/// <summary>
/// The identity gate: the capture runs only with a Medium-integrity, non-elevated token whose SID and level are recorded. Anything else is refused
/// before a collector is created, and nothing is done to make the process pass (no privilege, no ACL change, no elevation).
/// </summary>
public sealed class IdentityGateTests
{
    [Theory]
    [InlineData(IntegrityLevels.Medium)]
    [InlineData(IntegrityLevels.MediumPlus)]
    public void A_medium_integrity_token_passes(int rid)
    {
        var env = new FakeEnvironment(integrityRid: rid).Probe();
        Assert.Empty(IdentityGate.Problems(env));
        Assert.True(IntegrityLevels.IsMedium(rid));
    }

    [Theory]
    [InlineData(IntegrityLevels.Untrusted, "Untrusted")]
    [InlineData(IntegrityLevels.Low, "Low")]
    [InlineData(IntegrityLevels.High, "High")]
    [InlineData(IntegrityLevels.System, "System")]
    [InlineData(0x5000, "High-or-above")]
    public async Task A_token_that_is_not_medium_is_refused_before_any_collector_is_created(int rid, string name)
    {
        using var rig = new Rig(environment: new FakeEnvironment(integrityRid: rid));
        Assert.Equal(name, rig.Environment.Probe().IntegrityLevel);
        Assert.NotEmpty(IdentityGate.Problems(rig.Environment.Probe()));

        Assert.Equal(2, await rig.Capture());
        Assert.Contains("identity gate refused", rig.Err.ToString());
        Assert.Contains("Medium is required", rig.Err.ToString());
        Assert.Contains("no privilege is requested", rig.Err.ToString());
        Assert.Equal(0, rig.Collectors.Created);
        Assert.Empty(rig.Collectors.Calls);
        Assert.False(Directory.Exists(rig.OutRoot));
    }

    [Fact]
    public async Task An_unreadable_integrity_level_is_not_proof_of_a_standard_token()
    {
        using var rig = new Rig(environment: new FakeEnvironment(integrityRid: null));
        Assert.Contains("could not be read", string.Join(" ", IdentityGate.Problems(rig.Environment.Probe())));
        Assert.Equal(2, await rig.Capture());
        Assert.Equal(0, rig.Collectors.Created);
    }

    [Fact]
    public async Task An_elevated_process_is_refused_even_if_the_level_looks_medium()
    {
        using var rig = new Rig(environment: new FakeEnvironment(elevated: true, integrityRid: IntegrityLevels.Medium));
        Assert.Equal(2, await rig.Capture());
        Assert.Contains("elevated", rig.Err.ToString());
        Assert.Equal(0, rig.Collectors.Created);
    }

    [Fact]
    public async Task An_enabled_administrators_role_is_refused_even_at_medium_integrity()
    {
        using var rig = new Rig(environment: new FakeEnvironment(administratorRole: true));
        Assert.Contains("Administrators role", string.Join(" ", IdentityGate.Problems(rig.Environment.Probe())));
        Assert.Equal(2, await rig.Capture());
        Assert.Equal(0, rig.Collectors.Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sid")]
    public async Task A_missing_or_malformed_sid_is_refused(string sid)
    {
        using var rig = new Rig(environment: new FakeEnvironment(sid: sid));
        Assert.Contains("SID", string.Join(" ", IdentityGate.Problems(rig.Environment.Probe())));
        Assert.Equal(2, await rig.Capture());
        Assert.Equal(0, rig.Collectors.Created);
    }

    [Fact]
    public async Task Verify_shows_the_sid_the_integrity_level_and_the_gate_result_and_is_not_ok_for_a_high_token()
    {
        using var ok = new Rig { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(0, await ok.Run("verify"));
        var text = ok.Out.ToString();
        Assert.Contains("SID: S-1-5-21-1111111111-2222222222-3333333333-1001", text);
        Assert.Contains("integrity Medium (0x2000)", text);
        Assert.Contains("elevation type: Limited", text);
        Assert.Contains("identity gate: PASS", text);

        using var high = new Rig(environment: new FakeEnvironment(integrityRid: IntegrityLevels.High)) { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(2, await high.Run("verify"));
        Assert.Contains("identity gate: REFUSED", high.Out.ToString());
        Assert.Contains("integrity level High (0x3000)", high.Out.ToString());
        Assert.Equal(0, high.CollectorFactoryCalls);

        using var json = new Rig { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(0, await json.Run("verify", "--json"));
        var identity = JsonNode.Parse(json.Out.ToString())!["identity"]!;
        Assert.Equal("Medium", identity["integrityLevel"]!.GetValue<string>());
        Assert.Equal("0x2000", identity["integrityRid"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_sid_integrity_level_and_elevation_type_are_recorded_in_the_snapshot_every_fixture_and_the_ledger()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.Capture());

        var env = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "environment.json")))!.AsObject();
        Assert.Equal("S-1-5-21-1111111111-2222222222-3333333333-1001", env["userSid"]!.GetValue<string>());
        Assert.Equal("Medium", env["integrityLevel"]!.GetValue<string>());
        Assert.Equal("0x2000", env["integrityRid"]!.GetValue<string>());
        Assert.Equal("Limited", env["elevationType"]!.GetValue<string>());
        Assert.False(env["isElevated"]!.GetValue<bool>());
        Assert.Empty(env["identityGate"]!["gateProblems"]!.AsArray());

        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "index.json")))!.AsObject();
        Assert.Equal("Medium", index["capturedBy"]!["integrityLevel"]!.GetValue<string>());

        foreach (var path in Directory.GetFiles(Path.Combine(rig.SnapshotDir, "fixtures"), "F*.json"))
        {
            var identity = JsonNode.Parse(File.ReadAllText(path))!["provenance"]!["identity"]!;
            Assert.Equal("S-1-5-21-1111111111-2222222222-3333333333-1001", identity["sid"]!.GetValue<string>());
            Assert.Equal("Medium", identity["integrityLevel"]!.GetValue<string>());
            Assert.Equal("0x2000", identity["integrityRid"]!.GetValue<string>());
            Assert.Equal("Limited", identity["elevationType"]!.GetValue<string>());
            Assert.False(identity["elevated"]!.GetValue<bool>());
        }

        var ledger = File.ReadAllLines(Path.Combine(rig.SnapshotDir, "capture-ledger.jsonl"));
        var verified = Assert.Single(ledger, l => l.Contains("\"event\":\"identityVerified\""));
        Assert.Contains("0x2000", verified);
        Assert.True(Array.FindIndex(ledger, l => l.Contains("identityVerified")) < Array.FindIndex(ledger, l => l.Contains("queryStarted")));
    }

    [Fact]
    public async Task Data_the_standard_token_cannot_read_stays_partial_or_unavailable_and_the_capture_keeps_it_as_such()
    {
        // The real F01 already shows it: under a non-elevated token the minidump source is unavailable and the stability result is partial.
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Tool == "system.stability"
            ? FakeData.Make(spec, now, r =>
            {
                r["status"] = "partial";
                r["sources"]!.AsArray().Add(new JsonObject { ["name"] = "windows.minidump", ["status"] = "unavailable", ["detail"] = "The minidump directory is not readable by this identity; it usually requires elevation." });
            }, ToolResultCompleteness.Partial)
            : FakeData.Result(spec, now);
        Assert.Equal(0, await rig.Capture());
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.SnapshotDir, "fixtures", "FB-Q06-system.stability.json")))!.AsObject();
        Assert.Contains("windows.minidump", fixture["result"]!["Output"]!.GetValue<string>());
        Assert.Equal("Partial", fixture["provenance"]!["toolCompleteness"]!.GetValue<string>());
        Assert.Equal("partial", fixture["provenance"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Event_channels_the_token_cannot_read_stop_the_batch_instead_of_being_worked_around()
    {
        using var rig = new Rig();
        rig.Collectors.Respond = (spec, now) => spec.Id == "Q01"
            ? FakeData.Make(spec, now, r => { r["sources"]![0]!["status"] = "unavailable"; r["status"] = "partial"; r["complete"] = false; }, ToolResultCompleteness.Partial)
            : FakeData.Result(spec, now);
        Assert.Equal(4, await rig.Capture());
        Assert.Single(rig.Collectors.Calls);
        Assert.False(Directory.Exists(Path.Combine(rig.SnapshotDir, "fixtures")));
    }

    // ---- the real probe (reads this test process's own token; no collector, no privilege change)

    [Fact]
    public void The_real_probe_reports_a_sid_an_integrity_level_and_an_elevation_type_consistent_with_the_token()
    {
        Assert.True(OperatingSystem.IsWindows());
        var env = new RealEnvironmentProbe().Probe();
        using var identity = WindowsIdentity.GetCurrent();

        Assert.Equal(identity.User!.Value, env.UserSid);
        Assert.StartsWith("S-1-5-", env.UserSid);
        Assert.NotNull(env.IntegrityRid);
        Assert.NotEqual("unknown", env.IntegrityLevel);
        Assert.Contains(env.ElevationType, new[] { "Default", "Full", "Limited" });

        // Whatever the account running the tests: an elevated token is High or above, a standard one is below it, and the gate agrees.
        Assert.Equal(env.IsElevated, env.IntegrityRid >= IntegrityLevels.High);
        Assert.Equal(env.IsElevated || env.IsAdministratorRole || !IntegrityLevels.IsMedium(env.IntegrityRid), IdentityGate.Problems(env).Count > 0);
        if (env.ElevationType == "Full")
        {
            Assert.True(env.IsElevated);
        }
    }

    [Fact]
    public void Integrity_level_names_cover_the_documented_levels()
    {
        Assert.Equal("unknown", IntegrityLevels.Name(null));
        Assert.Equal("Medium", IntegrityLevels.Name(0x2000));
        Assert.Equal("MediumPlus", IntegrityLevels.Name(0x2100));
        Assert.Equal("Medium-class", IntegrityLevels.Name(0x2500));
        Assert.Equal("High", IntegrityLevels.Name(0x3000));
        Assert.Equal("System", IntegrityLevels.Name(0x4000));
        Assert.False(IntegrityLevels.IsMedium(0x1fff));
        Assert.False(IntegrityLevels.IsMedium(0x3000));
        Assert.False(IntegrityLevels.IsMedium(null));
    }

    [Fact]
    public void The_gate_never_changes_the_token_or_requests_privileges()
    {
        // The source of the harness must not contain any privilege-adjusting, impersonation or ACL API.
        var root = AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            var source = Path.Combine(directory.FullName, "tests", "bOps.ReplayFixtureCapture");
            if (!Directory.Exists(source))
            {
                continue;
            }

            var text = string.Join("\n", Directory.GetFiles(source, "*.cs", SearchOption.TopDirectoryOnly).Select(File.ReadAllText));
            foreach (var forbidden in new[] { "AdjustTokenPrivileges", "ImpersonateLoggedOnUser", "\"runas\"", "SetAccessControl", "SetSecurityInfo", "LogonUser", "ProcessStartInfo", "Process.Start" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }

            return;
        }

        Assert.Fail("harness source not found above " + root);
    }
}
