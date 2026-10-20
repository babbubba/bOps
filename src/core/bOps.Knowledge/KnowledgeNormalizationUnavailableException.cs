// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// The hosting process cannot normalize non-ASCII text (for example it runs with invariant globalization), so a Knowledge discovery
/// step that needs the NFKC fold of ADR-0057 sections S.2 and U.1 (U6) cannot be judged exactly. Discovery stops with this typed
/// failure instead of scoring or admitting text with a fold that never happened. It carries no text of the goal, query or
/// description, only the closed <see cref="DiagnosticCode"/>; the caller treats the Knowledge index as unavailable and the task
/// goes on without it. It grants no authority and changes no trust decision.
/// </summary>
public sealed class KnowledgeNormalizationUnavailableException : InvalidOperationException
{
    /// <summary>The closed diagnostic code, safe to audit and to show an operator.</summary>
    public const string Code = "KNOW-NORM-UNAVAILABLE";

    private const string FixedMessage =
        "Knowledge discovery needs Unicode normalization (NFC/NFKC), which this process does not provide; "
        + "run the host with full globalization (InvariantGlobalization=false).";

    /// <summary>Creates the exception with the fixed, text-free message.</summary>
    public KnowledgeNormalizationUnavailableException()
        : base(FixedMessage)
    {
    }

    /// <summary>Creates the exception; the message is ignored in favour of the fixed, text-free one.</summary>
    /// <param name="message">Ignored: the message never carries caller text. Present because CA1032 requires the constructor.</param>
    public KnowledgeNormalizationUnavailableException(string? message)
        : base(FixedMessage)
    {
    }

    /// <summary>Creates the exception with an inner exception; the message is the fixed, text-free one.</summary>
    /// <param name="message">Ignored: the message never carries caller text. Present because CA1032 requires the constructor.</param>
    /// <param name="innerException">The cause, if any.</param>
    public KnowledgeNormalizationUnavailableException(string? message, Exception? innerException)
        : base(FixedMessage, innerException)
    {
    }

    /// <summary>The closed diagnostic code (<see cref="Code"/>).</summary>
    public string DiagnosticCode { get; } = Code;
}
