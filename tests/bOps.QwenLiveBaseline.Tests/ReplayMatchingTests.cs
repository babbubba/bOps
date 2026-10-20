// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.QwenLiveBaseline;

/// <summary>
/// REPLAY-DEFAULT-EQUIVALENCE-01. The replay may answer a call whose optional arguments differ from the capture only when the tool itself
/// would read both calls identically (a verified default) or the capture proves the difference cannot change the records. Every test
/// that expects a match has a counterpart that expects none: the point is that semantically different calls are never paired.
/// </summary>
public sealed class ReplayMatchingTests
{
    private const string Untruncated = "{\"truncated\":false,\"observedGroups\":36,\"observedEvents\":36,\"observedItems\":36}";
    private const string Truncated = "{\"truncated\":true,\"observedGroups\":36,\"observedEvents\":36,\"observedItems\":36}";

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    private static Fixture Fx(string id, string tool, string arguments, string output = Untruncated, string? origin = null) =>
        new(id, tool, Args(arguments), new ToolCallResult(ToolOutcome.Success, output, null), origin, "task", "sha");

    private static (string? Fixture, string? Rule, string Verdict) Ask(string tool, string arguments, params Fixture[] fixtures)
    {
        var broker = new ReplayBroker(fixtures, "T02");
        var result = broker.Answer(tool, Args(arguments));
        var call = broker.Ledger.Single();
        Assert.Equal(call.Verdict == "SERVED", result.Outcome == ToolOutcome.Success);
        return (call.FixtureId, call.Rule, call.Verdict);
    }

    private static void AssertServed(string expectedFixture, string expectedRule, (string? Fixture, string? Rule, string Verdict) actual)
    {
        Assert.Equal("SERVED", actual.Verdict);
        Assert.Equal(expectedFixture, actual.Fixture);
        Assert.Equal(expectedRule, actual.Rule);
    }

    private static void AssertUnsupported((string? Fixture, string? Rule, string Verdict) actual)
    {
        Assert.Equal("UNSUPPORTED_REPLAY", actual.Verdict);
        Assert.Null(actual.Fixture);
    }

    // ---- an omitted parameter with a verified default

    [Fact]
    public void Omitted_limit_equal_to_the_verified_default_matches_system_stability()
    {
        // StabilityLimits.DefaultGroups = 50: the T02-r3 call {windowDays:1} and the F01 capture {windowDays:1, limit:50} are the same call.
        AssertServed("F01", "default-equivalent",
            Ask("system.stability", """{"windowDays":1}""", Fx("F01", "system.stability", """{"windowDays":1,"limit":50}""")));
    }

    [Fact]
    public void Omitted_mode_matches_the_default_mode_of_system_crashes()
    {
        // SystemCrashesArguments: the mode defaults to aggregate. The T02-r2 call {sinceMinutes:1440} is F06's {mode:aggregate, sinceMinutes:1440}.
        AssertServed("F06", "default-equivalent",
            Ask("system.crashes", """{"sinceMinutes":1440}""", Fx("F06", "system.crashes", """{"mode":"aggregate","sinceMinutes":1440}""")));
    }

    [Fact]
    public void Omitted_everything_matches_the_all_defaults_capture()
    {
        AssertServed("F", "default-equivalent",
            Ask("system.crashes", "{}", Fx("F", "system.crashes", """{"mode":"aggregate","sinceMinutes":1440,"limit":100}""")));
        AssertServed("E", "default-equivalent",
            Ask("system.events", "{}", Fx("E", "system.events", """{"mode":"raw","windowMinutes":60,"limit":50,"maxOutputBytes":32768}""")));
    }

    [Fact]
    public void The_default_applies_to_the_capture_as_well_as_to_the_request()
    {
        AssertServed("F10", "default-equivalent",
            Ask("system.events", """{"mode":"raw","limit":50,"minSeverity":"warning"}""", Fx("F10", "system.events", """{"limit":50,"minSeverity":"warning"}""")));
    }

    // ---- an explicit parameter equal to the default; explicit parameter different from it

    [Fact]
    public void Explicit_value_equal_to_the_default_is_the_same_call_as_the_omitted_one()
    {
        AssertServed("F01", "default-equivalent",
            Ask("system.stability", """{"windowDays":1,"limit":50}""", Fx("F01", "system.stability", """{"windowDays":1}""")));
    }

    [Fact]
    public void Literally_equal_arguments_in_another_property_order_are_exact()
    {
        AssertServed("F02", "exact",
            Ask("system.events", """{"mode":"aggregate","limit":500,"minSeverity":"warning","windowMinutes":1440}""",
                Fx("F02", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}""")));
    }

    [Fact]
    public void Explicit_value_different_from_the_default_does_not_match_the_omitted_one()
    {
        AssertUnsupported(Ask("system.crashes", """{"mode":"raw","sinceMinutes":1440}""", Fx("F06", "system.crashes", """{"sinceMinutes":1440}""")));
        AssertUnsupported(Ask("system.stability", """{"windowDays":1,"limit":51}""", Fx("F01", "system.stability", """{"windowDays":1}""", Truncated)));
        AssertUnsupported(Ask("system.events", """{"mode":"aggregate","windowMinutes":60}""", Fx("F", "system.events", """{"windowMinutes":60}""")));
        AssertUnsupported(Ask("system.events", """{"windowMinutes":60,"maxOutputBytes":16384}""", Fx("F", "system.events", """{"windowMinutes":60}""")));
    }

    [Fact]
    public void An_omitted_argument_without_a_verified_default_never_matches_an_explicit_value()
    {
        // minSeverity omitted means every severity (SystemEventsArguments.TrySeverity), not "warning": T02-r1 against F02 is a different call.
        AssertUnsupported(Ask("system.events", """{"mode":"aggregate","windowMinutes":1440}""",
            Fx("F02", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}""")));
        AssertUnsupported(Ask("system.events", """{"mode":"aggregate","windowMinutes":1440,"limit":500}""",
            Fx("F02", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}""")));
        AssertUnsupported(Ask("system.events", """{"windowMinutes":60,"minSeverity":"verbose"}""", Fx("F", "system.events", """{"windowMinutes":60}""")));
        AssertUnsupported(Ask("system.events", """{"windowMinutes":60,"source":"Kernel-Power"}""", Fx("F", "system.events", """{"windowMinutes":60}""")));
        AssertUnsupported(Ask("system.events", """{"windowMinutes":60,"excludeSources":"Foo"}""", Fx("F", "system.events", """{"windowMinutes":60}""")));
    }

    // ---- null versus an omitted property

    [Fact]
    public void Null_is_an_omitted_argument_only_for_a_tool_whose_reader_is_verified()
    {
        // ArgumentSchema skips null values and SystemEventsArguments/StabilityArguments/SystemCrashesArguments test `is null`.
        AssertServed("F01", "default-equivalent",
            Ask("system.stability", """{"windowDays":1,"limit":null}""", Fx("F01", "system.stability", """{"windowDays":1,"limit":50}""")));
        AssertServed("F", "default-equivalent",
            Ask("system.events", """{"windowMinutes":60,"minSeverity":null}""", Fx("F", "system.events", """{"windowMinutes":60}""")));

        // A tool with no verified spec gets no such reading.
        AssertUnsupported(Ask("fs.list", """{"path":"C:\\x","pattern":null}""", Fx("F", "fs.list", """{"path":"C:\\x"}""")));
        AssertUnsupported(Ask("fs.list", """{"path":"C:\\x"}""", Fx("F", "fs.list", """{"path":"C:\\x","pattern":null}""")));
        AssertServed("F", "exact", Ask("fs.list", """{"path":"C:\\x","pattern":null}""", Fx("F", "fs.list", """{"path":"C:\\x","pattern":null}""")));
    }

    [Fact]
    public void Null_is_not_a_default_nor_an_explicit_non_default_value()
    {
        AssertUnsupported(Ask("system.events", """{"windowMinutes":60,"minSeverity":null}""", Fx("F", "system.events", """{"windowMinutes":60,"minSeverity":"warning"}""")));
        AssertUnsupported(Ask("system.stability", """{"windowDays":1,"limit":null}""", Fx("F01", "system.stability", """{"windowDays":1,"limit":10}""", Truncated)));
        AssertUnsupported(Ask("system.stability", """{"windowDays":null}""", Fx("F01", "system.stability", """{"windowDays":1}""")));
    }

    // ---- different limits

    [Fact]
    public void A_larger_limit_matches_only_an_untruncated_capture()
    {
        var capture = """{"windowMinutes":1440,"limit":50,"mode":"aggregate"}""";
        AssertServed("F", "limit-covered", Ask("system.events", """{"windowMinutes":1440,"limit":500,"mode":"aggregate"}""", Fx("F", "system.events", capture, Untruncated)));
        AssertUnsupported(Ask("system.events", """{"windowMinutes":1440,"limit":500,"mode":"aggregate"}""", Fx("F", "system.events", capture, Truncated)));
        // The default is the larger limit: 100 (crashes) against a capture taken with 10.
        AssertServed("C", "limit-covered", Ask("system.crashes", """{"sinceMinutes":1440}""", Fx("C", "system.crashes", """{"limit":10,"sinceMinutes":1440}""", Untruncated)));
        AssertUnsupported(Ask("system.crashes", """{"sinceMinutes":1440}""", Fx("C", "system.crashes", """{"limit":10,"sinceMinutes":1440}""", Truncated)));
    }

    [Fact]
    public void A_smaller_limit_matches_only_when_the_capture_proves_it_cuts_nothing()
    {
        // T02-r3: the default limit (50) against F02's 500, whose complete capture observed 36 groups.
        var f02 = Fx("F02", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}""", Untruncated);
        AssertServed("F02", "limit-not-binding", Ask("system.events", """{"minSeverity":"warning","mode":"aggregate","windowMinutes":1440}""", f02));
        AssertServed("F02", "limit-not-binding", Ask("system.events", """{"minSeverity":"warning","mode":"aggregate","windowMinutes":1440,"limit":36}""", f02));
        AssertUnsupported(Ask("system.events", """{"minSeverity":"warning","mode":"aggregate","windowMinutes":1440,"limit":35}""", f02));
        AssertUnsupported(Ask("system.events", """{"minSeverity":"warning","mode":"aggregate","windowMinutes":1440,"limit":10}""", f02));

        // A truncated capture proves nothing about a smaller limit.
        AssertUnsupported(Ask("system.events", """{"minSeverity":"warning","mode":"aggregate","windowMinutes":1440}""",
            Fx("F02", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}""", Truncated)));

        // The count compared is the mode's: raw counts observedEvents, aggregate counts observedGroups.
        var raw = Fx("R", "system.events", """{"windowMinutes":60,"limit":500,"mode":"raw"}""", """{"truncated":false,"observedEvents":80,"observedGroups":3}""");
        AssertUnsupported(Ask("system.events", """{"windowMinutes":60,"mode":"raw"}""", raw));
        var aggregate = Fx("A", "system.events", """{"windowMinutes":60,"limit":500,"mode":"aggregate"}""", """{"truncated":false,"observedEvents":80,"observedGroups":3}""");
        AssertServed("A", "limit-not-binding", Ask("system.events", """{"windowMinutes":60,"mode":"aggregate"}""", aggregate));
    }

    [Fact]
    public void A_different_limit_never_hides_a_difference_in_another_argument()
    {
        var f02 = Fx("F02", "system.events", """{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}""", Untruncated);
        AssertUnsupported(Ask("system.events", """{"minSeverity":"error","mode":"aggregate","windowMinutes":1440}""", f02));
        AssertUnsupported(Ask("system.events", """{"minSeverity":"warning","mode":"raw","windowMinutes":1440,"limit":500}""", f02));
        AssertUnsupported(Ask("system.events", """{"minSeverity":"warning","mode":"aggregate","windowMinutes":1441,"limit":600}""", f02));
    }

    [Fact]
    public void A_tool_without_a_verified_spec_gets_the_literal_limit_covered_rule_only()
    {
        AssertServed("L", "limit-covered", Ask("fs.list", """{"path":"C:\\x","limit":100}""", Fx("L", "fs.list", """{"path":"C:\\x","limit":10}""")));
        AssertUnsupported(Ask("fs.list", """{"path":"C:\\x","limit":5}""", Fx("L", "fs.list", """{"path":"C:\\x","limit":10}""")));
        AssertUnsupported(Ask("fs.list", """{"path":"C:\\x"}""", Fx("L", "fs.list", """{"path":"C:\\x","limit":10}""")));
    }

    // ---- different time windows

    [Fact]
    public void Different_windows_never_match()
    {
        var capture = Fx("F", "system.stability", """{"windowDays":1,"limit":50}""");
        AssertUnsupported(Ask("system.stability", """{"windowDays":2,"limit":50}""", capture));    // T02-r2
        AssertUnsupported(Ask("system.stability", """{"limit":50}""", capture));                       // the default window is 30 days
        AssertServed("F", "default-equivalent", Ask("system.stability", """{"windowDays":1}""", capture));

        var events = Fx("E", "system.events", """{"windowMinutes":1440,"mode":"aggregate"}""");
        AssertUnsupported(Ask("system.events", """{"windowMinutes":1439,"mode":"aggregate"}""", events));
        AssertUnsupported(Ask("system.events", """{"mode":"aggregate"}""", events));                   // default 60 minutes
        AssertUnsupported(Ask("system.events", """{"windowMinutes":1440}""", events));                 // raw, not aggregate
        AssertUnsupported(Ask("system.events", """{"windowMinutes":1440,"mode":"raw"}""", events));    // T02-r2 shape
        AssertUnsupported(Ask("system.crashes", """{"sinceMinutes":1439}""", Fx("C", "system.crashes", """{"sinceMinutes":1440}""")));
        AssertUnsupported(Ask("system.crashes", """{"sinceDays":1}""", Fx("C", "system.crashes", """{"sinceMinutes":1440}""")));
    }

    [Fact]
    public void A_default_window_is_not_applied_next_to_an_explicit_day_window()
    {
        // windowMinutes defaults to 60 only when windowDays is absent: {windowDays:7, aggregate} is not {windowMinutes:60, aggregate}.
        AssertUnsupported(Ask("system.events", """{"windowDays":7,"mode":"aggregate"}""", Fx("E", "system.events", """{"mode":"aggregate"}""")));
        AssertUnsupported(Ask("system.events", """{"mode":"aggregate"}""", Fx("E", "system.events", """{"windowDays":7,"mode":"aggregate"}""")));
        AssertServed("E", "default-equivalent", Ask("system.events", """{"windowDays":7,"mode":"aggregate","limit":50}""", Fx("E", "system.events", """{"windowDays":7,"mode":"aggregate"}""")));
        // No alias is inferred between the minute and the day argument, even though 1 day is 1,440 minutes.
        AssertUnsupported(Ask("system.events", """{"windowDays":1,"mode":"aggregate"}""", Fx("E", "system.events", """{"windowMinutes":1440,"mode":"aggregate"}""")));
    }

    // ---- enums

    [Fact]
    public void A_valid_enum_value_matches_only_itself()
    {
        AssertServed("E", "exact", Ask("system.events", """{"windowMinutes":60,"mode":"aggregate","minSeverity":"error"}""",
            Fx("E", "system.events", """{"windowMinutes":60,"mode":"aggregate","minSeverity":"error"}""")));
        foreach (var severity in new[] { "critical", "warning", "information", "verbose" })
        {
            AssertUnsupported(Ask("system.events", $$"""{"windowMinutes":60,"mode":"aggregate","minSeverity":"{{severity}}"}""",
                Fx("E", "system.events", """{"windowMinutes":60,"mode":"aggregate","minSeverity":"error"}""")));
        }
    }

    [Theory]
    [InlineData("Aggregate")]
    [InlineData("AGGREGATE")]
    [InlineData("agg")]
    [InlineData("")]
    [InlineData("bogus")]
    public void An_invalid_enum_value_never_matches_and_never_receives_the_default(string mode)
    {
        var request = new JsonObject { ["windowMinutes"] = 60, ["mode"] = mode };
        foreach (var capture in new[] { Fx("R", "system.events", """{"windowMinutes":60,"mode":"raw"}"""), Fx("A", "system.events", """{"windowMinutes":60,"mode":"aggregate"}"""), Fx("D", "system.events", """{"windowMinutes":60}""") })
        {
            var broker = new ReplayBroker([capture], "T02");
            broker.Answer("system.events", request);
            Assert.Equal("UNSUPPORTED_REPLAY", broker.Ledger.Single().Verdict);
        }
    }

    [Fact]
    public void Severity_with_another_case_is_another_value()
    {
        AssertUnsupported(Ask("system.events", """{"windowMinutes":60,"minSeverity":"Warning"}""", Fx("E", "system.events", """{"windowMinutes":60,"minSeverity":"warning"}""")));
    }

    // ---- types

    [Fact]
    public void Types_are_preserved()
    {
        AssertUnsupported(Ask("system.stability", """{"windowDays":"1"}""", Fx("F01", "system.stability", """{"windowDays":1}""")));
        AssertUnsupported(Ask("system.events", """{"windowMinutes":60,"eventId":41}""", Fx("E", "system.events", """{"windowMinutes":60,"eventId":"41"}""")));
        AssertServed("E", "exact", Ask("system.events", """{"windowMinutes":60,"eventId":"41"}""", Fx("E", "system.events", """{"windowMinutes":60,"eventId":"41"}""")));
        AssertUnsupported(Ask("system.stability", """{"limit":true}""", Fx("F01", "system.stability", """{"limit":1}""")));
    }

    // ---- several candidates, an uncovered tool

    [Fact]
    public void Several_derived_candidates_are_never_chosen_between()
    {
        var broker = new ReplayBroker(
            [
                Fx("A", "system.stability", """{"windowDays":1,"limit":50}""", origin: "T02"),
                Fx("B", "system.stability", """{"windowDays":1,"limit":50}""", origin: null),
            ], "T02");
        var result = broker.Answer("system.stability", Args("""{"windowDays":1}"""));
        var call = broker.Ledger.Single();
        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Equal("UNSUPPORTED_REPLAY", call.Verdict);
        Assert.Equal("ambiguous", call.Rule);
        Assert.Null(call.FixtureId);
        Assert.Equal(["A", "B"], call.OtherCandidates);
        Assert.Contains("does not choose between them", result.ErrorMessage);
    }

    [Fact]
    public void Candidates_of_different_derived_rules_are_still_ambiguous()
    {
        var broker = new ReplayBroker(
            [
                Fx("A", "system.stability", """{"windowDays":1,"limit":50}"""),
                Fx("B", "system.stability", """{"windowDays":1,"limit":10}""", Untruncated),
            ], "T02");
        broker.Answer("system.stability", Args("""{"windowDays":1}"""));
        Assert.Equal("ambiguous", broker.Ledger.Single().Rule);
    }

    [Fact]
    public void A_literal_exact_capture_beats_derived_ones_and_lists_them()
    {
        var broker = new ReplayBroker(
            [
                Fx("A", "system.stability", """{"windowDays":1,"limit":50}"""),
                Fx("B", "system.stability", """{"windowDays":1}"""),
            ], "T02");
        broker.Answer("system.stability", Args("""{"windowDays":1}"""));
        var call = broker.Ledger.Single();
        Assert.Equal("SERVED", call.Verdict);
        Assert.Equal("B", call.FixtureId);
        Assert.Equal("exact", call.Rule);
        Assert.Equal(["A"], call.OtherCandidates);
    }

    [Fact]
    public void Literal_duplicates_keep_the_legacy_order_scenario_first_then_lowest_id()
    {
        var broker = new ReplayBroker(
            [
                Fx("F04", "system.info", "{}", origin: "T02"),
                Fx("F07", "system.info", "{}", origin: null),
                Fx("F14", "system.info", "{}", origin: "T08"),
            ], "T08");
        broker.Answer("system.info", Args("{}"));
        var call = broker.Ledger.Single();
        Assert.Equal("F14", call.FixtureId);
        Assert.Equal("exact", call.Rule);
        Assert.Equal(["F04", "F07"], call.OtherCandidates);
    }

    [Fact]
    public void A_tool_with_no_capture_is_unsupported_and_never_borrows_another_tools_fixture()
    {
        AssertUnsupported(Ask("system.stability", """{"windowDays":1}""", Fx("F", "system.crashes", """{"windowDays":1}""")));
        AssertUnsupported(Ask("system.stability", """{"windowDays":1}"""));
        AssertUnsupported(Ask("system.events", """{"windowMinutes":60}""", Fx("F", "system.events.other", """{"windowMinutes":60}""")));
    }

    [Fact]
    public void Tool_names_are_compared_ordinally()
    {
        AssertUnsupported(Ask("System.Stability", """{"windowDays":1}""", Fx("F", "system.stability", """{"windowDays":1}""")));
    }

    [Fact]
    public void An_unsupported_call_is_a_failed_tool_result_that_says_so()
    {
        var broker = new ReplayBroker([], "T02");
        var result = broker.Answer("system.events", Args("""{"windowMinutes":60}"""));
        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Equal(ToolFailureKind.Environment, result.FailureKind);
        Assert.Equal(ToolResultCompleteness.Unavailable, result.Completeness);
        Assert.StartsWith("UNSUPPORTED_REPLAY:", result.ErrorMessage);
    }

    // ---- a served result is the capture, byte for byte

    [Fact]
    public void A_default_equivalent_match_returns_the_capture_unchanged()
    {
        var capture = Fx("F01", "system.stability", """{"windowDays":1,"limit":50}""", """{"truncated":false,"complete":false,"observedGroups":7,"returnedGroups":7}""");
        var broker = new ReplayBroker([capture], "T02");
        var result = broker.Answer("system.stability", Args("""{"windowDays":1}"""));
        Assert.Same(capture.Result, result);
        Assert.Contains("\"complete\":false", result.Output);
    }

    // ---- the specs name only what was verified

    [Fact]
    public void Every_spec_default_is_a_declared_optional_parameter_of_a_system_tool_with_the_documented_value()
    {
        var expected = new Dictionary<string, string[]>
        {
            ["system.events"] = ["mode=raw", "windowMinutes=60", "limit=50", "maxOutputBytes=32768"],
            ["system.stability"] = ["windowDays=30", "limit=50"],
            ["system.crashes"] = ["mode=aggregate", "sinceMinutes=1440", "limit=100"],
        };
        Assert.Equal([.. expected.Keys.Order()], ToolArgumentSpecs.All.Select(s => s.Tool).Order().ToArray());
        foreach (var spec in ToolArgumentSpecs.All)
        {
            Assert.True(spec.NullIsAbsent);
            Assert.Equal(expected[spec.Tool], spec.Defaults.Select(d => $"{d.Name}={d.Value.ToJsonString().Trim('"')}").ToArray());
            Assert.DoesNotContain(spec.Defaults, d => d.Name == "minSeverity");
        }
    }
}
