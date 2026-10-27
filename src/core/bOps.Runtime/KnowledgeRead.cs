// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// What bOps decided about one model turn, handed to a knowledge session by the runner (KNOW-01B, ADR-0056). It is a snapshot of the
/// authority the runner already computed (the tools it offered on the very request that produced the reply, the step's expected
/// tool, whether reads are eligible); a session adds none of its own.
/// </summary>
/// <param name="OfferedTools">The tool names offered to the model on the request that produced the reply.</param>
/// <param name="ExpectedTool">The planned step's expected tool, if any. A knowledge read never completes it.</param>
/// <param name="Eligible">Whether the runtime says this step may read knowledge.</param>
/// <param name="Allowance">
/// How many tokens a read may add to the context of this very request, from the selected model's real capacity and what the request
/// already holds; <c>null</c> when the runtime has no budget to state, in which case only the session's fixed limits apply.
/// </param>
public sealed record KnowledgeReadAuthority(
    IReadOnlyList<string> OfferedTools, string? ExpectedTool, bool Eligible, KnowledgeReadAllowance? Allowance = null);

/// <summary>What a knowledge session did with one model reply.</summary>
/// <param name="Handled">
/// <c>false</c> when the reply contains nothing that concerns knowledge (no control call of the session ahead of any other runtime
/// function): the runner handles it as it always does, and the session produced no turn, no audit and no model call.
/// </param>
/// <param name="Code">How the read ended, when it was handled.</param>
/// <param name="ContinuationTurns">
/// The turns that answer every call of the reply (the assistant calls, then one result per call id), ready to be appended to the
/// model context of the same logical call.
/// </param>
/// <param name="ProtocolBoundExceeded">The model keeps sending reads that return nothing; the runtime should stop the task.</param>
public sealed record KnowledgeReadOutcome(
    bool Handled, KnowledgeReadResultCode? Code, IReadOnlyList<ChatTurn> ContinuationTurns, bool ProtocolBoundExceeded)
{
    /// <summary>The reply is not the session's.</summary>
    public static KnowledgeReadOutcome NotHandled { get; } = new(false, null, [], false);
}

/// <summary>
/// The task signals a session may use to select index entries (ADR-0057). The first milestone carries the goal only; later milestones
/// add optional members. The goal is the operator's text: a session uses it to order entries and never records or repeats it.
/// </summary>
/// <param name="Goal">The operator's goal for the task.</param>
public sealed record KnowledgeIndexQuery(string Goal);

/// <summary>
/// One rendering of the index data (ADR-0057): a single data turn plus the host-generated facts about it. The runtime verifies the
/// turn's provenance against the task's persisted binding and inserts it; it never reads the content to decide anything.
/// </summary>
/// <param name="DataTurn">The <see cref="ChatRole.User"/> turn that carries the third-party reference data. Its provenance is assigned by the session.</param>
/// <param name="ListedRefs">The host-generated references listed in the turn (<c>K1</c>, ...), in selection order.</param>
/// <param name="Omitted">How many authorized entries are not listed in this rendering.</param>
/// <param name="IndexHash">The SHA-256, lowercase hex, of the content of <paramref name="DataTurn"/>.</param>
public sealed record KnowledgeIndexRendering(ChatTurn DataTurn, IReadOnlyList<string> ListedRefs, int Omitted, string IndexHash);

/// <summary>
/// What a session offers as its index for one request (ADR-0057). <see cref="Status"/> says whether an index exists; a published view
/// carries both renderings, a suppressed one carries a closed reason and none. The listing and counts are host-generated; only the
/// content of a rendering's data turn originates from a publisher, and it has no authority.
/// </summary>
/// <param name="Status">Whether the index is published.</param>
/// <param name="Suppression">Why it is not, when suppressed.</param>
/// <param name="Cause">The closed cause reported by the authority in force, for an authority or snapshot suppression.</param>
/// <param name="Normal">The rendering for a normal request; not null exactly when published.</param>
/// <param name="Aggressive">The reduced rendering for a request retried after a context overflow; not null exactly when published.</param>
/// <param name="Listed">The entries listed in the normal rendering, for audit.</param>
/// <param name="AuthorizedTotal">How many entries the authority in force covers.</param>
/// <param name="SelectorVersion">The version of the selector that ordered the entries.</param>
public sealed record KnowledgeIndexView(
    KnowledgeIndexStatus Status,
    KnowledgeIndexSuppression? Suppression,
    KnowledgeAdmissionCode? Cause,
    KnowledgeIndexRendering? Normal,
    KnowledgeIndexRendering? Aggressive,
    IReadOnlyList<KnowledgeIndexListing> Listed,
    int AuthorizedTotal,
    string SelectorVersion);

/// <summary>
/// The knowledge a task may read during one execution attempt (KNOW-01B, ADR-0056). The runner offers <see cref="Manifest"/> beside the
/// step's tool view and gives every reply to <see cref="HandleAsync"/> before it looks at the reply itself. A session reads nothing it
/// was not admitted for, never calls a model, and never executes a tool; whatever it serves is attributed data.
/// </summary>
public interface IKnowledgeReadSession : IDisposable
{
    /// <summary>
    /// The control function the model may call. The runner accepts it only if it is a <c>runtime.</c>-namespace <see cref="RiskLevel.Read"/>
    /// function other than the evidence read; it is never registered as a package tool, never planned and never completes a step.
    /// </summary>
    ToolManifest Manifest { get; }

    /// <summary>
    /// Re-admits an exact, previously served result for this task under current authority, without reading content again.
    /// Sessions that cannot verify issuance, scope and current admission fail closed. This grants no instruction authority.
    /// </summary>
    bool IsEvidenceAdmitted(ChatTurn result, ModelInvocationContext context) => false;

    /// <summary>
    /// Describes the index of what this task may read, under the authority in force when it is called (ADR-0057). It is evaluated
    /// again on every call and its authority outcome is never remembered. <c>null</c> means the session does not support an index, and
    /// the runner then builds the request exactly as it did before the index existed. It confers no read authority: a read is
    /// authorized when it happens.
    /// </summary>
    KnowledgeIndexView? DescribeIndex(KnowledgeIndexQuery query) => null;

    /// <summary>
    /// How many reads the session will still serve in this attempt, or <c>null</c> when it cannot say, in which case the function is
    /// offered as before (ADR-0057).
    /// </summary>
    int? ReadsRemaining => null;

    /// <summary>
    /// The largest share of the input space left after the mandatory context that reference knowledge may take, as configured by the
    /// host (see <see cref="KnowledgeContextBudget"/>), or <c>null</c> when the session states none and the runner applies no dynamic
    /// budget. It is host policy, never data from a pack.
    /// </summary>
    double? ContextShare => null;

    /// <summary>Handles one model reply: serves or refuses a call of this session, or reports that the reply is not its.</summary>
    Task<KnowledgeReadOutcome> HandleAsync(
        ModelResponse response, KnowledgeReadAuthority authority, ModelInvocationContext context, CancellationToken ct = default);
}

/// <summary>The three answers a host gives when asked to open knowledge for a task.</summary>
public enum KnowledgeOpeningStatus
{
    /// <summary>The host binds no knowledge to this task. It runs as a legacy task.</summary>
    NotBound = 0,

    /// <summary>The task is bound to a verified pack and has a session.</summary>
    Bound = 1,

    /// <summary>The task is bound to knowledge that could not be admitted. It must not run.</summary>
    Refused = 2,
}

/// <summary>The result of opening the knowledge of a new or resumed task.</summary>
public sealed class KnowledgeTaskOpening
{
    private KnowledgeTaskOpening(
        KnowledgeOpeningStatus status, IKnowledgeReadSession? session, TaskKnowledgeBinding? binding, KnowledgeAdmissionCode? cause)
    {
        Status = status;
        Session = session;
        Binding = binding;
        Cause = cause;
    }

    /// <summary>What happened.</summary>
    public KnowledgeOpeningStatus Status { get; }

    /// <summary>The session, when <see cref="Status"/> is <see cref="KnowledgeOpeningStatus.Bound"/>. The runner disposes it when the attempt ends.</summary>
    public IKnowledgeReadSession? Session { get; }

    /// <summary>The binding to persist (new task) or the one that was re-admitted (resume), when bound.</summary>
    public TaskKnowledgeBinding? Binding { get; }

    /// <summary>Why the host refused, when <see cref="Status"/> is <see cref="KnowledgeOpeningStatus.Refused"/>; <c>null</c> when it has no closed cause.</summary>
    public KnowledgeAdmissionCode? Cause { get; }

    /// <summary>The task has no knowledge.</summary>
    public static KnowledgeTaskOpening NotBound { get; } = new(KnowledgeOpeningStatus.NotBound, null, null, null);

    /// <summary>The task is bound to <paramref name="binding"/> and reads through <paramref name="session"/>.</summary>
    public static KnowledgeTaskOpening Bound(IKnowledgeReadSession session, TaskKnowledgeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(binding);
        return new KnowledgeTaskOpening(KnowledgeOpeningStatus.Bound, session, binding, null);
    }

    /// <summary>The knowledge could not be admitted, for <paramref name="cause"/>.</summary>
    public static KnowledgeTaskOpening Refused(KnowledgeAdmissionCode? cause) => new(KnowledgeOpeningStatus.Refused, null, null, cause);
}

/// <summary>
/// The host's decision about the knowledge of a task (KNOW-01B, ADR-0056). The host, never the model and never a pack, decides which
/// verified pack a task is bound to and under which tenant; the runtime only persists the result and asks again on resume.
/// </summary>
public interface IKnowledgeTaskHost
{
    /// <summary>Decides, admits and opens the knowledge of a task that is being created. Writes the admission audit event.</summary>
    Task<KnowledgeTaskOpening> OpenForNewTaskAsync(Guid taskId, ActorIdentity actor, CancellationToken ct = default);

    /// <summary>
    /// Admits the pinned content again for a resumed attempt: the same tenant, pack, version and bytes, under the authority that
    /// applies now, or a refusal. Writes the admission audit event.
    /// </summary>
    Task<KnowledgeTaskOpening> OpenForResumeAsync(
        Guid taskId, ActorIdentity actor, TaskKnowledgeBinding binding, CancellationToken ct = default);
}
