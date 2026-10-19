// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Policy;

namespace bOps.ReplayFixtureCapture.Tests;

public sealed class PlanAndVerifyTests
{
    [Fact]
    public void The_plan_has_ten_mandatory_and_two_optional_queries_and_the_optional_ones_are_disabled_by_default()
    {
        Assert.Equal(12, CapturePlan.All.Count);
        Assert.Equal(["OPT1", "OPT2"], CapturePlan.OptionalIds);
        var defaults = CapturePlan.Select([]);
        Assert.Equal(["Q01", "Q02", "Q03", "Q04", "Q05", "Q06", "Q07", "Q08", "Q09", "Q10"], defaults.Select(q => q.Id).ToArray());
        Assert.DoesNotContain(defaults, q => q.Optional);
        Assert.Equal(["Q01", "Q02", "Q03", "Q04", "Q05", "Q06", "Q07", "Q08", "Q09", "Q10", "OPT2"], CapturePlan.Select(["OPT2"]).Select(q => q.Id).ToArray());
        Assert.Throws<ArgumentException>(() => CapturePlan.Select(["Q01"]));
        Assert.Throws<ArgumentException>(() => CapturePlan.Select(["OPT3"]));
    }

    [Fact]
    public void The_plan_is_the_reviewed_list_with_exact_arguments_and_verified_effective_defaults()
    {
        var expected = new (string Id, string Tool, string Args, string Effective)[]
        {
            ("Q01", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}""", """{"limit":500,"maxOutputBytes":32768,"minSeverity":"warning","mode":"aggregate","windowMinutes":1440}"""),
            ("Q02", "system.events", """{"windowMinutes":1440,"limit":500,"mode":"aggregate"}""", """{"limit":500,"maxOutputBytes":32768,"mode":"aggregate","windowMinutes":1440}"""),
            ("Q03", "system.events", """{"windowMinutes":1440,"minSeverity":"error","limit":500,"mode":"aggregate"}""", """{"limit":500,"maxOutputBytes":32768,"minSeverity":"error","mode":"aggregate","windowMinutes":1440}"""),
            ("Q04", "system.events", """{"windowMinutes":1440,"minSeverity":"critical","limit":500,"mode":"raw"}""", """{"limit":500,"maxOutputBytes":32768,"minSeverity":"critical","mode":"raw","windowMinutes":1440}"""),
            ("Q05", "system.crashes", """{"mode":"aggregate","sinceMinutes":1440}""", """{"limit":100,"mode":"aggregate","sinceMinutes":1440}"""),
            ("Q06", "system.stability", """{"windowDays":1,"limit":200}""", """{"limit":200,"windowDays":1}"""),
            ("Q07", "system.stability", """{"windowDays":2,"limit":200}""", """{"limit":200,"windowDays":2}"""),
            ("Q08", "system.stability", """{"windowDays":3,"limit":200}""", """{"limit":200,"windowDays":3}"""),
            ("Q09", "system.stability", """{"windowDays":7,"limit":200}""", """{"limit":200,"windowDays":7}"""),
            ("Q10", "system.stability", """{"windowDays":30,"limit":200}""", """{"limit":200,"windowDays":30}"""),
            ("OPT1", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","mode":"raw"}""", """{"limit":50,"maxOutputBytes":32768,"minSeverity":"warning","mode":"raw","windowMinutes":1440}"""),
            ("OPT2", "system.events", """{"windowMinutes":1440,"minSeverity":"critical","limit":500,"mode":"aggregate"}""", """{"limit":500,"maxOutputBytes":32768,"minSeverity":"critical","mode":"aggregate","windowMinutes":1440}"""),
        };
        Assert.Equal(expected.Select(e => e.Id), CapturePlan.All.Select(q => q.Id));
        foreach (var e in expected)
        {
            var spec = CapturePlan.All.Single(q => q.Id == e.Id);
            Assert.Equal(e.Tool, spec.Tool);
            Assert.Equal(e.Args, spec.ArgumentsJson);
            Assert.Equal(e.Effective, CapturePlan.Canonical(spec.EffectiveArguments));
        }
    }

    [Fact]
    public void Only_three_read_only_tools_are_allowed_and_no_mutating_or_execution_tool_is_authorised()
    {
        Assert.Equal(["system.events", "system.stability", "system.crashes"], CapturePlan.AllowedTools);
        Assert.All(CapturePlan.All, q => Assert.Contains(q.Tool, CapturePlan.AllowedTools));
        var selected = CapturePlan.Select(["OPT1", "OPT2"]);
        foreach (var tool in new[] { "shell.run", "system.exec", "process.kill", "process.stop", "service.restart", "fs.write", "system.dump_analyze" })
        {
            Assert.Throws<UnauthorisedQueryException>(() => CapturePlan.Authorise(selected, "Q01", tool, new JsonObject()));
        }

        Assert.Throws<UnauthorisedQueryException>(() => new RealManifestSource().ManifestOf("process.kill"));
        Assert.Throws<UnauthorisedQueryException>(() => new RealCollectorSource().Create("shell.run"));
    }

    [Fact]
    public void A_call_that_is_not_the_planned_query_is_not_authorised()
    {
        var selected = CapturePlan.Select([]);
        var q01 = selected[0];
        Assert.Same(q01, CapturePlan.Authorise(selected, "Q01", q01.Tool, q01.Arguments));
        Assert.Throws<UnauthorisedQueryException>(() => CapturePlan.Authorise(selected, "Q01", q01.Tool, JsonNode.Parse("""{"windowMinutes":1440,"minSeverity":"warning","limit":499,"mode":"aggregate"}""")!.AsObject()));
        Assert.Throws<UnauthorisedQueryException>(() => CapturePlan.Authorise(selected, "Q01", "system.crashes", q01.Arguments));
        Assert.Throws<UnauthorisedQueryException>(() => CapturePlan.Authorise(selected, "Q99", q01.Tool, q01.Arguments));
        Assert.Throws<UnauthorisedQueryException>(() => CapturePlan.Authorise(selected, "OPT1", "system.events", CapturePlan.All.Single(q => q.Id == "OPT1").Arguments));
        Assert.Throws<UnauthorisedQueryException>(() => CapturePlan.Authorise(selected, "Q01", q01.Tool, JsonNode.Parse("""{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate","text":"x"}""")!.AsObject()));
    }

    [Fact]
    public void Every_planned_query_passes_the_runtime_argument_validation_and_the_read_only_policy_against_the_real_manifests()
    {
        var preflight = Preflight.Run(CapturePlan.Select(["OPT1", "OPT2"]), new RealManifestSource(), ReadOnlyPolicy.Create());
        Assert.Equal(12, preflight.Count);
        Assert.All(preflight, p =>
        {
            Assert.True(p.ReadOnlyRisk, p.Spec.Id);
            Assert.Null(p.ValidationError);
            Assert.Equal("Automatic", p.PolicyMode);
        });
    }

    [Fact]
    public void The_validator_and_the_policy_do_reject_what_the_plan_would_never_ask()
    {
        var manifest = new RealManifestSource().ManifestOf("system.events");
        Assert.NotNull(bOps.Runtime.ArgumentSchema.Validate(manifest.Parameters, ToolArguments.FromJson(JsonNode.Parse("""{"limit":501}""")!.AsObject())));
        Assert.NotNull(bOps.Runtime.ArgumentSchema.Validate(manifest.Parameters, ToolArguments.FromJson(JsonNode.Parse("""{"mode":"Aggregate"}""")!.AsObject())));
        Assert.NotNull(bOps.Runtime.ArgumentSchema.Validate(manifest.Parameters, ToolArguments.FromJson(JsonNode.Parse("""{"shell":"x"}""")!.AsObject())));

        var forbidden = new PolicyEngine(PolicyConfigLoader.Load("defaults:\n  read: forbidden\n  low: forbidden\n  medium: forbidden\n  high: forbidden\n  critical: forbidden\n"));
        Assert.All(Preflight.Run(CapturePlan.Select([]), new RealManifestSource(), forbidden), p => Assert.False(p.Ok));
    }

    [Fact]
    public void The_plan_hash_is_stable_and_changes_with_anything_that_changes_what_is_asked()
    {
        var all = CapturePlan.Select([]);
        Assert.Equal(CapturePlan.Hash(all), CapturePlan.Hash(CapturePlan.Select([])));
        Assert.Equal(64, CapturePlan.Hash(all).Length);
        Assert.NotEqual(CapturePlan.Hash(all), CapturePlan.Hash(CapturePlan.Select(["OPT1"])));
        Assert.NotEqual(CapturePlan.Hash(CapturePlan.Select(["OPT1"])), CapturePlan.Hash(CapturePlan.Select(["OPT2"])));
        Assert.NotEqual(CapturePlan.Hash(all), CapturePlan.Hash([.. all.Take(9)]));
        var altered = all.Select(q => q.Id == "Q03" ? q with { ArgumentsJson = q.ArgumentsJson.Replace("error", "critical") } : q).ToArray();
        Assert.NotEqual(CapturePlan.Hash(all), CapturePlan.Hash(altered));
        var looser = all.Select(q => q.Id == "Q03" ? q with { Expect = q.Expect with { Truncation = TruncationPolicy.Allowed } } : q).ToArray();
        Assert.NotEqual(CapturePlan.Hash(all), CapturePlan.Hash(looser));
        Assert.NotEqual(CapturePlan.Hash(all), CapturePlan.Hash([.. all.Reverse()]));
    }

    [Fact]
    public async Task Verify_checks_the_configuration_without_ever_creating_a_collector()
    {
        using var rig = new Rig { CollectorFactoryMustNotBeCalled = true };
        var code = await rig.Run("verify", "--out-root", rig.OutRoot);
        Assert.Equal(0, code);
        Assert.Equal(0, rig.CollectorFactoryCalls);
        Assert.Empty(rig.Collectors.Calls);
        Assert.False(Directory.Exists(rig.OutRoot));
        var text = rig.Out.ToString();
        Assert.Contains("RESULT: configuration OK", text);
        Assert.Contains("planSha256: " + Rig.DefaultPlanHash, text);
        Assert.Contains($"capture --enable-capture --plan-sha256 {Rig.DefaultPlanHash} --out-root", text);
        Assert.Contains("elevated: False", text);
        Assert.Contains("DOMAIN\\bob", text);
        Assert.DoesNotContain("OPT1 ", text.Replace("OPT1, OPT2", string.Empty));
    }

    [Fact]
    public async Task Verify_is_the_default_command_and_prints_json_on_request()
    {
        using var rig = new Rig { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(0, await rig.Run("--json"));
        var json = JsonNode.Parse(rig.Out.ToString())!.AsObject();
        Assert.Equal(Rig.DefaultPlanHash, json["planSha256"]!.GetValue<string>());
        Assert.Equal(10, json["queries"]!.AsArray().Count);
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal(0, rig.CollectorFactoryCalls);
    }

    [Fact]
    public async Task Verify_names_the_optional_queries_only_when_they_are_enabled()
    {
        using var rig = new Rig { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(0, await rig.Run("verify", "--include-optional", "OPT1"));
        var text = rig.Out.ToString();
        Assert.Contains("OPT1", text);
        Assert.Contains("[optional]", text);
        Assert.Contains("--include-optional OPT1", text);
        Assert.Contains("planSha256: " + CapturePlan.Hash(CapturePlan.Select(["OPT1"])), text);
    }

    [Fact]
    public async Task Verify_reports_an_elevated_process_and_an_output_root_inside_git_as_not_ok()
    {
        using var elevated = new Rig(elevated: true) { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(2, await elevated.Run("verify"));
        Assert.Contains("elevated", elevated.Out.ToString());

        using var inGit = new Rig { CollectorFactoryMustNotBeCalled = true };
        Directory.CreateDirectory(Path.Combine(inGit.Root.Path, "repo", ".git"));
        Assert.Equal(2, await inGit.Run("verify", "--out-root", Path.Combine(inGit.Root.Path, "repo", "snapshots")));
        Assert.Contains("Git working tree", inGit.Out.ToString());
    }

    [Fact]
    public async Task Verify_with_a_policy_that_forbids_reads_is_not_ok()
    {
        var forbidden = new PolicyEngine(PolicyConfigLoader.Load("defaults:\n  read: forbidden\n  low: forbidden\n  medium: forbidden\n  high: forbidden\n  critical: forbidden\n"));
        using var rig = new Rig(policy: forbidden) { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(2, await rig.Run("verify"));
        Assert.Contains("NOT OK", rig.Out.ToString());
    }

    [Fact]
    public async Task Unknown_commands_and_arguments_are_refused()
    {
        using var rig = new Rig { CollectorFactoryMustNotBeCalled = true };
        Assert.Equal(2, await rig.Run("run-everything"));
        Assert.Equal(2, await rig.Run("verify", "stray"));
        Assert.Equal(2, await rig.Run("verify", "--include-optional", "Q01"));
        Assert.Equal(0, rig.CollectorFactoryCalls);
    }

    [Fact]
    public void The_production_project_wires_only_the_real_read_only_collectors()
    {
        // Construction of a collector only stores its dependencies; the machine is read by ExecuteAsync. This asserts the wiring, without running it.
        var source = new RealCollectorSource();
        Assert.Equal("system.events", source.Create("system.events").Manifest.Name);
        Assert.Equal("system.stability", source.Create("system.stability").Manifest.Name);
        Assert.Equal("system.crashes", source.Create("system.crashes").Manifest.Name);
        Assert.All(new[] { "system.events", "system.stability", "system.crashes" },
            tool => Assert.Equal(RiskLevel.Read, source.Create(tool).Manifest.Risk));
        Assert.All(new[] { "system.events", "system.stability", "system.crashes" },
            tool => Assert.Equal(ManifestFingerprint.Of(new RealManifestSource().ManifestOf(tool)), ManifestFingerprint.Of(source.Create(tool).Manifest)));
    }
}
