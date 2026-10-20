// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace bOps.Runtime;

/// <summary>
/// LIVE-F1-FIX-01: whether the text of a final answer carries any answer content. A provider's <c>stop</c> finish reason says only
/// that the reply ended; a reply made only of whitespace, punctuation, symbols or Markdown markers (<c>&gt;</c>, <c>...</c>,
/// <c>**</c>, <c>---</c>, an empty code fence) answers nothing and is never taken as the operator's answer (rule S3). The test is
/// deliberately minimal — at least one Unicode letter or number outside a code-fence delimiter line — so a short real answer such as
/// <c>OK</c>, <c>No.</c> or <c>0</c> is always kept, and it judges usability only, never correctness. It applies to the final answer
/// alone: plan replies, tool calls and grounding check replies keep their own contracts.
/// </summary>
internal static class FinalAnswerContent
{
    /// <summary>Whether <paramref name="text"/> has at least one letter or number outside a code-fence delimiter line.</summary>
    internal static bool HasContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (IsFenceDelimiter(trimmed))
            {
                continue;
            }

            foreach (var rune in trimmed.EnumerateRunes())
            {
                if (Rune.IsLetter(rune) || Rune.IsNumber(rune))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A code-fence delimiter line: three or more backticks or tildes, optionally followed by a single info-string word such as a
    /// language name (<c>```json</c>). Text after the fence that is not one such word (<c>```OK```</c>) is content, not a delimiter.
    /// </summary>
    private static bool IsFenceDelimiter(ReadOnlySpan<char> line)
    {
        if (line.Length < 3 || line[0] is not ('`' or '~'))
        {
            return false;
        }

        var fence = line[0];
        var length = 0;
        while (length < line.Length && line[length] == fence)
        {
            length++;
        }

        if (length < 3)
        {
            return false;
        }

        foreach (var c in line[length..])
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+' or '.' or '#'))
            {
                return false;
            }
        }

        return true;
    }
}
