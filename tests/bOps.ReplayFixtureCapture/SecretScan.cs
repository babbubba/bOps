// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace bOps.ReplayFixtureCapture;

internal enum ScanCategory
{
    /// <summary>Something that looks like a credential. Any hit needs a human decision before the fixtures are shared or used.</summary>
    Secret,

    /// <summary>Not a credential but identifying: addresses, SIDs, profile paths, e-mail. Counted so a minimization rule set can be written.</summary>
    Identifier,
}

internal sealed record ScanPattern(string Id, ScanCategory Category, Regex Regex);

internal sealed record ScanHit(string PatternId, ScanCategory Category, int Count, string[] DistinctValueDigests);

/// <summary>
/// Scans text for credential-shaped strings and identifying strings. A hit never contains the matched text: only the pattern, a count and short
/// digests of the distinct values (so two occurrences of one value can be told from two different values without storing either).
/// The scan reads; it never rewrites a payload.
/// </summary>
internal static class SecretScanner
{
    public static readonly IReadOnlyList<ScanPattern> Patterns =
    [
        new("private-key-block", ScanCategory.Secret, new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)),
        new("aws-access-key-id", ScanCategory.Secret, new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)),
        new("github-token", ScanCategory.Secret, new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.CultureInvariant)),
        new("slack-token", ScanCategory.Secret, new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.CultureInvariant)),
        new("jwt", ScanCategory.Secret, new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.CultureInvariant)),
        new("bearer-credential", ScanCategory.Secret, new Regex(@"\bBearer\s+[A-Za-z0-9\-._~+/]{20,}=*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        new("password-assignment", ScanCategory.Secret, new Regex(@"\b(?:password|passwd|pwd)\s*[=:]\s*[""']?[^\s;,""'&]{4,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        new("secret-or-key-assignment", ScanCategory.Secret, new Regex(@"\b(?:api[_-]?key|secret|client[_-]?secret|access[_-]?key|auth[_-]?token)\s*[=:]\s*[""']?[A-Za-z0-9/+_\-]{12,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        new("url-with-credentials", ScanCategory.Secret, new Regex(@"\b[a-z][a-z0-9+.\-]*://[^\s/@:""]+:[^\s/@""]+@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        new("windows-sid", ScanCategory.Identifier, new Regex(@"\bS-1-5-21-\d+-\d+-\d+-\d+\b", RegexOptions.CultureInvariant)),
        new("ipv4-address", ScanCategory.Identifier, new Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.CultureInvariant)),
        new("email-address", ScanCategory.Identifier, new Regex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)),
        new("user-profile-path", ScanCategory.Identifier, new Regex(@"[A-Za-z]:\\\\?Users\\\\?[^\\""\s]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        new("unc-path", ScanCategory.Identifier, new Regex(@"\\\\\\\\[A-Za-z0-9._\-]+\\\\[A-Za-z0-9._\-$]+", RegexOptions.CultureInvariant)),
    ];

    public static IReadOnlyList<ScanHit> Scan(string? text, IEnumerable<string>? extraLiterals = null)
    {
        var hits = new List<ScanHit>();
        if (string.IsNullOrEmpty(text))
        {
            return hits;
        }

        foreach (var pattern in Patterns)
        {
            var matches = pattern.Regex.Matches(text);
            if (matches.Count > 0)
            {
                hits.Add(new ScanHit(pattern.Id, pattern.Category, matches.Count, Digests(matches.Select(m => m.Value))));
            }
        }

        // The capture's own identity strings (host, user): identifying values the patterns above cannot know.
        foreach (var literal in extraLiterals ?? [])
        {
            if (string.IsNullOrWhiteSpace(literal))
            {
                continue;
            }

            var count = Regex.Count(text, Regex.Escape(literal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (count > 0)
            {
                hits.Add(new ScanHit("capture-identity-literal", ScanCategory.Identifier, count, Digests([literal])));
            }
        }

        return hits;
    }

    public static JsonArray ToJson(IEnumerable<ScanHit> hits) => new(hits.Select(h => (JsonNode)new JsonObject
    {
        ["pattern"] = h.PatternId,
        ["category"] = h.Category.ToString(),
        ["count"] = h.Count,
        ["distinctValues"] = h.DistinctValueDigests.Length,
        ["valueDigests"] = new JsonArray(h.DistinctValueDigests.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
    }).ToArray());

    private static string[] Digests(IEnumerable<string> values) =>
        [.. values.Distinct(StringComparer.Ordinal).Select(v => CapturePlan.Sha256(v)[..8]).Order(StringComparer.Ordinal)];
}
