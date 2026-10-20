# ADR-0054 — MAF convergence decision: B+ with a mandatory context-governance gate (ARCH-03)

Status: **Proposed — 2026-10-12** (documentation-only; awaits operator acceptance)
Date: 2026-10-12
Refines: [ADR-0053](0053-maf-convergence-architecture-boundaries.md) §3 rows 2, 4, 11 and D2/D5 · [ADR-0052](0052-microsoft-agent-framework-bops-boundary.md) §15–16
Preserves: ADR-0014, 0038, 0040, 0045–0047, 0050, 0051 and invariants I-1…I-10 of ADR-0053.
Evidence (not duplicated here): [ARCH-02A](../arch-02a-maf-parity-report.md) · [ARCH-02R](../arch-02r-maf-deep-reassessment.md) · [ARCH-02S](../arch-02s-maf-bplus-conformance.md).
Spike code lives only in commits `3aa8608`, `ce8ed54`, `4572ae0` (tags `archive/arch-02{a,r,s}-spike`); it is never merged.

## 1. Decision

**B+ is approved as the target architecture, conditionally.** Adoption of each capability is gated as in §3; nothing
in §3 marked CONDITIONAL may reach production before its gate in §4 passes (D7 of ADR-0053: evidence-based adoption).

B+ = one bOps engine; each model turn is hosted by a **stateless** MAF `ChatClientAgent`
(`UseProvidedChatClientAsIs = true`, no `FunctionInvokingChatClient`, declaration-only tools, no `AgentSession`
reuse); context is assembled by the MAF `AIContextProvider` pipeline; the leaf is the bOps `GovernedChatClient` →
`ModelInvocationBroker`. Planning, step authority, validation, policy, entitlement, approval, journal, fencing,
resume, finalization and audit **stay in bOps** until equivalent evidence exists (a Workflow or Harness is not such
evidence). MAF can propose, never execute: a model-emitted call is a surfaced `FunctionCallContent` consumed by the
bOps executor.

Rejected for the task loop: A-with-auto-invoke, C (MAF Workflow), D (Harness/Todo/Mode/Loop/FileMemory/ToolApproval).
Workflows are re-assessed only for the V1.4-D Coordinator, after ND-3.

## 2. Scope of the evidence — what is and is not proven

All experiments are offline, scripted-model, MAF 1.24.0, synthetic one-turn/short scenarios. Equivalence with the
**current `AgentRunner`** end to end (persisted events, audit rows, terminal states, streaming) is **not** claimed anywhere
(I-10). "Parity" below means: MAF contributes nothing the runner lacks, or carries data intact.

## 3. Capability matrix (verified capabilities only)

Class: **ADOPT** · **CONDITIONAL** (gate) · **REJECT** · **ND** (not determined). Proof: **F** = full parity vs bOps code
demonstrated; **S** = synthetic single-scenario only; **—** = not exercised.

| # | MAF capability | Result (report) | Proof | bOps code kept | bOps code removable | Prerequisite | Decision |
|---|---|---|---|---|---|---|---|
| 1 | Auto tool loop (FICC) default | Runs bodies, hidden 2nd call, absorbs retries (02A E1a/c, E2a) | S | all step authority | none | — | **REJECT** (I-1, I-4, I-7) |
| 2 | `ChatClientAgent` as-is, no FICC, one inner call per run | 1 call, 0 bodies, calls surfaced; providers still run (02R R1) | S | `ToolWireNames`, first-call-wins, correction budget, verdicts | none from authority code | pipeline arch test (C6) | **ADOPT** |
| 3 | Agent function middleware + `Terminate` | Works, leaves synthetic result bOps must strip (R2) | S | same | none | — | **REJECT** as primary path; fallback only |
| 4 | Declaration-only tools / `TerminateOnUnknownCalls` | Safe, bOps still decides (02A E1b/d/f) | S | all | none | AF-TURN-01 (leaf accepts declarations) | **ADOPT** (inside #2) |
| 5 | Usage / `finish_reason` / empty-text propagation | Intact, no hidden retry (02A E3, non-streaming) | S | `EarlyFinalRecovery`, plan-coverage gate | none | streaming parity = ND-7 | **ADOPT** (carrier) |
| 6 | Stateless resume (no session) | Byte-identical request after restart; reused session detected at leaf (02S G1) | S | `TaskState`, projection, fencing | none | leaf check C1; recorded-request test vs current runner (ND-1) | **ADOPT**, parity unproven |
| 7 | `AIContextProvider` pipeline (composition) | Context reaches broker, tokens/calls accounted, 0 provider model calls (02S G2) | S | protected-context rules, budgets | ~150–400 lines of prompt assembly *relocated* (estimate) | **G2** | **CONDITIONAL** |
| 8 | Provider provenance at the leaf | Partial: tag `AIContextProvider`+type name only; lost in mapper/instructions (G2) | S | provenance contract (C2) | none | **G2** | **CONDITIONAL** |
| 9 | Role/tenant safety of providers | Not a MAF guarantee: System elevation, forged tags, shared-cache tenant leak (G2) | S | admission, decorators, per-task construction | none | **G2** | **CONDITIONAL** — MAF is composition, not a trust boundary |
| 10 | `AgentSkillsProvider` + skills sources (SKILL.md model, filtering/dedup/caching) | Adds 3 tools by default (R3); content/L1 usable behind guards | S | pack envelope, `ToolSurfaceGuard`, pin | custom parsing/discovery/caching (not yet written, est. 1.5–3k lines avoided incl. retrieval) | **G2** + G3 contract (§5) | **CONDITIONAL** |
| 11 | Native progressive loading (`load_skill` via FICC) | Needs FICC → unavailable in B+ (G3) | S | — | — | — | **REJECT** native; bOps-invoked adapter instead (§5) |
| 12 | `run_skill_script`, script runners | Never exposed; adapter rejects (G3) | S | manifest rejects packs with scripts | — | — | **REJECT** |
| 13 | `TextSearchProvider`, VectorData (Experience read) | Documented, not exercised | — | curated write path, tenant partition | custom retrieval | ND-5; **G2** | **ND → CONDITIONAL** in KNOW-01 |
| 14 | `ChatHistoryMemoryProvider`, FileMemory, FileAccess, Todo, AgentMode, LoopAgent, ToolApproval, HarnessAgent | Documented overlap with bOps K-rows; auto-write/persist/standing approvals | — | `TaskState`, plan, approval | — | — | **REJECT** (I-2, I-3, I-7, I-8) |
| 15 | Workflows (routing, `InterceptUnterminatedFunctionCalls`, checkpoints) | Routing works (R4); checkpoint replay hazard (docs) | S | state machine, journal | none | ND-3 | **NOT DETERMINED** → V1.4-D |
| 16 | Compaction / summarization | Already in production (AF-02/03) | F (own tests) | budgets, protected context | done | ND-6 for in-pipeline use | **ADOPT** (unchanged) |
| 17 | Offline evaluation API | Documented only | — | scenarios, metrics | custom harness | KNOW-03 | **ADOPT** later, offline only |
| 18 | Structured output `AgentResponse<T>` | Not exercised | — | plan parser | — | ND-4 | **ND** |

Net removable code *from the runner today*: ≈0 authority lines. The benefit is concentrated in code not yet written
(KNOW-01…03) and in relocating prompt assembly behind one composition point. This ADR claims no larger saving.

## 4. G2 — mandatory security gate (from ARCH-02S C1–C6)

**Rule:** no non-bOps-authored `AIContextProvider`, MAF skills source or Knowledge/Experience/Skills content may be
active in any production configuration until every G2 requirement below passes its verification. bOps-authored providers
that only project persisted task state (step, plan, evidence digest) are not blocked, but they already pass through C1.

| Req | Requirement (normative) | Verification (architecture/behaviour test) |
|---|---|---|
| G2-1 (C1) | **Final context check** in `GovernedChatClient` on the fully merged request, before mapping and before the broker: reject any `ChatHistory`-sourced message; accept `System` role only from bOps-projected messages; accept provider messages only from admitted providers; reject tools outside the step allow-list. Fail closed, audited, **no broker call**. | Reuse of an `AgentSession` → 0 broker calls; a provider emitting `System` → rejected; a provider overriding `InvokingCoreAsync` and rewriting instructions/tags → rejected or demoted |
| G2-2 (C2) | **Typed provenance** in the bOps contract: every `ChatTurn`/prompt section carries `(origin ∈ {Runtime, Task, Knowledge, Experience, Skill, ToolResult}, admittedProviderId, packId@version#hash)`; persisted in the model-call audit event. The `ModelRequest` hash/shape stays serializable. Provenance is assigned **by bOps code at the point of admission** and is carried by bOps types, never re-derived from MAF tags, message ids, `AdditionalProperties`, pack metadata or any identifier supplied by an external source. | Provenance survives compaction/projection/mapping (golden test); a forged MAF tag or pack-claimed id yields `origin ≠ Knowledge` or rejection; audit row contains the fields |
| G2-3 (C3) | **Host-assigned trust**: trust level (`Runtime` / `Trusted` / `Untrusted`) and admission are assigned by a host-side allow-list keyed on the **bOps-registered provider instance**, not on type name or self-declared metadata. Every MAF/third-party provider is wrapped by a bOps decorator that re-stamps attribution, demotes `System`→`User` (labelled data), sanitises `Instructions`, strips tools. Only bOps-authored providers may override `InvokingCoreAsync`. | Unregistered provider → composition fails at startup; self-declared `trusted` ignored; two instances of one type get distinct ids |
| G2-4 (C4) | **No elevation** of Knowledge/Skills to `system`: they enter as attributed, delimited, lower-authority sections; they can never alter step authority, tool surface, policy or protected context. Skill `name`/`description` length-capped and validated before reaching any prompt; bOps owns the skills prompt template (the default one mentions `run_skill_script`). | Injection pack ("ignore previous rules") produces no change of tools/step/protected context; template contains no script instruction |
| G2-5 (C5) | **Tenant and cache isolation**, also under concurrent tasks: providers/sources constructed **per task with the tenant bound**; `CachingAgentSkillsSource` only with `CacheIsolationKeySelector = tenant`; experience search applies a mandatory tenant filter that callers cannot omit; no static/shared mutable cache; pack identity pinned per task. | Two concurrent tasks of different tenants over shared registrations: no cross-visibility in advertisement, content, cache or audit; filter-bypass attempt fails |
| G2-6 (C6) | **Architecture tests** (CI, in `bOps.AgentFramework` tests): no `FunctionInvokingChatClient`/`UseProvidedChatClientAsIs=false`; no invocable (non-declaration) `AIFunction` reachable from the agent; no `ChatHistoryProvider`/`AgentSession` reused across turns; tool surface of the final request ⊆ step allow-list; provider allow-list is closed; no MAF type in `bOps.Abstractions`/Runtime public surface. | Tests fail on each deliberate violation |

G2 closes when all six verifications pass in the repository suite. Until then KNOW-01 content providers remain
CONDITIONAL and disabled by composition (not by configuration flag alone).

## 5. Progressive Skills — minimum contract for AF-TURN-01 (not implemented here)

Name: `runtime.knowledge_read`, class `Read`, governed like `runtime.evidence_read`; MAF supplies content, bOps supplies the tool.

1. **Governed declarations.** `load_skill` and `read_skill_resource` are exposed to the model only as bOps-registered
   declarations (`ToolSurfaceGuard` over `AgentSkillsProvider`), offered only when the step's eligibility rule says so,
   in addition to — never replacing — the exact `ExpectedTool` view.
2. **Authorization at call time.** An adapter honours a call only if the tool is read-only, on the allow-list and offered
   on that turn; bOps then invokes the MAF `AIFunction` itself. Honouring is counted against a `knowledge_read` budget and
   audited like any tool decision.
3. **No scripts.** `run_skill_script` is never declared, never executable; no script runner is registered; packs with
   scripts or `AllowedTools` grants are rejected at manifest validation (frontmatter `allowed-tools` is never a grant).
4. **Integrity.** The task pins `(packId, version, manifest hash)` **and** per-resource hashes for the entire pack at
   task start; every `read_skill_resource` result is re-verified against its pinned hash before entering context; mismatch
   fails closed before the model call. Content is served from an immutable, content-addressed snapshot (TOCTOU of
   file-based sources is closed by construction — to be proven in AF-TURN-01). The audit records which resources were
   actually read (name + hash).
5. **Tenant isolation.** Per-task source/provider construction with tenant bound (G2-5); pack visibility derived from
   bOps entitlement, not from pack metadata.
6. **No hidden model call.** Each load/read is a bOps-turn tool decision; model calls per task equal broker records
   (assert equality in test). No FICC, no MAF follow-up, no provider-initiated inference.
7. **Provenance.** Loaded content enters as `origin=Skill` with `packId@version#hash` (G2-2); it is data, never instruction
   of higher authority than the step.
8. Declarations pass the leaf: `GovernedChatClient` stops throwing on `ChatOptions.Tools` only for declaration-only
   tools whose names equal the step allow-list (G2-6).

## 6. Roadmap (small, independent, ordered)

Existing queue (AF-05H2-R1 → BENCH-01 → AF-EVID-01 → AF-05H4-VERIFY → BENCH-00 3×3 → PR #100 decision) is untouched.
PR #100–#104 are not altered. No task below changes `AgentRunner` authority logic.

| Order | Task | Content | Depends on | Minimum gate to merge |
|---|---|---|---|---|
| 1 | **AF-CONTEXT-01** | G2-1, G2-2, G2-6 on the existing leaf: final-context check, typed provenance in `ChatTurn`/`ModelRequest`/audit, architecture tests. No new MAF capability, no behaviour change for today's bOps-authored context. | ADR-0054 accepted | Provenance golden tests; session-reuse and forged-tag tests; full non-live suite green |
| 2 | **AF-TURN-01** | Turn agent behind the adapter (§1), declaration-only tools accepted by the leaf, §5 contract; prompt assembly moved to bOps providers; recorded-request equivalence tests vs current path (closes ND-1/ND-2/ND-6) | 1 | Byte-equal leaf request and audit row on recorded `TaskState` fixtures; benchmark smoke unchanged |
| 3 | **KNOW-01** | Pack envelope (manifest, signature, pin), G2-3/G2-4/G2-5 decorators and admission, `runtime.knowledge_read`, Experience read path (ND-5 closed first); KNOW-02 (playbook) and KNOW-03 (offline eval) follow separately | 1 and 2; **G2 fully passed** | All G2 verifications green; tenant-concurrency test; injection-pack test |
| — | **AF-FINAL-01, AF-EVID-01, AF-GROUND-01** | Remain bOps hardening of B03/EvidenceRead/grounding; independent of MAF; **may and should run before or beside 1–3** and are never fixed through the adapter (D6) | per active hardening plan | per their own packets |

Out of this roadmap: Workflows/Coordinator (V1.4-D, needs ND-3), structured output (ND-4), streaming parity (ND-7), MCP skill sources (alpha).

## 7. Consequences

- **Gain:** one composition point for Knowledge/Experience/Skills; avoids a custom retrieval/skills engine; MAF confined to
  one adapter; every request is audited at the leaf after all providers ran.
- **Cost:** adapter plus decorators (est. 300–500 lines); MAF 1.24.0 pinned with experimental surfaces; each upgrade re-runs
  the G2 and architecture tests.
- **Risk accepted:** equivalence is demonstrated on synthetic scenarios only; AF-TURN-01 must convert it into recorded-request
  evidence before any behaviour depends on the turn agent. If it cannot, #2 reverts to bOps-only context assembly and
  KNOW-01 uses plain bOps context injection (valid, non-failure outcome, ADR-0053 D7).

## 8. Documentation follow-ups (separate, minimal)

ADR-0053 receives a pointer "Refined by ADR-0054" (done in this change). On operator acceptance: amend ADR-0053 §3 rows
2/4/11 as stated in §1/§3 above; add `agentic/06-decisions.md` entry; no change to code, dependencies or configuration.
