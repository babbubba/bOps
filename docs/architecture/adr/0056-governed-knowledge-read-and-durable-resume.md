# ADR-0056 — Governed knowledge read in the runner and durable resume (KNOW-01B)

Status: **Proposed — 2026-10-12** (continues ADR-0055; ADR-0054 is still Proposed on `docs/arch-03-maf-convergence-decision`)
Date: 2026-10-12
Refines: ADR-0055 §2.4–§2.7 · ADR-0054 §5
Preserves: ADR-0052, ADR-0053, ADR-0055, the ContextGate of AF-CONTEXT-01 / AF-TURN-01B, ADR-0038 (first call wins), ADR-0040 (resume),
ADR-0046 (runtime control functions), invariants I-1…I-10 of ADR-0053.
Base: KNOW-01A `b027459`. Report: [`know-01b-runtime-knowledge-read.md`](../know-01b-runtime-knowledge-read.md).

## 1. Context

KNOW-01A produced verified packs, tenant snapshots, serializable pins and a bridge that serves them, but `AgentRunner` neither
offered nor served `runtime.knowledge_read`, and no task state carried a pin, so a resumed task could not prove it was reading the
content it started with. This decision fixes how the runner uses the bridge, what a task persists, and what a resume may and may
not do. It adds no tool, no orchestrator, no second executor and no activation in any production host.

## 2. Decision

1. **`runtime.knowledge_read` is a runtime control function, not a registered package tool.** It has the same standing as
   `runtime.evidence_read` (ADR-0046): `Risk = Read`, offered *beside* the step's tool view, never in the `ToolRegistry` (which reserves
   the `runtime.` namespace), never planned (a plan step naming it is rejected as an unknown tool like any other), and it never
   completes the step. It passes through the ordinary gates that apply to a control function: it is served only when the model-call
   view that produced the reply offered it; the first runtime control call of a reply wins and every other call id is answered
   `Not executed` (ADR-0038); malformed or unknown-argument calls are rejected; the read budget and the rejection bound are
   enforced; every attempt, refusal included, is audited; and the content is attributed data in a `Tool` turn. What a package tool
   additionally passes (policy, approval, journal) does not apply because a read of an admitted pack changes nothing on the machine.
   > **Refined by [ADR-0057](0057-knowledge-discovery-index.md) (§G.2):** the closed argument set is extended with `ref`, `query` and
   > `page`; `Tool` remains true for content actually read; the pre-read index is the single exception, a `User`-role data turn
   > governed by S5 (external reference data).
2. **The runtime depends on two small contracts, not on the Knowledge engine.** `bOps.Runtime` gains `IKnowledgeTaskHost` (open the
   knowledge session of a new or resumed task) and `IKnowledgeReadSession` (handle one model reply). The only implementation lives in
   `bOps.AgentFramework` over the KNOW-01A bridge. `bOps.Runtime` still references only `bOps.Abstractions`; the session declares its
   own control manifest, which the runner accepts only if it is a `runtime.`-namespace `Read` function other than the evidence read.
3. **The pin is bOps's, taken at admission, and lives in `TaskState`.** `TaskState.KnowledgeBinding` (`TaskKnowledgeBinding`, an
   additive `bOps.Abstractions` record) holds the authorising identity, the tenant, the pack id, version, kind, publisher, key id,
   manifest hash, the hash of every payload file and the verified snapshot id. It is written by the runner from the host's
   admission result before the first model call, in the same first save as the task, never from a model value; the tool has no
   tenant, pack or version argument. A task without a binding is a legacy task and gets no knowledge, however the host is composed.
4. **A pin is not an authorisation.** Every read and every resume re-checks the *current* authority: the provider registration for
   the tenant, the host grant of that exact pack version and kind to that tenant, the trust store (unknown, revoked key or revoked
   publisher), and the integrity of the snapshot. Anything that fails is a refusal with a cause code. This supersedes the ADR-0055
   consequence that revocation applies only to new admissions: it now applies to the next read of a running task.
5. **Resume re-admits the pinned content or fails closed.** `ExecuteAcquiredResumeAsync` re-runs the full admission (provider, grant,
   source read, signature, hashes, snapshot) with the stored pin as the expected pin and the *resuming* actor's tenant. It never
   resolves "latest", never admits a different pack, never changes tenant, and accepts only bytes whose manifest hash and file hashes
   equal the pin. If admission is refused for any reason (revoked key or publisher, grant withdrawn, other tenant, republished or
   altered content, source or snapshot unavailable, no knowledge host composed) the attempt ends `Failed` with the new
   `TaskTerminalKind.KnowledgeUnavailable`, the cause code is on the failure step and in the audit, and nothing runs. The task stays
   resumable once the operator restores the authority; the pin is unchanged. A new task whose admission is refused fails the same
   way before planning and records no binding.
   > **Clarified by [ADR-0057](0057-knowledge-discovery-index.md) (§G.2, B1):** "current authority" covers the grant and the trust store
   > only as far as the host replaces them. `KnowledgeGrants` and `KnowledgeTrustStore` are immutable and captured by
   > `KnowledgeAdmissionService`, so within one attempt only provider deregistration, a tenant change and snapshot tampering are observed;
   > a withdrawn grant or a revoked key or publisher is seen at the next resume. Immediate revocation is not claimed.
6. **Provenance.** A served turn keeps its origin (by signed pack kind), pack id, version, manifest hash, tenant and provider id, and
   now also the operation and the item and content hash read (`ContextProvenance.Operation/Item/ContentHash`, optional). Model-call
   audit grouping ignores the three read-level fields, so the audit schema of the model call is unchanged. `KnowledgeReadAuditEvent`
   gains `CauseCode` and `KnowledgeReadResultCode` gains `NotAuthorized` and `SnapshotUnavailable`. No event carries content.
   > **Refined by [ADR-0057](0057-knowledge-discovery-index.md) (§G.2):** `Operation` also takes the value `index`; the model-call
   > grouping keeps ignoring it.

## 3. Alternatives considered

- **Register the tool in `ToolRegistry` with an `ITool`.** Rejected: the registry reserves `runtime.`; a registered tool is plannable,
  completes steps and is a second execution path. A control function is already the established way to give the model a read that
  grants nothing.
- **Reference `bOps.Knowledge` from `bOps.Runtime`.** Rejected: ADR-0055 and an architecture test keep the Knowledge engine behind the
  MAF adapter; two interfaces cost less than a dependency edge.
- **Store the pin as an opaque string, or only the manifest hash.** Rejected: the per-file hashes are what makes "altered content" a
  pin mismatch instead of a trust decision, and a typed record can be inspected and audited.
- **Continue without knowledge when the pack is no longer available.** Rejected: the model would silently decide on less than it was
  authorised to consult; the cause must reach the operator.
- **Keep the model's knowledge turns in the persisted history.** Rejected: they are reference data for the current logical call; a
  resume re-reads from the pinned snapshot, so content is never stored twice outside the tenant's snapshot.

## 4. Consequences

- Additive `bOps.Abstractions` members only: `TaskKnowledgeBinding`, `TaskState.KnowledgeBinding`, `TaskTerminalKind.KnowledgeUnavailable`,
  two `KnowledgeReadResultCode` values, `KnowledgeReadAuditEvent.CauseCode`, three optional `ContextProvenance` members.
- One pack per task. A second pack, Experience search and a pack picker are not designed here.
- The reads of one attempt share one budget; a resume starts a new attempt and a new budget.
- Nothing is activated: the host, policy and session classes are internal to `bOps.AgentFramework`, no production host composes
  them, and no configuration key exists.
- Not decided here: how a Coordinator chooses the pack for a task (the host policy is an input), pack distribution, snapshot
  garbage collection, live trust-store reload, Experience write path.
