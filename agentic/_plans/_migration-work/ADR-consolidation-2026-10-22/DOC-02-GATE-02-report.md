# DOC-02 — GATE-02 report

Non-normative working artifact of the DOC-02 phase of `agentic/_plans/ADR-consolidation-2026-10-22.md`. It is migration evidence, not current documentation, not a contract, and must not be loaded by the bootstrap.

**Verdict: READY FOR GATE-02 APPROVAL**

| | |
|---|---|
| Task | `BOPS-0121` / `DOC-CONSOL-01` (stays `IN_PROGRESS`) |
| Branch | `docs/adr-consolidation-2026-10-22` |
| HEAD before DOC-02 | `21c10f6ef806bbb9ab649468197090aef77e64a9` |
| HEAD after DOC-02 | the single DOC-02 commit that contains this file (its SHA is reported in the session summary; a file cannot contain the hash of its own commit) |
| Plan | `agentic/_plans/ADR-consolidation-2026-10-22.md` §7.2–§7.3 |

## 1. Mandatory checks (details in `DOC-02-checks.md`)

| Check | Result | Summary |
|---|---|---|
| CP-02a | PASS | 55/55 ADRs read in full; ranges cover every line; SHA-256 equal to the files on disk; every level 1–2 heading falls in a matrix row |
| CP-02b | PASS | 2,619 normative statements extracted from the 55 ADRs and `00`/`01`/`03`/`06`/`07`, all linked to a matrix row with a disposition, 0 orphans; semantic token comparison in `DOC-02-uncovered-normative-content.md` |
| CP-02c | PASS | 149 IDs (A1–A13, B1–B10, C1–C9, D, S1–S11, D-001…D-050, 55 ADRs): 0 missing, 0 duplicates (plus the block ID `P1-P7` of `00-project-spec`) |
| CP-02d | PASS | 12 dense ADRs: every heading of level 1–4 has a disposition; every technical-detail seat exists |
| CP-02e | PASS | No Proposed rule in a binding section; 0057 and 0060 as in plan §3.4; every `Implemented` rule has verified `file:line` anchors; S1–S11 and the other invariants of CP-02e(3) are checked rule by rule; no rule without a source row |
| Additional | PASS | Contract structure, technical references, git scope (no ADR, `01`/`03`/`06`, bootstrap/router, code, test, CI or script touched), `validate_register.py` green |

## 2. The five contracts (all `Candidate — pending GATE-02`)

| Contract | Binding rules | Implementation status of the rules | Pending entries (non-binding) | Decision |
|---|---:|---|---|---|
| `docs/architecture/current/01-maf-agent-infrastructure.md` | 21 | {'Implemented': 20, 'Partially implemented': 1} | C01-P01 (ADR-0053), C01-P02 (ADR-0054) | D1 |
| `docs/architecture/current/02-tools-safety-authority.md` | 29 | {'Implemented': 27, 'Not implemented / target in the product composition': 1, 'Not verified': 1} | none | — |
| `docs/architecture/current/03-task-state-resume-mutations.md` | 24 | {'Implemented': 24} | C03-P01 (ADR-0050), C03-P02 (ADR-0060 note) | D4 |
| `docs/architecture/current/04-evidence-and-diagnosis.md` | 18 | {'Implemented': 18} | C04-P01 (ADR-0050 §5) | D4 |
| `docs/architecture/current/05-interfaces-knowledge-and-hosts.md` | 12 | {'Implemented': 10, 'Partially implemented': 1, 'Not verified': 1} | C05-P01 (ADR-0053 §4.5, 0054 §4–§5), C05-P02 (ADR-0055), C05-P03 (ADR-0056), C05-P04 (ADR-0057 mechanism), C05-P05 (ADR-0058, D-050), C05-P06 (ADR-0059) | D5 |

Total 104 binding rules. Matrix: 928 rows ({'CONTRACT': 624, 'HISTORICAL': 175, 'TECH-REF': 41, 'PENDING': 88}), 55 ADRs plus `01`, `03`, `06`, `00-project-spec`, `07`. Contract 02 contains S1–S11 (invariant map at the end of the file). Contract lengths exceed the 60–120 line guidance because the plan forbids shortening safety and governance invariants (G1-04) and the contracts must carry the rule text that the archived ADRs held.

**Placement decisions made in DOC-02 (plan §3.2 left them open or provisional):**

- ADR-0060 (`PlanningGuidance`) is placed in Contract 03 (C03-R24): it governs what the planner sees of a manifest parameter, is display-only and independent of ADR-0050.
- Distribution of every ADR over the contracts: table in section 2a (generated from the matrix); ADR-0011 is split between Contract 02 (safety part) and Contract 05 (SDK part), ADR-0049 between Contract 02 and Contract 05 (messages).
- No sixth contract was created.

## 2a. ADR → contracts (generated from the matrix; rows per disposition)

| ADR | Status | Contract rules (rows) | Pending (rows) | Tech-ref (rows) | Historical (rows) |
|---|---|---|---|---|---|
| 0001 | Accepted | C01:5 | — | — | 1 |
| 0002 | Accepted | C02:3 | — | — | 2 |
| 0005 | Accepted | C01:4 | — | — | 1 |
| 0006 | Accepted | C02:4 | — | — | 2 |
| 0011 | Accepted | C02:4 | — | — | 2 |
| 0012 | Accepted | C05:2 | — | — | 3 |
| 0013 | Accepted | C01:4 | — | — | 1 |
| 0014 | Accepted | C01:3, C03:14, C04:7 | — | — | 5 |
| 0015 | Accepted | C02:5, C03:1 | — | 1 | 4 |
| 0016 | Accepted | C02:6 | — | — | 3 |
| 0017 | Accepted | C02:1, C03:6 | — | 1 | 1 |
| 0018 | Accepted | C02:1, C05:6 | — | — | 4 |
| 0019 | Accepted | C05:3 | — | — | 4 |
| 0020 | Accepted | C02:7 | — | — | 3 |
| 0021 | Accepted | C02:3 | — | 2 | 2 |
| 0022 | Accepted | C02:8, C04:2, C05:3 | — | — | 3 |
| 0023 | Accepted | C02:6, C04:2 | — | — | 4 |
| 0024 | Accepted | C02:5, C04:1 | — | — | 4 |
| 0025 | Accepted | C02:8, C03:2 | — | — | 1 |
| 0026 | Accepted | C02:6 | — | — | 2 |
| 0027 | Accepted | C02:9, C05:1 | — | 1 | 2 |
| 0028 | Accepted | C02:9, C04:1 | — | — | 2 |
| 0029 | Accepted | C01:1, C02:8, C05:3 | — | — | 1 |
| 0030 | Accepted | C02:12, C03:3, C04:1, C05:2 | — | — | — |
| 0031 | Accepted | C02:7, C03:1, C05:1 | — | — | 1 |
| 0032 | Accepted | C02:6, C04:19 | — | 6 | 5 |
| 0033 | Accepted | C02:9, C04:1 | — | 2 | 1 |
| 0034 | Accepted | C02:7, C04:1 | — | 4 | 2 |
| 0035 | Accepted | C02:10 | — | 2 | 3 |
| 0036 | Accepted | C02:14 | — | — | — |
| 0037 | Accepted | C02:13, C05:3 | — | — | 2 |
| 0038 | Accepted | C01:7, C03:1 | — | 1 | 1 |
| 0039 | Accepted | C01:9, C03:2 | — | 2 | 2 |
| 0040 | Accepted | C03:20, C05:2 | — | 1 | 2 |
| 0041 | Accepted | C02:2, C04:17 | — | 4 | — |
| 0042 | Accepted | C04:32 | — | — | 7 |
| 0043 | Accepted | C05:21 | — | 2 | 3 |
| 0044 | Accepted | C02:9, C03:1, C04:4, C05:11 | — | 2 | 4 |
| 0045 | Accepted | C01:16 | — | — | 2 |
| 0046 | Accepted | C01:1, C03:10, C04:2 | — | — | 3 |
| 0047 | Accepted | C03:6 | — | — | 2 |
| 0048 | Accepted | C02:8, C04:1 | — | 1 | 2 |
| 0049 | Accepted | C02:6, C05:6 | — | 1 | 1 |
| 0050 | Proposed | — | 13 | — | — |
| 0051 | Accepted | C03:35, C05:3 | — | 1 | 6 |
| 0052 | Accepted | C01:23, C04:4 | — | — | 8 |
| 0053 | Proposed | — | 9 | — | 4 |
| 0054 | Proposed | — | 6 | — | 3 |
| 0055 | Proposed | — | 9 | — | 2 |
| 0056 | Proposed | — | 8 | — | 2 |
| 0057 | Accepted | C02:1 | 26 | 1 | 17 |
| 0058 | Proposed | — | 11 | — | 5 |
| 0059 | Proposed | — | 4 | — | 3 |
| 0060 | Accepted | C03:9 | — | — | 7 |
| 0061 | Accepted | C04:11 | — | 1 | 7 |

## 3. Technical extractions (G1-04) and their seats

Appended technical sections: `docs/operator-configuration.md` (§15), `docs/system-stability.md`, `docs/process-diagnostics.md`, `docs/network-diagnostics.md`, `docs/system-events.md` (incl. the per-read 10-second cap found by the token check), `docs/system-maintenance.md`, `docs/agents/delegation-policy.md`, `docs/governed-recursive-deletion.md`, `docs/windows-dump-analysis.md`, `docs/knowledge/local-agent-pilot.md`. Created: `docs/architecture/reference/task-store-and-mutation-journal.md` (ADR-0040 §4.2, ADR-0051: schema, writes, crash windows C0–C8, refusal tables, audit stages) and `docs/architecture/reference/provider-adapters-and-service-tools.md` (ADR-0038 §2, ADR-0021). Table with ADR rows: `DOC-02-uncovered-normative-content.md` part A.

## 4. REVIEW entries and contradictions (details in `DOC-02-effective-rules.md`)

- 22 amendment chains reconstructed; 7 contradictions found while reading, all resolved by evidence (explicit supersession in a later Accepted ADR, or the code): retry location, `EvidenceRead` exhaustion, disclosure re-ask, empty-plan degradation, restart-to-apply, no-authentication statement, `0.x until V1.0`.
- REVIEW-01: Accepted ADR-0042 (PRE-5, AF-05G) and ADR-0046 (AF-05H) borrow vocabulary from the Proposed ADR-0050 (`Matched`, `Activation`, conditional steps). C04-R14 carries the behaviour with an explicit dependency note; if D4 rejects ADR-0050 those phrases need re-expression.
- REVIEW-02: ADR-0057 is Accepted but its mechanism depends on Proposed ADRs; only the S5 amendment is binding (C02-R12).
- REVIEW-03…07: implemented-but-Proposed code; names in ADR text absent from code; rules not verifiable here; the ADR-0048 timeout conflict (recorded, not resolved); the open Knowledge limits B1/B2.

## 5. Divergences between decision and implementation

- Implemented in code but not accepted: ADR-0050 (`ExpectedArguments`, `SemanticMismatch`, `EvidenceFact`), ADR-0054 (`MafGovernedTurn`), ADR-0055/0056/0058/0059 (Knowledge). Recorded as pending; the code is not treated as acceptance.
- Accepted but not (or only partly) in the product: C02-R12 (S5 amendment: the index exists only in an opt-in pilot), C01-R21 (bootstrap default: partially implemented, disclosure wording not re-verified), C05-R11 (SDK 1.x preview; stable 1.0 not verified), C01-R03 (the optional streaming interface is a stated future addition).
- Not verifiable by code: C02-R15 (documentation wording for in-process trust) and C05-R12 (open-core topology, CLA, licensing).
- ADR wording stale against later Accepted ADRs or code: retry in `AgentRunner` (ADR-0039/0045) versus the broker (ADR-0052 §5); HARDEN-8 history mechanism and `Agent:VerbatimHistorySteps` removed by AF-05; names in `DOC-02-uncovered-normative-content.md` part C.

## 6. Decisions requested of the operator

1. **GATE-02:** approve, amend or reject the five candidate contracts, the matrix, the id-coverage map and the placement decisions above; record G2-xx in plan §7.3.
2. **D1, D4, D5** remain open in the register. Their positions decide whether the pending entries C01-P01/P02, C03-P01, C04-P01 and C05-P01…P06 become binding rules. Note the REVIEW-01 consequence of rejecting D4.
3. Whether DOC-03 should keep ADR-number comments in code resolvable through `DOC-02-id-coverage.tsv` only, and how to treat the dead symbol names of REVIEW-04.
4. Whether to resolve the ADR-0048 timeout conflict (REVIEW-06) or keep it recorded.

## 7. Remaining risks

- Evidence anchors identify the implementing symbol and the architecture test, not every clause; clauses are covered by the named tests, which were not run in this documentation session.
- A matrix row can map to several rules when a source section feeds several rules; where a row maps to a rule only partly, the rule text is the authority, and the `rationale` and the effective-rules chains record the exceptions.
- Contract sizes (180–360 lines) are well above the orientation value of plan §3.3; they can be split into technical references in a later task only with the operator.
- The token comparison is a lexical aid: it finds missing identifiers and numbers, not changed meaning. The semantic read was done row by row while the rules were written.
- Nothing in DOC-02 touched code, tests, CI or scripts; the architecture tests that cite ADR numbers still resolve because no ADR was moved.

## 8. Scope and state

- No ADR, `01`, `03`, `06`, bootstrap, router, runtime, code, test, CI or script was modified; no ZIP, PR or merge was made; DOC-03 and DOC-04 were not started. `HANDOFF.md` records GATE-02 as the next step.
- Task register: `BOPS-0121` unchanged (`IN_PROGRESS`); status bookkeeping beyond this is owed to GATE-02.
