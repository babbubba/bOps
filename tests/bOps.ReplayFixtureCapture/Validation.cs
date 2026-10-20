// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.QwenLiveBaseline;

namespace bOps.ReplayFixtureCapture;

internal static class SnapshotIntegrity
{
    /// <summary>SHA256SUMS, the ledger hash chain and the read-only flag of every fixture. Empty means intact.</summary>
    public static IReadOnlyList<string> Problems(string snapshot)
    {
        var problems = new List<string>();
        var sums = Path.Combine(snapshot, "SHA256SUMS.txt");
        if (!File.Exists(sums))
        {
            problems.Add("SHA256SUMS.txt is missing");
        }
        else
        {
            foreach (var line in File.ReadAllLines(sums, Encoding.UTF8).Where(l => l.Length > 0))
            {
                var hash = line[..64];
                var relative = line[66..];
                var path = Path.Combine(snapshot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) || CaptureRunner.Sha256File(path) != hash)
                {
                    problems.Add($"{relative}: missing or changed");
                }
            }
        }

        var ledger = Path.Combine(snapshot, "capture-ledger.jsonl");
        if (!File.Exists(ledger) || !CaptureLedger.Verify(ledger))
        {
            problems.Add("capture-ledger.jsonl is missing or its hash chain is broken");
        }

        var fixtures = Path.Combine(snapshot, "fixtures");
        if (Directory.Exists(fixtures))
        {
            problems.AddRange(Directory.EnumerateFiles(fixtures, "F*.json").Where(p => !File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly))
                .Select(p => $"{Path.GetFileName(p)}: not read-only"));
        }
        else
        {
            problems.Add("fixtures/ is missing (an aborted or unpromoted snapshot)");
        }

        return problems;
    }
}

internal enum CheckStatus
{
    Pass,
    Warn,
    Fail,
}

internal sealed record Check(string Area, string Name, CheckStatus Status, string Detail);

/// <summary>A call that an earlier campaign could not answer; the validator asks the current matcher, unchanged, whether the snapshot would.</summary>
internal sealed record CoverageProbe(string Label, string Source, string Tool, string ArgumentsJson, bool ExpectedServed, string Note);

internal sealed class ValidationReport(string snapshot, IReadOnlyList<Check> checks, JsonObject details)
{
    public IReadOnlyList<Check> Checks => checks;

    public bool HasFail => checks.Any(c => c.Status == CheckStatus.Fail);

    public bool HasCredentialHits => details["sensitive"]?["credentialShapedHits"]?.GetValue<int>() > 0;

    public JsonObject ToJson() => new()
    {
        ["snapshot"] = snapshot,
        ["verdict"] = HasFail ? "FAIL" : HasCredentialHits ? "REVIEW-CREDENTIAL-SHAPES" : checks.Any(c => c.Status == CheckStatus.Warn) ? "PASS-WITH-WARNINGS" : "PASS",
        ["note"] = "A mechanical validation. Whether the snapshot is fit as a frozen benchmark dataset is a decision for the reviewer.",
        ["checks"] = new JsonArray(checks.Select(c => (JsonNode)new JsonObject { ["area"] = c.Area, ["name"] = c.Name, ["status"] = c.Status.ToString().ToUpperInvariant(), ["detail"] = c.Detail }).ToArray()),
        ["details"] = details.DeepClone(),
    };

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"SNAPSHOT VALIDATION  {snapshot}");
        foreach (var group in checks.GroupBy(c => c.Area))
        {
            text.AppendLine($"[{group.Key}]");
            foreach (var c in group)
            {
                text.AppendLine($"  {c.Status.ToString().ToUpperInvariant(),-4} {c.Name}{(c.Detail.Length > 0 ? " - " + c.Detail : string.Empty)}");
            }
        }

        text.AppendLine("[Outputs]");
        foreach (var row in details["outputs"]!.AsArray())
        {
            text.AppendLine($"  {row!["id"],-5} {row["tool"],-17} status={row["status"]} complete={row["complete"]} truncated={row["truncated"]} toolCompleteness={row["toolCompleteness"]} observed={row["observed"]} returned={row["returned"]} nonAvailableSources=[{string.Join(", ", row["nonAvailableSources"]!.AsArray().Select(s => s!.GetValue<string>()))}]");
        }

        text.AppendLine("[Theoretical coverage of earlier unsupported T02 calls (current matcher, unchanged)]");
        foreach (var row in details["coverage"]!.AsArray())
        {
            text.AppendLine($"  {row!["label"],-4} {(row["served"]!.GetValue<bool>() ? "SERVED     " : "UNSUPPORTED")} {row["tool"],-16} {row["arguments"]} -> {row["result"]}");
        }

        text.AppendLine($"[Summary] checks: {checks.Count(c => c.Status == CheckStatus.Pass)} pass, {checks.Count(c => c.Status == CheckStatus.Warn)} warn, {checks.Count(c => c.Status == CheckStatus.Fail)} fail; credential-shaped hits: {details["sensitive"]!["credentialShapedHits"]}");
        text.AppendLine($"VERDICT: {ToJson()["verdict"]}");
        return text.ToString();
    }
}

/// <summary>
/// Offline validation of a captured T02-B snapshot. It reads files only (no collector, no network, no model) and changes nothing except writing its own report.
/// The matcher is used as it is, never altered: coverage is a statement about what the unchanged rules would do with these fixtures.
/// </summary>
internal static class SnapshotValidator
{

    public static readonly IReadOnlyList<CoverageProbe> Probes =
    [
        new("O1", "T02 original r1", "system.events", """{"mode":"aggregate","windowMinutes":1440}""", true, "no severity filter, default limit 50"),
        new("O2", "T02 original r2", "system.events", """{"minSeverity":"warning","mode":"raw","windowMinutes":1440}""", false, "raw warning 24 h at the default limit: only the optional OPT1 would cover it"),
        new("O3", "T02 original r2", "system.stability", """{"limit":50,"windowDays":2}""", true, "2 days, limit 50"),
        new("O4", "T02 original r2", "system.crashes", """{"sinceMinutes":1440}""", true, "default mode and limit"),
        new("O5", "T02 original r3", "system.events", """{"minSeverity":"warning","mode":"aggregate","windowMinutes":1440}""", true, "default limit 50 against a capture at 500"),
        new("O6", "T02 original r3", "system.stability", """{"windowDays":1}""", true, "default limit 50 against a capture at 200"),
        new("R1", "T02 revalidation r2", "system.events", """{"limit":500,"minSeverity":"error","mode":"aggregate","windowMinutes":1440}""", true, "error, aggregate, 500"),
        new("R2", "T02 revalidation r2", "system.stability", """{"limit":100,"windowDays":30}""", true, "30 days, limit 100 against a capture at 200"),
        new("R3", "T02 revalidation r3", "system.events", """{"limit":100,"minSeverity":"critical","mode":"raw","windowMinutes":1440}""", true, "raw critical, limit 100 against a capture at 500"),
        new("R4", "T02 revalidation r3", "system.stability", """{"limit":100,"windowDays":3}""", true, "3 days, limit 100 against a capture at 200"),
        new("S1", "T02 revalidation r1/r3 (served before)", "system.events", """{"limit":200,"minSeverity":"warning","mode":"aggregate","windowMinutes":1440}""", true, "warning aggregate with limit 200"),
        new("S2", "T02 revalidation r2 (served before)", "system.crashes", """{"limit":100,"mode":"aggregate","sinceMinutes":1440}""", true, "crashes with the defaults spelled out"),
    ];

    /// <summary>Never throws on a damaged snapshot: anything it cannot read becomes a failed check.</summary>
    public static ValidationReport Validate(string snapshot, TimeSpan maxSpread)
    {
        var checks = new List<Check>();
        var details = new JsonObject();
        try
        {
            return ValidateCore(snapshot, maxSpread, checks, details);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException or FormatException or UnauthorizedAccessException)
        {
            checks.Add(new Check("Structure", "the snapshot can be read completely", CheckStatus.Fail, $"{ex.GetType().Name}: {ex.Message}"));
            details["outputs"] ??= new JsonArray();
            details["coverage"] ??= new JsonArray();
            details["sensitive"] ??= new JsonObject { ["credentialShapedHits"] = 0 };
            return new ValidationReport(snapshot, checks, details);
        }
    }

    private static ValidationReport ValidateCore(string snapshot, TimeSpan maxSpread, List<Check> checks, JsonObject details)
    {
        void Add(string area, string name, CheckStatus status, string detail = "") => checks.Add(new Check(area, name, status, detail));
        void Gate(string area, string name, bool ok, string detailIfFail, CheckStatus failStatus = CheckStatus.Fail, string detailIfOk = "") =>
            Add(area, name, ok ? CheckStatus.Pass : failStatus, ok ? detailIfOk : detailIfFail);

        var fixturesDir = Path.Combine(snapshot, "fixtures");
        var promoted = Directory.Exists(fixturesDir) && !Directory.Exists(Path.Combine(snapshot, "pending"))
            && !Directory.Exists(Path.Combine(snapshot, "aborted")) && !File.Exists(Path.Combine(snapshot, "ABORTED.json"));
        Gate("Structure", "snapshot is promoted (fixtures/ present; no pending/, aborted/ or ABORTED.json)", promoted, "the batch did not complete; this is not a fixture pool");
        if (!promoted)
        {
            details["outputs"] = new JsonArray();
            details["coverage"] = new JsonArray();
            details["sensitive"] = new JsonObject { ["credentialShapedHits"] = 0 };
            return new ValidationReport(snapshot, checks, details);
        }

        JsonObject Load(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(snapshot, file), Encoding.UTF8))!.AsObject();
        var index = Load("index.json");
        var plan = Load("plan.json");
        var environment = Load("environment.json");
        var temporal = Load("temporal-report.json");
        var fixtures = FixtureStore.Load(fixturesDir);
        var files = Directory.GetFiles(fixturesDir, "F*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();

        var snapshotId = index["snapshotId"]!.GetValue<string>();
        var planName = snapshotId.StartsWith("T02-C-", StringComparison.Ordinal) ? "T02-C" : "T02-B";
        var all = CapturePlan.Queries(planName);
        var MandatoryIds = all.Where(q => !q.Optional).Select(q => q.Id).ToArray();
        var fixturePrefix = planName == "T02-C" ? "FC-" : "FB-";
        Gate("Structure", "snapshot identity and version agree across metadata and every fixture",
            snapshotId.StartsWith(planName + "-", StringComparison.Ordinal)
            && Path.GetFileName(Path.TrimEndingDirectorySeparator(snapshot)) == snapshotId
            && environment["snapshotId"]?.GetValue<string>() == snapshotId
            && plan["planVersion"]?.GetValue<int>() == CapturePlan.VersionOf(planName)
            && (planName == "T02-B" || plan["planName"]?.GetValue<string>() == planName)
            && files.All(f => JsonNode.Parse(File.ReadAllText(f))!["provenance"]!["snapshotId"]?.GetValue<string>() == snapshotId),
            "mixed snapshot identities, directory name or plan version");

        // ---- structure and pool separation
        var parent = new DirectoryInfo(Path.GetFullPath(snapshot));
        var insideGit = false;
        for (var dir = parent; dir is not null; dir = dir.Parent)
        {
            insideGit |= Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"));
        }

        Gate("Structure", "kept outside any Git working tree", !insideGit, "the snapshot lives inside a Git working tree");
        Gate("Structure", "pool is separate from the historical fixtures (only plan-specific F* files, all scenario T02)",
            fixtures.Count > 0 && fixtures.All(f => f.Id.StartsWith(fixturePrefix, StringComparison.Ordinal) && f.OriginScenario == "T02")
            && files.All(f => Path.GetFileName(f).StartsWith(fixturePrefix, StringComparison.Ordinal)),
            "a fixture with a historical id, or another scenario, is present");

        // ---- completion of the plan
        var presentQueries = files.Select(f => JsonNode.Parse(File.ReadAllText(f))!["provenance"]!["queryId"]!.GetValue<string>()).ToList();
        var missing = MandatoryIds.Where(id => presentQueries.Count(p => p == id) != 1).ToList();
        Gate("Completion", $"all {MandatoryIds.Length} mandatory acquisitions present exactly once", missing.Count == 0 && files.Length >= MandatoryIds.Length,
            "missing or duplicated: " + string.Join(", ", missing), detailIfOk: string.Join(", ", MandatoryIds));
        var optionalPresent = presentQueries.Where(id => id.StartsWith("OPT", StringComparison.Ordinal)).ToList();
        Add("Completion", "optional queries", optionalPresent.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn,
            optionalPresent.Count == 0 ? "none captured (disabled, as approved)" : "captured: " + string.Join(", ", optionalPresent));
        var authorised = presentQueries.All(id => all.Any(q => q.Id == id))
            && presentQueries.Distinct(StringComparer.Ordinal).Count() == presentQueries.Count;
        Gate("Completion", "only authorised queries, no duplicates", authorised, "unknown or duplicated query id");
        if (!authorised)
        {
            details["outputs"] = new JsonArray();
            details["coverage"] = new JsonArray();
            details["sensitive"] = new JsonObject { ["credentialShapedHits"] = 0 };
            return new ValidationReport(snapshot, checks, details);
        }

        var selected = CapturePlan.Select(optionalPresent, planName);
        var expectedHash = CapturePlan.Hash(selected, planName);
        var planned = plan["queries"]!.AsArray();
        Gate("Completion", "recorded plan has the exact reviewed queries in order",
            planned.Count == selected.Count && planned.Zip(selected).All(pair =>
                pair.First?["id"]?.GetValue<string>() == pair.Second.Id
                && pair.First?["tool"]?.GetValue<string>() == pair.Second.Tool
                && ReplayArguments.Literal(pair.First?["arguments"]) == ReplayArguments.Literal(pair.Second.Arguments)
                && ReplayArguments.Literal(pair.First?["effectiveArguments"]) == ReplayArguments.Literal(pair.Second.EffectiveArguments)),
            "recorded query list differs from the reviewed plan");
        Gate("Completion", "plan hash matches the reviewed plan", plan["planSha256"]!.GetValue<string>() == expectedHash && index["planSha256"]!.GetValue<string>() == expectedHash,
            $"plan.json/index.json record another plan than the reviewed one ({expectedHash})", detailIfOk: expectedHash);

        var argumentMismatch = new List<string>();
        foreach (var path in files)
        {
            var fx = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var spec = all.First(q => q.Id == fx["provenance"]!["queryId"]!.GetValue<string>());
            if (fx["tool"]!.GetValue<string>() != spec.Tool || ReplayArguments.Literal(fx["arguments"]) != ReplayArguments.Literal(spec.Arguments)
                || ReplayArguments.Literal(fx["provenance"]!["queryJson"]) != ReplayArguments.Literal(spec.Arguments)
                || fx["provenance"]!["atomicSnapshot"]?.GetValue<bool>() != false
                || fx["provenance"]!["manifestSha256"]?.GetValue<string>() != ManifestFingerprint.Of(new RealManifestSource().ManifestOf(spec.Tool))
                || ReplayArguments.Literal(fx["provenance"]!["effectiveArguments"]) != ReplayArguments.Literal(spec.EffectiveArguments))
            {
                argumentMismatch.Add(spec.Id);
            }
        }

        Gate("Completion", "tool and arguments of every fixture are exactly the planned ones", argumentMismatch.Count == 0, "differ: " + string.Join(", ", argumentMismatch));

        // ---- integrity
        var integrity = SnapshotIntegrity.Problems(snapshot);
        Gate("Integrity", "SHA256SUMS, ledger hash chain and read-only fixtures", integrity.Count == 0, string.Join("; ", integrity));
        var indexed = index["fixtures"]!.AsArray();
        Gate("Integrity", "index lists each fixture exactly once",
            indexed.Count == files.Length
            && indexed.Select(e => e!["file"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() == indexed.Count
            && indexed.Select(e => e!["file"]!.GetValue<string>()).Order(StringComparer.Ordinal)
                .SequenceEqual(files.Select(Path.GetFileName).Order(StringComparer.Ordinal)),
            "index and fixture file list disagree");
        var hashProblems = new List<string>();
        foreach (var entry in index["fixtures"]!.AsArray().Select(f => f!.AsObject()))
        {
            var path = Path.Combine(fixturesDir, entry["file"]!.GetValue<string>());
            if (!File.Exists(path))
            {
                hashProblems.Add(entry["id"] + ": file missing");
                continue;
            }

            var fx = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var result = fx["result"]!.AsObject();
            if (entry["id"]?.GetValue<string>() != fx["id"]?.GetValue<string>())
            {
                hashProblems.Add(entry["id"] + ": index id differs");
            }

            if (FixtureWriter.ContentSha256(fx["tool"]!.GetValue<string>(), fx["arguments"]!.AsObject(), result) != fx["sha256"]!.GetValue<string>())
            {
                hashProblems.Add(entry["id"] + ": content sha256");
            }

            if (CapturePlan.Sha256(result["Output"]?.GetValue<string>() ?? string.Empty) != fx["outputSha256"]!.GetValue<string>())
            {
                hashProblems.Add(entry["id"] + ": output sha256");
            }

            if (CaptureRunner.Sha256File(path) != entry["fileSha256"]!.GetValue<string>() || entry["sha256"]!.GetValue<string>() != fx["sha256"]!.GetValue<string>())
            {
                hashProblems.Add(entry["id"] + ": index digests");
            }
        }

        Gate("Integrity", "content, output and file SHA-256 recomputed and equal to the index", hashProblems.Count == 0, string.Join("; ", hashProblems));
        var ledgerEntries = File.ReadAllLines(Path.Combine(snapshot, "capture-ledger.jsonl")).Select(l => JsonNode.Parse(l)!).ToArray();
        Gate("Integrity", "ledger query identities follow the selected plan exactly once in order",
            new[] { "queryStarted", "queryCompleted" }.All(kind => ledgerEntries
                .Where(e => e["event"]!.GetValue<string>() == kind)
                .Select(e => e["data"]?["query"]?.GetValue<string>()).SequenceEqual(selected.Select(q => q.Id))),
            "ledger query ids differ from the reviewed sequence");
        var ledgerLines = File.ReadAllLines(Path.Combine(snapshot, "capture-ledger.jsonl")).Select(l => JsonNode.Parse(l)!["event"]!.GetValue<string>()).ToList();
        var started = ledgerLines.Count(e => e == "queryStarted");
        var completed = ledgerLines.Count(e => e == "queryCompleted");
        Gate("Integrity", "ledger: identityVerified first, one start and one completion per query, promoted, no abort",
            ledgerLines.FirstOrDefault() == "identityVerified" && started == files.Length && completed == files.Length && ledgerLines.Contains("promoted") && !ledgerLines.Contains("aborted"),
            $"events: {string.Join(",", ledgerLines.Distinct())}; started {started}, completed {completed}, fixtures {files.Length}");

        // ---- identity
        var identities = files.Select(f => JsonNode.Parse(File.ReadAllText(f))!["provenance"]!["identity"]!.AsObject()).ToList();
        string Field(string name) => string.Join("|", identities.Select(i => i[name]?.ToJsonString()).Distinct());
        Gate("Identity", "Medium integrity, Limited elevation type, not elevated, no Administrators role, SID recorded in every fixture",
            identities.All(i => i["integrityLevel"]!.GetValue<string>() is "Medium" or "MediumPlus" && i["elevationType"]!.GetValue<string>() == "Limited"
                && !i["elevated"]!.GetValue<bool>() && !i["administratorRole"]!.GetValue<bool>() && (i["sid"]?.GetValue<string>() ?? string.Empty).StartsWith("S-1-5-", StringComparison.Ordinal)),
            $"levels {Field("integrityLevel")}, elevation {Field("elevationType")}, elevated {Field("elevated")}, admin {Field("administratorRole")}",
            detailIfOk: $"{identities[0]["user"]} {identities[0]["sid"]} {identities[0]["integrityLevel"]} {identities[0]["integrityRid"]} {identities[0]["elevationType"]}");
        Gate("Identity", "one identity for the whole batch and equal to environment.json",
            identities.Select(i => i.ToJsonString()).Distinct().Count() == 1 && identities[0]["sid"]!.GetValue<string>() == environment["userSid"]!.GetValue<string>()
            && environment["integrityRid"]!.GetValue<string>() == identities[0]["integrityRid"]!.GetValue<string>(),
            "fixtures or environment.json disagree about the identity");
        var binaryHashes = files.Select(f => JsonNode.Parse(File.ReadAllText(f))!["provenance"]!["binaries"]!.ToJsonString()).Distinct().ToList();
        Gate("Identity", "the same binaries (SHA-256) produced every fixture and match environment.json",
            binaryHashes.Count == 1 && binaryHashes[0] == environment["binaries"]!.ToJsonString(), "binary hashes differ between fixtures or against environment.json");

        // ---- temporal coherence
        var temporalProblems = new List<string>();
        var rows = new List<(string Id, DateTimeOffset Start, DateTimeOffset End, DateTimeOffset WindowTo)>();
        var outputs = new JsonArray();
        var credentialHits = 0;
        var sensitiveRows = new JsonArray();
        var literals = new[] { environment["machineName"]!.GetValue<string>(), CaptureRunner.UserOnly(environment["user"]!.GetValue<string>()) };
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var unexpected = new List<string>();
        var partials = 0;
        var truncatedCount = 0;
        DateTimeOffset previousEnd = DateTimeOffset.MinValue;
        // In capture order (file names sort the optional queries first).
        var inCaptureOrder = files.OrderBy(f => DateTimeOffset.Parse(JsonNode.Parse(File.ReadAllText(f))!["provenance"]!["startedAtUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture)).ToArray();
        foreach (var f in inCaptureOrder)
        {
            var fx = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
            var prov = fx["provenance"]!.AsObject();
            var spec = all.First(q => q.Id == prov["queryId"]!.GetValue<string>());
            var fixture = fixtures.First(x => x.Id == fx["id"]!.GetValue<string>());
            var start = DateTimeOffset.Parse(prov["startedAtUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var end = DateTimeOffset.Parse(prov["endedAtUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var from = DateTimeOffset.Parse(prov["windowFromUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var to = DateTimeOffset.Parse(prov["windowToUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            rows.Add((spec.Id, start, end, to));
            if ((to - from - spec.RequestedWindow).Duration() > TimeSpan.FromMilliseconds(1))
            {
                temporalProblems.Add($"{spec.Id}: window {to - from}, requested {spec.RequestedWindow}");
            }

            if (to < start - TimeSpan.FromSeconds(5) || to > end + TimeSpan.FromSeconds(5) || start > end || start < previousEnd)
            {
                temporalProblems.Add($"{spec.Id}: window end/intervals are not coherent ({start:O}..{end:O}, windowTo {to:O})");
            }

            previousEnd = end;

            var facts = ResultChecks.Read(spec, fixture.Result);
            var violations = facts is null ? ["no readable facts"] : ResultChecks.Violations(spec, fixture.Result, facts, start, end);
            unexpected.AddRange(violations.Select(v => $"{spec.Id}: {v}"));
            var nonAvailable = new JsonArray();
            if (JsonNode.Parse(fixture.Result.Output!)!["sources"] is JsonArray sources)
            {
                foreach (var s in sources.Where(s => s!["status"]?.GetValue<string>() != "available"))
                {
                    nonAvailable.Add($"{s!["name"]}={s["status"]}");
                }
            }

            var isPartial = fixture.Result.Completeness != ToolResultCompleteness.Complete || facts?.Status != "complete" || nonAvailable.Count > 0;
            partials += isPartial ? 1 : 0;
            truncatedCount += facts?.Truncated == true ? 1 : 0;
            outputs.Add(new JsonObject
            {
                ["id"] = spec.Id,
                ["tool"] = spec.Tool,
                ["status"] = facts?.Status,
                ["complete"] = facts?.Complete,
                ["truncated"] = facts?.Truncated,
                ["toolCompleteness"] = fixture.Result.Completeness.ToString(),
                ["observed"] = facts?.Observed,
                ["returned"] = facts?.Returned,
                ["partialOrIncomplete"] = isPartial,
                ["nonAvailableSources"] = nonAvailable,
                ["truncationAllowedByPlan"] = spec.Expect.Truncation == TruncationPolicy.Allowed,
                ["limitCoverage"] = prov["limitCoverage"]!.DeepClone(),
                ["violations"] = new JsonArray(violations.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
            });

            var hits = SecretScanner.Scan(CaptureRunner.ScanText(fixture.Result), literals);
            credentialHits += hits.Where(h => h.Category == ScanCategory.Secret).Sum(h => h.Count);
            foreach (var h in hits)
            {
                totals[h.PatternId] = totals.GetValueOrDefault(h.PatternId) + h.Count;
            }

            sensitiveRows.Add(new JsonObject { ["id"] = spec.Id, ["hits"] = SecretScanner.ToJson(hits) });
        }

        Gate("Temporal", "every window is exactly the requested length, inside its call interval, and the calls are sequential", temporalProblems.Count == 0, string.Join("; ", temporalProblems));
        var report = TemporalReport.Build(rows);
        Gate("Temporal", $"window ends span at most {maxSpread.TotalSeconds:F0} s", report.WindowEndSpread <= maxSpread,
            $"the spread is {report.WindowEndSpread.TotalSeconds:F1} s", detailIfOk: $"{report.WindowEndSpread.TotalSeconds:F1} s over {report.TotalElapsed.TotalSeconds:F1} s of capture");
        var recorded = temporal["windowEndSpreadSeconds"]!.GetValue<double>();
        Gate("Temporal", "temporal-report.json agrees with the recomputed spread and declares the batch non-atomic",
            Math.Abs(recorded - report.WindowEndSpread.TotalSeconds) < 0.01 && temporal["atomicSnapshot"]!.GetValue<bool>() == false,
            $"recorded {recorded} s, recomputed {report.WindowEndSpread.TotalSeconds:F3} s");
        details["temporal"] = report.ToJson();

        // ---- completeness of outputs
        Gate("Outputs", "every result meets the plan's expectations (status, completeness, truncation, sources, window, budget)", unexpected.Count == 0, string.Join("; ", unexpected));
        Add("Outputs", "Partial / incomplete results", partials == 0 ? CheckStatus.Pass : CheckStatus.Warn,
            $"{partials} of {files.Length} are Partial or have a non-available source (expected for stability/crashes under a standard token); see the table");
        Add("Outputs", "Truncated results", truncatedCount == 0 ? CheckStatus.Pass : CheckStatus.Warn,
            $"{truncatedCount} of {files.Length} are truncated; a truncated capture states no smaller limit and cannot serve other limits");
        details["outputs"] = outputs;

        // ---- sensitive data
        Add("Sensitive", "credential-shaped strings", credentialHits == 0 ? CheckStatus.Pass : CheckStatus.Warn,
            credentialHits == 0 ? "none found by the 9 patterns (heuristic)" : $"{credentialHits} hit(s): review before any use or distribution");
        var identifierTotal = totals.Where(kv => SecretScanner.Patterns.Any(p => p.Id == kv.Key && p.Category == ScanCategory.Identifier) || kv.Key == "capture-identity-literal").Sum(kv => kv.Value);
        Add("Sensitive", "identifying strings (host, account, SIDs, IPs, e-mail, profile and UNC paths)", identifierTotal == 0 ? CheckStatus.Pass : CheckStatus.Warn,
            identifierTotal == 0 ? "none found" : "present, as expected for event logs: " + string.Join(", ", totals.Where(kv => !SecretScanner.Patterns.Any(p => p.Id == kv.Key && p.Category == ScanCategory.Secret)).Select(kv => $"{kv.Key} x{kv.Value}")));
        details["sensitive"] = new JsonObject
        {
            ["credentialShapedHits"] = credentialHits,
            ["totalsByPattern"] = System.Text.Json.JsonSerializer.SerializeToNode(totals),
            ["perFixture"] = sensitiveRows,
            ["note"] = "Counts and digests only; the matched text is never stored.",
        };

        // ---- theoretical coverage with the unchanged matcher
        var coverage = new JsonArray();
        var unexpectedlyUncovered = new List<string>();
        var served = 0;
        foreach (var probe in Probes)
        {
            var broker = new ReplayBroker(fixtures, "T02");
            var arguments = JsonNode.Parse(probe.ArgumentsJson)!.AsObject();
            var candidates = broker.Candidates(probe.Tool, arguments);
            broker.Answer(probe.Tool, arguments);
            var call = broker.Ledger.Single();
            var isServed = call.Verdict == "SERVED";
            served += isServed ? 1 : 0;
            var expectedServed = probe.ExpectedServed || planName == "T02-C";
            if (expectedServed && !isServed)
            {
                unexpectedlyUncovered.Add($"{probe.Label} ({probe.Note})");
            }

            coverage.Add(new JsonObject
            {
                ["label"] = probe.Label,
                ["source"] = probe.Source,
                ["tool"] = probe.Tool,
                ["arguments"] = probe.ArgumentsJson,
                ["served"] = isServed,
                ["result"] = isServed ? $"{call.FixtureId} via {call.Rule}" : call.Rule == "ambiguous" ? "ambiguous: " + string.Join(", ", call.OtherCandidates) : "no equivalent fixture in the snapshot",
                ["expectedByDesign"] = expectedServed,
                ["note"] = planName == "T02-C" && probe.Label == "O2" ? "Dedicated mandatory C2 at limit 50; actual coverage depends on captured data." : probe.Note,
                ["candidates"] = new JsonArray(candidates.Select(c => (JsonNode)JsonValue.Create($"{c.Fixture.Id}:{c.Rule}")!).ToArray()),
            });
        }

        Add("Coverage", $"{served} of {Probes.Count} earlier calls would be served by the snapshot (matcher unchanged)",
            unexpectedlyUncovered.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn,
            unexpectedlyUncovered.Count == 0 ? "all calls the plan was designed to cover are covered" : "designed to be covered but not: " + string.Join("; ", unexpectedlyUncovered));
        details["coverage"] = coverage;
        details["summary"] = new JsonObject { ["fixtures"] = files.Length, ["partial"] = partials, ["truncated"] = truncatedCount, ["probesServed"] = served, ["probes"] = Probes.Count };
        return new ValidationReport(snapshot, checks, details);
    }
}
