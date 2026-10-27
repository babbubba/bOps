// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using Xunit.Abstractions;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// BOPS-0128 offline replay. The tool results a past benchmark campaign captured (private, never in the repository) are turned back
/// into the snapshots their collectors produced, and the current tools are run on those snapshots: nothing is read from a host, no
/// model is called, and the archive is only read. The test does nothing unless <c>BOPS_REPLAY_CAMPAIGN</c> names a campaign directory
/// (one with a <c>runs</c> folder of captured task states), so it never needs the private data to be present.
/// </summary>
public sealed class Bops0128ReplayTests(ITestOutputHelper output)
{
    private const string CampaignVariable = "BOPS_REPLAY_CAMPAIGN";

    [Fact]
    public async Task ReplayOfCapturedCrashResults_ListsEveryKernelCode_AndEveryGroup()
    {
        var replayed = 0;
        foreach (var run in CapturedRuns())
        {
            var step = ToolStep(run, "system.crashes");
            if (step is null)
            {
                continue;
            }

            var captured = JsonNode.Parse(step.Output)!.AsObject();
            if (captured["groups"] is not JsonArray { Count: > 0 } capturedGroups || capturedGroups[0]!["uncorrelatedCount"] is null
                || captured["returnedGroups"]?.GetValue<int>() < captured["observedGroups"]?.GetValue<int>())
            {
                // An older schema, a raw result or a cut one: the collector's snapshot cannot be rebuilt from it.
                output.WriteLine($"{run.Name} crashes: not replayable (schema {captured["schemaVersion"]}, mode {captured["mode"]}).");
                continue;
            }

            replayed++;
            var now = DateTimeOffset.Parse(captured["window"]!["toUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var snapshot = RebuildCrashes(captured);

            var tool = new CrashesTool(snapshot, now);
            var result = await tool.ExecuteAsync(ToolArguments.FromJson(step.Arguments));

            var replay = JsonNode.Parse(result.Output!)!.AsObject();
            // Fidelity first: the rebuilt snapshot must give back the captured groups, or nothing below says anything about the capture.
            Assert.Equal(captured["observedItems"]!.GetValue<int>(), replay["observedItems"]!.GetValue<int>());
            Assert.Equal(GroupKeys(captured["groups"]!.AsArray()), GroupKeys(replay["groups"]!.AsArray()));

            // Slice A: every kernel code of the capture is in the typed facts, with its count.
            var expected = captured["groups"]!.AsArray()
                .Where(group => group!["kind"]!.GetValue<string>() is CrashKinds.KernelBugcheck or CrashKinds.KernelLiveDump)
                .GroupBy(group => group!["kind"]!.GetValue<string>())
                .ToDictionary(kind => kind.Key, kind => kind.ToDictionary(group => group!["code"]!.GetValue<string>(), group => group!["count"]!.GetValue<int>()));
            foreach (var (kind, codes) in expected)
            {
                var fact = Assert.Single(result.Facts, f => f.Type == SystemCrashFacts.KernelCodesType && f.Key == kind);
                var listed = fact.Value.GetValue<string>().Split(',').Select(entry => entry.Split(':')).ToDictionary(parts => parts[0], parts => int.Parse(parts[1], CultureInfo.InvariantCulture));
                Assert.Equal(codes.OrderBy(pair => pair.Key, StringComparer.Ordinal), listed.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            }

            // The compact summary lists every group (kernel groups first) and says what, if anything, is omitted.
            var summary = replay["groupSummary"]!.AsObject();
            output.WriteLine(
                $"{run.Name} crashes: output {result.Output!.Length} chars (captured {step.Output.Length}); facts {result.Facts.Count} (captured {step.Facts}); " +
                $"kernel codes {string.Join(" | ", result.Facts.Where(f => f.Type == SystemCrashFacts.KernelCodesType).Select(f => f.Key + "=" + f.Value))}; " +
                $"summary observed {summary["observedEntries"]} listed {summary["listedEntries"]} omitted {summary["omittedEntries"]} " +
                $"({Encoding.UTF8.GetByteCount(summary.ToJsonString())} bytes, summary starts at char {result.Output.IndexOf("\"groupSummary\"", StringComparison.Ordinal)} and groups at char {result.Output.IndexOf("\"groups\"", StringComparison.Ordinal)}); " +
                $"selection and coverage facts {string.Join(", ", result.Facts.Where(f => f.Type is SystemEvidenceMetadata.CrashesSelectionType or SystemEvidenceMetadata.CrashesCoverageType).Select(f => f.Key + "=" + f.Value))}; captured coverage state {captured["coverage"]!["state"]}");
            // Visibility after compaction (the model of the R01 capture saw about the first 6 500 characters): how many groups are named in the head, before and after.
            var identities = captured["groups"]!.AsArray();
            output.WriteLine(
                $"{run.Name} crashes visibility: groups named in the first {VisibilityHead} chars {VisibleGroups(step.Output, identities)}/{identities.Count} before, {VisibleGroups(result.Output, identities)}/{identities.Count} after; " +
                $"kernel codes named {VisibleGroups(step.Output, KernelOnly(identities))}/{KernelOnly(identities).Count} before, {VisibleGroups(result.Output, KernelOnly(identities))}/{KernelOnly(identities).Count} after");
            Assert.Equal(summary["observedEntries"]!.GetValue<int>(), summary["listedEntries"]!.GetValue<int>() + summary["omittedEntries"]!.GetValue<int>());
            Assert.True(result.Output.IndexOf("\"groupSummary\"", StringComparison.Ordinal) < result.Output.IndexOf("\"groups\"", StringComparison.Ordinal));
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(result.Facts).Length <= EvidenceFactBounds.MaximumSerializedBytes);
            Assert.True(result.Facts.Count <= EvidenceFactBounds.MaximumFacts);

            // The kernel groups lead the summary: the rare codes are the ones a count ranking would push out.
            var kernelGroups = expected.Values.Sum(codes => codes.Count);
            var leading = summary["entries"]!.AsArray().Take(kernelGroups).Select(entry => entry!["kind"]!.GetValue<string>());
            Assert.All(leading, kind => Assert.True(kind is CrashKinds.KernelBugcheck or CrashKinds.KernelLiveDump, kind));
        }

        AssertSomethingWasReplayed(replayed, "system.crashes");
    }

    [Fact]
    public void ReplayOfCapturedEventResults_SummarizesEveryGroup_StatesTheSelection_AndStaysWithinTheBudget()
    {
        var replayed = 0;
        foreach (var run in CapturedRuns())
        {
            foreach (var step in Steps(run, "system.events").Where(step => step.Output.Contains("\"groups\"", StringComparison.Ordinal)))
            {
                var captured = JsonNode.Parse(step.Output)!.AsObject();
                if (captured["returnedGroups"] is null || captured["observedGroups"] is null || captured["groups"] is not JsonArray
                    || captured["excludeSources"] is not JsonArray || captured["coverage"] is not JsonObject || captured["sources"] is not JsonArray
                    || captured["truncated"] is null
                    || captured["returnedGroups"]!.GetValue<int>() < captured["observedGroups"]!.GetValue<int>())
                {
                    // The capture holds only the groups the limit or the budget kept, so the snapshot cannot be rebuilt whole.
                    output.WriteLine($"{run.Name} events: not replayable, the capture returned {captured["returnedGroups"]} of {captured["observedGroups"]} groups.");
                    continue;
                }

                replayed++;
                var now = DateTimeOffset.Parse(captured["window"]!["toUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                var from = DateTimeOffset.Parse(captured["window"]!["fromUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                var (snapshot, query) = RebuildEvents(captured, step.Arguments, from, now);
                var budget = step.Arguments["maxOutputBytes"]?.GetValue<int>() ?? SystemEventsLimits.DefaultOutputBytes;
                var limit = step.Arguments["limit"]?.GetValue<int>() ?? 50;

                var formatted = JsonNode.Parse(SystemEventFormatting.Format(snapshot, query, EvidenceMode.Aggregate, limit, budget))!.AsObject();
                var facts = SystemEventFacts.WithMetadata(snapshot, query);

                Assert.True(
                    captured["observedEvents"]!.GetValue<int>() == formatted["observedEvents"]!.GetValue<int>()
                    && captured["observedGroups"]!.GetValue<int>() == formatted["observedGroups"]!.GetValue<int>(),
                    $"{run.Name} {step.Arguments.ToJsonString()}: captured {captured["observedEvents"]} events in {captured["observedGroups"]} groups, replay {formatted["observedEvents"]} in {formatted["observedGroups"]}.");
                if (formatted["observedGroups"]!.GetValue<int>() == 0)
                {
                    // Nothing to summarize: the result says so by observedGroups and carries no summary.
                    Assert.Null(formatted["groupSummary"]);
                    output.WriteLine($"{run.Name} events: no group in the capture; no summary, as designed.");
                    continue;
                }

                var summary = Assert.IsType<JsonObject>(formatted["groupSummary"], exactMatch: false);
                var entries = summary["entries"]!.AsArray();
                Assert.Equal(summary["observedEntries"]!.GetValue<int>(), entries.Count + summary["omittedEntries"]!.GetValue<int>());
                Assert.All(entries, entry => Assert.True(entry!["distinctMessages"]!.GetValue<int>() >= 1));
                Assert.True(Encoding.UTF8.GetByteCount(formatted.ToJsonString()) <= budget);
                Assert.True(facts.Count <= EvidenceFactBounds.MaximumFacts);
                Assert.True(JsonSerializer.SerializeToUtf8Bytes(facts).Length <= EvidenceFactBounds.MaximumSerializedBytes);

                var applied = Assert.Single(facts, fact => fact.Type == SystemEvidenceMetadata.EventsSelectionType).Value.GetValue<string>();
                var coverage = facts.Where(fact => fact.Type == SystemEvidenceMetadata.EventsCoverageType).ToDictionary(fact => fact.Key, fact => fact.Value.ToJsonString());
                var unlisted = facts.FirstOrDefault(fact => fact.Type == SystemEventFacts.UnlistedGroupsType)?.Value.ToJsonString() ?? "0";
                output.WriteLine(
                    $"{run.Name} events: args {step.Arguments.ToJsonString()} -> {applied}; groups observed {captured["observedGroups"]} returned {formatted["returnedGroups"]}; " +
                    $"summary listed {summary["listedEntries"]}/{summary["observedEntries"]} (omitted {summary["omittedEntries"]}, {Encoding.UTF8.GetByteCount(summary.ToJsonString())} bytes) " +
                    $"vs captured described groups {(captured["messageDistribution"]?["describedGroups"]?.ToJsonString() ?? "n/a (captured before message distributions)")}; facts {facts.Count} (captured {step.Facts}), unlisted pairs {unlisted}; " +
                    $"coverage {string.Join(", ", coverage.Select(pair => pair.Key + "=" + pair.Value))}; captured coverage state {captured["coverage"]!["state"]}");
            }
        }

        AssertSomethingWasReplayed(replayed, "system.events");
    }

    /// <summary>With a campaign named, a replay that replayed nothing proves nothing, so it fails instead of passing empty.</summary>
    private void AssertSomethingWasReplayed(int replayed, string tool)
    {
        output.WriteLine($"{tool}: {replayed} captured result(s) replayed.");
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CampaignVariable)))
        {
            Assert.True(replayed > 0, $"{CampaignVariable} names a campaign with no replayable {tool} result.");
        }
    }

    // ---- rebuilding what the collectors returned ----

    private static CrashEvidenceSnapshot RebuildCrashes(JsonObject captured)
    {
        var items = new List<CrashEvidence>();
        foreach (var node in captured["groups"]!.AsArray())
        {
            var group = node!.AsObject();
            var kind = group["kind"]!.GetValue<string>();
            var count = group["count"]!.GetValue<int>();
            var first = Time(group["firstSeenUtc"]);
            var last = Time(group["lastSeenUtc"]);
            var uncorrelated = group["uncorrelatedCount"]!.GetValue<int>();
            var dumps = group["dumpReferenceCount"]!.GetValue<int>();
            var sources = group["evidenceSources"]!.AsArray().Select(source => source!.GetValue<string>()).ToArray();
            var code = group["code"]?.GetValue<string>();
            for (var index = 0; index < count; index++)
            {
                var at = count == 1 || index == 0 ? first : index == count - 1 ? last : first + ((last - first) * index / (count - 1));
                items.Add(new CrashEvidence(at, Enum.Parse<EvidenceTimestampKind>(group["timestampKind"]!.GetValue<string>(), ignoreCase: true), sources.FirstOrDefault() ?? "wer", kind)
                {
                    EventName = group["eventName"]?.GetValue<string>(),
                    Process = group["application"]?.GetValue<string>(),
                    FaultModule = group["module"]?.GetValue<string>(),
                    BugcheckCode = kind == CrashKinds.KernelBugcheck ? code : null,
                    LiveDumpCode = kind == CrashKinds.KernelLiveDump ? code : null,
                    ExceptionCode = kind is CrashKinds.ApplicationCrash or CrashKinds.ApplicationHang ? code : null,
                    ReportId = index < uncorrelated ? null : "replay-" + index,
                    DumpPath = index < dumps ? @"C:\replay\" + index + ".dmp" : null,
                    EvidenceSources = sources,
                });
            }
        }

        var statuses = captured["sources"]!.AsArray()
            .Select(source => new MaintenanceSource(
                source!["name"]!.GetValue<string>(),
                Enum.Parse<InventorySourceStatus>(source["status"]!.GetValue<string>(), ignoreCase: true),
                source["detail"]?.GetValue<string>())
            { ExaminedFromUtc = source["examinedFromUtc"] is { } examined ? Time(examined) : null })
            .ToArray();
        return new CrashEvidenceSnapshot(items, statuses)
        {
            Stores = Stores(captured, [.. statuses.Select(source => source.Name)]),
        };
    }

    private static (SystemEventSnapshot Snapshot, SystemEventQuery Query) RebuildEvents(JsonObject captured, JsonObject arguments, DateTimeOffset from, DateTimeOffset to)
    {
        var records = new List<SystemEventRecord>();
        foreach (var node in captured["groups"]!.AsArray())
        {
            var group = node!.AsObject();
            var count = group["count"]!.GetValue<int>();
            var first = Time(group["firstSeenUtc"]);
            var last = Time(group["lastSeenUtc"]);
            var sample = group["sampleMessage"]?.GetValue<string>() ?? string.Empty;
            var severity = Enum.Parse<SystemEventSeverity>(group["severity"]!.GetValue<string>(), ignoreCase: true);
            var messages = Messages(group, count, sample, arguments["text"]?.GetValue<string>() ?? string.Empty);
            for (var index = 0; index < count; index++)
            {
                var at = count == 1 || index == count - 1 ? last : index == 0 ? first : first + ((last - first) * index / (count - 1));
                records.Add(new SystemEventRecord(at, severity, group["source"]!.GetValue<string>(), group["eventId"]?.GetValue<string>(), group["channel"]?.GetValue<string>(), messages[index], null, null, group["unit"]?.GetValue<string>()));
            }
        }

        var sources = captured["sources"]!.AsArray()
            .Select(source => new InventorySourceResult(
                source!["name"]!.GetValue<string>(),
                Enum.Parse<InventorySourceStatus>(source["status"]!.GetValue<string>(), ignoreCase: true),
                source["detail"]?.GetValue<string>())
            { ExaminedFromUtc = source["examinedFromUtc"] is { } examined ? Time(examined) : null })
            .ToArray();
        var snapshot = new SystemEventSnapshot(records, sources, captured["truncated"]!.GetValue<bool>())
        {
            Stores = Stores(captured, [.. sources.Select(source => source.Name)]),
        };
        var query = new SystemEventQuery(
            from,
            to,
            arguments["minSeverity"] is { } floor ? Enum.Parse<SystemEventSeverity>(floor.GetValue<string>(), ignoreCase: true) : null,
            arguments["source"]?.GetValue<string>(),
            arguments["eventId"]?.GetValue<string>(),
            arguments["channel"]?.GetValue<string>(),
            arguments["text"]?.GetValue<string>(),
            SystemEventsLimits.ScanCeiling,
            [.. captured["excludeSources"]!.AsArray().Select(entry => entry!.GetValue<string>())]);
        return (snapshot, query);
    }

    /// <summary>The messages of a group's records, newest last: the variants the capture described, filler to the distinct count, then the sample.</summary>
    private static string[] Messages(JsonObject group, int count, string sample, string text)
    {
        var messages = new List<string>();
        if (group["messageVariants"]?["variants"] is JsonArray variants)
        {
            foreach (var variant in variants)
            {
                messages.AddRange(Enumerable.Repeat(variant!["message"]!.GetValue<string>(), variant["count"]!.GetValue<int>()));
            }
        }

        var distinct = group["distinctMessages"]?.GetValue<int>() ?? 1;
        for (var extra = messages.Distinct(StringComparer.Ordinal).Count(); extra < distinct; extra++)
        {
            // A record must still match the text filter the capture applied, or the snapshot would lose it to the filter once more.
            messages.Add("replay-variant-" + extra + " " + text);
        }

        messages = [.. messages.Take(Math.Max(0, count - 1))];
        while (messages.Count < count - 1)
        {
            messages.Add(sample);
        }

        messages.Add(sample);
        return [.. messages];
    }

    private static CoverageStore[] Stores(JsonObject captured, string[] sourceNames) =>
        [.. captured["coverage"]!["stores"]!.AsArray().Select(store => new CoverageStore(
            store!["name"]!.GetValue<string>(),
            Enum.Parse<CoverageBasis>(store["basis"]!.GetValue<string>(), ignoreCase: true),
            store["oldestAvailableUtc"] is { } oldest ? Time(oldest) : null,
            store["logMaximumBytes"]?.GetValue<long>(),
            sourceNames))];

    private const int VisibilityHead = 6_500;

    private static readonly string[] IdentityFields = ["kind", "code", "application"];

    private static JsonArray KernelOnly(JsonArray groups) =>
        [.. groups.Where(group => group!["kind"]!.GetValue<string>() is CrashKinds.KernelBugcheck or CrashKinds.KernelLiveDump).Select(group => group!.DeepClone())];

    /// <summary>An approximation: a group counts as named when its kind and its code or application each appear as a value in the head of the output.</summary>
    private static int VisibleGroups(string text, JsonArray groups)
    {
        var head = text[..Math.Min(text.Length, VisibilityHead)];
        return groups.Count(group => IdentityFields
            .Select(name => group![name])
            .Where(value => value is not null)
            .All(value => head.Contains(value!.ToJsonString(), StringComparison.Ordinal)));
    }

    private static readonly string[] GroupKeyFields =
        ["kind", "eventName", "code", "application", "module", "count", "firstSeenUtc", "lastSeenUtc", "dumpReferenceCount", "uncorrelatedCount"];

    private static string[] GroupKeys(JsonArray groups) =>
        [.. groups.Select(group => string.Join('/', GroupKeyFields.Select(name => group![name]?.ToJsonString() ?? "null")))];

    private static DateTimeOffset Time(JsonNode? node) =>
        DateTimeOffset.Parse(node!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    // ---- reading the archive (read only) ----

    private sealed record Captured(string Name, JsonObject State);

    private sealed record ToolStepResult(JsonObject Arguments, string Output, int Facts);

    private static IEnumerable<Captured> CapturedRuns()
    {
        var campaign = Environment.GetEnvironmentVariable(CampaignVariable);
        if (string.IsNullOrWhiteSpace(campaign) || !Directory.Exists(Path.Combine(campaign, "runs")))
        {
            yield break;
        }

        foreach (var run in Directory.EnumerateDirectories(Path.Combine(campaign, "runs")).Order(StringComparer.Ordinal))
        {
            var state = Directory.EnumerateFiles(Path.Combine(run, "capture", "tasks"), "state_json.utf8", SearchOption.AllDirectories).FirstOrDefault();
            if (state is not null)
            {
                yield return new Captured(Path.GetFileName(run), JsonNode.Parse(File.ReadAllText(state, Encoding.UTF8))!.AsObject());
            }
        }
    }

    private static IEnumerable<ToolStepResult> Steps(Captured run, string tool) =>
        Property(run.State, "steps")!.AsArray()
            .Select(step => step!.AsObject())
            .Where(step => Property(step, "toolCall") is JsonObject call && Property(call, "toolName")?.GetValue<string>() == tool
                && Property(step, "result") is JsonObject result && Property(result, "output") is JsonValue)
            .Select(step => new ToolStepResult(
                Property((JsonObject)Property(step, "toolCall")!, "arguments") as JsonObject ?? [],
                Property((JsonObject)Property(step, "result")!, "output")!.GetValue<string>(),
                (Property((JsonObject)Property(step, "result")!, "facts") as JsonArray)?.Count ?? 0));

    private static ToolStepResult? ToolStep(Captured run, string tool) => Steps(run, tool).FirstOrDefault();

    private static JsonNode? Property(JsonObject node, string name) =>
        node.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private sealed class CrashesTool(CrashEvidenceSnapshot snapshot, DateTimeOffset now) : SystemCrashesToolBase("replay", new FixedClock(now))
    {
        protected override Task<CrashEvidenceSnapshot> CollectAsync(CrashesQuery query, CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
