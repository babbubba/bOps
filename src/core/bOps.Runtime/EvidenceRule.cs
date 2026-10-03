// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// The standing evidence rule appended to the runtime system prompt (ADR-0042 §4): how the model weighs and describes
/// evidence. It is a constant, platform- and product-neutral, carried by every plan, replan and step call. It states
/// clauses E1–E8 of the ADR in at most <see cref="MaxCharacters"/> UTF-16 code units, and contains no identifier-shaped
/// token, no operating system, product, provider or symptom term and no example drawn from an incident.
/// </summary>
internal static class EvidenceRule
{
    /// <summary>The most characters the rule may take (ADR-0042 §4, §11).</summary>
    internal const int MaxCharacters = 2000;

    /// <summary>The heading the final answer must carry when the runtime lists evidence limitations (clause 8, ADR-0042 §6).</summary>
    internal const string DisclosureHeading = "Evidence limitations";

    /// <summary>The paragraph appended to the base system prompt: a fixed lead-in and the rule. At most <see cref="MaxCharacters"/>.</summary>
    internal const string Paragraph = "Evidence rules:\n" + Text;

    /// <summary>The rule text. Its length and vocabulary are asserted by tests; any change is an amendment of ADR-0042.</summary>
    // Explicit separators keep the compiled prompt identical with LF and CRLF source checkouts.
    internal const string Text =
        "1. Report what tool results show as observations; label anything beyond them an inference or hypothesis.\n" +
        "2. A current reading is weak evidence about a past or intermittent problem; prefer evidence covering the period asked. One sample is not a trend.\n" +
        "3. A result marked partial, unavailable or truncated, one with shorter coverage than requested, or one you could not see in full is bounded; its counts are minimums.\n" +
        "4. No result proves something did not happen. Say at most that no matching records were observed in readable sources covering the requested period and filters, and only if the result says it is complete; otherwise say it is incomplete. Not collected, unavailable, outside retained history, excluded or not applicable means unknown, never zero.\n" +
        "5. A time may be when something happened or when it was recorded or reported; never present a reported time as when it happened. A time with no stated kind is the time of the record, so the thing happened no later. Order two different events as fact only if both times share one comparable clock and universal time, no clock change or restart lies between them, and the gap exceeds the coarser precision (two seconds if unstated); otherwise the order is a hypothesis.\n" +
        "6. Relate independent sources by time to form hypotheses. Closeness in time, co-occurrence, frequency or similarity never proves a cause. When tools say their evidence overlaps, do not add counts.\n" +
        "7. Never state a cause as your own conclusion. If a record states a reason, report it as the record's statement; otherwise offer hypotheses with evidence for and against and what would confirm them.\n" +
        "8. Disclose runtime-listed limitations. Use a section headed exactly Evidence limitations on its own line (the section may be in your language) unless runtime sets another format. Name each tool and what was partial, missing or cut; say it was not visible when failed or unseen, never guess contents. With none listed, no disclosure is needed.";
}
