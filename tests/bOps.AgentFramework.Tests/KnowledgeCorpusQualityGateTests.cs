// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.AgentFramework.Tests;

/// <summary>Offline checks of the corpus quality gate (D3): an insufficient corpus is reported unsuitable, never repaired or replaced.</summary>
public sealed class KnowledgeCorpusQualityGateTests
{
    private static readonly string[] RequiredChannels = ["Application", "System"];
    private static readonly string[] NoUsable = [CorpusQualityGate.NoUsableEvents];
    private static readonly string[] ParametersOnly = [CorpusQualityGate.ParametersMismatch];

    private static CorpusQuality Judge(string output) => CorpusFixtures.Captured(output).Quality;

    [Fact]
    public void AGoodNativeShapedCapture_IsSuitable_AndReportsItsFigures()
    {
        var (corpus, hash, quality) = CorpusFixtures.Captured();

        Assert.True(quality.Suitable, quality.Describe());
        Assert.Empty(quality.Failures);
        Assert.Equal(9, quality.Events);
        Assert.Equal(6, quality.UsableEvents); // The three unknown-provider events are retained but not diagnostic.
        Assert.Equal(RequiredChannels, quality.Channels);
        var before = CanaryEventsCorpus.Canonical(corpus);
        Assert.True(CorpusQualityGate.Evaluate(corpus, hash, KnowledgeBenchmark.Arguments).Suitable);
        Assert.Equal(before, CanaryEventsCorpus.Canonical(corpus)); // The gate reads; it never repairs.
        Assert.Equal(quality, CorpusQualityGate.Require(quality));
    }

    [Fact]
    public void AnEmptyCapture_IsNotSuitable_AndNothingIsSynthesized()
    {
        var (corpus, _, quality) = CorpusFixtures.Captured(CorpusFixtures.Output([]));

        Assert.False(quality.Suitable);
        Assert.Equal(NoUsable, quality.Failures);
        Assert.Empty(JsonNode.Parse(corpus["output"]!.GetValue<string>())!["events"]!.AsArray());
        var refusal = Assert.Throws<InvalidOperationException>(() => CorpusQualityGate.Require(quality));
        Assert.Equal("Corpus quality gate failed: NoUsableEvents", refusal.Message);
    }

    [Fact]
    public void OnlyEventsFromNonDiagnosticProviders_AreNotUsable()
    {
        var events = new[] { CorpusFixtures.Record(5, SystemEventSeverity.Error, "Contoso-Internal-Agent", "7", "System", "x") };

        Assert.Equal(NoUsable, Judge(CorpusFixtures.Output(events)).Failures);
    }

    [Fact]
    public void ATruncatedCapture_IsNotSuitable()
    {
        var quality = Judge(CorpusFixtures.Output(CorpusFixtures.DiagnosticEvents(), truncated: true));

        Assert.False(quality.Suitable);
        Assert.Contains(CorpusQualityGate.Truncated, quality.Failures);
        Assert.Contains(CorpusQualityGate.CaptureIncomplete, quality.Failures);
    }

    [Fact]
    public void APartialOrUnavailableSource_IsNotSuitable()
    {
        foreach (var status in new[] { InventorySourceStatus.Partial, InventorySourceStatus.Unavailable })
        {
            var quality = Judge(CorpusFixtures.Output(CorpusFixtures.DiagnosticEvents(), application: status));

            Assert.False(quality.Suitable);
            Assert.Contains(CorpusQualityGate.SourcesInconsistent, quality.Failures);
            Assert.Contains(CorpusQualityGate.CaptureIncomplete, quality.Failures);
        }
    }

    [Fact]
    public void MissingApplicationCoverage_IsNotSuitable()
    {
        var quality = Judge(CorpusFixtures.Output(CorpusFixtures.DiagnosticEvents(), includeApplication: false));

        Assert.Contains(CorpusQualityGate.SourcesInconsistent, quality.Failures);
        Assert.Contains(CorpusQualityGate.CoverageIncomplete, quality.Failures);
    }

    [Fact]
    public void HistoryThatStartsInsideTheWindow_IsNotSuitable()
    {
        var quality = Judge(CorpusFixtures.Output(CorpusFixtures.DiagnosticEvents(), deepHistory: false));

        Assert.False(quality.Suitable);
        Assert.Contains(CorpusQualityGate.CoverageIncomplete, quality.Failures);
    }

    [Fact]
    public void ParametersThatDifferFromTheScenario_AreNotSuitable()
    {
        var other = ToolArguments.FromJson(new JsonObject
            { ["mode"] = "raw", ["windowMinutes"] = 1440, ["minSeverity"] = "warning", ["limit"] = 100, ["maxOutputBytes"] = 65536 });
        var (corpus, hash, ownRequest) = CorpusFixtures.Captured(CorpusFixtures.Output(CorpusFixtures.DiagnosticEvents(), arguments: other), other);

        Assert.True(ownRequest.Suitable, ownRequest.Describe()); // Judged against its own request it is fine...
        Assert.Equal(ParametersOnly,
            CorpusQualityGate.Evaluate(corpus, hash, KnowledgeBenchmark.Arguments).Failures); // ...but it is not the scenario's.
    }

    [Fact]
    public void AnAlteredOrUnsupportedOrControlledCorpus_IsNotSuitable()
    {
        var (corpus, hash, _) = CorpusFixtures.Captured();

        Assert.Contains(CorpusQualityGate.IntegrityMismatch, CorpusQualityGate.Evaluate(corpus, new string('0', 64), KnowledgeBenchmark.Arguments).Failures);

        var unsupported = (JsonObject)corpus.DeepClone();
        unsupported["format"] = "bops-system-events-corpus-v0";
        Assert.Contains(CorpusQualityGate.UnsupportedSchema, CorpusQualityGate.Evaluate(unsupported, CanaryEventsCorpus.Hash(unsupported), KnowledgeBenchmark.Arguments).Failures);

        var oldMinimization = (JsonObject)corpus.DeepClone();
        oldMinimization["minimization"] = "remove-free-text-provider-unit-process-and-source-details-v1";
        Assert.Contains(CorpusQualityGate.MinimizationMismatch, CorpusQualityGate.Evaluate(oldMinimization, CanaryEventsCorpus.Hash(oldMinimization), KnowledgeBenchmark.Arguments).Failures);

        var synthetic = CanaryEventsCorpus.Build(KnowledgeBenchmark.Arguments, CorpusFixtures.Output(CorpusFixtures.DiagnosticEvents()), "synthetic", CorpusFixtures.Now);
        Assert.Contains(CorpusQualityGate.NotNativeCapture, CorpusQualityGate.Evaluate(synthetic, CanaryEventsCorpus.Hash(synthetic), KnowledgeBenchmark.Arguments).Failures);

        Assert.Equal(hash, CanaryEventsCorpus.Hash(corpus)); // None of the above touched the original.
    }

    [Fact]
    public void ARecordThatRetainsFreeText_IsAMinimizationViolation()
    {
        var (corpus, _, _) = CorpusFixtures.Captured();
        var leaked = (JsonObject)corpus.DeepClone();
        var output = JsonNode.Parse(leaked["output"]!.GetValue<string>())!.AsObject();
        output["events"]![0]!["message"] = "C:\\Users\\jdoe";
        leaked["output"] = CanaryEventsCorpus.Canonical(output);

        var quality = CorpusQualityGate.Evaluate(leaked, CanaryEventsCorpus.Hash(leaked), KnowledgeBenchmark.Arguments);

        Assert.False(quality.Suitable);
        Assert.Contains(CorpusQualityGate.MinimizationViolation, quality.Failures);
    }

    [Fact]
    public void ACorpusWithoutAnyShape_FailsClosedWithoutThrowing()
    {
        var quality = CorpusQualityGate.Evaluate(new JsonObject(), new string('a', 64), KnowledgeBenchmark.Arguments);

        Assert.False(quality.Suitable);
        Assert.Contains(CorpusQualityGate.UnsupportedSchema, quality.Failures);
    }
}
