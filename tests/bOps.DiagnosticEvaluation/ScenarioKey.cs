// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace bOps.DiagnosticEvaluation;

/// <summary>Selects typed facts by type and either an exact key, a key prefix or a set of keys (no selector = every key).</summary>
internal sealed record FactSelector(string Type, string? Key = null, string? KeyPrefix = null, IReadOnlyList<string>? KeyIn = null)
{
    public bool Matches(RunFact fact) =>
        string.Equals(fact.Type, Type, StringComparison.Ordinal)
        && (Key is null || string.Equals(fact.Key, Key, StringComparison.Ordinal))
        && (KeyPrefix is null || fact.Key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        && (KeyIn is null || KeyIn.Contains(fact.Key, StringComparer.Ordinal));
}

/// <summary>A family of counts the answer may talk about (real bugchecks, live dumps, unexpected shutdowns…). <c>ConfusableWith</c> names the
/// families whose count, given for this one, is a classification error rather than a stray number.</summary>
internal sealed record CountFamily(
    string Id,
    string Label,
    IReadOnlyList<FactSelector> Facts,
    IReadOnlyList<string> Terms,
    bool CrashLike = false,
    bool LiveDump = false,
    IReadOnlyList<string>? ConfusableWith = null);

/// <summary>When a Knowledge entry applies to a run: always, when one of the families has a non-zero count, when at least N families do,
/// or when one of the selected facts is non-zero. Conditions are alternatives.</summary>
internal sealed record KnowledgeEntryKey(
    string Skill,
    bool Always = false,
    IReadOnlyList<string>? AnyFamily = null,
    int? MinFamilies = null,
    IReadOnlyList<FactSelector>? AnyFact = null);

internal sealed record KnowledgeKey(IReadOnlyList<KnowledgeEntryKey> Entries);

/// <summary>
/// The versioned answer key of one or more scenarios: what the typed facts of a run mean and which Knowledge entries apply. It holds fact
/// selectors and wording only, never evidence text. The ground truth of a run is computed from the run's own facts through this key.
/// </summary>
internal sealed record ScenarioKey(
    string Schema,
    IReadOnlyList<string> Scenarios,
    IReadOnlyList<CountFamily> Families,
    IReadOnlyList<string> CrashWords,
    IReadOnlyList<string> Negations,
    IReadOnlyList<string> LimitationWords,
    IReadOnlyList<string> IdentifierContext,
    IReadOnlyList<FactSelector> WindowFacts,
    KnowledgeKey Knowledge)
{
    public const string SchemaId = "bops.diagnostic-evaluation-key/1";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The SHA-256 of the key file bytes, recorded with every result so a score names the key that produced it.</summary>
    [JsonIgnore]
    public string Sha256 { get; private init; } = string.Empty;

    public static ScenarioKey Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var key = JsonSerializer.Deserialize<ScenarioKey>(bytes, Options)
            ?? throw new InvalidDataException($"Empty scenario key: {path}");
        if (!string.Equals(key.Schema, SchemaId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported scenario key schema '{key.Schema}' in {path}");
        }

        return key with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    }

    /// <summary>Loads every <c>*.json</c> key of a directory and indexes it by scenario id; a scenario named twice is an error.</summary>
    public static IReadOnlyDictionary<string, ScenarioKey> LoadDirectory(string directory)
    {
        var byScenario = new Dictionary<string, ScenarioKey>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var key = Load(file);
            foreach (var scenario in key.Scenarios)
            {
                if (!byScenario.TryAdd(scenario, key))
                {
                    throw new InvalidDataException($"Scenario {scenario} is defined by more than one key in {directory}");
                }
            }
        }

        return byScenario;
    }
}
