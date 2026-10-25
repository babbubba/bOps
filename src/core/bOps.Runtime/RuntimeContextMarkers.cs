// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Runtime;

/// <summary>
/// The lines the runtime itself writes into the model's history when it compacts tool interactions (ADR-0052): a fixed header and one
/// status bullet per tool call. They are context for the model, never an answer: a model that copies them as its final reply has
/// answered nothing, whatever its finish reason (rule S3). The producer (the MAF context projector) and the consumer
/// (<see cref="FinalAnswerContent"/>) share these definitions so the two cannot drift apart. Recognition is deliberately narrow — the whole
/// line must be a marker — so a real sentence that merely mentions a status is never taken for one.
/// </summary>
public static partial class RuntimeContextMarkers
{
    /// <summary>The first line of a compacted group of tool interactions.</summary>
    public const string CompactionHeader = "[Compacted tool interactions; persisted result data omitted]";

    /// <summary>The status of a compacted call whose result was recorded.</summary>
    public const string ResultRecorded = "result recorded";

    /// <summary>The status of a compacted call that was proposed but not run.</summary>
    public const string NotExecuted = "not executed";

    /// <summary>The bullet line the projector writes for one compacted call.</summary>
    public static string StatusLine(string callName, string status) => $"- {callName}: {status}";

    /// <summary>
    /// Whether <paramref name="line"/> is exactly a runtime context marker: the compaction header, or a bullet naming one tool call
    /// (optionally with a step number) and one of the two fixed statuses. A leading quote marker and a trailing full stop are tolerated
    /// because a model copying the line often adds them; any other extra word makes the line real text.
    /// </summary>
    internal static bool IsMarkerLine(ReadOnlySpan<char> line)
    {
        var text = line.Trim();
        while (text.Length > 0 && text[0] == '>')
        {
            text = text[1..].TrimStart();
        }

        if (text.Length == 0)
        {
            return false;
        }

        return text.Equals(CompactionHeader, StringComparison.OrdinalIgnoreCase) || StatusBullet().IsMatch(text);
    }

    [GeneratedRegex(
        @"\A[-*]\s+[A-Za-z0-9_][A-Za-z0-9_.\-]{0,127}(?:\s*\((?:step|passo)\s*\d{1,6}\))?\s*:\s*(?:result recorded|not executed)\.?\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StatusBullet();
}
