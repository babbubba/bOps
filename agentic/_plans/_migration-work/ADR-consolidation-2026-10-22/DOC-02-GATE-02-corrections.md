# DOC-02 — GATE-02 corrections

Non-normative working artifact of the DOC-02 phase of `agentic/_plans/ADR-consolidation-2026-10-22.md`. It is migration evidence, not current documentation, not a contract, and must not be loaded by the bootstrap.

Task `BOPS-0121` (stays `IN_PROGRESS`). Base: DOC-02 commit `79fac1dae2aff02abca619386b0b2fcc235393ec`. One corrective commit on `docs/adr-consolidation-2026-10-22`, authorised by the operator for items P1–P4 (P5 recorded). GATE-02 is **not** approved by this report; D1, D4 and D5 stay open.

## P1 — Accepted guarantees of ADR-0057 recovered (M1 development scope)

- **New rule `C05-R13`** (contract 05), limited to the M1 development scope authorised by ADR-0057 rev. 2 and D-049. Six points: §D.4 invariants; closed arguments (`ref`, `query`, `page`); reference authorization and resolution (§R.1, §R.3, §R.5: current authority verified before the ref is resolved, never memoised, no enumeration oracle); revocation (§R.2, B1, §R.4: same-attempt revocations versus immutable grants/trust store, no dynamic revocation promised); D-049 restrictions (no production-host composition, no live benchmark before B2 and §H.4, no merge into the goal turn, no implicit commercial release); provenance and trust boundaries (§D.1, §D.5).
- **Technical detail:** `docs/knowledge/local-agent-pilot.md` now has the section "Admission contract and marker neutralization" with U1–U10 (full table), the `query` rule, the §U.4 accepted limits and the §U.2 marker neutralization. C05-R13 points to it as necessary detail, not as a new authority.
- **C02-R12** now points to C05-R13 and to that section as the concrete admission check required by its point (3); the contract-02 pending paragraph and technical references were aligned.
- **C05-P04** corrected: the M1 arguments are `ref`, `query`, `page`; `knowledgeRefs` is a plan field reserved for a possible phase M4, not a function argument. C05-P04 now covers only the index mechanism not carried by C05-R13.
- **Labels:** the section 1 sentence, the section 3 status line, the ADR-0057 paragraph and the closing sentence no longer declare the whole Knowledge area "not in force": Proposed parts stay non-binding, the Accepted M1 guarantees bind in their limited scope.
- ADR-0054/0055/0056/0058/0059 stay Proposed (D1, D5 open). No rule was invented: each point restates the cited ADR-0057 sections.

## P2 — C04-R14 grounding semantics

Verified against ADR-0042 amendment PRE-5 §5 and `EvidenceGrounding.ParseVerdict` (no code change). The rule now states: entries within the closed shape that are not citations are ignored; an entry outside the closed shape makes the reply "check unavailable"; no entries originally = "no contradiction cited" (a model judgement, never "verified"); entries present but none a valid citation = "check unavailable", never "no contradiction cited"; "check unavailable" is never a positive verification. All other conditions and outcome transitions are unchanged.

## P3 — `system.dump_analyze` timeout (REVIEW-06 RESOLVED)

- `docs/windows-dump-analysis.md`: the "Timeout conflict (recorded)" section is replaced by a pointer to the existing "Time" section.
- C02-R10: effective timeout = manifest `RequestedExecutionTimeout`, else `Agent:DefaultToolTimeout`, never above `Agent:MaxToolTimeout`; evidence anchors added (`Tools.cs:220`, `AgentRunner.cs:4835`, `SystemToolManifests.cs:161`).
- REVIEW-06: "RESOLVED — stale ADR wording superseded by implemented manifest timeout support." `DOC-02-effective-rules.md`, `DOC-02-GATE-02-report.md` (no statement left that the conflict is open; operator question 4 removed) and `DOC-02-uncovered-normative-content.md` updated.
- Matrix row `A0048-010`: now HISTORICAL / `historical-only` (stale wording kept as history, not a current rule).
- No timeout, configuration or code was changed.

## P4 — Archive plan coherence

Plan §1, §5.1, §5.2, §7.4 step 1, CP-03a, the §7.5 gate and the exit criteria now say: the ZIP has exactly 57 entries, all unmodified originals (55 ADRs, `01-architecture-rules.md`, `06-decisions.md`); `MANIFEST.md`, `MANIFEST.csv` and an optional `.sha256` sidecar are external files in `docs/architecture/archive/`; the manifest lists path, size, content SHA-256 (distinct from the Git blob SHA), destination and reason, and does not contain its own hash. CP-03a verifies 57 entries, no extra entry, each entry against the Git blob content of the baseline, content SHA-256, byte-identical test extraction, and manifest/CSV consistent with all 57 originals. The ZIP is **not** created (DOC-03, after GATE-02).

## P5 — Readability (residual, not blocking)

Nine rule paragraphs exceed 4,000 characters (C02-R20, R21, R22, R25, R27, R28, C04-R14, C05-R04, C05-R07). Recorded in the plan §7.5 (step 9) and in the exit criteria as a formatting task before the final PR, without changing meaning; no contract refactoring project.

## Counts

| | DOC-02 | Now |
|---|---:|---:|
| Binding rules | 104 | 105 (C05: 12 → 13) |
| Matrix rows | 928 | 928 |
| CONTRACT / HISTORICAL / TECH-REF / PENDING | 624 / 175 / 41 / 88 | 640 / 176 / 40 / 72 |
| Evidence anchors | 211 | 218 |
| Implementation statuses | 99 / 2 / 1 / 2 | 99 / 3 / 1 / 2 (Implemented / Partially / Not impl. / Not verified) |

Matrix rows changed: 15 ADR-0057 rows and D-049 → CONTRACT `C05-R13`; ADR-0048 §10 → HISTORICAL. Extraction (30 statements), dense coverage (19 heading rows) and the id-coverage rows for ADR-0057, ADR-0048 and D-049 were aligned. The read-ledger is unchanged.

## Checks

CP-02a…CP-02e re-run on the corrected evidence: all PASS (`DOC-02-checks.md`, last section).

## Still open

D1, D4 and D5 (not approved, not rejected here). REVIEW-01…05 and REVIEW-07 stay documented; none is a loss of an Accepted rule. B1 and B2 stay open.

## Files modified

See the commit; contracts 02, 04, 05, `docs/knowledge/local-agent-pilot.md`, `docs/windows-dump-analysis.md`, the plan, `HANDOFF.md` (one sentence) and the DOC-02 working artifacts listed in the commit message.
