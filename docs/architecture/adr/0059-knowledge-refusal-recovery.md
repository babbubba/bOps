# ADR-0059 — Knowledge refusal recovery: a refused read ends the reading, not the task

Status: **Proposed — 2026-10-17. Implemented as part of the experimental Local Agent knowledge pilot** (the only host that composes
knowledge, ADR-0058), under the operator's instruction of 2026-10-17 to make the pilot reliable before the next live check. Like ADR-0058
it is **not accepted**: acceptance is the operator's separate act. No D-number is assigned and `agentic/06-decisions.md` is not edited.
Date: 2026-10-17
Base: worktree `bOps-knowbench01a`, branch `codex/know-bench-01a`, HEAD `1ddde56` plus uncommitted changes.
Refines: [ADR-0056](0056-governed-knowledge-read-and-durable-resume.md) §3 ("the rejection bound [is] enforced") · [ADR-0057](0057-knowledge-discovery-index.md) AF-04 ("withdrawn once the session says no read is left") · [ADR-0058](0058-local-agent-knowledge-pilot.md) §3.8 (the budget keys).
Preserves: every admission, authority, pin, integrity and attribution rule of ADR-0054…0058; ADR-0038 first-call-wins; no new tool, no configuration key; the only audit changes are the appended result code `InfrastructureFailure = 12` (§2) and the `Withdrawn` flag.

## 1. The problem

`runtime.knowledge_read` is a runtime control function: it is optional, it reads reference data and it completes nothing. The bridge counts
**every** refusal, whatever the cause, in one counter, and when `rejections > MaxRejections` (default 4: the fifth refusal) the runner ends
the whole task as `RuntimeFailure` ("the model keeps sending control requests that return nothing").

That treats a model that cannot spell a ref like an attack on the runtime. The operational tool of the step is untouched and still runnable,
the read was optional, and the task is destroyed anyway. Observed shape of the failure: a model that misreads the index (`K9`, `K1.R9`, a
wrong operation, a page out of range) five times, or that keeps calling the function after its read budget was spent, fails a diagnosis
that needed none of it.

## 2. Classification

Every refusal falls in exactly one class. The class is decided by the bridge from the closed result code (and, for one code, from where it
was raised); it never depends on text, on the model or on the pack.

| Result code | Where it comes from | Class |
|---|---|---|
| `Malformed` | arguments are not the closed set, a ref has the wrong shape, an operation is unknown | **Recoverable** |
| `UnknownSkill`, `UnknownResource` | a name or ref that is not in the pin (indistinguishable from a no-longer-authorised one, ADR-0057 R.5) | **Recoverable** |
| `OutOfRange` | a discovery page beyond the last; no read is spent | **Recoverable** |
| `TooLarge` | the verified content is over the per-read bound; nothing returned | **Recoverable** |
| `Unsupported` | the call names a framework function, a script runner or another runtime function; nothing is invoked | **Recoverable** (audited every time) |
| `InfrastructureFailure` | the framework function threw while reading a snapshot whose integrity was just verified, or the host cannot normalize text for a discovery page | **Recoverable** (infrastructure; nothing returned) |
| `LimitExceeded`, `NotOffered` | the read budget is spent, the function is not offered, or the step is not eligible | **Unavailable** |
| `IntegrityMismatch` |  the content produced is not the content pinned | **Integrity** |
| `NotAuthorized`, `SnapshotUnavailable` | the authority in force no longer covers the pack, or the snapshot is not intact | **Integrity** |
| `Success`, *same item again* | a duplicate read is served (`Repeat = true`) and spends a read | not a refusal |

The infrastructure case has its own audit code, `InfrastructureFailure = 12` (appended; no existing value changes). It was first audited as `IntegrityMismatch`, which
made a host fault indistinguishable from tampering for operators and for the benchmark safety check. `IntegrityMismatch` now means only: the bytes differ from the pin.

## 3. Decision

1. **Recoverable refusals end the reading, not the task.** They are audited exactly as before. They are counted apart; when their count
   exceeds `MaxRejections` the session **withdraws the function for the rest of the attempt**: `ReadsRemaining` becomes 0, so the runner
   stops offering it (ADR-0057 AF-04, unchanged mechanism) and the step goes on with its operational tool. The refusal that causes the
   withdrawal is audited with `Withdrawn = true` and the model is told in plain words. The task is not failed.
2. **Unavailable calls are bounded hard.** A call to a function that is spent, withdrawn or not offered is a protocol violation by the
   model, not a mistake about a ref. More than two of them in an attempt (`MaxUnavailableCalls = 2`, a constant, not configuration) is
   `ProtocolBoundExceeded`: the task fails exactly as it does today. This is the loop protection after a withdrawal.
3. **Integrity refusals keep the old rule, unchanged.** `NotAuthorized`, `SnapshotUnavailable` and a content-versus-pin mismatch are
   security signals, not model errors. They are counted apart; more than `MaxRejections` of them is `ProtocolBoundExceeded`, as today. No
   security refusal is ever turned into a recoverable event, and withdrawing does not weaken any of them: authority is still asked on every
   read, and evidence from a withdrawn authority is still dropped at the next request (ADR-0056).
4. **A duplicate read is not changed.** It is served, audited with `Repeat = true` and spends one of `MaxReads`; the budget bounds a read
   loop, and the spent budget withdraws the function (decision 1's mechanism). Serving it again is deliberate: the earlier result may have
   been compacted out of the context. The benchmark's `Repeat` instrumentation therefore measures the same thing as before.
5. **The refusal reply is actionable and fixed.** It keeps its prefix (`Runtime control call rejected: <Code>.`), is chosen per class from
   closed sentences, contains no pack text and no model text, says the reading is optional, says what to do next (use a ref exactly as
   listed in the index, or continue with the current step's operational tool), and, on the withdrawing refusal, says the function is no
   longer available.
6. **Nothing else moves.** Same result codes (plus the appended `InfrastructureFailure`, §2), same audit event types, same budget keys and ceilings (ADR-0058 §3.8), same first-call-wins,
   same admission and resume. Only the consequence of the fifth recoverable refusal changes.

## 4. Consequences

- A model that mishandles refs, pages or the budget keeps its task. Worst case before the task would have ended: `MaxRejections + 1`
  recoverable refusals (default 5), then at most 3 more calls to the withdrawn function: at most 8 model calls spent on a function the
  model was told is gone. They are bounded by the same token and attempt budgets as every other call.
- `RuntimeFailure` from the knowledge protocol now means: the model kept calling a function after being told it was gone, or kept hitting
  integrity or authority refusals. Both are real faults.
- The index control text of a withdrawn session reads "The read budget for this attempt is spent; the index is shown for reference only."
  (ADR-0057 Part C, a pinned wording). It is not exact for a withdrawal after refusals; the withdrawing refusal's own reply is. Changing
  Part C is outside this ADR.
- Operators who relied on "five bad refs fail the task" as a signal now see `Withdrawn = true` in the audit instead.

## 5. Alternatives rejected

- *Keep one counter and raise the default.* Moves the cliff; still destroys the task at it.
- *Treat every refusal as recoverable.* A tampered or withdrawn pack would be probed forever; rejected by the operator's instruction and by S1.
- *Refund the read budget on a refusal.* Invites a loop; the budget is the loop bound.
- *A new result code for duplicates.* An audit-schema change for a distinction `Repeat` already carries. (A code for infrastructure failures was first rejected here and then adopted as `InfrastructureFailure = 12`, §2, because a host fault audited as `IntegrityMismatch` read as tampering.)
- *Re-open the function after a pause.* Non-monotone state inside an attempt is harder to audit; a resume starts a fresh session anyway.

## 6. Still open

- ~~The infrastructure failure shares `IntegrityMismatch` with the hash mismatch.~~ Done: `InfrastructureFailure = 12`. The benchmark safety check still rejects a run that contains one (the run did not measure what it claims).
- The "spent" wording of Part C for a withdrawn session (§4).
- A `KnowledgeReadAuditEvent` is not written for a knowledge call that follows an evidence read in the same reply (it is `NotHandled`, then
  counted by routing telemetry only). Documented in `docs/benchmarks/knowledge-discovery-investigation.md` §5; unchanged here.
- Acceptance of this ADR, of ADR-0058 and of D-050 is the operator's.
