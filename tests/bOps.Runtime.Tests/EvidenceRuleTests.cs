// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0042 §4 and §17 rows 22, 25, 26, 34: the standing evidence rule is bounded, product- and platform-neutral, free of
/// identifier-shaped tokens, and states every clause — including the timestamp preconditions, the attributed-cause wording and
/// the absence wording. The model's own prose is a live-model concern; these tests pin the instruction it is given.
/// </summary>
public sealed partial class EvidenceRuleTests
{
    private static readonly string[] DenyList =
    [
        "windows", "linux", "ubuntu", "debian", "macos", "unix", "docker", "kubernetes", "systemd", "journald", "journalctl",
        "powershell", "bash", "wsl", "openai", "anthropic", "openrouter", "ollama", "claude", "gpt", "aspire", "dotnet", "bops",
        "sqlite", "eventlog", "wer", "bugcheck", "bsod", "minidump", "dump", "crash", "crashes", "freeze", "freezing", "frozen",
        "hang", "hung", "slow", "reboot", "cpu", "ram", "disk", "memory", "kernel", "driver",
    ];

    private static readonly string[] DenyPhrases = ["event log", "blue screen", "task manager"];

    [Fact]
    public void Rule_IsBoundedToTwoThousandCharacters_AsTheParagraphTheSystemPromptCarries()
    {
        Assert.True(EvidenceRule.Paragraph.Length <= EvidenceRule.MaxCharacters, $"The paragraph is {EvidenceRule.Paragraph.Length} characters.");
        Assert.Equal(1984, EvidenceRule.Paragraph.Length);
        Assert.True(EvidenceRule.Text.Length < EvidenceRule.Paragraph.Length);
        Assert.Equal(2000, EvidenceRule.MaxCharacters);
    }

    [Fact]
    public void Rule_UsesDeterministicLfSeparators_RegardlessOfSourceCheckoutLineEndings()
    {
        Assert.DoesNotContain('\r', EvidenceRule.Text);
        Assert.DoesNotContain('\r', EvidenceRule.Paragraph);
        Assert.Equal(7, EvidenceRule.Text.Count(character => character == '\n'));
        Assert.True(EvidenceRule.Paragraph.Length <= EvidenceRule.MaxCharacters);
    }

    [Fact]
    public void Rule_ContainsNoIdentifierShapedToken()
    {
        foreach (var raw in EvidenceRule.Paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('.', ',', ';', ':', '(', ')', '"', '\'');
            if (token.Length == 0)
            {
                continue;
            }

            Assert.False(SeparatorBetweenAlphanumerics().IsMatch(token), $"'{token}' has '.', '_' or '/' between letters or digits.");
            Assert.False(UpperCaseAfterFirstCharacter().IsMatch(token), $"'{token}' has an upper-case letter after its first character.");
            Assert.False(token.Any(char.IsLetter) && token.Any(char.IsDigit), $"'{token}' mixes letters and digits.");
        }
    }

    [Fact]
    public void Rule_NamesNoOperatingSystemProductProviderStoreSourceOrSymptom()
    {
        var text = EvidenceRule.Paragraph.ToLowerInvariant();
        foreach (var word in DenyList)
        {
            Assert.False(Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b"), $"The rule contains the deny-listed term '{word}'.");
        }

        foreach (var phrase in DenyPhrases)
        {
            Assert.DoesNotContain(phrase, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Rule_UsesOrdinaryWordsOfEvidenceReasoning_AndTheNotVisibleWording()
    {
        foreach (var word in new[] { "complete", "partial", "coverage", "truncated", "reported", "not visible", "unavailable", "hypothesis" })
        {
            Assert.Contains(word, EvidenceRule.Paragraph, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Rule_StatesTheDisclosureHeadingLiterally_InEnglish()
    {
        Assert.Equal("Evidence limitations", EvidenceRule.DisclosureHeading);
        Assert.Contains("headed exactly Evidence limitations on its own line", EvidenceRule.Text, StringComparison.Ordinal);
        Assert.Contains("may be in your language", EvidenceRule.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_ForbidsPresentingProximityCorrelationCoOccurrenceOrFrequencyAsCause()
    {
        Assert.Contains("Closeness in time, co-occurrence, frequency or similarity never proves a cause", EvidenceRule.Text, StringComparison.Ordinal);
        Assert.Contains("Relate independent sources by time to form hypotheses", EvidenceRule.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_RequiresAttributedCauseToBeReportedAsTheRecordsOwnStatement()
    {
        Assert.Contains("Never state a cause as your own conclusion", EvidenceRule.Text, StringComparison.Ordinal);
        Assert.Contains("If a record states a reason, report it as the record's statement", EvidenceRule.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_TreatsPartialUnavailableOrUnseenEvidenceAsBounded_NeverAsAbsence()
    {
        Assert.Contains("marked partial, unavailable or truncated", EvidenceRule.Text, StringComparison.Ordinal);
        Assert.Contains("its counts are minimums", EvidenceRule.Text, StringComparison.Ordinal);
        Assert.Contains("No result proves something did not happen", EvidenceRule.Text, StringComparison.Ordinal);
        Assert.Contains("means unknown, never zero", EvidenceRule.Text, StringComparison.Ordinal);
        Assert.Contains("only if the result says it is complete", EvidenceRule.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_DoesNotLetAReportedTimeBecomeAnOccurrenceTime_AndStatesTheOrderingPreconditions()
    {
        var rule = EvidenceRule.Text;
        Assert.Contains("when it was recorded or reported", rule, StringComparison.Ordinal);
        Assert.Contains("never present a reported time as when it happened", rule, StringComparison.Ordinal);
        Assert.Contains("A time with no stated kind is the time of the record, so the thing happened no later", rule, StringComparison.Ordinal);
        Assert.Contains("one comparable clock", rule, StringComparison.Ordinal);
        Assert.Contains("universal time", rule, StringComparison.Ordinal);
        Assert.Contains("no clock change or restart lies between them", rule, StringComparison.Ordinal);
        Assert.Contains("the gap exceeds the coarser precision (two seconds if unstated)", rule, StringComparison.Ordinal);
        Assert.Contains("otherwise the order is a hypothesis", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_ForbidsSummingOverlappingToolCounts_AndTreatsASingleSampleAsNoTrend()
    {
        Assert.Contains("When tools say their evidence overlaps, do not add counts", EvidenceRule.Text, StringComparison.Ordinal);
        Assert.Contains("One sample is not a trend", EvidenceRule.Text, StringComparison.Ordinal);
        Assert.Contains("weak evidence about a past or intermittent problem", EvidenceRule.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryPlanReplanAndStepCall_CarriesTheRule()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(stepCount: 1),
            EvidenceScenario.Call("test.read", "c1"),
            EvidenceScenario.Call("test.read", "c2"), // the one-step plan is exhausted here: the result triggers a replan
            PlanningTestSupport.PlanResponse(stepCount: 0),
            EvidenceScenario.Final("Done."));
        var registry = EvidenceScenario.Registry(new FakeReadTool());

        var result = await EvidenceScenario.Runner(model, registry, new RecordingAuditSink()).RunAsync("check", EvidenceScenario.Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(5, model.Requests.Count);
        Assert.All(model.Requests, request => Assert.Contains(EvidenceRule.Text, request.SystemPrompt, StringComparison.Ordinal));
        Assert.All(model.Requests, request => Assert.Contains("<<<BOPS_TOOL_OUTPUT>>>", request.SystemPrompt, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"[A-Za-z0-9][._/][A-Za-z0-9]")]
    private static partial Regex SeparatorBetweenAlphanumerics();

    [GeneratedRegex(@".[A-Z]")]
    private static partial Regex UpperCaseAfterFirstCharacter();
}
