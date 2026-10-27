# ARCH-02 — MAF parity spike (plan only; not started)

Status: this packet is the plan of the spike; task state lives only in the register. BOPS-0085 ARCH-02 is `DONE` (offline spike executed; report `docs/architecture/arch-02s-maf-bplus-conformance.md`). Defined by ADR-0053 (ARCH-01). Feeds ARCH-03 (BOPS-0075, blocked on decision D1).
Branch: new, off the then-current mainline; **throwaway spike code never merges**. Only the evidence report does.

## Scope and rules

- Purpose: produce evidence for ADR-0053 §3 cells marked **V**. Not a migration. No change to `AgentRunner`,
  policy, tools, API, UI or dependencies of shipped projects. The spike lives in a separate, unreferenced
  project/test assembly pinned to one explicit `Microsoft.Agents.AI` version (record it; start from `[1.24.0]`).
- Deterministic **scripted fake model** (`IChatModel`/`IChatClient` returning canned turns) for every experiment.
  No live LLM, no benchmark; a real model only if a fake cannot express the case, and then recorded as such.
- Same fixtures and assertions are run against (a) the current bOps path and (b) the MAF candidate path.
  A result is only comparable if input turns, plan, tool manifests and budgets are identical.
- Do not fix AF-EVID-01 / AF-FINAL-01 / AF-GROUND-01 here. Parity is measured against **current behaviour at the
  runtime reference commit**, including its known defects, which are recorded but not counted for or against MAF.
- Any invariant I-1…I-10 (ADR-0053 §5) failing in any experiment is an immediate NO-GO for that capability.
- An experiment that cannot be run cheaply is reported **Not determined** (not GO). Budget: one working spike,
  roughly the size of the four existing adapter files; stop at the first decisive NO-GO per capability.

## Experiments

Parity = MAF path produces the **same observable outcome** (persisted events, tool executions, terminal state,
audit records) as the bOps path under the same script. NO-GO = any listed condition, or any I-n violation.

| Id | Question (matrix row) | Scripted case | Parity | NO-GO |
|---|---|---|---|---|
| E1 | Tool calling and step authority (2, 3) | Plan with two same-tool steps (ADR-0050 regression); model emits (a) correct tool, (b) wrong tool, (c) same tool with wrong constrained arg, (d) two calls in one turn, (e) provider-escaped wire name | Only the first call considered; `ExpectedTool`/`ExpectedArguments` checks run before any invocation; (b)→replan, (c)→`SemanticMismatch` then one correction; (d) 2nd call kept as `UnexecutedToolCalls`; (e) resolves to the offered tool; zero MAF-side tool bodies execute operations | Any tool body runs without bOps check; auto-invoke loop cannot be stopped at first call; a second call executes; cursor held by MAF |
| E2 | Correction of wrong calls (3) | Argument-validation failure on the expected tool; then a second failure | Exactly one ADR-0047 correction, same one-tool view, cursor unchanged, second failure → replan; budgets independent from semantic correction | MAF retries or corrects on its own; correction count differs; extra model call outside the broker |
| E3 | Early-final and empty/truncated output (5) | (a) final answer with pending steps; (b) empty content; (c) `finish_reason=length` with partial/no plan on replan; (d) valid empty final after steps done | (a) not `Completed`, replan; (b)/(c) classified and bounded with identical retry count and terminal cause (AF-05H2); (d) `Completed`. `finish_reason` and token usage reach the broker intact | MAF swallows/rewrites `finish_reason` or empty content; hidden retry; `Completed` with pending mandatory step |
| E4 | Checkpoint / resume (1, 7) | Kill after step *n* (before and after a persisted tool result); resume from persisted `TaskState` only, discarding any MAF session/checkpoint | Next `ModelRequest` byte-equal (modulo ids/timestamps) to the bOps-resumed request; same cursor and plan revision; stale/lost MAF state changes nothing | Resume needs MAF state to be correct; divergence between MAF checkpoint and `TaskState` is silently accepted; fencing bypassed |
| E5 | Approval, policy, unknown outcome (6, 8) | (a) mutating tool requiring approval, restart while pending; (b) policy denial; (c) simulated crash with unknown outcome; (d) force MAF retry / superstep replay / checkpoint restore after (c) | (a) approval is the bOps record, survives restart, MAF approval is never a grant; (b) denial audited, no execution; (c)+(d) tool invoked **at most once**, outcome stays unknown, reconciliation required | Any second mutating invocation; approval inferred from MAF state; policy evaluated after side effect; missing audit row |
| E6 | Evidence handling (4, 10) | Large tool results + MAF compaction (+ governed summarizer) over a long script; then EvidenceRead of an ID from a compacted region; grounding/limitations blocks present | Evidence IDs remain addressable; raw store byte-identical; grounding, limitations, current step and correction state survive compaction; summary token use appears in model-call audit | Evidence ID lost or rewritten; summary treated as evidence; protected context compacted away; summarizer call outside the broker |
| E7 | Planning/replanning host (1) — **optional, run only if E1–E5 are all parity** | Host the plan state machine in a MAF Workflow with `TaskState` as sole authority; replan twice | One cursor (persisted); workflow state discardable; replan transactionality (ADR-0046) preserved; no extra model calls | Second cursor/plan authority; workflow checkpoint needed for correctness; any I-n failure |

"Resampling" (re-asking after a bad plan/tool choice) is covered inside E1–E3: the number of model calls and their
ordering must match the bOps path; additional or reordered calls are a divergence to be explained, not hidden.

## Evidence to record per run

Script id, MAF version, bOps commit, per-path: model calls (count, ordering, finish reasons), tool invocations
(by whom), persisted events, terminal state, audit rows, tokens. One report table: experiment × {parity, divergence,
NO-GO, not determined} with links to raw output. Raw outputs and any task DB stay out of Git.

## Gates for ARCH-03

- **GO for a capability**: its experiments are all *parity*; E5 and E3 pass for any capability that touches
  tools, retries or checkpoints; no invariant failed; added maintenance cost stated.
- **NO-GO for a capability**: any NO-GO cell, or *not determined* on E3/E4/E5. The capability stays in bOps; this
  is a valid result and requires no further work in ARCH-02.
- **Divergence** (different but safe behaviour): ARCH-03 may accept it only with an explicit written rationale and
  an ADR amendment; never by default.
- Nothing is adopted by ARCH-02 itself. ARCH-03 decides row by row (ADR-0053 D7).
