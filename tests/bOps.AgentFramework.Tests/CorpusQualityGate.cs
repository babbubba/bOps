// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.AgentFramework.Tests;

/// <summary>The verdict of <see cref="CorpusQualityGate"/>: closed failure codes and counts only, never event content.</summary>
internal sealed record CorpusQuality(bool Suitable, IReadOnlyList<string> Failures, int Events, int UsableEvents, IReadOnlyList<string> Channels)
{
    internal string Describe() => Suitable ? "suitable" : string.Join(',', Failures);
}

/// <summary>
/// Fitness check of a frozen <c>system.events</c> corpus before the operator may approve it (KNOW-BENCH corpus provenance, D3).
/// It only reads: it never synthesizes, repairs or replaces an event, and an insufficient corpus is reported unsuitable, not adapted.
/// </summary>
internal static class CorpusQualityGate
{
    internal const string IntegrityMismatch = "IntegrityMismatch";
    internal const string UnsupportedSchema = "UnsupportedSchema";
    internal const string MinimizationMismatch = "MinimizationMismatch";
    internal const string NotNativeCapture = "NotNativeCapture";
    internal const string ParametersMismatch = "ParametersMismatch";
    internal const string CaptureIncomplete = "CaptureIncomplete";
    internal const string Truncated = "Truncated";
    internal const string CoverageIncomplete = "CoverageIncomplete";
    internal const string SourcesInconsistent = "SourcesInconsistent";
    internal const string NoUsableEvents = "NoUsableEvents";
    internal const string MinimizationViolation = "MinimizationViolation";

    private static readonly string[] DiagnosticSeverities = ["critical", "error", "warning"];

    /// <summary>Evaluates <paramref name="corpus"/> against its pinned <paramref name="hash"/> and the scenario's <paramref name="expected"/> request.</summary>
    internal static CorpusQuality Evaluate(JsonObject corpus, string hash, ToolArguments expected)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(expected);
        var failures = new List<string>();
        var events = 0;
        var usable = 0;
        var channels = new List<string>();
        try
        {
            if (CanaryEventsCorpus.Hash(corpus) != hash) failures.Add(IntegrityMismatch);
            if (corpus["format"]?.GetValue<string>() != "bops-system-events-corpus-v1" || corpus["tool"]?.GetValue<string>() != "system.events"
                || corpus["schemaVersion"]?.GetValue<int>() != SystemEventFormatting.SchemaVersion) failures.Add(UnsupportedSchema);
            if (corpus["minimization"]?.GetValue<string>() != CorpusMinimizer.Version) failures.Add(MinimizationMismatch);
            if (corpus["origin"]?.GetValue<string>() != "windows-capture" || corpus["capture"] is not JsonObject) failures.Add(NotNativeCapture);
            if (corpus["arguments"]?.GetValue<string>() != CanaryEventsCorpus.Canonical(expected.ToJson())
                || corpus["mode"]?.GetValue<int>() != (int)EvidenceMode.Raw) failures.Add(ParametersMismatch);
            var output = JsonNode.Parse(corpus["output"]!.GetValue<string>())!.AsObject();
            if (corpus["complete"]?.GetValue<bool>() != true || corpus["status"]?.GetValue<string>() != "complete"
                || corpus["completeness"]?.GetValue<int>() != (int)ToolResultCompleteness.Complete
                || output["complete"]?.GetValue<bool>() != true || output["status"]?.GetValue<string>() != "complete") failures.Add(CaptureIncomplete);
            if (corpus["truncated"]?.GetValue<bool>() != false || output["truncated"]?.GetValue<bool>() != false) failures.Add(Truncated);

            var required = RequiredChannels(expected);
            channels.AddRange(required);
            if (!CoverageComplete(output, required)) failures.Add(CoverageIncomplete);
            if (!SourcesComplete(output, required)) failures.Add(SourcesInconsistent);

            var window = output["window"]!.AsObject();
            var from = ParseUtc(window["fromUtc"]!.GetValue<string>());
            var to = ParseUtc(window["toUtc"]!.GetValue<string>());
            var records = output["events"]?.AsArray() ?? [];
            events = records.Count;
            foreach (var record in records.OfType<JsonObject>())
            {
                if (!ConformsToMinimization(record)) failures.Add(MinimizationViolation);
                else if (IsUsable(record, from, to, required)) usable++;
            }

            if (output["groups"] is not null) failures.Add(ParametersMismatch);
            if (usable == 0) failures.Add(NoUsableEvents);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or FormatException or JsonException
                                       or InvalidCastException or KeyNotFoundException or ArgumentException)
        {
            failures.Add(UnsupportedSchema);
        }

        return new CorpusQuality(failures.Count == 0, [.. failures.Distinct(StringComparer.Ordinal)], events, usable, channels);
    }

    /// <summary>Throws with the closed failure codes when the corpus is not suitable.</summary>
    internal static CorpusQuality Require(CorpusQuality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        if (!quality.Suitable) throw new InvalidOperationException("Corpus quality gate failed: " + quality.Describe());
        return quality;
    }

    private static string[] RequiredChannels(ToolArguments expected) =>
        expected.ToJson()["channel"]?.GetValue<string>() is { } channel ? [channel] : ["Application", "System"];

    private static bool CoverageComplete(JsonObject output, string[] required)
    {
        var coverage = output["coverage"]?.AsObject();
        if (coverage?["state"]?.GetValue<string>() != TemporalCoverage.Complete) return false;
        var stores = coverage["stores"]?.AsArray() ?? [];
        return required.All(channel => stores.OfType<JsonObject>().Any(store =>
            store["name"]?.GetValue<string>() == "windows.channel." + channel
            && store["state"]?.GetValue<string>() == TemporalCoverage.Complete));
    }

    // Every listed source must be available (a partial or unavailable channel contradicts a complete capture); the required ones must be there.
    private static bool SourcesComplete(JsonObject output, string[] required)
    {
        var sources = (output["sources"]?.AsArray() ?? []).OfType<JsonObject>().ToArray();
        return sources.Length > 0
            && sources.All(source => source["status"]?.GetValue<string>() is "available" or "notApplicable")
            && required.All(channel => sources.Any(source =>
                source["name"]?.GetValue<string>() == "windows.channel." + channel && source["status"]?.GetValue<string>() == "available"));
    }

    // A record the minimizer could not have produced: any retained free text, process data or non-allowlisted provider.
    private static bool ConformsToMinimization(JsonObject record)
    {
        foreach (var key in new[] { "message", "sampleMessage", "unit", "processId", "processName", "detail" })
        {
            if (record[key] is not null) return false;
        }

        var provider = record["source"]?.GetValue<string>();
        return provider is null || CorpusMinimizer.IsRetainedProvider(provider) || CorpusMinimizer.IsRedactedProvider(provider);
    }

    // Usable: an allowlisted provider, a numeric Event ID, a diagnostic severity, a required channel, and a time inside the window.
    private static bool IsUsable(JsonObject record, DateTimeOffset from, DateTimeOffset to, string[] required)
    {
        var provider = record["source"]?.GetValue<string>();
        var eventId = record["eventId"]?.GetValue<string>();
        var channel = record["channel"]?.GetValue<string>();
        var severity = record["severity"]?.GetValue<string>();
        if (!CorpusMinimizer.IsRetainedProvider(provider) || string.IsNullOrEmpty(eventId) || !eventId.All(char.IsAsciiDigit)
            || channel is null || !required.Contains(channel, StringComparer.Ordinal)
            || severity is null || !DiagnosticSeverities.Contains(severity, StringComparer.Ordinal)) return false;
        return DateTimeOffset.TryParse(record["timestampUtc"]?.GetValue<string>(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) && time >= from && time <= to;
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
