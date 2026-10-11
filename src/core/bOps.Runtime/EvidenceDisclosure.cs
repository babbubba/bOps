// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace bOps.Runtime;

/// <summary>
/// The final-answer disclosure (ADR-0042 §6, AF-05H amendment): a deterministic, line-based detector of the
/// <see cref="EvidenceRule.DisclosureHeading"/> heading, and the deterministic runtime-authored section appended when a final answer
/// given under listed limitations lacks it. No Markdown parser and no model call is involved: the safety phase never rewrites, and so
/// can never alter, the model's own claims. The heading is English on purpose — a stable technical marker that makes enforcement
/// deterministic in every language.
/// </summary>
internal static class EvidenceDisclosure
{
    /// <summary>
    /// The template version of the appended section. <c>EvidenceDisclosure/v1</c> was the removed model re-ask; persisted rows that
    /// carry its markers are still read (<see cref="FinalResponse"/>).
    /// </summary>
    internal const string Version = "EvidenceDisclosure/v2";

    /// <summary>The fixed line under the heading of the appended section.</summary>
    internal const string AppendedLead =
        "Added by bOps from typed tool results (" + Version + "); the answer above is unchanged. "
        + "The results of these steps were incomplete, unavailable, failed or cut:";

    /// <summary>The most characters the appended section takes, separator and any fence closing included.</summary>
    internal const int MaxAppendedCharacters = EvidenceLimitationsDigest.MaxCharacters;

    /// <summary>
    /// The runtime-authored section: the heading, <see cref="AppendedLead"/> and the digest's listed entries, which are rendered
    /// from typed fields only (no tool output, error message, observation, argument or model text), plus the omission line when the
    /// digest is not complete. Deterministic: the same persisted steps give the same section.
    /// </summary>
    internal static string Section(EvidenceLimitations limitations)
    {
        ArgumentNullException.ThrowIfNull(limitations);
        var text = new StringBuilder("## ").Append(EvidenceRule.DisclosureHeading).Append('\n').Append(AppendedLead);
        foreach (var entry in limitations.Entries)
        {
            text.Append('\n').Append(entry);
        }

        if (limitations.OmittedCount > 0)
        {
            text.Append('\n').Append(CultureInfo.InvariantCulture,
                $"- {limitations.OmittedCount} earlier listed step(s) are not shown, so this list is not complete.");
        }

        return text.ToString();
    }

    /// <summary>
    /// <paramref name="answer"/> followed by the deterministic section. The original text is an exact prefix of the result: nothing
    /// in it is changed, trimmed or reinterpreted. When the answer ends inside an unclosed code fence, a closing fence line is
    /// appended first so that the heading is a real heading.
    /// </summary>
    internal static string Append(string answer, EvidenceLimitations limitations)
    {
        ArgumentNullException.ThrowIfNull(answer);
        Scan(answer, out var openFence, out var openLength);
        var closing = openLength > 0 ? "\n" + new string(openFence, openLength) : string.Empty;
        return answer + closing + "\n\n" + Section(limitations);
    }

    /// <summary>
    /// Whether <paramref name="answer"/> contains a valid heading line. Lines are split on <c>\n</c> (a trailing <c>\r</c> is
    /// removed). Fenced code blocks are skipped — a fence opens on a line that, after at most three spaces, starts with at
    /// least three backticks or three tildes, and closes on the next line that, after at most three spaces, consists only of
    /// at least as many of the same character and optional whitespace; an unclosed fence runs to the end. Lines indented by
    /// a tab or four or more spaces are not candidates. A candidate is normalized (see <see cref="NormalizeHeadingLine"/>)
    /// and is a heading when it equals the heading ignoring case.
    /// </summary>
    internal static bool HasHeading(string? answer) =>
        !string.IsNullOrEmpty(answer) && Scan(answer, out _, out _);

    /// <summary>The one line scan behind <see cref="HasHeading"/> and <see cref="Append"/>; it also reports a fence still open at the end.</summary>
    private static bool Scan(string answer, out char fenceCharacter, out int fenceLength)
    {
        fenceCharacter = '\0';
        fenceLength = 0;
        var found = false;

        foreach (var rawLine in answer.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            var spaces = LeadingSpaces(line);
            var content = line[spaces..];

            if (fenceLength > 0)
            {
                if (spaces <= 3 && IsFenceClose(content, fenceCharacter, fenceLength))
                {
                    fenceLength = 0;
                }

                continue;
            }

            if (spaces <= 3 && TryFenceOpen(content, out fenceCharacter, out fenceLength))
            {
                continue;
            }

            if (spaces >= 4 || (spaces < line.Length && line[spaces] == '\t'))
            {
                continue;
            }

            found |= string.Equals(NormalizeHeadingLine(line), EvidenceRule.DisclosureHeading, StringComparison.OrdinalIgnoreCase);
        }

        return found;
    }

    /// <summary>
    /// The heading normalization of ADR-0042 §6, in order: trim; remove one leading ATX prefix of one to six <c>#</c> followed
    /// by a space or tab, and trim; remove one trailing <c>:</c> and trim; remove one surrounding pair of <c>**</c> or of
    /// <c>__</c>, and trim; remove one trailing <c>:</c> and trim.
    /// </summary>
    internal static string NormalizeHeadingLine(string line)
    {
        var text = line.Trim();

        var hashes = 0;
        while (hashes < text.Length && text[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is >= 1 and <= 6 && hashes < text.Length && text[hashes] is ' ' or '\t')
        {
            text = text[hashes..].Trim();
        }

        text = TrimOneColon(text);

        foreach (var pair in new[] { "**", "__" })
        {
            if (text.Length >= 2 * pair.Length && text.StartsWith(pair, StringComparison.Ordinal) && text.EndsWith(pair, StringComparison.Ordinal))
            {
                text = text[pair.Length..^pair.Length].Trim();
                break;
            }
        }

        return TrimOneColon(text);
    }

    private static string TrimOneColon(string text) =>
        text.EndsWith(':') ? text[..^1].TrimEnd() : text;

    private static int LeadingSpaces(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static bool TryFenceOpen(string content, out char fenceCharacter, out int fenceLength)
    {
        fenceCharacter = '\0';
        fenceLength = 0;
        if (content.Length < 3 || content[0] is not ('`' or '~'))
        {
            return false;
        }

        var character = content[0];
        var length = 0;
        while (length < content.Length && content[length] == character)
        {
            length++;
        }

        if (length < 3)
        {
            return false;
        }

        fenceCharacter = character;
        fenceLength = length;
        return true;
    }

    private static bool IsFenceClose(string content, char fenceCharacter, int fenceLength)
    {
        var length = 0;
        while (length < content.Length && content[length] == fenceCharacter)
        {
            length++;
        }

        return length >= fenceLength && string.IsNullOrWhiteSpace(content[length..]);
    }
}
