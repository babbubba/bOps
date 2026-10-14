// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace bOps.Knowledge;

/// <summary>Why <see cref="KnowledgeTextPolicy"/> refused a text. A closed code, never a copy of the text (ADR-0057 section U.1).</summary>
public enum KnowledgeTextViolation
{
    /// <summary>The text is acceptable.</summary>
    None = 0,

    /// <summary>The text is null, empty, or longer than the UTF-16 or UTF-8 limit (U5).</summary>
    Length = 1,

    /// <summary>The text contains an unpaired surrogate (U1).</summary>
    MalformedUtf16 = 2,

    /// <summary>The text is not in Unicode normalization form C (U2).</summary>
    NotNormalized = 3,

    /// <summary>A code point is outside the allowed Unicode categories (U3).</summary>
    DisallowedCategory = 4,

    /// <summary>The text contains an ASCII character that is reserved for markup, framing or escaping (U4).</summary>
    ForbiddenCharacter = 5,

    /// <summary>The text has leading, trailing or consecutive spaces (U5).</summary>
    Spacing = 6,

    /// <summary>The text names a runtime function, a framing marker or a template token (U6).</summary>
    ForbiddenContent = 7,

    /// <summary>The text starts a role label such as <c>system:</c>, in any width or case (U6).</summary>
    RoleLabel = 8,

    /// <summary>
    /// The text is not ASCII and this process cannot normalize non-ASCII text (for example under invariant globalization), so U2 and the
    /// folding of U6 cannot be judged. The text is refused: the policy fails closed instead of guessing.
    /// </summary>
    NormalizationUnavailable = 9,
}

/// <summary>
/// The admission contract for the publisher text that a task's model request may carry: skill descriptions and, later, summaries
/// and titles (ADR-0057 section U). The policy only judges: a text that does not conform is refused as a whole and the pack with it;
/// nothing is normalized, trimmed or repaired, so what an operator reviewed, what is pinned and what is shown are the same string.
/// Passing the policy gives a text no authority; it only makes the text representable as quoted data.
/// </summary>
/// <remarks>
/// Pure and culture-independent: categories come from <see cref="Rune.GetUnicodeCategory(Rune)"/> (.NET tables), comparisons are
/// ordinal and case folding is invariant. No regular expression is used, so the cost is linear in the (bounded) text length.
/// </remarks>
public static class KnowledgeTextPolicy
{
    /// <summary>The longest <c>query</c> of a discover call, in UTF-16 code units (ADR-0057 section C.3).</summary>
    public const int MaxQueryLength = 128;

    /// <summary>The longest skill description, in UTF-16 code units (U5).</summary>
    public const int MaxDescriptionLength = 256;

    /// <summary>The longest skill description, in UTF-8 bytes (U5).</summary>
    public const int MaxDescriptionUtf8Bytes = 768;

    /// <summary>
    /// Whether this process really normalizes non-ASCII text. Some runtime configurations (invariant globalization) leave non-ASCII
    /// text unchanged, which would make U2 and the NFKC fold of U6 vacuous. The answer is fixed for the life of the process; when it is
    /// false, <see cref="ValidateDescription"/> and <see cref="IsValidQuery"/> refuse every text with a non-ASCII character, and
    /// every ASCII text is judged exactly (ASCII is invariant under every normalization form).
    /// </summary>
    public static bool NormalizationAvailable { get; } = ProbeNormalization();

    // U6 substrings, matched on the NFKC + invariant-lowercase form.
    private static readonly string[] ForbiddenContents =
    [
        "runtime.", "load_skill", "read_skill_resource", "run_skill_script", "knowledge-index", "skill-data",
        "bops_tool_output", "im_start", "im_end", "endoftext",
    ];

    // U6 role labels, matched on the same folded form, followed by optional whitespace and a colon.
    private static readonly string[] RoleLabels = ["system", "developer", "user", "assistant", "tool", "operator", "human"];

    /// <summary>True when <paramref name="text"/> conforms to U1-U6 within the given length limits.</summary>
    public static bool IsValidDescription(string? text, int maxUtf16, int maxUtf8) =>
        ValidateDescription(text, maxUtf16, maxUtf8) == KnowledgeTextViolation.None;

    /// <summary>
    /// Judges <paramref name="text"/> against U1-U6. The first violated rule is reported, in a fixed order: UTF-16 length, code points
    /// in text order (U1, U3), UTF-8 length, U4, U5, U2, U6; so the same text always reports the same code.
    /// </summary>
    public static KnowledgeTextViolation ValidateDescription(string? text, int maxUtf16, int maxUtf8)
    {
        if (text is null || maxUtf16 < 1 || maxUtf8 < 1 || text.Length == 0 || text.Length > maxUtf16)
        {
            return KnowledgeTextViolation.Length;
        }

        // The UTF-16 bound is checked first, so every later step works on at most maxUtf16 units.
        var wellFormed = CheckCodePoints(text, static rune => IsAllowedCategory(rune), out var violation);
        if (!wellFormed)
        {
            return violation;
        }

        // U1 held, so the UTF-8 length is exact (no replacement characters are involved).
        if (Encoding.UTF8.GetByteCount(text) > maxUtf8)
        {
            return KnowledgeTextViolation.Length;
        }

        if (HasForbiddenAscii(text))
        {
            return KnowledgeTextViolation.ForbiddenCharacter;
        }

        if (text[0] == ' ' || text[^1] == ' ' || text.Contains("  ", StringComparison.Ordinal))
        {
            return KnowledgeTextViolation.Spacing;
        }

        if (!IsAscii(text))
        {
            if (!NormalizationAvailable)
            {
                return KnowledgeTextViolation.NormalizationUnavailable;
            }

            if (!text.IsNormalized(NormalizationForm.FormC))
            {
                return KnowledgeTextViolation.NotNormalized;
            }
        }

        return CheckFoldedContent(text);
    }

    /// <summary>
    /// True when <paramref name="text"/> is an acceptable <c>query</c> of a discover call: 1-128 UTF-16 units, well-formed (U1), in
    /// normalization form C (U2), within the allowed categories (U3) and made only of letters, digits, U+0020 and <c>-</c>
    /// (ADR-0057 section U.1, last paragraph). The query is the model's text, not the publisher's, so U4-U6 do not apply.
    /// </summary>
    public static bool IsValidQuery(string? text)
    {
        if (text is null || text.Length is 0 or > MaxQueryLength)
        {
            return false;
        }

        var accepted = CheckCodePoints(
            text,
            static rune => IsAllowedCategory(rune) && (Rune.IsLetterOrDigit(rune) || rune.Value is ' ' or '-'),
            out _);
        if (!accepted)
        {
            return false;
        }

        return IsAscii(text) || (NormalizationAvailable && text.IsNormalized(NormalizationForm.FormC));
    }

    /// <summary>
    /// True for a skill name: <c>[a-z0-9][a-z0-9-]{0,63}</c> (U7). Written as a loop, not a pattern, so it is trivially linear and
    /// independent of the culture.
    /// </summary>
    public static bool IsValidSkillName(string? name)
    {
        if (name is null || name.Length is 0 or > 64 || !IsLowerAsciiOrDigit(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!IsLowerAsciiOrDigit(character) && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAscii(string text) => !text.AsSpan().ContainsAnyExceptInRange((char)0, (char)127);

    private static bool ProbeNormalization()
    {
        // U+212B ANGSTROM SIGN has the singleton decomposition U+00C5 in every form; U+FF21 FULLWIDTH A folds to 'A' under NFKC.
        try
        {
            return !"\u212B".IsNormalized(NormalizationForm.FormC)
                && string.Equals("\u212B".Normalize(NormalizationForm.FormC), "\u00C5", StringComparison.Ordinal)
                && string.Equals("\uFF21".Normalize(NormalizationForm.FormKC), "A", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or PlatformNotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsLowerAsciiOrDigit(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';

    /// <summary>
    /// Walks the text by code point. Returns false with the violation when a surrogate is unpaired (U1) or when
    /// <paramref name="accept"/> refuses a code point (U3 and the query alphabet).
    /// </summary>
    private static bool CheckCodePoints(string text, Func<Rune, bool> accept, out KnowledgeTextViolation violation)
    {
        var index = 0;
        while (index < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                violation = KnowledgeTextViolation.MalformedUtf16;
                return false;
            }

            if (!accept(rune))
            {
                violation = KnowledgeTextViolation.DisallowedCategory;
                return false;
            }

            index += consumed;
        }

        violation = KnowledgeTextViolation.None;
        return true;
    }

    /// <summary>
    /// U3: letters, marks excluded; numbers; connector, dash, open/close, initial/final and other punctuation; math, currency,
    /// modifier and other symbols; and, among separators, only U+0020. Everything else, including every unassigned code point, is out.
    /// </summary>
    private static bool IsAllowedCategory(Rune rune)
    {
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.OtherNumber:
            case UnicodeCategory.ConnectorPunctuation:
            case UnicodeCategory.DashPunctuation:
            case UnicodeCategory.OpenPunctuation:
            case UnicodeCategory.ClosePunctuation:
            case UnicodeCategory.InitialQuotePunctuation:
            case UnicodeCategory.FinalQuotePunctuation:
            case UnicodeCategory.OtherPunctuation:
            case UnicodeCategory.MathSymbol:
            case UnicodeCategory.CurrencySymbol:
            case UnicodeCategory.ModifierSymbol:
            case UnicodeCategory.OtherSymbol:
                return true;
            case UnicodeCategory.SpaceSeparator:
                return rune.Value == ' ';
            default:
                // Cc, Cf, Cs, Co, Cn, Zl, Zp, other Zs, Mn, Mc, Me.
                return false;
        }
    }

    /// <summary>U4: <c>&lt; &gt; [ ] { } ` | \ "</c>.</summary>
    private static bool HasForbiddenAscii(string text)
    {
        foreach (var character in text)
        {
            if (character is '<' or '>' or '[' or ']' or '{' or '}' or '`' or '|' or '\\' or '"')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>U6 on the NFKC + invariant-lowercase form, so fullwidth, compatibility and case variants are judged like their ASCII form.</summary>
    private static KnowledgeTextViolation CheckFoldedContent(string text)
    {
        // ADR-0057 section U.1 (U6) fixes the fold as NFKC then invariant lower case; this is case folding, not a comparison with upper case.
#pragma warning disable CA1308
        var folded = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
#pragma warning restore CA1308

        foreach (var forbidden in ForbiddenContents)
        {
            if (folded.Contains(forbidden, StringComparison.Ordinal))
            {
                return KnowledgeTextViolation.ForbiddenContent;
            }
        }

        return ContainsRoleLabel(folded) ? KnowledgeTextViolation.RoleLabel : KnowledgeTextViolation.None;
    }

    /// <summary>
    /// The pattern <c>(^|[^\p{L}\p{N}])(role)\s*:</c> as a single pass: for every occurrence of a role word, the preceding code point
    /// must not be a letter or digit, and the word must be followed by optional whitespace and a colon.
    /// </summary>
    private static bool ContainsRoleLabel(string folded)
    {
        foreach (var role in RoleLabels)
        {
            var from = 0;
            while (from <= folded.Length - role.Length)
            {
                var at = folded.IndexOf(role, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                from = at + 1;
                if (at > 0 && PrecedingIsLetterOrDigit(folded, at))
                {
                    continue;
                }

                var next = at + role.Length;
                while (next < folded.Length && char.IsWhiteSpace(folded[next]))
                {
                    next++;
                }

                if (next < folded.Length && folded[next] == ':')
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool PrecedingIsLetterOrDigit(string text, int index)
    {
        // Look one code point back: a trailing low surrogate belongs to a pair that starts one unit earlier.
        var start = index - 1;
        if (char.IsLowSurrogate(text[start]) && start > 0 && char.IsHighSurrogate(text[start - 1]))
        {
            start--;
        }

        return Rune.TryGetRuneAt(text, start, out var rune) && Rune.IsLetterOrDigit(rune);
    }
}
