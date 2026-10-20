// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Policy;

namespace bOps.ReplayFixtureCapture;

internal sealed record CaptureOptions(
    string OutRoot,
    string PlanSha256,
    IReadOnlyList<string> IncludeOptional,
    TimeSpan MaxWindowEndSpread,
    TimeSpan CallTimeout,
    string? RequiredElevationType = null,
    string PlanName = "T02-B");

internal sealed record CaptureOutcome(int ExitCode, string SnapshotDirectory, string Message);

/// <summary>
/// The only code path that asks a collector for data. It runs the reviewed plan, in order, one query at a time, and stops at the first thing that is
/// not as expected: an unauthorised query, a failed or incomplete result, a clock anomaly, an exception. It never retries, never substitutes another
/// query, never edits a result, and promotes the fixtures only when the whole batch is clean.
/// </summary>
internal sealed class CaptureRunner(
    IEnvironmentProbe environment,
    IManifestSource manifests,
    ICollectorSource collectors,
    IPolicyEngine policy,
    TimeProvider clock,
    Func<string> randomSuffix)
{
    public async Task<CaptureOutcome> RunAsync(CaptureOptions options, CancellationToken ct)
    {
        var selected = CapturePlan.Select(options.IncludeOptional, options.PlanName);
        var planHash = CapturePlan.Hash(selected, options.PlanName);
        if (!string.Equals(options.PlanSha256, planHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new CaptureAbortException($"--plan-sha256 does not match the plan that would run ({planHash}); review the plan and pass its hash.", 2);
        }

        var env = environment.Probe();
        var identityProblems = IdentityGate.Problems(env);
        if (identityProblems.Count > 0)
        {
            throw new CaptureAbortException("identity gate refused: " + string.Join("; ", identityProblems) + ". Nothing was read; no privilege is requested.", 2);
        }

        if ((options.PlanName == "T02-C" ? "Limited" : options.RequiredElevationType) is { } requiredType && !string.Equals(env.ElevationType, requiredType, StringComparison.Ordinal))
        {
            throw new CaptureAbortException($"identity gate refused: the token's elevation type is '{env.ElevationType}', '{requiredType}' was required. Nothing was read; no privilege is requested.", 2);
        }

        var preflight = Preflight.Run(selected, manifests, policy);
        if (preflight.Any(p => !p.Ok))
        {
            throw new CaptureAbortException("preflight failed: " + string.Join("; ", preflight.Where(p => !p.Ok).Select(p =>
                $"{p.Spec.Id}: risk-read={p.ReadOnlyRisk} validation='{p.ValidationError}' policy={p.PolicyMode}")), 2);
        }

        var startedAt = clock.GetUtcNow();
        var snapshotId = $"{options.PlanName}-{startedAt:yyyyMMdd'T'HHmmss'Z'}-{randomSuffix()}";
        var problems = OutputRootGuard.Problems(options.OutRoot, snapshotId);
        if (problems.Count > 0)
        {
            throw new CaptureAbortException(string.Join("; ", problems), 2);
        }

        var snapshotDir = Path.Combine(Path.GetFullPath(options.OutRoot), snapshotId);
        var pendingDir = Path.Combine(snapshotDir, "pending");
        Directory.CreateDirectory(pendingDir);
        var ledger = new CaptureLedger(Path.Combine(snapshotDir, "capture-ledger.jsonl"), clock);
        WriteJson(Path.Combine(snapshotDir, "environment.json"), EnvironmentJson(env, snapshotId, startedAt));
        WriteJson(Path.Combine(snapshotDir, "plan.json"), PlanJson(selected, planHash, preflight, options));
        ledger.Append("identityVerified", IdentityGate.ToJson(env));
        ledger.Append("planVerified", new JsonObject { ["snapshotId"] = snapshotId, ["planSha256"] = planHash, ["queries"] = selected.Count });

        var identityLiterals = new[] { env.MachineName, UserOnly(env.UserName) };
        var captured = new List<CapturedQuery>();
        foreach (var query in preflight)
        {
            var spec = query.Spec;
            try
            {
                captured.Add(await ExecuteAsync(spec, selected, options, pendingDir, snapshotId, env, ledger, identityLiterals, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                ledger.Append("aborted", new JsonObject { ["query"] = spec.Id, ["reason"] = ex.Message, ["type"] = ex.GetType().Name });
                var aborted = Path.Combine(snapshotDir, "aborted");
                Directory.Move(pendingDir, aborted);
                WriteJson(Path.Combine(snapshotDir, "ABORTED.json"), new JsonObject
                {
                    ["snapshotId"] = snapshotId,
                    ["failedQuery"] = spec.Id,
                    ["reason"] = ex.Message,
                    ["statement"] = "Not promoted. The files in aborted/ are partial evidence of a failed batch and must not be used as a fixture pool.",
                });
                var code = ex is CaptureAbortException abort ? abort.ExitCode : 5;
                return new CaptureOutcome(code, snapshotDir, $"{spec.Id} failed: {ex.Message}");
            }
        }

        var temporal = TemporalReport.Build([.. captured.Select(c => (c.Spec.Id, c.Started, c.Ended, c.WindowTo))]);
        WriteJson(Path.Combine(snapshotDir, "temporal-report.json"), temporal.ToJson());
        ledger.Append("temporalReport", new JsonObject { ["windowEndSpreadSeconds"] = temporal.WindowEndSpread.TotalSeconds, ["totalElapsedSeconds"] = temporal.TotalElapsed.TotalSeconds });
        if (temporal.WindowEndSpread > options.MaxWindowEndSpread)
        {
            ledger.Append("aborted", new JsonObject { ["reason"] = "window-end spread above the limit" });
            Directory.Move(pendingDir, Path.Combine(snapshotDir, "aborted"));
            WriteJson(Path.Combine(snapshotDir, "ABORTED.json"), new JsonObject
            {
                ["snapshotId"] = snapshotId,
                ["reason"] = $"window ends are {temporal.WindowEndSpread.TotalSeconds:F1} s apart; the limit is {options.MaxWindowEndSpread.TotalSeconds:F0} s. Not promoted.",
            });
            return new CaptureOutcome(6, snapshotDir, $"temporal spread {temporal.WindowEndSpread.TotalSeconds:F1} s exceeds {options.MaxWindowEndSpread.TotalSeconds:F0} s; the snapshot was not promoted");
        }

        var fixturesDir = Path.Combine(snapshotDir, "fixtures");
        Directory.Move(pendingDir, fixturesDir);
        var scanHits = captured.Sum(c => c.SecretHits.Count);
        ledger.Append("promoted", new JsonObject { ["fixtures"] = captured.Count, ["secretPatternHits"] = scanHits });
        WriteIndex(snapshotDir, snapshotId, planHash, selected, captured, env, temporal);
        WriteSums(snapshotDir);
        return new CaptureOutcome(scanHits > 0 ? 3 : 0, snapshotDir,
            scanHits > 0 ? $"{captured.Count} fixtures promoted; {scanHits} credential-shaped hit(s) need review before any use or distribution" : $"{captured.Count} fixtures promoted");
    }

    private sealed record CapturedQuery(
        QuerySpec Spec, DateTimeOffset Started, DateTimeOffset Ended, DateTimeOffset WindowTo, ResultFacts Facts, string FileName, string FixtureId,
        string Sha256, string OutputSha256, string FileSha256, IReadOnlyList<ScanHit> Hits, ToolCallResult Result)
    {
        public IReadOnlyList<string> SecretHits => [.. Hits.Where(h => h.Category == ScanCategory.Secret).Select(h => h.PatternId)];
    }

    private async Task<CapturedQuery> ExecuteAsync(
        QuerySpec spec, IReadOnlyList<QuerySpec> selected, CaptureOptions options, string pendingDir, string snapshotId, EnvironmentInfo env,
        CaptureLedger ledger, string[] identityLiterals, CancellationToken ct)
    {
        var arguments = spec.Arguments;
        CapturePlan.Authorise(selected, spec.Id, spec.Tool, arguments);
        var staticManifest = manifests.ManifestOf(spec.Tool);

        // The collector is created only now, for a query that passed every gate.
        var tool = collectors.Create(spec.Tool);
        if (ManifestFingerprint.Of(tool.Manifest) != ManifestFingerprint.Of(staticManifest))
        {
            throw new CaptureAbortException($"the manifest of the real '{spec.Tool}' differs from the reviewed one.");
        }

        var toolArguments = ToolArguments.FromJson(arguments);
        if (bOps.Runtime.ArgumentSchema.Validate(tool.Manifest.Parameters, toolArguments) is { } invalid)
        {
            throw new CaptureAbortException($"arguments rejected by the runtime validator: {invalid.Message}");
        }

        var decision = policy.Evaluate(new PolicyContext(NodeId.Local, new PackageId("bops.packages.system.windows"), PackageTrustLevel.Official,
            tool.Manifest, toolArguments, ActorIdentity.FromOperatingSystemUser("replay-fixture-capture")));
        if (decision.Mode != PolicyMode.Automatic || tool.Manifest.Risk != RiskLevel.Read)
        {
            throw new CaptureAbortException($"policy refused '{spec.Tool}' ({decision.Mode}): {decision.Reason}");
        }

        var timeout = tool.Manifest.RequestedExecutionTimeout is { } requested && requested > options.CallTimeout ? requested : options.CallTimeout;
        ledger.Append("queryStarted", new JsonObject { ["query"] = spec.Id, ["tool"] = spec.Tool, ["arguments"] = JsonNode.Parse(CapturePlan.Canonical(arguments)) });
        var started = clock.GetUtcNow();
        ToolCallResult result;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            try
            {
                result = await tool.ExecuteAsync(toolArguments, cts.Token).WaitAsync(timeout + TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new CaptureAbortException($"the collector did not return a result ({ex.GetType().Name}: {ex.Message})", 5);
            }
        }

        var ended = clock.GetUtcNow();
        var facts = ResultChecks.Read(spec, result);
        var violations = ResultChecks.Violations(spec, result, facts, started, ended);
        ledger.Append("queryCompleted", new JsonObject
        {
            ["query"] = spec.Id,
            ["outcome"] = result.Outcome.ToString(),
            ["completeness"] = result.Completeness.ToString(),
            ["outputSha256"] = CapturePlan.Sha256(result.Output ?? string.Empty),
            ["outputBytes"] = Encoding.UTF8.GetByteCount(result.Output ?? string.Empty),
            ["violations"] = new JsonArray(violations.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        });
        if (violations.Count > 0)
        {
            throw new CaptureAbortException("unexpected result: " + string.Join(" | ", violations));
        }

        var hits = SecretScanner.Scan(ScanText(result), identityLiterals);
        var effective = spec.EffectiveArguments;
        var provenance = new JsonObject
        {
            ["originScenario"] = "T02",
            ["sourceTaskId"] = $"capture:{snapshotId}:{spec.Id}",
            ["kind"] = "controlled-capture",
            ["snapshotId"] = snapshotId,
            ["queryId"] = spec.Id,
            ["gap"] = spec.Gap,
            ["queryJson"] = JsonNode.Parse(CapturePlan.Canonical(arguments)),
            ["effectiveArguments"] = JsonNode.Parse(CapturePlan.Canonical(effective)),
            ["startedAtUtc"] = started.ToString("O", CultureInfo.InvariantCulture),
            ["endedAtUtc"] = ended.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = (long)(ended - started).TotalMilliseconds,
            ["windowFromUtc"] = facts!.WindowFrom!.Value.ToString("O", CultureInfo.InvariantCulture),
            ["windowToUtc"] = facts.WindowTo!.Value.ToString("O", CultureInfo.InvariantCulture),
            ["status"] = facts.Status,
            ["complete"] = facts.Complete,
            ["truncated"] = facts.Truncated,
            ["toolCompleteness"] = result.Completeness.ToString(),
            ["limitCoverage"] = ResultChecks.LimitCoverage(spec, facts),
            ["identity"] = IdentityGate.ToJson(env),
            ["binaries"] = JsonSerializer.SerializeToNode(env.Binaries),
            ["manifestSha256"] = ManifestFingerprint.Of(tool.Manifest),
            ["atomicSnapshot"] = false,
        };
        var path = FixtureWriter.Write(pendingDir, spec, result, provenance, hits.Where(h => h.Category == ScanCategory.Secret).Select(h => h.PatternId));
        var fixture = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
        return new CapturedQuery(spec, started, ended, facts.WindowTo.Value, facts, Path.GetFileName(path), fixture["id"]!.GetValue<string>(),
            fixture["sha256"]!.GetValue<string>(), fixture["outputSha256"]!.GetValue<string>(), Sha256File(path), hits, result);
    }

    /// <summary>Everything a fixture exposes that a scan must read: the output, the error text and the facts, in their JSON form.</summary>
    internal static string ScanText(ToolCallResult result) =>
        string.Join("\n", [result.Output, result.ErrorMessage, FixtureWriter.ResultToJson(result)["Facts"]!.ToJsonString()]);

    internal static string UserOnly(string account) => account.Contains('\\') ? account[(account.LastIndexOf('\\') + 1)..] : account;

    private static JsonObject EnvironmentJson(EnvironmentInfo env, string snapshotId, DateTimeOffset startedAt) => new()
    {
        ["snapshotId"] = snapshotId,
        ["startedAtUtc"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
        ["sensitive"] = "contains the account and machine name of the capture; keep with the snapshot, do not publish",
        ["user"] = env.UserName,
        ["userSid"] = env.UserSid,
        ["machineName"] = env.MachineName,
        ["isElevated"] = env.IsElevated,
        ["isAdministratorRole"] = env.IsAdministratorRole,
        ["integrityLevel"] = env.IntegrityLevel,
        ["integrityRid"] = env.IntegrityRid is { } rid ? $"0x{rid:X4}" : null,
        ["elevationType"] = env.ElevationType,
        ["identityGate"] = IdentityGate.ToJson(env),
        ["os"] = env.OsDescription,
        ["dotnet"] = env.DotNetVersion,
        ["processId"] = env.ProcessId,
        ["binaries"] = JsonSerializer.SerializeToNode(env.Binaries),
    };

    private static JsonObject PlanJson(IReadOnlyList<QuerySpec> selected, string planHash, IReadOnlyList<PreflightQuery> preflight, CaptureOptions options) => new()
    {
        ["planVersion"] = CapturePlan.VersionOf(options.PlanName),
        ["planName"] = options.PlanName,
        ["planSha256"] = planHash,
        ["maxWindowEndSpreadSeconds"] = options.MaxWindowEndSpread.TotalSeconds,
        ["queries"] = new JsonArray(preflight.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Spec.Id,
            ["tool"] = p.Spec.Tool,
            ["optional"] = p.Spec.Optional,
            ["arguments"] = JsonNode.Parse(CapturePlan.Canonical(p.Spec.Arguments)),
            ["effectiveArguments"] = JsonNode.Parse(CapturePlan.Canonical(p.Spec.EffectiveArguments)),
            ["requestedWindowMinutes"] = p.Spec.RequestedWindow.TotalMinutes,
            ["gap"] = p.Spec.Gap,
            ["manifestSha256"] = p.ManifestSha256,
            ["policy"] = p.PolicyMode,
        }).ToArray()),
    };

    private void WriteIndex(string snapshotDir, string snapshotId, string planHash, IReadOnlyList<QuerySpec> selected, IReadOnlyList<CapturedQuery> captured, EnvironmentInfo env, TemporalReport temporal)
    {
        var index = new JsonObject
        {
            ["snapshotId"] = snapshotId,
            ["scenario"] = "T02",
            ["kind"] = "controlled-capture",
            ["atomicSnapshot"] = false,
            ["planSha256"] = planHash,
            ["capturedBy"] = IdentityGate.ToJson(env),
            ["windowEndSpreadSeconds"] = Math.Round(temporal.WindowEndSpread.TotalSeconds, 3),
            ["totalElapsedSeconds"] = Math.Round(temporal.TotalElapsed.TotalSeconds, 3),
            ["fixtures"] = new JsonArray(captured.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.FixtureId,
                ["file"] = c.FileName,
                ["tool"] = c.Spec.Tool,
                ["arguments"] = JsonNode.Parse(CapturePlan.Canonical(c.Spec.Arguments)),
                ["effectiveArguments"] = JsonNode.Parse(CapturePlan.Canonical(c.Spec.EffectiveArguments)),
                ["windowFromUtc"] = c.Facts.WindowFrom!.Value.ToString("O", CultureInfo.InvariantCulture),
                ["windowToUtc"] = c.WindowTo.ToString("O", CultureInfo.InvariantCulture),
                ["status"] = c.Facts.Status,
                ["complete"] = c.Facts.Complete,
                ["truncated"] = c.Facts.Truncated,
                ["completeness"] = c.Result.Completeness.ToString(),
                ["outputChars"] = c.Result.Output?.Length ?? 0,
                ["sha256"] = c.Sha256,
                ["outputSha256"] = c.OutputSha256,
                ["fileSha256"] = c.FileSha256,
                ["secretScanHits"] = new JsonArray(c.SecretHits.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
                ["scan"] = SecretScanner.ToJson(c.Hits),
            }).ToArray()),
        };
        WriteJson(Path.Combine(snapshotDir, "index.json"), index);
    }

    private static void WriteSums(string snapshotDir)
    {
        var lines = Directory.EnumerateFiles(snapshotDir, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).Equals("SHA256SUMS.txt", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => $"{Sha256File(p)}  {Path.GetRelativePath(snapshotDir, p).Replace('\\', '/')}");
        File.WriteAllText(Path.Combine(snapshotDir, "SHA256SUMS.txt"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    internal static string Sha256File(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal static void WriteJson(string path, JsonNode node) =>
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
}
