// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The typed evidence facts <c>system.dump_analyze</c> emits beside its output (ADR-0050; ADR-0052 AF-04), read from the fields the
/// tool's own formatter already emitted. What the debugger attributed stays qualified in the fact type (<c>debugger-attributed-*</c>):
/// it says where the debugger found the failure, never that the named module caused it. A field the debugger did not print yields no
/// fact, a value too long to keep whole is skipped rather than cut, and nothing is emitted for an analysis that produced no evidence.
/// </summary>
public static class DumpAnalysisFacts
{
    /// <summary>The fact type: how complete the analysis is (<c>complete</c>, <c>partial</c> or <c>invalid</c>). The key is <see cref="StatusKey"/>.</summary>
    public const string AnalysisStatusType = "system.dump_analyze.analysis-status";

    /// <summary>The fact type: the analyzed dump's approved path. The key is <see cref="AnalyzedKey"/>.</summary>
    public const string DumpPathType = "system.dump_analyze.dump-path";

    /// <summary>The fact type: the bugcheck code the debugger reported (canonical hexadecimal). The key is <see cref="BugcheckKey"/>.</summary>
    public const string BugcheckCodeType = "system.dump_analyze.bugcheck-code";

    /// <summary>The fact type: the module the debugger's automated analysis named. The key is <see cref="ModuleKey"/>.</summary>
    public const string AttributedModuleType = "system.dump_analyze.debugger-attributed-module";

    /// <summary>The fact type: the failure bucket the debugger's automated analysis named. The key is <see cref="FailureBucketKey"/>.</summary>
    public const string AttributedFailureBucketType = "system.dump_analyze.debugger-attributed-failure-bucket";

    /// <summary>The fact type: the debugger's symbol resolution status. The key is <see cref="SymbolsKey"/>.</summary>
    public const string SymbolsStatusType = "system.dump_analyze.symbols-status";

    /// <summary>The key of <see cref="AnalysisStatusType"/>.</summary>
    public const string StatusKey = "status";

    /// <summary>The key of <see cref="DumpPathType"/>.</summary>
    public const string AnalyzedKey = "analyzed";

    /// <summary>The key of <see cref="BugcheckCodeType"/>.</summary>
    public const string BugcheckKey = "bugcheck";

    /// <summary>The key of <see cref="AttributedModuleType"/>.</summary>
    public const string ModuleKey = "module";

    /// <summary>The key of <see cref="AttributedFailureBucketType"/>.</summary>
    public const string FailureBucketKey = "failure-bucket";

    /// <summary>The key of <see cref="SymbolsStatusType"/>.</summary>
    public const string SymbolsKey = "symbols";

    /// <summary>The facts of one call: <paramref name="output"/> is the JSON <see cref="DumpAnalysisFormatting.Format(DumpAnalysisReport)"/> produced.</summary>
    public static IReadOnlyList<EvidenceFact> From(string output)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(output) as JsonObject;
        }
        catch (JsonException)
        {
            return [];
        }

        var status = Text(root?["status"]);
        if (root is null || status is not ("complete" or "partial" or "invalid"))
        {
            return [];
        }

        var facts = new List<EvidenceFact> { Fact(AnalysisStatusType, StatusKey, status, ToolParameterType.String)! };
        AddIfPresent(facts, Fact(DumpPathType, AnalyzedKey, Text(root["dump"]?["path"]), ToolParameterType.Path, DumpAnalysisLimits.PathCharacters));
        AddIfPresent(facts, Fact(BugcheckCodeType, BugcheckKey, Text(root["bugcheck"]?["code"]), ToolParameterType.String, 32));
        AddIfPresent(facts, Fact(AttributedModuleType, ModuleKey, Text(root["analysis"]?["moduleName"]), ToolParameterType.String));
        AddIfPresent(facts, Fact(AttributedFailureBucketType, FailureBucketKey, Text(root["analysis"]?["failureBucketId"]), ToolParameterType.String));
        AddIfPresent(facts, Fact(SymbolsStatusType, SymbolsKey, Text(root["symbols"]?["status"]), ToolParameterType.String, 32));
        return EvidenceFactBounds.Fit(facts);
    }

    private static void AddIfPresent(List<EvidenceFact> facts, EvidenceFact? fact)
    {
        if (fact is not null)
        {
            facts.Add(fact);
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static EvidenceFact? Fact(
        string type, string key, string? value, ToolParameterType valueType, int maximum = EvidenceFactBounds.MaximumValueCharacters) =>
        value is null || value.Length > maximum ? null : new EvidenceFact(type, key, valueType, JsonValue.Create(value)!);
}
