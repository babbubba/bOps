// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace bOps.DiagnosticEvaluation;

/// <summary>An integer the answer states, with the sentence it is in, its offset there and its length in characters.</summary>
internal sealed record AnswerNumber(int Sentence, int Offset, long Value, int Length);

/// <summary>
/// Splits a final answer into sentences (a table row or a list item counts as one) and finds the integers it states. Decimals, percentages,
/// sizes, dates, times and hexadecimal codes are not counts and are skipped. Matching is case-insensitive on lower-cased text.
/// </summary>
internal static partial class AnswerText
{
    [GeneratedRegex(@"(?<=[.!?;])\s+|\r?\n+")]
    private static partial Regex SentenceBreak();

    // An integer, possibly with thousands separators ("1.769", "1,769", "1 769", "1 769"), not glued to a word character or to another
    // digit group. A number after a hyphen (any Unicode dash) or a slash is part of a date, a range or a series ("2026-09-16", "16–21/10").
    [GeneratedRegex(@"(?<![\w.,:/\-‐-―])(\d{1,3}(?:[.,   ]\d{3})+|\d+)(?![\w])")]
    private static partial Regex Integer();

    // Decimals, percentages, sizes, durations and lengths are not counts of events.
    [GeneratedRegex(@"^\s*(?:[.,]\d|%|\s*%|\s*(?:mb|gb|kb|tb|mib|gib|mhz|ghz|ms|°|s|sec|secondi|secondo|seconds?|min|minuti|minuto|minutes?|ore|ora|hours?|h|giorni|giorno|days?|settimane|weeks?|mesi|months?|anni|years?|caratteri|characters|byte|bytes)\b|[:/\-‐-―]\d)",
        RegexOptions.IgnoreCase)]
    private static partial Regex NotACountAfter();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitGroup();

    // Between a term and a number, these end one list item and start the next. A table cell border ("|") does not: a row names its subject
    // in one cell and its count in the next.
    private static readonly string[] ListSeparators = [",", ";", "+", " e ", " ed ", " and "];

    // Three or more integers in a comma or whitespace list ("2, 2, 10, 22", "4   2   10"): a per-period breakdown, not a family total.
    [GeneratedRegex(@"\d+(?:(?:\s*,\s*|\s+)\d+){2,}")]
    private static partial Regex IntegerList();

    public static IReadOnlyList<string> Sentences(string answer) =>
        SentenceBreak().Split(answer ?? string.Empty)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.ToLower(CultureInfo.GetCultureInfo("it-IT")))
            .ToList();

    /// <summary>
    /// The integers of the sentences. A group joined by a plain space ("1 232") is one number only when <paramref name="known"/> recognises
    /// the joined value (the run's evidence holds it); otherwise its groups are separate numbers, as two cells of a table ("30 410") are.
    /// </summary>
    public static IReadOnlyList<AnswerNumber> Numbers(IReadOnlyList<string> sentences, Func<long, bool>? known = null)
    {
        var numbers = new List<AnswerNumber>();
        for (var i = 0; i < sentences.Count; i++)
        {
            foreach (Match match in Integer().Matches(sentences[i]))
            {
                var after = sentences[i][(match.Index + match.Length)..];
                if (NotACountAfter().IsMatch(after) || InsideList(sentences[i], match.Index))
                {
                    continue;
                }

                if (Parse(match.Value) is not { } value)
                {
                    continue;
                }

                if (match.Value.Contains(' ', StringComparison.Ordinal) && known?.Invoke(value) != true)
                {
                    foreach (Match group in DigitGroup().Matches(match.Value))
                    {
                        numbers.Add(new AnswerNumber(i, match.Index + group.Index, Parse(group.Value)!.Value, group.Length));
                    }

                    continue;
                }

                numbers.Add(new AnswerNumber(i, match.Index, value, match.Length));
            }
        }

        return numbers;
    }

    private static long? Parse(string text)
    {
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is > 0 and <= 12 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool InsideList(string sentence, int offset) =>
        IntegerList().Matches(sentence).Any(m => offset >= m.Index && offset < m.Index + m.Length);

    public static bool ContainsAny(string sentence, IEnumerable<string> terms) =>
        terms.Any(t => sentence.Contains(t, StringComparison.Ordinal));

    /// <summary>Like <see cref="ContainsAny"/>, but a term counts only where it starts a word ("no " in "no BSOD", not in "compaiono 60").</summary>
    public static bool ContainsAnyWord(string text, IEnumerable<string> terms) =>
        terms.Any(t =>
        {
            for (var at = text.IndexOf(t, StringComparison.Ordinal); at >= 0; at = text.IndexOf(t, at + 1, StringComparison.Ordinal))
            {
                if (at == 0 || !char.IsLetterOrDigit(text[at - 1]))
                {
                    return true;
                }
            }

            return false;
        });

    /// <summary>
    /// The family whose term occurs closest to the number (in characters, either side, at most <paramref name="window"/> away), so that in a
    /// sentence or table row naming several families each number goes to the subject next to it. Ties go to the term before the number,
    /// then to the key order. A negated term ("live dump, non BSOD") names what the number is not, so it is skipped. Null when no term is near.
    /// </summary>
    public static string? NearestFamily(string sentence, AnswerNumber number, IReadOnlyList<CountFamily> families, IReadOnlyList<string> negations,
        int window = 48)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        var bestBefore = false;
        foreach (var family in families)
        {
            foreach (var term in family.Terms)
            {
                for (var at = sentence.IndexOf(term, StringComparison.Ordinal); at >= 0; at = sentence.IndexOf(term, at + 1, StringComparison.Ordinal))
                {
                    var before = at < number.Offset;
                    var distance = before ? Math.Max(0, number.Offset - (at + term.Length)) : Math.Max(0, at - (number.Offset + number.Length));
                    if (distance > window || (at <= number.Offset && at + term.Length > number.Offset)
                        || ContainsAnyWord(sentence[Math.Max(0, at - 12)..at], negations))
                    {
                        continue; // too far, the number is inside the term itself ("0x193"), or the term is negated ("non BSOD")
                    }

                    var between = before ? sentence[(at + term.Length)..number.Offset] : sentence[(number.Offset + number.Length)..at];
                    if (ListSeparators.Any(separator => between.Contains(separator, StringComparison.Ordinal)))
                    {
                        continue; // the term belongs to another item of a list ("31 arresti inattesi, 10 report di bugcheck")
                    }

                    if (distance < bestDistance || (distance == bestDistance && before && !bestBefore))
                    {
                        (best, bestDistance, bestBefore) = (family.Id, distance, before);
                    }
                }
            }
        }

        return best;
    }

    /// <summary>True when one of the context words ends within <paramref name="window"/> characters before the offset.</summary>
    public static bool PrecededBy(string sentence, int offset, IEnumerable<string> words, int window = 16) =>
        ContainsAnyWord(sentence[Math.Max(0, offset - window)..offset], words);
}
