# BOPS-0128 DIAG-EVIDENCE-02 — Evidence completeness, query selection and recommendation hygiene

| | |
|---|---|
| Task | `BOPS-0128` (register: `agentic/_tasks/register.json`); Issue and Project #6 item **owed** (`sync_pending`) |
| Status | `PLANNED`, **not authorized** (operator decision 2026-10-27: analysis and formalization only, no runtime change) |
| Priority / effort | P1 / 4–7 days (slices A–B 2.5–4; slice C 1.5–3 plus the normative decision) |
| Depends on | none in the register. Slice C needs a normative decision (see Gates). |
| Milestone / area | DIAG-QUALITY / Tools (slices A–B), Runtime (slice C) |

## Why

The human calibration of R01 (B02 rerun at cd00e81) found diagnosis defects that are not the evaluator's business. The tools and the runtime produce them, or do not prevent them:

1. **Query selection.**
   - The model queried `system.events` with `minSeverity=error` and `text=error`. That excludes exactly the warning-level TDR and WHEA events its own hypothesis H2 needed.
   - The answer then blamed output truncation (C74, C102–C103).
   - Nothing in the result tells the model, the grounding check or a reader which filters shaped the evidence.
2. **Incomplete typed facts.**
   - `system.crashes` types at most 4 kernel codes (`SystemCrashFacts.MaximumKernelCodes = 4`), within the 16-fact / 4096-byte bound. The run had 7 bugcheck codes and 2 live-dump codes.
   - Of a 20 532-character output the model saw about 6 500 characters, so the 0xa, 0x7f, 0xd1 and 0x50 groups and the MsMpEng count (226) reached neither the facts nor the model (C42, C45, C47).
3. **Recommendations.**
   - The first suggested check was impossible: dump files that start on 16/10 cannot contain bugchecks of 03–09/10 (C113).
   - BIOS and driver updates, service restore, stress tests and monitoring changes were listed beside read-only checks with no distinction (C70, C76, C84, C115, C117, C118).

## Reconciliation with existing tasks (nothing below is duplicated)

| Existing task | Status here | What it already covers | What it does not cover | Decision proposed |
|---|---|---|---|---|
| **BOPS-0111** DIAG-ASSURE-01 (differential analysis: [`bops-0111-differential-01.md`](../../docs/maintenance/bops-0111-differential-01.md)) | `READY`, not authorized. Implemented on `codex/bops-0111-diagnostic-assurance` (`b8a3fe7`); its PR [#124](https://github.com/babbubba/bOps/pull/124) is **closed as set aside, not merged**, and that branch's register says `VALIDATION` / authorized: a **divergence to reconcile** by the operator | Run-level assurance reasons: `EvidencePartial`, `EvidenceUnavailable`, `ObservationShortenedNotRead` ("coverage not established"), `NoTypedFactsForStep`, `GroundingFactsOmitted`; test-only `NumericClaimFeasibilityTests` (a scoped claim contract proposal) | Which filters shaped a query; per-group visibility; recommendations | Slice B emits the filter metadata that a future `0111` reason (e.g. `EvidenceSelectionFiltered`) could count. No change to 0111's state or scope here |
| **BOPS-0080** AF-EVID-01 | `PLANNED` P2, not authorized, depends on 0111 | EvidenceRead `Repeat`/`Duplicate` audit, marking steps already fully visible, non-blocking dedup of identical reads | Navigation of long **structured** results by group or key instead of character offsets; head/tail compaction that hides middle groups of aggregates | **Proposed scope amendment to BOPS-0080** (operator decision): group-addressable EvidenceRead for JSON outputs. Not a new task |
| **BOPS-0068** AF-05G | `VALIDATION`; open gate "claim-verification objective not met: grounding is the same LLM over ≤12 typed facts of 5 tool families (see DIAG-CLAIMS-01)" | Evidence addressability, grounding diversity | Fact completeness per tool | Slice A widens what the ≤12/16 facts can say **within the existing bounds**; it does not touch the grounding design or close AF-05G's gate |
| **BOPS-0103** LIVE-04B | `DEFERRED` P3 ("reopen only with new proof") | Semantic grounding of message content | — | **New proof exists**: C54 (51 events attributed to one sample message), C56, C86. Recommend that the operator **reopens LIVE-04B**; not duplicated here |
| **BOPS-0115** DIAG-CLAIMS-01 | `PLANNED` P1, depends on 0111 and 0114 | Claim-ledger experiment and GO/NO-GO of a runtime claim contract | — | Runtime enforcement of recommendation feasibility needs structured claims: it **belongs to 0115's outcome**. BOPS-0127 measures it meanwhile |
| **BOPS-0113** HOST-SATURATION-01 | `PLANNED` P1 | Typed facts for cpu/memory/swap/io/process | Crashes, events | Complementary |
| **BOPS-0072** AF-05H5 | `DEFERRED` P3 | EvidenceRead guidance | — | Unchanged |

## Scope

### Slice A — complete bugcheck evidence within the existing bounds (package `System`)

- One additional typed fact per `system.crashes` call: every kernel bugcheck and live-dump code with its count, as one bounded list value. For example `kernel-codes = "0x1e:9,0xa:2,0x7f:2,0xd1:1,0x50:1,0x3b:1,0x101:1"`, and the same for live dumps.
- It is ordered by count, then code, and is cut only past `EvidenceFactBounds.MaximumValueCharacters`. In that case a `kernel-codes-omitted` count fact says how many codes were cut.
- No change to the runtime's 16-fact / 4096-byte limits, and no ADR change.
- The aggregated output lists, before the groups, a compact summary of every kind and code with counts and first/last seen. Head/tail compaction can then never hide a code that exists.

### Slice B — query selection and coverage metadata (packages `System` Windows/Linux)

- Every `system.events` and `system.crashes` result carries typed facts:
  - for the selection actually applied: `selection.min-severity`, `selection.text-filter` (present or absent, never its text), `selection.source-filter`, `selection.channel`;
  - for coverage: `coverage.oldest-retained` per store, `coverage.state`.
- The tool description states that a severity floor excludes warning-level events (TDR, WHEA corrected).
- No core change: these are package facts, data and never instruction.

### Slice C — recommendation classification in the final answer (runtime; needs the normative decision)

- The final-answer instructions ask that each suggested next action is labelled `read-only`, `offline analysis`, `configuration check` or `change (requires operator approval)`.
- Changes are never presented as checks.
- The runtime does not execute recommendations, and this changes no authority. It is a prompt/answer-contract change, so the PA-1 goldens are re-captured by decision, as before.
- **Feasibility enforcement** (dates of artefacts against target events) is **not** in this slice. It waits for BOPS-0115; BOPS-0127 measures it.

## Out of scope

- Raising the fact or byte bounds.
- New ADRs.
- A runtime claim contract (BOPS-0115).
- EvidenceRead navigation (BOPS-0080 amendment).
- Sample-message grounding (BOPS-0103).
- Any model judge.
- Any live run, unless the operator authorizes the validation campaign.

## Acceptance criteria

- **A1 (slice A):** on a synthetic capture with 7 bugcheck and 2 live-dump codes, the facts list all 9 with exact counts; a capture with more codes than fit produces `kernel-codes-omitted` > 0. The summary block precedes the groups in the output. Conformance tests on Windows; the Linux behaviour of the tool is unchanged.
- **A2 (slice B):** for each filter combination, the selection facts reflect the arguments actually applied; with no filter, the facts state "none" and are never absent. Coverage facts equal the coverage fields. No filter text is ever copied into a fact.
- **A3 (slice C):** offline scripted runs show the label instruction only in the answer turn. PA-1 goldens are re-captured by decision with a documented diff, and AE-1 is unchanged.
- **A4 (all):** no change to the authority, policy, approval, journal or resume tests; Architecture tests green (no tool name in the core); Release build with 0 warnings.
- **A5 (effect, offline):** the frozen B02 captures, re-evaluated with BOPS-0127 V2 on new synthetic runs only, show the selection facts available to the evaluator. Any live measurement needs a separate operator authorization.

## Implementation order (after authorization)

1. **Slice B:** metadata only, lowest risk, and immediately useful to BOPS-0127.
2. **Slice A.**
3. **Slice C**, only after the normative decision.

## Gates requiring operator approval

| Gate | When |
|---|---|
| `OPERATOR_AUTHORIZATION` | before any code |
| `NORMATIVE_DECISION` | before slice C: an answer-contract change touching `agentic/03-security-rules.md` / the final-answer contract |
| BOPS-0080 amendment and BOPS-0103 reopening | separate decisions |
| `LIVE_REAL_TOOLS` | if the operator wants the effect measured live |
| `CI_VERIFIED`, `OPERATOR_GATE` | as usual |
