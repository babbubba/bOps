// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// The final-answer disclosure check (ADR-0042 §6): a deterministic, line-based detector of the
/// <see cref="EvidenceRule.DisclosureHeading"/> heading, and the fixed instruction of the one bounded, tool-free re-ask. No
/// Markdown parser is involved. The heading is English on purpose — a stable technical marker that makes enforcement
/// deterministic in every language — and the content of the section may be written in the language of the answer.
/// </summary>
internal static class EvidenceDisclosure
{
    /// <summary>The template version of the re-ask instruction.</summary>
    internal const string Version = "EvidenceDisclosure/v1";

    /// <summary>
    /// The runtime-authored user turn of the re-ask. A constant: no tool output, argument or model text enters it. The
    /// original answer is never placed in it; it is the preceding assistant turn.
    /// </summary>
    internal const string Instruction =
        Version + ": Restate your final answer and include the required " + EvidenceRule.DisclosureHeading + " section. " +
        "Keep your conclusions unchanged unless the limitations require qualifying them. Start no new analysis. Do not call tools.";

    /// <summary>
    /// Whether <paramref name="answer"/> contains a valid heading line. Lines are split on <c>\n</c> (a trailing <c>\r</c> is
    /// removed). Fenced code blocks are skipped — a fence opens on a line that, after at most three spaces, starts with at
    /// least three backticks or three tildes, and closes on the next line that, after at most three spaces, consists only of
    /// at least as many of the same character and optional whitespace; an unclosed fence runs to the end. Lines indented by
    /// a tab or four or more spaces are not candidates. A candidate is normalized (see <see cref="NormalizeHeadingLine"/>)
    /// and is a heading when it equals the heading ignoring case.
    /// </summary>
    internal static bool HasHeading(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
        {
            return false;
        }

        char fenceCharacter = '\0';
        var fenceLength = 0;

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

            if (string.Equals(NormalizeHeadingLine(line), EvidenceRule.DisclosureHeading, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
