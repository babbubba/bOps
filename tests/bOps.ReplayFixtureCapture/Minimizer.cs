// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace bOps.ReplayFixtureCapture;

internal sealed record MinimizationRule(string Id, string Kind, string Value, string Replacement, bool IgnoreCase);

internal sealed record MinimizationRules(IReadOnlyList<MinimizationRule> Rules, string Sha256)
{
    private static readonly Regex SafeReplacement = new(@"^[A-Za-z0-9 _<>.:/$\-]*$", RegexOptions.CultureInvariant);

    public static MinimizationRules Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var rules = new List<MinimizationRule>();
        foreach (var node in root["rules"]!.AsArray())
        {
            var o = node!.AsObject();
            var rule = new MinimizationRule(
                o["id"]!.GetValue<string>(), o["kind"]!.GetValue<string>(), o["value"]?.GetValue<string>() ?? o["pattern"]!.GetValue<string>(),
                o["replacement"]!.GetValue<string>(), o["ignoreCase"]?.GetValue<bool>() ?? false);
            if (rule.Kind is not ("literal" or "regex"))
            {
                throw new FormatException($"rule '{rule.Id}': kind must be 'literal' or 'regex'.");
            }

            if (rule.Value.Length == 0 || !SafeReplacement.IsMatch(rule.Replacement))
            {
                throw new FormatException($"rule '{rule.Id}': empty match, or a replacement outside [A-Za-z0-9 _<>.:/$-] (it must stay valid inside a JSON string).");
            }

            rules.Add(rule);
        }

        if (rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count)
        {
            throw new FormatException("rule ids must be unique.");
        }

        return new MinimizationRules(rules, CapturePlan.Sha256(string.Join("\n", rules.Select(r => $"{r.Id}|{r.Kind}|{r.Value}|{r.Replacement}|{r.IgnoreCase}"))));
    }
}

/// <summary>
/// Produces a DERIVED copy of a snapshot with identifying strings replaced by fixed tokens. The originals are only read. The copy is for sharing and
/// inspection: its bytes differ from the evidence, so it is not a valid replay pool for measurement (output sizes and message grouping change).
/// Verifiable: the manifest records the rules hash and every original and minimized digest, and `Verify` rebuilds the copy in memory and compares.
/// </summary>
internal static class Minimizer
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string OutputDirectory(string snapshotDir, MinimizationRules rules) => Path.Combine(snapshotDir, "minimized", rules.Sha256[..12]);

    public sealed record Minimized(string FileName, string Text, JsonObject Entry);

    public static IReadOnlyList<Minimized> Build(string snapshotDir, MinimizationRules rules)
    {
        var result = new List<Minimized>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(snapshotDir, "fixtures"), "F*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var original = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
            var copy = (JsonObject)original.DeepClone();
            var counts = rules.Rules.ToDictionary(r => r.Id, _ => 0, StringComparer.Ordinal);
            var res = copy["result"]!.AsObject();
            var originalOutput = res["Output"]?.GetValue<string>();

            string? Apply(string? text, bool jsonText)
            {
                if (text is null)
                {
                    return null;
                }

                foreach (var rule in rules.Rules)
                {
                    text = ApplyRule(rule, text, jsonText, counts);
                }

                return text;
            }

            res["Output"] = Apply(originalOutput, jsonText: true);
            res["ErrorMessage"] = Apply(res["ErrorMessage"]?.GetValue<string>(), jsonText: false);
            foreach (var fact in res["Facts"]!.AsArray())
            {
                if (fact!["Value"] is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    fact["Value"] = Apply(s, jsonText: false);
                }
            }

            var skeletonEqual = SkeletonEqual(originalOutput, res["Output"]?.GetValue<string>());
            if (!skeletonEqual)
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)}: the minimized Output no longer has the structure of the original (a rule broke the JSON); nothing was written.");
            }

            var minimizedSha = FixtureWriter.ContentSha256(copy["tool"]!.GetValue<string>(), copy["arguments"]!.AsObject(), res);
            copy["secretScanHits"] = new JsonArray();
            copy["sha256"] = minimizedSha;
            copy["outputSha256"] = CapturePlan.Sha256(res["Output"]?.GetValue<string>() ?? string.Empty);
            var prov = copy["provenance"]!.AsObject();
            prov["minimized"] = new JsonObject
            {
                ["rulesSha256"] = rules.Sha256,
                ["originalFixtureSha256"] = original["sha256"]!.GetValue<string>(),
                ["originalFileSha256"] = CaptureRunner.Sha256File(path),
                ["replacements"] = JsonSerializer.SerializeToNode(counts),
                ["statement"] = "Derived copy. Identifying strings were replaced; the bytes are not the captured evidence and this copy is not a valid replay pool for measurement.",
            };
            var text = copy.ToJsonString(Indented);
            var entry = new JsonObject
            {
                ["file"] = Path.GetFileName(path),
                ["originalFileSha256"] = CaptureRunner.Sha256File(path),
                ["originalOutputSha256"] = original["outputSha256"]!.GetValue<string>(),
                ["minimizedOutputSha256"] = copy["outputSha256"]!.GetValue<string>(),
                ["minimizedFileSha256"] = CapturePlan.Sha256(text),
                ["replacements"] = JsonSerializer.SerializeToNode(counts),
                ["structurePreserved"] = true,
                ["residualScan"] = SecretScanner.ToJson(SecretScanner.Scan(CaptureRunner.ScanText(new bOps.Abstractions.ToolCallResult(
                    bOps.Abstractions.ToolOutcome.Success, res["Output"]?.GetValue<string>(), res["ErrorMessage"]?.GetValue<string>())))),
            };
            result.Add(new Minimized(Path.GetFileName(path), text, entry));
        }

        return result;
    }

    /// <summary>Writes the derived copy and its manifest next to (never over) the snapshot. Refuses to overwrite an existing derived copy.</summary>
    public static string Write(string snapshotDir, MinimizationRules rules)
    {
        var built = Build(snapshotDir, rules);
        var directory = OutputDirectory(snapshotDir, rules);
        if (Directory.Exists(directory))
        {
            throw new CaptureAbortException($"'{directory}' already exists; a derived copy is never overwritten.", 2);
        }

        Directory.CreateDirectory(Path.Combine(directory, "fixtures"));
        foreach (var item in built)
        {
            File.WriteAllText(Path.Combine(directory, "fixtures", item.FileName), item.Text, new UTF8Encoding(false));
        }

        var manifest = new JsonObject
        {
            ["rulesSha256"] = rules.Sha256,
            ["rules"] = new JsonArray(rules.Rules.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["kind"] = r.Kind, ["replacement"] = r.Replacement, ["ignoreCase"] = r.IgnoreCase }).ToArray()),
            ["note"] = "The matched values of the rules are deliberately not recorded here. Derived copy; the originals are untouched.",
            ["fixtures"] = new JsonArray(built.Select(b => (JsonNode)b.Entry.DeepClone()).ToArray()),
        };
        CaptureRunner.WriteJson(Path.Combine(directory, "minimization-manifest.json"), manifest);
        return directory;
    }

    /// <summary>Rebuilds the derived copy in memory and compares it with what is on disk. Returns the discrepancies (empty = verified).</summary>
    public static IReadOnlyList<string> Verify(string snapshotDir, MinimizationRules rules)
    {
        var problems = new List<string>();
        var directory = OutputDirectory(snapshotDir, rules);
        if (!Directory.Exists(directory))
        {
            return [$"no derived copy for these rules at '{directory}'"];
        }

        var built = Build(snapshotDir, rules);
        var onDisk = Directory.EnumerateFiles(Path.Combine(directory, "fixtures"), "F*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!onDisk.SequenceEqual(built.Select(b => b.FileName).Order(StringComparer.Ordinal)))
        {
            problems.Add("the set of minimized fixtures differs from the set of originals");
        }

        foreach (var item in built)
        {
            var file = Path.Combine(directory, "fixtures", item.FileName);
            if (!File.Exists(file) || File.ReadAllText(file, Encoding.UTF8) != item.Text)
            {
                problems.Add($"{item.FileName}: the minimized file is not what the rules produce from the original");
            }
        }

        var manifestPath = Path.Combine(directory, "minimization-manifest.json");
        if (!File.Exists(manifestPath))
        {
            problems.Add("minimization-manifest.json is missing");
        }
        else
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8))!.AsObject();
            if (manifest["rulesSha256"]?.GetValue<string>() != rules.Sha256)
            {
                problems.Add("the manifest records other rules");
            }

            var expected = new JsonArray(built.Select(b => (JsonNode)b.Entry.DeepClone()).ToArray()).ToJsonString();
            if (manifest["fixtures"]?.ToJsonString() != expected)
            {
                problems.Add("the manifest entries differ from the rebuilt ones");
            }
        }

        return problems;
    }

    private static string ApplyRule(MinimizationRule rule, string text, bool jsonText, Dictionary<string, int> counts)
    {
        var options = RegexOptions.CultureInvariant | (rule.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        string[] patterns = rule.Kind == "regex"
            ? [rule.Value]
            : jsonText
                ? [Regex.Escape(rule.Value), Regex.Escape(JsonEncoded(rule.Value))]
                : [Regex.Escape(rule.Value)];
        foreach (var pattern in patterns.Distinct(StringComparer.Ordinal))
        {
            text = Regex.Replace(text, pattern, m =>
            {
                counts[rule.Id]++;
                return rule.Kind == "regex" ? m.Result(rule.Replacement) : rule.Replacement;
            }, options);
        }

        return text;
    }

    private static string JsonEncoded(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value, Indented)[1..^1];

    /// <summary>True when the two texts parse to the same JSON structure with every string leaf treated as equal.</summary>
    internal static bool SkeletonEqual(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        JsonNode? original;
        try
        {
            original = JsonNode.Parse(a);
        }
        catch (JsonException)
        {
            // The original was not JSON (an error text): the minimized one must not have become JSON.
            try
            {
                JsonNode.Parse(b);
                return false;
            }
            catch (JsonException)
            {
                return true;
            }
        }

        try
        {
            return Skeleton(original) == Skeleton(JsonNode.Parse(b));
        }
        catch (JsonException)
        {
            // The original was JSON and the minimized text no longer is: a rule broke the structure.
            return false;
        }
    }

    private static string Skeleton(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.Select(p => JsonSerializer.Serialize(p.Key) + ":" + Skeleton(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Skeleton)) + "]",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => "\"s\"",
        JsonValue v => v.ToJsonString(),
        _ => "?",
    };

    /// <summary>A starter rule set from what the snapshot recorded about its own capture (machine and account). A reviewer must extend it with the scan's findings.</summary>
    public static JsonObject Suggest(string snapshotDir)
    {
        var environment = JsonNode.Parse(File.ReadAllText(Path.Combine(snapshotDir, "environment.json"), Encoding.UTF8))!.AsObject();
        var machine = environment["machineName"]!.GetValue<string>();
        var user = CaptureRunner.UserOnly(environment["user"]!.GetValue<string>());
        return new JsonObject
        {
            ["version"] = 1,
            ["note"] = "Starter rules from environment.json. Review the scan report and add rules for hosts, domain controllers, accounts and paths it lists.",
            ["rules"] = new JsonArray(
                new JsonObject { ["id"] = "machine-name", ["kind"] = "literal", ["value"] = machine, ["replacement"] = "<HOST>", ["ignoreCase"] = true },
                new JsonObject { ["id"] = "account-name", ["kind"] = "literal", ["value"] = user, ["replacement"] = "<USER>", ["ignoreCase"] = true },
                new JsonObject
                {
                    ["id"] = "user-profile-path",
                    ["kind"] = "regex",
                    ["pattern"] = @"([A-Za-z]:\\{1,2}Users\\{1,2})[^\\""\s]+",
                    ["replacement"] = "$1<USER>",
                    ["ignoreCase"] = true,
                }),
        };
    }

    public static string Describe(JsonObject o) => o.ToJsonString(Indented);
}
