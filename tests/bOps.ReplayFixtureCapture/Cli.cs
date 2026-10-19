// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Policy;

namespace bOps.ReplayFixtureCapture;

/// <summary>
/// The command line. Only `capture` can read the machine, and only with --enable-capture, the hash of the reviewed plan and an output root outside
/// Git. `verify`, `plan`, `scan`, `suggest-rules`, `minimize` and `check-snapshot` never construct a collector (they are given none).
/// </summary>
internal sealed class CliApp(
    TextWriter output,
    TextWriter error,
    IEnvironmentProbe environment,
    IManifestSource manifests,
    Func<ICollectorSource> collectorFactory,
    IPolicyEngine policy,
    TimeProvider clock,
    Func<string> randomSuffix,
    string executablePath)
{
    public const string Usage = """
        bOps.ReplayFixtureCapture: controlled, read-only capture of T02-B / T02-C replay snapshots.
          Select --plan T02-C for the independent version-2 plan (default: T02-B).
          verify  [--include-optional OPT1,OPT2] [--out-root <dir>] [--json]
                    Checks the configuration WITHOUT reading the machine: plan, manifests, argument validation, read-only policy, identity/elevation,
                    output location. Prints the plan hash and the exact capture command. Default command.
          capture --enable-capture --plan-sha256 <hash> --out-root <dir> [--include-optional ...] [--max-window-end-spread-seconds 300] [--call-timeout-seconds 30]
                    Runs the reviewed plan against the real collectors. Needs all three of the enabling arguments.
          scan    --snapshot <dir>              Re-scans a captured snapshot for credential-shaped and identifying strings (writes a report; payloads untouched).
          suggest-rules --snapshot <dir>        Prints a starter minimization rule set from the snapshot's own environment record.
          minimize --snapshot <dir> --rules <file> [--verify]
                    Writes (or with --verify, rebuilds and compares) a derived minimized copy next to the snapshot. Originals are never modified.
          check-snapshot --snapshot <dir>       Verifies SHA256SUMS, the ledger hash chain and that the fixtures are read-only.
          validate-snapshot --snapshot <dir> [--max-window-end-spread-seconds 300] [--json]
                    Offline validation of a captured snapshot: completeness of the plan, temporal coherence, hashes, Partial/Truncated outputs,
                    sensitive data and the theoretical coverage of the T02 calls that were unsupported. Writes reports/validation-*.json.
          Also accepted by verify and capture: --require-elevation-type Limited (refuse a token whose UAC elevation type is another).
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var command = args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal) ? "verify" : args[0];
        var rest = args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal) ? args : args[1..];
        try
        {
            var options = Options.Parse(rest);
            switch (command)
            {
                case "verify":
                case "plan":
                    return Verify(options);
                case "capture":
                    return await CaptureAsync(options, ct).ConfigureAwait(false);
                case "scan":
                    return Scan(options);
                case "suggest-rules":
                    await output.WriteLineAsync(Minimizer.Describe(Minimizer.Suggest(options.Required("--snapshot")))).ConfigureAwait(false);
                    return 0;
                case "minimize":
                    return Minimize(options);
                case "check-snapshot":
                    return CheckSnapshot(options);
                case "validate-snapshot":
                    return ValidateSnapshot(options);
                default:
                    await error.WriteLineAsync($"unknown command '{command}'.\n{Usage}").ConfigureAwait(false);
                    return 2;
            }
        }
        catch (CaptureAbortException ex)
        {
            await error.WriteLineAsync("REFUSED: " + ex.Message).ConfigureAwait(false);
            return ex.ExitCode;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or UnauthorisedQueryException or IOException or JsonException or KeyNotFoundException)
        {
            await error.WriteLineAsync("ERROR: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private int Verify(Options o)
    {
        var planName = o.Value("--plan") ?? "T02-B";
        var selected = CapturePlan.Select(o.List("--include-optional"), planName);
        var hash = CapturePlan.Hash(selected, planName);
        var env = environment.Probe();
        var preflight = Preflight.Run(selected, manifests, policy);
        var problems = new List<string>();
        problems.AddRange(IdentityGate.Problems(env).Select(p => "identity gate: " + p));
        var requiredType = planName == "T02-C" ? "Limited" : o.Value("--require-elevation-type");
        if (requiredType is not null && !string.Equals(env.ElevationType, requiredType, StringComparison.Ordinal))
        {
            problems.Add($"identity gate: the token's elevation type is '{env.ElevationType}', '{requiredType}' was required");
        }

        problems.AddRange(preflight.Where(p => !p.Ok).Select(p => $"{p.Spec.Id}: risk-read={p.ReadOnlyRisk} validation='{p.ValidationError}' policy={p.PolicyMode}"));
        if (o.Value("--out-root") is { } root)
        {
            problems.AddRange(OutputRootGuard.Problems(root, $"{planName}-<id>"));
        }

        var command = $"dotnet \"{executablePath}\" capture --enable-capture --plan-sha256 {hash} --out-root \"{o.Value("--out-root") ?? "<dir outside the repository>"}\""
            + (planName != "T02-B" ? $" --plan {planName}" : string.Empty)
            + (o.List("--include-optional").Count > 0 ? $" --include-optional {string.Join(",", o.List("--include-optional"))}" : string.Empty)
            + (requiredType is not null ? $" --require-elevation-type {requiredType}" : string.Empty);

        if (o.Flag("--json"))
        {
            output.WriteLine(new JsonObject
            {
                ["planName"] = planName,
                ["planVersion"] = CapturePlan.VersionOf(planName),
                ["planSha256"] = hash,
                ["ok"] = problems.Count == 0,
                ["problems"] = new JsonArray(problems.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
                ["identity"] = IdentityGate.ToJson(env),
                ["captureCommand"] = command,
                ["queries"] = new JsonArray(preflight.Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Spec.Id,
                    ["tool"] = p.Spec.Tool,
                    ["optional"] = p.Spec.Optional,
                    ["arguments"] = JsonNode.Parse(CapturePlan.Canonical(p.Spec.Arguments)),
                    ["effectiveArguments"] = JsonNode.Parse(CapturePlan.Canonical(p.Spec.EffectiveArguments)),
                    ["policy"] = p.PolicyMode,
                    ["validation"] = p.ValidationError ?? "ok",
                }).ToArray()),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return problems.Count == 0 ? 0 : 2;
        }

        output.WriteLine("VERIFY (no collector is constructed; nothing is read from the machine)");
        output.WriteLine($"  identity: {env.UserName}   SID: {env.UserSid}");
        output.WriteLine($"  token: integrity {env.IntegrityLevel} (0x{env.IntegrityRid ?? 0:X4})   elevation type: {env.ElevationType}   elevated: {env.IsElevated}   administrator role: {env.IsAdministratorRole}");
        output.WriteLine($"  identity gate: {(IdentityGate.Problems(env).Count == 0 ? "PASS (Medium integrity, non-elevated)" : "REFUSED")}");
        output.WriteLine($"  plan: version {CapturePlan.VersionOf(planName)}, {selected.Count} queries ({selected.Count(q => q.Optional)} optional enabled; optional available: {string.Join(", ", CapturePlan.OptionalIds)})");
        foreach (var p in preflight)
        {
            output.WriteLine($"  {p.Spec.Id,-5} {p.Spec.Tool,-17} {(p.Spec.Optional ? "[optional] " : string.Empty)}args {CapturePlan.Canonical(p.Spec.Arguments)}");
            output.WriteLine($"        effective {CapturePlan.Canonical(p.Spec.EffectiveArguments)}   window {p.Spec.RequestedWindow}   manifest risk-read={p.ReadOnlyRisk} validation={(p.ValidationError ?? "ok")} policy={p.PolicyMode}");
            output.WriteLine($"        expects status {string.Join("|", p.Spec.Expect.AllowedStatuses)}, completeness {string.Join("|", p.Spec.Expect.AllowedCompleteness)}, truncation {p.Spec.Expect.Truncation}, budget {p.Spec.Expect.OutputBudgetBytes} B");
        }

        output.WriteLine($"  planSha256: {hash}");
        output.WriteLine(problems.Count == 0 ? "  RESULT: configuration OK" : "  RESULT: NOT OK\n    - " + string.Join("\n    - ", problems));
        output.WriteLine("  capture command (not executed):");
        output.WriteLine("    " + command);
        return problems.Count == 0 ? 0 : 2;
    }

    private async Task<int> CaptureAsync(Options o, CancellationToken ct)
    {
        if (!o.Flag("--enable-capture"))
        {
            throw new CaptureAbortException("capture needs --enable-capture. Run `verify` first; nothing was read.", 2);
        }

        var runner = new CaptureRunner(environment, manifests, new LazyCollectorSource(collectorFactory), policy, clock, randomSuffix);
        var outcome = await runner.RunAsync(
            new CaptureOptions(
                o.Required("--out-root"), o.Required("--plan-sha256"), o.List("--include-optional"),
                TimeSpan.FromSeconds(o.Number("--max-window-end-spread-seconds", 300)), TimeSpan.FromSeconds(o.Number("--call-timeout-seconds", 30)),
                o.Value("--require-elevation-type"), o.Value("--plan") ?? "T02-B"),
            ct).ConfigureAwait(false);
        (outcome.ExitCode is 0 or 3 ? output : error).WriteLine($"{(outcome.ExitCode is 0 or 3 ? "CAPTURE" : "ABORTED")}: {outcome.Message}\n  snapshot: {outcome.SnapshotDirectory}");
        return outcome.ExitCode;
    }

    private int Scan(Options o)
    {
        var snapshot = o.Required("--snapshot");
        var literals = new List<string>();
        var environmentFile = Path.Combine(snapshot, "environment.json");
        if (File.Exists(environmentFile))
        {
            var env = JsonNode.Parse(File.ReadAllText(environmentFile, Encoding.UTF8))!.AsObject();
            literals.Add(env["machineName"]!.GetValue<string>());
            literals.Add(CaptureRunner.UserOnly(env["user"]!.GetValue<string>()));
        }

        var report = new JsonArray();
        var secrets = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(snapshot, "fixtures"), "F*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var fixture = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
            var result = fixture["result"]!.AsObject();
            var text = string.Join("\n", [result["Output"]?.GetValue<string>(), result["ErrorMessage"]?.GetValue<string>(), result["Facts"]!.ToJsonString()]);
            var hits = SecretScanner.Scan(text, literals);
            secrets += hits.Count(h => h.Category == ScanCategory.Secret);
            report.Add(new JsonObject { ["file"] = Path.GetFileName(path), ["hits"] = SecretScanner.ToJson(hits) });
        }

        var reportsDir = Path.Combine(snapshot, "reports");
        Directory.CreateDirectory(reportsDir);
        var name = $"scan-{clock.GetUtcNow():yyyyMMddTHHmmssZ}.json";
        CaptureRunner.WriteJson(Path.Combine(reportsDir, name), new JsonObject
        {
            ["note"] = "Hits carry the pattern, counts and short digests of distinct values; never the matched text. Fixture payloads were only read.",
            ["credentialShapedHits"] = secrets,
            ["files"] = report,
        });
        output.WriteLine($"SCAN: {secrets} credential-shaped hit(s); report reports/{name}");
        return secrets > 0 ? 3 : 0;
    }

    private int Minimize(Options o)
    {
        var snapshot = o.Required("--snapshot");
        var rules = MinimizationRules.Parse(File.ReadAllText(o.Required("--rules"), Encoding.UTF8));
        if (o.Flag("--verify"))
        {
            var problems = Minimizer.Verify(snapshot, rules);
            output.WriteLine(problems.Count == 0 ? $"MINIMIZATION VERIFIED (rules {rules.Sha256[..12]})" : "MINIMIZATION NOT VERIFIED:\n  - " + string.Join("\n  - ", problems));
            return problems.Count == 0 ? 0 : 2;
        }

        var directory = Minimizer.Write(snapshot, rules);
        output.WriteLine($"MINIMIZED copy written to {directory} (originals untouched; derived copy, not a valid replay pool)");
        return 0;
    }

    private int CheckSnapshot(Options o)
    {
        var snapshot = o.Required("--snapshot");
        var problems = SnapshotIntegrity.Problems(snapshot);
        output.WriteLine(problems.Count == 0 ? "SNAPSHOT OK" : "SNAPSHOT NOT OK:\n  - " + string.Join("\n  - ", problems));
        return problems.Count == 0 ? 0 : 2;
    }

    private int ValidateSnapshot(Options o)
    {
        var snapshot = o.Required("--snapshot");
        var report = SnapshotValidator.Validate(snapshot, TimeSpan.FromSeconds(o.Number("--max-window-end-spread-seconds", 300)));
        var reportsDir = Path.Combine(snapshot, "reports");
        Directory.CreateDirectory(reportsDir);
        var name = $"validation-{clock.GetUtcNow():yyyyMMddTHHmmssZ}.json";
        CaptureRunner.WriteJson(Path.Combine(reportsDir, name), report.ToJson());
        output.WriteLine(o.Flag("--json") ? report.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : report.ToText());
        output.WriteLine($"validation report: reports/{name}");
        return report.HasFail ? 2 : report.HasCredentialHits ? 3 : 0;
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"unexpected argument '{args[i]}'.");
                }

                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                o.values[args[i]] = hasValue ? args[++i] : null;
            }

            return o;
        }

        public bool Flag(string name) => values.ContainsKey(name);

        public string? Value(string name) => values.GetValueOrDefault(name);

        public string Required(string name) => Value(name) ?? throw new ArgumentException($"missing {name}.");

        public IReadOnlyList<string> List(string name) =>
            Value(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        public double Number(string name, double fallback) =>
            Value(name) is { } text ? double.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : fallback;
    }
}
