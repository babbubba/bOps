# ADR-0053 — MAF convergence: ownership matrix, single OSS engine and knowledge boundaries (ARCH-01)

Status: **Proposed — 2026-10-12** (documentation-only; no code, dependency or configuration change)
Date: 2026-10-12
Refines: [ADR-0052](0052-microsoft-agent-framework-bops-boundary.md) (does not supersede it)
Preserves: ADR-0014, ADR-0038, ADR-0040, ADR-0045, ADR-0046, ADR-0047, ADR-0050, ADR-0051
Plan: `agentic/_plans/2026-10-11-maf-knowledge-convergence.md` (step 7). Next: `agentic/_tasks/2026-10-12-arch-02-maf-parity-spike.md`.
Runtime reference: `998e9523e2ffda0e1b6ce663d152dd5416ecbee7` (branch `fix/af-toolroute-01-tool-routing`).

## 1. Context

ADR-0052 already settled *whether* bOps uses MAF and the first-order split ("MAF owns generic agent
infrastructure; bOps owns operational authority"). AF-01…AF-05H implemented the context, compaction and
governed-inference slice: `bOps.AgentFramework` pins `Microsoft.Agents.AI` `[1.24.0]` and contains only
`GovernedChatClient`, `GovernedSummarizationChatClient`, `MafChatTurnMapper` and `MafContextProjector`.
**No planning, tool-calling, workflow, session or checkpoint capability of MAF is used today.**

BENCH-00 1×3 (n=1 per cell, no statistics) left four runtime defects open: premature finalization (B03),
planned-step coverage, EvidenceRead repetition (39 proposed vs 16) and diagnostic grounding. They belong to
bOps authority code. They must not be "solved" by moving that code into a framework whose equivalent
behaviour is unproven.

This ADR answers the question ADR-0052 left open — *which remaining responsibilities, if any, can move to MAF* —
and fixes the engine/pack/adapter boundaries before KNOW-01. It decides ownership **targets and test criteria**;
it does not adopt anything beyond ADR-0052. Adoption of any further MAF capability is ARCH-03's decision.

## 2. Vocabulary for the matrix

| Mark | Meaning |
|---|---|
| **C** | Confirmed: exercised by code in this repository (AF-01…05H), or fixed by ADR-0052 research baseline 1.24.0. |
| **D** | Documented MAF capability, **not exercised** in bOps. Not evidence of equivalence. Verify in ARCH-02. |
| **K** | Must remain in bOps regardless of MAF capability (safety/contract). |
| **V** | Unverified: behaviour that decides parity; ARCH-02 experiment id in the last column. |

Owner values: **MAF**, **bOps** (runtime core), **Adapter** (`bOps.AgentFramework`-style assembly translating
between the two; contains no authority).

## 3. Ownership matrix

"Proposed owner" is the **target for ARCH-02/03**, not a statement about the current code. Where MAF is proposed
it is always behind the adapter and below bOps authority.

| # | Capability | Proposed owner | MAF (C/D) | Stays in bOps (K) | To verify (V) → exp. |
|---|---|---|---|---|---|
| 1 | Agent lifecycle, planning, replanning | **bOps** (state machine); MAF optional as *executor of a single governed turn* only | C: none used. D: `AIAgent`/`AgentSession`, Workflows (graph executors, supersteps). | `AgentPlan`, revisions, cursor, conditional steps, replan budgets, transactional replan (ADR-0014/0046/0050). Plan is persisted fact, not framework state. | Whether a Workflow can host the bOps plan state machine with the persisted plan as sole authority, without a second cursor → E7 |
| 2 | Tool routing and function calling | **bOps** decides visibility and executes; **Adapter** may expose the bOps single-tool view as MAF `AIFunction`s whose invocation is a *proposal* | C: `IChatClient` adaptation over the broker. D: `FunctionInvokingChatClient` auto-loop. | Exact `ExpectedTool` view (ADR-0046); first-call-wins and `UnexecutedToolCalls` (ADR-0038); one provider-wire name map (`ToolWireNames`). | Auto-invoke loop must be disabled or replaced by "terminate on first call, hand to bOps". Parallel-call handling and wire-name aliasing → E1 |
| 3 | Step authority and contract validation | **bOps** | none | `ExpectedArguments` subset-equality, `SemanticMismatch`, one argument-validation correction, manifest validation, ordering of checks (ADR-0047/0050). | Whether MAF middleware ordering can be placed strictly *before* any invocation → E1, E2 |
| 4 | Context, compaction, EvidenceRead | **MAF** (compaction/summarization, per ADR-0052) / **bOps** (`runtime.evidence_read`, protected context) | C: `Microsoft.Agents.AI` compaction strategies, message mapper, governed summarizer. | Raw evidence persistence, EvidenceRead as exact drill-down, protected context (ADR-0052 §7, §13), summary ≠ evidence. | Evidence reuse policy (proposed/served/duplicate) is *not* a MAF concern; AF-EVID-01 stays in bOps. Only whether compaction keeps evidence IDs addressable → E6 |
| 5 | Finalization; recovery from empty/truncated output | **bOps** | D: retry/continuation middleware (generic). None checks plan coverage. | "No `Completed` with unresolved mandatory steps"; early-final → replan (ADR-0046 amendments); `finish_reason=length` classified, bounded, fail-closed (AF-05H2). | Whether MAF surfaces `finish_reason`/empty content intact to the broker, rather than swallowing or retrying silently → E3 |
| 6 | Policy, entitlement, approval | **bOps** | D: `ApprovalRequiredAIFunction` / human-in-the-loop request-response. | All of it (ADR-0030/0036, security rules). MAF approval is UX plumbing at most, never a grant. | Whether an approval round-trip can be driven solely by the bOps approval record and survive restart → E5 |
| 7 | Audit, persistence, checkpoint, resume | **bOps** | D: session/thread serialization, Workflow checkpoints. | `TaskState`, attempt fencing, resume state machine (ADR-0017/0040), model-call audit (ADR-0013/0039). MAF state, if used, is a non-authoritative cache that may be discarded. | Rebuild MAF session solely from persisted task on resume with identical next model request → E4 |
| 8 | Mutation journal, unknown outcome, fencing | **bOps** | none | Entire ADR-0051 contract; unknown outcome never auto-repeated; reconciliation by operator/verification. | Any MAF retry, superstep re-run or checkpoint replay must be provably unable to re-invoke a mutating tool → E5 |
| 9 | Provider/model abstraction and fallback | **bOps** broker + project-owned `IChatModel`; **Adapter** exposes it as `IChatClient` | C: `GovernedChatClient` over `IModelInvocationBroker`. | Pinning, fallback chain, credentials, retry/`Retry-After`, budgets, audit (ADR-0045/0052 §5). MAF never holds credentials. | Nothing blocking; confirm no MAF component bypasses the client (E1 observes) |
| 10 | Grounding and diagnostic validation | **bOps** (engine) with **Evidence & grounding** module (§4.4) | none; summaries are untrusted | `EvidenceFact`, completeness, limitations, observed/correlated/hypothesis separation (ADR-0042). | Out of MAF scope; AF-GROUND-01 is a bOps task. Only check that MAF context does not drop grounding blocks → E6 |
| 11 | Knowledge, Experience, Playbook | **bOps OSS engine** (contract, loader, retrieval, declarative interpreter) | D: `AIContextProvider`/memory as *retrieval transport* only | Signing, versioning, pinning, tenant separation, "knowledge is context, never current proof". Playbook step = ordinary `PlannedStep` through the normal path. | Whether `AIContextProvider` is useful vs. plain context injection by bOps; decided at KNOW-01, not ARCH-02 |

**Reading the matrix.** Row 4 and row 9 are the only rows where MAF is already the implementation; both are C.
Rows 1, 2, 7 are the only candidates for further movement and each requires ARCH-02 evidence. Rows 3, 5, 6, 8, 10
are **K**: MAF may be a transport, never the owner. No MAF capability is assumed equivalent to its bOps
counterpart merely because the names resemble each other (e.g. MAF "workflow checkpoint" ≠ `TaskState`; MAF
"approval" ≠ bOps entitlement-plus-approval; MAF "function invocation" ≠ first-call-wins step authority).

## 4. Target architecture and boundaries

```text
            Host (Local Agent | Coordinator)          bOps.Commercial adapters (private)
                       │                                       │  tenant/RBAC, entitlement source,
                       ▼                                       │  pack catalog/registry, pgvector
        ┌──────────────────────────────────────────────────────▼───────────────┐
        │ 4.1 OSS engine (single)  bOps.Runtime / Policy / Memory / Audit      │
        │   task state machine · governed tool executor · model broker         │
        └───────┬───────────────────┬───────────────────┬──────────────────────┘
                │                   │                   │
   4.2 MAF adapter assembly   4.3 Tool/Skill packages   4.4 Evidence & grounding
   (context, compaction,      (manifests, facts,        (EvidenceFact, limits,
    IChatClient over broker)   verification)             grounding checks)
                                        ▲
                       4.5 Knowledge / Experience / Playbook engine
                           (reads signed packs; feeds context + declarative steps)
```

### 4.1 One OSS engine, two hosts

Local Agent and Coordinator embed the **same** engine. A host supplies identity, storage location, entitlement
source and UI; it does not fork planning, step authority, policy, journal, resume or grounding. Any behaviour
needed by only one host is an extension point (interface in the engine, implementation in the host), not a
second code path. `bOps.Commercial` depends on the OSS contracts and extends through published interfaces and
versions; it contains no copy of engine logic. A change that requires editing engine behaviour for a commercial
reason goes through the OSS repository first.

### 4.2 Microsoft Agent Framework

Sits behind the adapter assembly (ADR-0052 §4). No MAF/`Microsoft.Extensions.AI` type in `bOps.Abstractions`
or in the plugin SDK. MAF state is never authoritative. The adapter contains no policy, no tool execution, no
cursor, no budget. If an ARCH-02 experiment shows MAF can only work by owning one of those, the capability stays
in bOps (rows 3, 5, 6, 8 are not negotiable).

### 4.3 Tool/Skill packages

Unchanged by this ADR: declarative manifests, `VerificationSpec`, serializable `ToolCallResult`, package-owned
fact vocabulary, runtime opaque to domain keys (ADR-0052 §14). Packages never see MAF types.

### 4.4 Evidence and diagnostic grounding

Stays in the engine. Authority order is that of ADR-0052 §12.3: raw evidence > facts/completeness/verification >
grounding/limitations > any summary. Knowledge may *suggest where to look*; it can neither create a fact nor
satisfy a mandatory observation.

### 4.5 Knowledge / Experience / Playbook engine

OSS, in the engine's trust domain, and **independent of the model framework** (it must work if MAF is replaced).
Contract-level requirements only (APIs deferred to KNOW-01):

| Requirement | Contract-level rule |
|---|---|
| Packs | Signed data, not assemblies/scripts. Manifest declares `id`, `version`, `kind` (knowledge / experience / playbook), engine-compat range, dependencies, content hash, signature. |
| Install/update | Independently of Core and Skills within a compatible closure; atomic activate; rollback to previous version. Trust store validates the signature before activation. |
| Pinning | Each task records `(packId, version, hash)` for every pack it used at start. Upgrades never alter a running or resumed task; resume re-reads the pinned version or fails closed with a recorded cause. |
| Tenant separation | Customer experience/memory is tenant-private, physically or by enforced key partition; shared packs are read-only and tenant-neutral. Retrieval takes a tenant scope as a required argument. |
| Provenance in output | Any statement derived from a pack is labelled as knowledge, with pack id/version, and kept distinct from live evidence. |
| Playbook | Declarative; compiles to ordinary `PlannedStep`s (`ExpectedTool` + `ExpectedArguments` + `Activation`). Every step passes validation, policy, entitlement, approval, journal and verification. A playbook cannot name a tool the delegation does not already authorize. |
| Diagnosis support | Knowledge raises hypotheses and checklists; a conclusion needs corroborating live `EvidenceFact`s or is reported as a hypothesis with its limitation. |

### 4.6 Commercial adapters

Private repository. May supply: tenant/RBAC, entitlement provider, durable multi-tenant store (pgvector),
pack registry/distribution, signing infrastructure, fleet orchestration. May not: replace or wrap
policy/approval/journal logic, add an alternative runner, or ship a modified copy of OSS runtime code.

## 5. Invariants no MAF migration may violate

Beyond ADR-0052 §21 (which stands), each is a test obligation in ARCH-02 and a regression gate in ARCH-03:

| # | Invariant |
|---|---|
| I-1 | No tool executes outside bOps authority. A MAF-side function body for an operational tool never performs the operation; it only returns a proposal consumed by the bOps executor. |
| I-2 | Policy, entitlement and approval cannot be bypassed, reordered, cached across calls or satisfied by MAF state/summaries. |
| I-3 | A mutation with unknown outcome is never repeated automatically — not by MAF retry, superstep replay, checkpoint restore or resume (ADR-0051). |
| I-4 | Every model call, tool decision, denial, rejection, timeout and replan is audited with the same fields as today; MAF-internal inference is not exempt (ADR-0052 §5). |
| I-5 | Resume reconstructs the next model request from persisted task state only; a lost or stale MAF session/checkpoint changes nothing observable. |
| I-6 | No `Completed` while a mandatory planned step is unresolved; a premature final replans or fails closed with a recorded cause. |
| I-7 | Failure handling is bounded: retries, corrections, replans, EvidenceRead, steps, tokens and duration keep explicit budgets; no unbounded framework-internal loop. |
| I-8 | MAF cannot widen the tool surface or privilege: no hosted search, file, shell/code, MAF skills or background agents (ADR-0052 §15); no implicit delegation expansion. |
| I-9 | Provider pinning/fallback and credentials stay in the broker. |
| I-10 | Equivalence is claimed only for a behaviour demonstrated by an ARCH-02 experiment with recorded evidence. Absence of a counter-example is not parity. |

## 6. Decisions

1. **D1 — Ownership.** The matrix in §3 is the target ownership for ARCH-02/03. Rows marked K are not up for
   migration. Rows 1, 2, 7 may move partially only on ARCH-02 evidence and ARCH-03 approval.
2. **D2 — Proposal-only MAF tool path.** If MAF function calling is used at all, bOps exposes the current step's
   tool(s) and the framework's loop terminates at the first call; execution is bOps's. No MAF auto-invoke.
3. **D3 — No framework-owned persistence.** MAF sessions/checkpoints, if adopted, are disposable caches.
   `TaskState` and the audit log remain the only authority (extends ADR-0052 §16).
4. **D4 — One engine.** Local Agent and Coordinator share one OSS engine; `bOps.Commercial` extends it through
   published interfaces and does not duplicate it.
5. **D5 — Knowledge engine is OSS and framework-independent.** It feeds the engine as context and declarative
   steps; it never replaces live evidence. Pack signing, versioning, per-task pinning and tenant separation are
   mandatory contract properties (details in KNOW-01).
6. **D6 — Open defects stay in bOps.** AF-EVID-01, AF-FINAL-01 and AF-GROUND-01 are bOps work, not MAF migration
   prerequisites or beneficiaries. ARCH-02 must not be used to fix them.
7. **D7 — Evidence-based adoption.** ARCH-03 adopts a further MAF capability only if its ARCH-02 experiment met
   the parity criterion and no invariant I-1…I-10 failed. Otherwise the capability stays in bOps, and this is a
   valid, non-failure outcome.

## 7. Trade-offs

| Choice | Gain | Cost / risk |
|---|---|---|
| Keep planning, step authority, resume in bOps | Behaviour already hardened and benchmarked; no regression risk from re-implementation | We keep maintaining a custom state machine; ADR-0052's aim of shrinking `AgentRunner` is met only by extraction (§17 there), not by replacement |
| Proposal-only MAF tools | Safety invariants I-1/I-2 hold by construction | Loses MAF's convenient auto-loop; extra adapter code; possible friction with MAF middleware assumptions |
| MAF state as disposable cache | Resume correctness independent of MAF versions | Possible duplicate work rebuilding sessions; performance cost on resume |
| Knowledge engine independent of MAF | Survives framework change; testable alone; fits Local and Coordinator | Does not reuse MAF memory/context-provider abstractions unless KNOW-01 shows a benefit |
| Defer Workflows/Harness | Avoids a second plan/cursor authority | May forgo MAF features that later become the mainstream path; mitigated by re-running ARCH-02 on a newer pinned version |
| MAF 1.24.0 pinned, compaction experimental | Isolated, reversible | Upgrades need re-validation (ADR-0052 §20) |

## 8. Out of scope

Any implementation, package or API design for packs (KNOW-01), playbook content (KNOW-02), A/B/C knowledge
evaluation (KNOW-03), Coordinator schema (V1.4-D), fixes for the open benchmark defects, and any change to the
ADRs listed under *Preserves*. If ARCH-03 later changes an ownership row, that ADR amends §3 here.

## 9. Open items (classified, not researched further)

- Behaviour of MAF function-invocation, middleware ordering, approval and checkpoint features at 1.24.0 is
  **documented-not-verified** in this repository; ARCH-02 verifies by experiment, not by reading documentation.
- Whether a newer MAF release changes any D-class capability is out of this ADR; ARCH-02 pins one version.
- Exact pack manifest schema, trust-store location and tenant partition mechanism → KNOW-01.
