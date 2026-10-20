// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// The per-logical-call accounting of the runtime control function (ADR-0052 AF-04). Two concepts are kept apart:
/// <list type="bullet">
/// <item><description><see cref="ServedReads"/>: raw evidence actually returned. Bounded by <see cref="EvidenceRead.MaxAttempts"/>;
/// reaching it exhausts the read service for the rest of the logical call, softly: the function stops being offered and the task goes on.</description></item>
/// <item><description><see cref="ControlRejections"/>: control requests that returned no evidence (malformed, unsupported, not offered,
/// invalid id/range/source, or a read asked for after exhaustion). They never consume a read slot, but a model that keeps
/// sending them is a protocol failure, bounded by <see cref="EvidenceRead.MaxControlRejections"/>.</description></item>
/// </list>
/// It lives in the <c>LogicalCallState</c>, which every logical call (live or resumed) starts fresh, so resume is deterministic:
/// a rebuilt call always begins with the full read budget and nothing is carried in memory across attempts.
/// </summary>
internal sealed class EvidenceReadBudget
{
    internal int ServedReads { get; private set; }

    internal int ControlRejections { get; private set; }

    internal bool Exhausted => ServedReads >= EvidenceRead.MaxAttempts;

    internal int ReadsRemaining => Math.Max(0, EvidenceRead.MaxAttempts - ServedReads);

    /// <summary>True once the rejected-request allowance is spent: the logical call can no longer be corrected.</summary>
    internal bool ProtocolBoundExceeded => ControlRejections > EvidenceRead.MaxControlRejections;

    internal void RecordServedRead() => ServedReads++;

    internal void RecordRejection() => ControlRejections++;
}
