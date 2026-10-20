// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace bOps.Knowledge;

/// <summary>One candidate with its lexical score for a goal or a query.</summary>
/// <param name="Candidate">The candidate, exactly as the caller declared it.</param>
/// <param name="Score">How many of the goal's tokens the candidate's name and description contain; an integer, 0 or more.</param>
public sealed record KnowledgeIndexRankedEntry(KnowledgeIndexCandidate Candidate, int Score);

/// <summary>
/// The ordered outcome of the M1 selector (ADR-0057 section S). <see cref="Ranked"/> contains every candidate the caller passed, none
/// excluded and none added: a low score changes the position, never the eligibility.
/// </summary>
/// <param name="Ranked">All candidates, by score descending then ref number ascending.</param>
/// <param name="Shortlist">The first <see cref="KnowledgeIndexLimits.Entries"/> of <paramref name="Ranked"/>.</param>
/// <param name="SelectorVersion">The version of the scoring and ordering rules.</param>
public sealed record KnowledgeIndexSelection(
    IReadOnlyList<KnowledgeIndexRankedEntry> Ranked,
    IReadOnlyList<KnowledgeIndexRankedEntry> Shortlist,
    string SelectorVersion)
{
    /// <summary>How many candidates the caller declared authorized.</summary>
    public int AuthorizedTotal => Ranked.Count;

    /// <summary>How many candidates the shortlist leaves out.</summary>
    public int Omitted => Ranked.Count - Shortlist.Count;
}

/// <summary>
/// The M1 index selector: a deterministic lexical ordering of the candidates the caller already declared admitted and authorized
/// (ADR-0057 sections S.2 and S.3). It uses no model, no embedding and no I/O, never excludes a candidate and never grants access:
/// the order is a preference, the candidate set is the caller's authority.
/// </summary>
public static class KnowledgeIndexSelector
{
    /// <summary>The version recorded in the audit for this scoring and ordering.</summary>
    public const string Version = "m1-lexical";

    /// <summary>The shortest token that counts, in code points.</summary>
    private const int MinTokenLength = 3;

    /// <summary>How many distinct tokens of a goal or a query are used.</summary>
    private const int MaxQueryTokens = 32;

    /// <summary>
    /// Orders <paramref name="candidates"/> for <paramref name="goal"/>. The result does not depend on the order in which the caller
    /// enumerates the candidates, on the current culture or on the clock.
    /// </summary>
    /// <exception cref="ArgumentException">A candidate breaks the contract of <see cref="KnowledgeIndexCandidate"/>, or the limits are out of range.</exception>
    /// <exception cref="KnowledgeNormalizationUnavailableException">
    /// The process cannot normalize non-ASCII text and the goal or a candidate has some: the NFKC fold would not happen, so nothing is
    /// ranked. An all-ASCII goal and candidate list is ranked exactly, because ASCII is invariant under every normalization form.
    /// </exception>
    public static KnowledgeIndexSelection Select(IReadOnlyList<KnowledgeIndexCandidate> candidates, string? goal, KnowledgeIndexLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.EnsureValid();
        ArgumentNullException.ThrowIfNull(candidates);
        EnsureFoldable(candidates, goal);
        KnowledgeIndexCandidate.EnsureValid(candidates);

        var ranked = Rank(candidates, goal, keepZero: true);
        return new KnowledgeIndexSelection(ranked, ranked.Take(limits.Entries).ToArray(), Version);
    }

    /// <summary>
    /// Scores every candidate against <paramref name="text"/> and orders them by score descending then <see cref="KnowledgeIndexCandidate.RefIndex"/>
    /// ascending; both are integers, so <c>K2</c> precedes <c>K10</c>. With <paramref name="keepZero"/> false the candidates that share
    /// no token with the text are dropped (the discover query), otherwise all are kept (the shortlist).
    /// </summary>
    internal static IReadOnlyList<KnowledgeIndexRankedEntry> Rank(IReadOnlyList<KnowledgeIndexCandidate> candidates, string? text, bool keepZero)
    {
        EnsureFoldable(candidates, text);
        var wanted = Tokenize(text, MaxQueryTokens);
        List<KnowledgeIndexRankedEntry> ranked = new(candidates.Count);
        foreach (var candidate in candidates)
        {
            var score = Score(wanted, candidate);
            if (keepZero || score > 0)
            {
                ranked.Add(new KnowledgeIndexRankedEntry(candidate, score));
            }
        }

        // A total order on integers: no two distinct candidates compare equal, so the sort is stable by construction.
        ranked.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.Candidate.RefIndex.CompareTo(right.Candidate.RefIndex);
        });
        return ranked;
    }

    /// <summary>
    /// Refuses, before anything is scored, a request whose NFKC fold this process cannot perform. Skill names are ASCII by U7, so only
    /// the goal or query and the descriptions can need it. Fail closed: no degraded ranking is ever produced.
    /// </summary>
    private static void EnsureFoldable(IReadOnlyList<KnowledgeIndexCandidate> candidates, string? text)
    {
        if (KnowledgeTextPolicy.NormalizationAvailable)
        {
            return;
        }

        if (NeedsFold(text))
        {
            throw new KnowledgeNormalizationUnavailableException();
        }

        foreach (var candidate in candidates)
        {
            if (candidate is not null && NeedsFold(candidate.Description))
            {
                throw new KnowledgeNormalizationUnavailableException();
            }
        }
    }

    internal static bool NeedsFold(string? text) => text is not null && text.AsSpan().ContainsAnyExceptInRange((char)0, (char)127);

    private static int Score(IReadOnlyList<string> wanted, KnowledgeIndexCandidate candidate)
    {
        if (wanted.Count == 0)
        {
            return 0;
        }

        // The name is split on '-' like any other non-alphanumeric run; its tokens and the description's are one set.
        HashSet<string> own = new(StringComparer.Ordinal);
        foreach (var token in Tokenize(candidate.Skill, int.MaxValue))
        {
            own.Add(token);
        }

        foreach (var token in Tokenize(candidate.Description, int.MaxValue))
        {
            own.Add(token);
        }

        var score = 0;
        foreach (var token in wanted)
        {
            if (own.Contains(token))
            {
                score++;
            }
        }

        return score;
    }

    /// <summary>
    /// Folds <paramref name="text"/> (NFKC, then invariant lower case) and returns its distinct tokens, in order of appearance, up to
    /// <paramref name="maximum"/>. A token is a maximal run of letter-or-digit code points of at least three code points. The text may
    /// be the operator's goal, which is not validated: an unpaired surrogate is replaced by U+FFFD so the fold cannot throw and the
    /// result stays deterministic.
    /// </summary>
    internal static IReadOnlyList<string> Tokenize(string? text, int maximum)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        // Last line of defence for any caller that reaches the fold without the guards above: a fold that would silently do nothing is a failure.
        if (!KnowledgeTextPolicy.NormalizationAvailable && NeedsFold(text))
        {
            throw new KnowledgeNormalizationUnavailableException();
        }

        // ADR-0057 section S.2 fixes the fold as NFKC then invariant lower case; this is case folding, not a comparison with upper case.
#pragma warning disable CA1308
        var folded = Repair(text).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
#pragma warning restore CA1308
        List<string> tokens = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        StringBuilder current = new();
        var length = 0;
        foreach (var rune in folded.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(rune.ToString());
                length++;
                continue;
            }

            if (!Flush(current, ref length, tokens, seen, maximum))
            {
                return tokens;
            }
        }

        Flush(current, ref length, tokens, seen, maximum);
        return tokens;
    }

    /// <summary>Closes the token being built. Returns false when the maximum has been reached.</summary>
    private static bool Flush(StringBuilder current, ref int length, List<string> tokens, HashSet<string> seen, int maximum)
    {
        if (length >= MinTokenLength)
        {
            var token = current.ToString();
            if (seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        current.Clear();
        length = 0;
        return tokens.Count < maximum;
    }

    private static string Repair(string text)
    {
        StringBuilder? repaired = null;
        var index = 0;
        while (index < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                repaired ??= new StringBuilder(text, 0, index, text.Length);
                repaired.Append(Rune.ReplacementChar.ToString());
            }
            else
            {
                repaired?.Append(rune.ToString());
            }

            index += consumed;
        }

        return repaired?.ToString() ?? text;
    }
}
