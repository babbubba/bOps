// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.ReplayFixtureCapture;

/// <summary>An append-only, hash-chained record of what the capture decided and did. It never contains a result payload, only digests.</summary>
internal sealed class CaptureLedger(string path, TimeProvider clock)
{
    private string previous = new('0', 64);
    private int sequence;

    public void Append(string eventType, JsonObject data)
    {
        var entry = new JsonObject
        {
            ["seq"] = sequence++,
            ["utc"] = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["event"] = eventType,
            ["data"] = data,
            ["prev"] = previous,
        };
        var hash = CapturePlan.Sha256(entry.ToJsonString());
        entry["hash"] = hash;
        previous = hash;
        File.AppendAllText(path, entry.ToJsonString() + "\n", new UTF8Encoding(false));
    }

    /// <summary>True when every entry's hash and link are intact.</summary>
    public static bool Verify(string path)
    {
        var prev = new string('0', 64);
        var expectedSeq = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var node = JsonNode.Parse(line)!.AsObject();
            var claimed = node["hash"]!.GetValue<string>();
            node.Remove("hash");
            if (node["prev"]!.GetValue<string>() != prev || node["seq"]!.GetValue<int>() != expectedSeq++ || CapturePlan.Sha256(node.ToJsonString()) != claimed)
            {
                return false;
            }

            prev = claimed;
        }

        return true;
    }
}

internal static class OutputRootGuard
{
    /// <summary>
    /// The snapshot directory must be new, outside every Git working tree and must not be an existing fixture pool: a snapshot never overwrites
    /// and never mixes with earlier acquisitions.
    /// </summary>
    public static IReadOnlyList<string> Problems(string outRoot, string snapshotId)
    {
        var problems = new List<string>();
        var full = Path.GetFullPath(outRoot);
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                problems.Add($"'{full}' is inside the Git working tree '{dir.FullName}'; snapshots are kept outside the repository");
                break;
            }
        }

        if (Directory.Exists(full)
            && (Directory.Exists(Path.Combine(full, "fixtures")) || Directory.EnumerateFiles(full, "F*.json").Any() || File.Exists(Path.Combine(full, "scenarios.json"))))
        {
            problems.Add($"'{full}' already is a fixture pool; use a dedicated parent directory so acquisitions are never mixed");
        }

        if (Directory.Exists(Path.Combine(full, snapshotId)))
        {
            problems.Add($"snapshot directory '{Path.Combine(full, snapshotId)}' already exists; nothing is overwritten");
        }

        return problems;
    }
}

/// <summary>Writes one captured result in the replay fixture format (FixtureStore.Load reads it), plus capture provenance. Payloads are never rewritten afterwards.</summary>
internal static class FixtureWriter
{
    public static JsonObject ResultToJson(ToolCallResult result) => new()
    {
        ["Outcome"] = (int)result.Outcome,
        ["Output"] = result.Output,
        ["ErrorMessage"] = result.ErrorMessage,
        ["FailureKind"] = (int)result.FailureKind,
        ["Completeness"] = (int)result.Completeness,
        ["Facts"] = new JsonArray(result.Facts.Select(f => (JsonNode)new JsonObject
        {
            ["Type"] = f.Type,
            ["Key"] = f.Key,
            ["ValueType"] = (int)f.ValueType,
            ["Value"] = f.Value?.DeepClone(),
        }).ToArray()),
    };

    /// <summary>SHA-256 over the canonical form of tool + arguments + result.</summary>
    public static string ContentSha256(string tool, JsonObject arguments, JsonObject result) =>
        CapturePlan.Sha256(new JsonObject
        {
            ["arguments"] = JsonNode.Parse(CapturePlan.Canonical(arguments)),
            ["result"] = JsonNode.Parse(CapturePlan.Canonical(result)),
            ["tool"] = tool,
        }.ToJsonString());

    public static string FixtureId(QuerySpec spec) => $"FB-{spec.Id}-{spec.Tool}";

    public static string Write(string directory, QuerySpec spec, ToolCallResult result, JsonObject provenance, IEnumerable<string> secretHits)
    {
        var resultJson = ResultToJson(result);
        var fixtureId = provenance["snapshotId"]?.GetValue<string>().StartsWith("T02-C-", StringComparison.Ordinal) == true
            ? $"FC-{spec.Id}-{spec.Tool}" : FixtureId(spec);
        var fixture = new JsonObject
        {
            ["id"] = fixtureId,
            ["tool"] = spec.Tool,
            ["arguments"] = spec.Arguments,
            ["result"] = resultJson,
            ["provenance"] = provenance,
            ["sha256"] = ContentSha256(spec.Tool, spec.Arguments, resultJson),
            ["outputSha256"] = CapturePlan.Sha256(result.Output ?? string.Empty),
            ["secretScanHits"] = new JsonArray(secretHits.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
        };
        var path = Path.Combine(directory, fixtureId + ".json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(new UTF8Encoding(false).GetBytes(fixture.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        }

        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }
}
