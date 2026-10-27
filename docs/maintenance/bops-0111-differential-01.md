# BOPS-0111 DIAG-ASSURE-01 — differential analysis of `b8a3fe7` against the current runtime

| | |
|---|---|
| Date | 2026-10-27 |
| Scope | Read-only analysis requested by the operator. No merge, no cherry-pick, no code change. |
| Commit analysed | `b8a3fe7` "feat(runtime): expose diagnostic evidence assurance (BOPS-0111)", branch `codex/bops-0111-diagnostic-assurance`, parent `e0e9de7` |
| Compared with | `codex/maf-ab-diagnostic-reliability` at `b296bf8` (16 commits after the same merge base `e0e9de7`, including PR #135's runtime fixes and BOPS-0125/0126) |
| Delivery status | PR [#124](https://github.com/babbubba/bOps/pull/124) **closed without merge** ("accantonata — non mergiare"). This is **not an accepted feature**. |

## Register divergence (to be decided by the operator, not changed here)

| Where | State of BOPS-0111 |
|---|---|
| This branch's register | `READY`, `authorized: false` |
| `b8a3fe7`'s register | `VALIDATION`, `authorized: true` (operator mandate dated 2026-10-21) |
| Project #6 | Status Validation (a sync conflict, reported by `sync_remote.py`) |

## Mechanical compatibility (verified)

- **Textual patch.** `git diff b8a3fe7^ b8a3fe7 -- src tests | git apply --check` against the current tree passes: the code and test changes apply textually.
- **Three-way merge.** `git merge-tree` (no work tree touched) conflicts only on `AGENTS.md` (add/add), `HANDOFF.md` and `docs/operator-configuration.md`.
- **Not verified.** Build and tests of the combination were not run, because that would require applying the change. Textual applicability is not semantic compatibility.

## Classification

| Part (files) | Class | Reason |
|---|---|---|
| Contract types `AssuranceReason`, `AssuranceReasonCode` (14 codes), `DiagnosticAssuranceLevel`, `DiagnosticAssuranceResult` (`bOps.Abstractions`) | **Reusable**, with a governance note | Closed codes and counts, no model text. They are new public types in `bOps.Abstractions`; the spec (§6.6) leaves an ADR-0042 amendment to review if a persisted field is added (`Audit.cs` adds one) |
| Projection `DiagnosticAssurance.cs` (`bOps.Runtime`, pure, read-only) | **Reusable but incomplete** | 1. `FinalResponse.GroundingOf` is mapped without `EvidenceGroundingOutcome.ClaimsCorrected`. That outcome was added later on this branch (`4530ae0`, PR #135: deterministic claim provenance), so a `ClaimsCorrected` final would fall into the default arm and be reported as `GroundingNotChecked`, which is wrong. It must be mapped (most likely to `Qualified`, like `GroundingCorrected`), with a test. 2. `ObservationShortenedNotRead` compares the observation prefix only: the audited EvidenceRead ranges are not used, so the code means "coverage not established" (as its packet says), not "unread" |
| Exposure: `Audit.cs` optional `Assurance` on `ExecutionTerminal`, `AuditJsonContext`, `TaskStateView` (API), `DiagnosticAssuranceText` + `Program.cs` (CLI) | **Reusable** | Additive, nullable, merges cleanly |
| Tests: `DiagnosticAssuranceTests` (342 lines), `DiagnosticAssuranceArchiveTests`, `Fixtures/diagnostic-assurance-metadata.json` (structural metadata of 12 archived states, payloads replaced), API/Audit/CLI tests, `EvidenceAddressabilityTests`, additions to `EvidenceGroundingLoopTests` | **Reusable**, to be re-run | Must be extended for `ClaimsCorrected`; the fixture's privacy treatment is documented in the 0111 packet and should be re-checked before any merge |
| `NumericClaimFeasibilityTests` (test-only claim-contract proposal: claims cite source/time/category/fact identity/completeness/mode; sums need an attested disjoint partition) | **Overlaps** BOPS-0115 and BOPS-0127 | Design input for BOPS-0115 (runtime claim contract) and for BOPS-0127 R1/R2/R5 (deterministic binding, correlation before dedup). Not production code |
| `GroundingFactsOmitted`, `NoTypedFactsForStep` | **Complementary to** BOPS-0128 slice A | 0111 counts what the bounded ledger omits; 0128 reduces the omission for crashes. No duplication |
| No reason for the query selection (filters) | **Missing** (a candidate for 0111 if authorized) | BOPS-0128 slice B would emit the metadata a future `EvidenceSelectionFiltered` reason could count |
| `AgentRunner.cs`: `run.TokensUsed > tokenCap` → `>=` | **Unrelated behaviour change bundled in the commit** | Changes the `BudgetExceeded` boundary. It needs its own review and test; it must not ride an assurance delivery unnoticed |
| `DumpAnalysis*.cs` (PRE-6: clipped scalars set `Partial` / output-truncated, typed DumpChk preflight, `processName` documented as debugger context) with `DumpAnalysisContractTests`, `HighDensityFactsTests` | **Reusable, separate** | Independent production fixes of `system.kernel_dump`; unchanged on this branch since the merge base. They should be delivered and reviewed apart from the assurance projection |
| Governance and docs: `agentic/_tasks/register.json` (numbering next 121, older statuses), `HANDOFF.md`, `AGENTS.md`, `CHANGELOG.md`, `docs/operator-configuration.md`, governance test tweaks | **Obsolete** | Superseded by later register states (next 129 here); three of them conflict. To be rewritten, never merged |

## Recommendation (no action taken)

**Do not merge or cherry-pick `b8a3fe7`.** If the operator authorizes BOPS-0111 again, re-deliver it on the current base in slices:

1. Contract types and projection, adapted to `ClaimsCorrected`, with the existing tests plus a `ClaimsCorrected` case and the three mutants.
2. API/CLI/audit exposure, together with the ADR-0042 decision on the persisted field.
3. The DumpAnalysis PRE-6 fixes as their own change.
4. The token-cap boundary as a separate decision.

In every slice the register, HANDOFF and Issue bookkeeping is redone, not merged. The register divergence above must be settled first.
