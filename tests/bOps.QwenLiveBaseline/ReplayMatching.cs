// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.QwenLiveBaseline;

/// <summary>
/// What the harness may assume about how ONE real tool reads its arguments, each fact verified against the production code and cited.
/// A tool without a spec gets literal matching only: no default is applied, a JSON null is never read as an absent property and no
/// limit is ever treated as non-binding unless the same literal rule that existed before (limit-covered) proves it.
/// </summary>
/// <param name="Tool">The canonical tool name (ordinal).</param>
/// <param name="NullIsAbsent">A JSON null argument is exactly an omitted one (ArgumentSchema skips null values; the tool's reader tests <c>json[name] is null</c>).</param>
/// <param name="Defaults">Values the tool applies when the argument is omitted, with the unconditional/conditional rule.</param>
/// <param name="CountFieldByMode">For the limit parameter: the output field that counts every entry the limit could cut, per mode ("" = mode-less).</param>
/// <param name="Source">Where the facts were verified.</param>
internal sealed record ToolArgumentSpec(
    string Tool,
    bool NullIsAbsent,
    IReadOnlyList<ArgumentDefault> Defaults,
    IReadOnlyDictionary<string, string> CountFieldByMode,
    string Source);

/// <summary>An omitted argument is read as <paramref name="Value"/>, unless any argument named in <paramref name="UnlessPresent"/> is supplied.</summary>
internal sealed record ArgumentDefault(string Name, JsonNode Value, params string[] UnlessPresent);

/// <summary>The verified argument semantics of the tools the replay may be asked to answer with a default-equivalent fixture.</summary>
internal static class ToolArgumentSpecs
{
    private static readonly IReadOnlyDictionary<string, ToolArgumentSpec> Specs = new[]
    {
        // SystemEventsArguments.TryRead + EvidenceModes.TryReadHorizon (mode default Raw, window default 60 minutes when no windowDays),
        // SystemEventsLimits.DefaultEvents = 50, DefaultOutputBytes = 32768. minSeverity has NO default (omitted = every severity, which is
        // NOT "warning" and is not proven equal to an explicit "verbose"). limit applies to Take(limit) after grouping (SystemEventFormatting).
        new ToolArgumentSpec(
            "system.events", NullIsAbsent: true,
            [
                new ArgumentDefault("mode", "raw"),
                new ArgumentDefault("windowMinutes", 60, "windowDays"),
                new ArgumentDefault("limit", 50),
                new ArgumentDefault("maxOutputBytes", 32768),
            ],
            new Dictionary<string, string> { ["aggregate"] = "observedGroups", ["raw"] = "observedEvents" },
            "SystemEventsArguments.cs; EvidenceMode.cs; SystemEventsLimits.cs; SystemEventFormatting.cs; SystemToolManifests.Events"),

        // StabilityArguments.TryRead: windowDays default 30, limit default 50 (StabilityLimits); no mode. groups.Take(limit) (StabilityFormatting).
        new ToolArgumentSpec(
            "system.stability", NullIsAbsent: true,
            [
                new ArgumentDefault("windowDays", 30),
                new ArgumentDefault("limit", 50),
            ],
            new Dictionary<string, string> { [string.Empty] = "observedGroups" },
            "StabilityEvidence.cs (StabilityArguments, StabilityLimits); StabilityFormatting.cs; SystemToolManifests.Stability"),

        // SystemCrashesArguments.TryRead: mode default Aggregate, sinceMinutes default 1440 when no sinceDays (SystemMaintenanceLimits), limit default 100.
        new ToolArgumentSpec(
            "system.crashes", NullIsAbsent: true,
            [
                new ArgumentDefault("mode", "aggregate"),
                new ArgumentDefault("sinceMinutes", 1440, "sinceDays"),
                new ArgumentDefault("limit", 100),
            ],
            new Dictionary<string, string> { ["aggregate"] = "observedGroups", ["raw"] = "observedItems" },
            "CrashEvidence.cs (SystemCrashesArguments, SystemCrashFormatting); SystemMaintenanceContracts.cs; SystemToolManifests.Crashes"),
    }.ToDictionary(s => s.Tool, StringComparer.Ordinal);

    public static ToolArgumentSpec? For(string tool) => Specs.GetValueOrDefault(tool);

    public static IEnumerable<ToolArgumentSpec> All => Specs.Values;
}

/// <summary>
/// Argument comparison for the replay. Two calls are the same call only when their arguments are equal AFTER the tool's verified
/// defaults are applied, with types preserved (the number 41 is not the string "41"), property order ignored, enum spellings compared
/// exactly and no property inferred. Nothing here is fuzzy.
/// </summary>
internal static class ReplayArguments
{
    /// <summary>The literal form: types tagged, keys ordered, nulls kept. Two literally equal calls need no knowledge of the tool.</summary>
    public static string Literal(JsonNode? node) => Encode(node, dropNulls: false);

    /// <summary>
    /// The effective form: for a tool with a verified spec, nulls are removed (null is an omitted argument) and every omitted argument
    /// with a verified default receives it. Without a spec the literal form is the effective form.
    /// </summary>
    public static JsonObject Effective(string tool, JsonObject arguments)
    {
        var clone = (JsonObject)arguments.DeepClone();
        if (ToolArgumentSpecs.For(tool) is not { } spec)
        {
            return clone;
        }

        if (spec.NullIsAbsent)
        {
            foreach (var name in clone.Where(p => p.Value is null).Select(p => p.Key).ToArray())
            {
                clone.Remove(name);
            }
        }

        foreach (var rule in spec.Defaults)
        {
            if (!clone.ContainsKey(rule.Name) && !rule.UnlessPresent.Any(clone.ContainsKey))
            {
                clone[rule.Name] = rule.Value.DeepClone();
            }
        }

        return clone;
    }

    public static string Canonical(string tool, JsonObject arguments) => Literal(Effective(tool, arguments));

    /// <summary>The arguments without <paramref name="name"/>, for the rules that compare everything except the limit.</summary>
    public static JsonObject Without(JsonObject arguments, string name)
    {
        var clone = (JsonObject)arguments.DeepClone();
        clone.Remove(name);
        return clone;
    }

    public static bool TryInteger(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue json && json.GetValueKind() == JsonValueKind.Number
            && decimal.TryParse(json.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && number == decimal.Truncate(number) && number >= long.MinValue && number <= long.MaxValue
            && (value = (long)number) == number;
    }

    private static string Encode(JsonNode? node, bool dropNulls)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject obj:
                return "{" + string.Join(",", obj.Where(p => !dropNulls || p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Encode(p.Value, dropNulls))) + "}";
            case JsonArray array:
                return "[" + string.Join(",", array.Select(item => Encode(item, dropNulls))) + "]";
            default:
                return node.GetValueKind() switch
                {
                    JsonValueKind.String => "s" + JsonSerializer.Serialize(node.GetValue<string>()),
                    JsonValueKind.Number => "n" + (decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        ? number.ToString("0.############################", CultureInfo.InvariantCulture)
                        : node.ToJsonString()),
                    JsonValueKind.True => "btrue",
                    JsonValueKind.False => "bfalse",
                    _ => "?" + node.ToJsonString(),
                };
        }
    }
}
