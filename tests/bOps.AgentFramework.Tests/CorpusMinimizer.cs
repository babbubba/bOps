// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// Deterministic minimization of a <c>system.events</c> output before it is frozen as a benchmark corpus (KNOW-BENCH corpus provenance).
/// Keeps what a diagnosis needs and nothing that identifies a person, path or machine:
/// <list type="bullet">
/// <item>always kept: UTC timestamp, channel (System/Application only), numeric Event ID, severity;</item>
/// <item>the provider, only when it is on <see cref="ProviderAllowlist"/>; any other provider becomes <c>[redacted-provider-N]</c>,
/// numbered by first appearance so repeated events of one unknown provider stay correlated without naming it;</item>
/// <item>structured details, only the fields each allowlisted provider/event rule extracts from the Windows message with a pattern that
/// constrains the value itself (file name, hex code, version, type name, closed word). A field the message does not carry is absent:
/// nothing is inferred, so a localized or reworded message yields no details rather than invented ones;</item>
/// <item>never kept: the message text, paths, user names, addresses, command lines, process ids, machine identifiers, source detail.</item>
/// </list>
/// The transformation is idempotent (<c>Apply(Apply(x)) == Apply(x)</c>): a stored corpus is rebuilt from its own sanitized output on read.
/// </summary>
internal static class CorpusMinimizer
{
    internal const string Version = "allowlist-provider-and-structured-details-v2";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // Keys removed wherever they appear outside an event record (the event record is handled field by field).
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.Ordinal)
        { "message", "sampleMessage", "source", "unit", "processId", "processName", "detail" };
    private static readonly HashSet<string> Channels = new(StringComparer.Ordinal) { "System", "Application" };
    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal)
        { "critical", "error", "warning", "information", "verbose", "unknown" };
    private static readonly Regex EventId = new(@"\A[0-9]{1,5}\z", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex RedactedProvider = new(@"\A\[redacted-provider-[1-9][0-9]{0,3}\]\z", RegexOptions.CultureInvariant, MatchTimeout);

    private const string FileName = @"[A-Za-z0-9_][A-Za-z0-9_.\-]{0,62}\.(?:exe|dll|sys)";
    private const string Hex32 = @"0x[0-9a-f]{1,8}";

    /// <summary>One structured field: where it is introduced in the message and the only value shape that may be kept.</summary>
    private sealed class Field(string key, string label, string value, string suffix = "")
    {
        internal string Key { get; } = key;
        internal Regex Extract { get; } = new(label + "(?<v>" + value + ")" + suffix, Options, MatchTimeout);
        internal Regex Valid { get; } = new(@"\A(?:" + value + @")\z", Options, MatchTimeout);
    }

    private sealed class ProviderRule(string name, IReadOnlyDictionary<string, Field[]>? byEvent = null, Field[]? any = null)
    {
        internal string Name { get; } = name;

        internal Field[] For(string? eventId) =>
            [.. (any ?? []), .. (eventId is not null && byEvent is not null && byEvent.TryGetValue(eventId, out var fields) ? fields : [])];
    }

    private static readonly ProviderRule[] Rules =
    [
        new("Microsoft-Windows-Kernel-Power", byEvent: new Dictionary<string, Field[]>
        {
            ["41"] = [new("bugcheckCode", @"BugcheckCode\s*[:=]?\s*", @"0x[0-9a-f]{1,8}|[0-9]{1,10}")],
        }),
        new("Microsoft-Windows-WHEA-Logger", any:
        [
            new("errorClass", @"\bA\s+", "corrected|fatal|recoverable", @"(?=\s+hardware\s+error)"),
            new("component", @"\bComponent:\s*", @"[A-Za-z0-9][A-Za-z0-9 \-/()]{0,47}"),
            new("errorSource", @"\bError Source:\s*", @"[A-Za-z0-9][A-Za-z0-9 \-/()]{0,63}"),
        ]),
        new("Application Error", byEvent: new Dictionary<string, Field[]>
        {
            ["1000"] =
            [
                new("applicationName", @"Faulting application name:\s*", FileName),
                new("moduleName", @"Faulting module name:\s*", FileName + "|unknown"),
                new("exceptionCode", @"Exception code:\s*", Hex32),
                new("faultOffset", @"Fault offset:\s*", @"0x[0-9a-f]{1,16}"),
            ],
        }),
        new("Windows Error Reporting", byEvent: new Dictionary<string, Field[]>
        {
            ["1001"] =
            [
                new("eventName", @"Event Name:\s*", "[A-Za-z0-9_]{1,40}"),
                new("faultBucketType", @"Fault bucket\s+[^,\r\n]{0,64},\s*type\s+", "[0-9]{1,3}"),
            ],
        }),
        new("EventLog"),
        new(".NET Runtime", byEvent: new Dictionary<string, Field[]>
        {
            ["1026"] =
            [
                new("applicationName", @"Application:\s*", FileName),
                new("frameworkVersion", @"Framework Version:\s*", @"v[0-9]+(?:\.[0-9]+){1,3}"),
                new("exceptionType", @"Exception Info:\s*", @"[A-Za-z_][A-Za-z0-9_]{0,63}(?:\.[A-Za-z_][A-Za-z0-9_]{0,63}){0,5}"),
            ],
        }),
        new("Microsoft-Windows-WER-SystemErrorReporting", byEvent: new Dictionary<string, Field[]>
        {
            ["1001"] = [new("bugcheckCode", @"The bugcheck was:\s*", Hex32)],
        }),
        new("Service Control Manager"),
        new("Disk"),
        new("Ntfs"),
    ];

    /// <summary>The providers whose name may appear in a corpus (canonical spelling).</summary>
    internal static IReadOnlyList<string> ProviderAllowlist { get; } = [.. Rules.Select(rule => rule.Name)];

    /// <summary>Whether <paramref name="provider"/> is a retained (allowlisted) provider name, not a redaction or an arbitrary text.</summary>
    internal static bool IsRetainedProvider(string? provider) => provider is not null && FindRule(provider) is not null;

    internal static bool IsRedactedProvider(string? provider) => provider is not null && RedactedProvider.IsMatch(provider);

    /// <summary>Minimizes the whole output in place.</summary>
    internal static void Apply(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Walk(root, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
    }

    private static void Walk(JsonNode? node, Dictionary<string, int> unknownProviders)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("eventId") && (obj.ContainsKey("message") || obj.ContainsKey("sampleMessage")))
            {
                MinimizeEvent(obj, unknownProviders);
                return;
            }

            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (SensitiveKeys.Contains(key)) obj[key] = null;
                else Walk(obj[key], unknownProviders);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array) Walk(child, unknownProviders);
        }
    }

    private static void MinimizeEvent(JsonObject record, Dictionary<string, int> unknownProviders)
    {
        var eventId = Text(record, "eventId");
        if (eventId is null || !EventId.IsMatch(eventId)) eventId = null;
        record["eventId"] = eventId;
        if (record.ContainsKey("channel") && (Text(record, "channel") is not { } channel || !Channels.Contains(channel))) record["channel"] = null;
        if (record.ContainsKey("severity") && (Text(record, "severity") is not { } severity || !Severities.Contains(severity))) record["severity"] = null;

        var rawProvider = Text(record, "source");
        var rule = rawProvider is null ? null : FindRule(rawProvider);
        var message = Text(record, "message") ?? Text(record, "sampleMessage");
        var priorDetails = record["details"] as JsonObject;
        var details = new JsonObject();
        if (rule is not null)
        {
            foreach (var field in rule.For(eventId))
            {
                var value = message is not null ? Extract(field, message) : priorDetails is not null ? Revalidate(field, priorDetails) : null;
                if (value is not null) details[field.Key] = value;
            }
        }

        record["source"] = rule?.Name ?? (IsRedactedProvider(rawProvider) ? rawProvider : RedactedName(rawProvider, unknownProviders));
        if (details.Count > 0) record["details"] = details;
        else record.Remove("details");
        foreach (var key in new[] { "message", "sampleMessage", "unit", "processId", "processName", "detail" })
        {
            if (record.ContainsKey(key)) record[key] = null;
        }
    }

    private static string? RedactedName(string? provider, Dictionary<string, int> unknownProviders)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        if (!unknownProviders.TryGetValue(provider, out var number)) unknownProviders[provider] = number = unknownProviders.Count + 1;
        return $"[redacted-provider-{number}]";
    }

    private static string? Extract(Field field, string message)
    {
        var match = field.Extract.Match(message);
        return match.Success ? Normalize(field, match.Groups["v"].Value.Trim()) : null;
    }

    private static string? Revalidate(Field field, JsonObject prior) =>
        prior[field.Key] is JsonValue value && value.TryGetValue<string>(out var text) ? Normalize(field, text) : null;

    private static string? Normalize(Field field, string value) =>
        value.Length is > 0 and <= 160 && field.Valid.IsMatch(value) ? (field.Key == "errorClass" ? value.ToLowerInvariant() : value) : null;

    private static ProviderRule? FindRule(string provider) =>
        Rules.FirstOrDefault(rule => string.Equals(rule.Name, provider.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonObject record, string key) =>
        record[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
