# BOPS-0126 MAF-DIAG-EVAL-01 — MAF evaluation of diagnostic quality in the A/B/C/D benchmark

| | |
|---|---|
| Task | `BOPS-0126` (register: `agentic/_tasks/register.json`), Issue and Project #6 item recorded there |
| Builds on | `BOPS-0125` MAF-CLI-AB-01 (campaign captures, `scripts/windows/maf_cli_analysis.py`, blind evaluation sheets) |
| Priority / effort | P1 / medium — mirrored from BOPS-0125, **to be confirmed or changed by the operator** |
| Depends on | nothing in the register (BOPS-0125 is the source of the captures, not a gate: P1 reads campaigns already on disk) |
| Branch / PR | `codex/maf-ab-diagnostic-reliability`, PR #135 (operator condition: no new branch or worktree) |

## Purpose

The benchmark measures with certainty what the runtime records (status, steps, tokens, time, Knowledge reads). Diagnostic quality today
comes only from regex heuristics (class H, they decide nothing) and blind sheets filled by hand (class Q, `PENDING` until then), so
comparing models and Knowledge versions still means reading every diagnosis. This task adds a repeatable, offline score per run that
can be recomputed on runs already recorded, built on the evaluation framework of Microsoft Agent Framework.

## Operator authorization (2026-10-26)

"ok registra e parti ma a due condizioni... 1 non aprire nuovi worktree rami o altro ma continua ad aggiungere lavoro sulla pr aperta....
2 per le schede di compilazione vediamo di farle insieme" — given on the proposal of the same day (decisions 1, phase P1).

- Authorized: registration and phase **P1** (deterministic evaluators, no model judge), on the open PR #135 branch.
- Condition 1: no new worktree or branch; the work is committed on `codex/maf-ab-diagnostic-reliability` and rides PR #135.
- Condition 2: the calibration sheets (blind human grading) are filled **together with the operator**; nothing is graded unattended.
- Not decided yet (needed before P2): the judge model and endpoint (a remote judge sends private evidence and needs its own
  authorization), the packages `Microsoft.Extensions.AI.Evaluation.Quality` / `.Reporting`.
- Recorded reading of C01-R02, to be confirmed at review: the harness is a test-side project that is never shipped and is referenced by no
  `src/` project; it references `Microsoft.Agents.AI` at the same exact pin as `bOps.AgentFramework`. No MAF type enters
  `bOps.Abstractions`, the plugin SDK, the Runtime public surface or persistence contracts.

## What MAF provides (checked)

`Microsoft.Agents.AI` 1.24.0 (already pinned) ships `EvalItem`, `IAgentEvaluator`, `LocalEvaluator`, `FunctionEvaluator`, `EvalCheckResult`,
`EvalChecks`, `ExpectedToolCall`, `ConversationSplitters`, `AgentEvaluationResults` and `MeaiEvaluatorAdapter`, over
`Microsoft.Extensions.AI.Evaluation` 10.10.0. Recorded responses are evaluated without re-running the agent; local checks and judged
(MEAI) evaluators can be mixed. Judged evaluators (`GroundednessEvaluator`, `TaskAdherenceEvaluator`, …) live in a package bOps does not
reference yet; Microsoft notes their prompts are tuned for GPT-4o and may be poor with small local models. Foundry and safety
evaluators are out of scope: they send evidence to a cloud service.

## Principles

1. Offline and post hoc: the evaluator reads a finished campaign directory and never writes into it except its own `auto-evaluation/`
   folder; it never runs the agent, never enters the runtime, the CLI or the API.
2. Deterministic first: anything computable from typed state and audit is computed by rules (class C/H). A model judge (class J, P2)
   is used only where meaning is needed and never overrides a rule; a disagreement is reported.
3. The ground truth is the evidence the run acquired (its typed facts), not the machine's true state. Scenario keys in the repository say
   which fact types mean what (crash, live dump, counts) and which Knowledge entries apply; they contain no evidence text.
4. Evidence stays on the machine; nothing is uploaded; no evidence text is copied into a report or into the repository.
5. Blind: evaluation is keyed by the campaign's blind codes; the variant is attached only when the report joins the key.
6. Reproducible: the output records the evaluator version, the scenario-key hash and the input hashes.

## The five metrics

| Metric | P1 (deterministic) | P2 (judge, not authorized yet) |
|---|---|---|
| M1 Crash vs live-dump classification | Evidence side from typed facts (live dumps and real bugchecks by code, unexpected shutdowns); answer side by rule: a sentence that names a live-dump code or term together with crash/BSOD wording and no negation; numbers attributed to the wrong family (the live-dump count presented as crashes). | Per event family: `crash` / `live dump` / `not mentioned` / `ambiguous`. |
| M2 Count accuracy | Answer key = every count fact of the run; each number in the answer near a recognised subject is `exact`, `wrong-family` (equals a count of another family), or `unverified`. | Extraction of `(subject, number)` pairs from free text. |
| M3 Claims supported by evidence | Existing structural signals only (`numbersNotInEvidence`, limitation wording, unsupported numbers). | Per-claim verdict with cited fact ids; MEAI Groundedness. |
| M4 Pertinent Knowledge use | From the audit: index published, entries read, reads refused / not executed, reads in the answer turn; scenario key lists the applicable entries → precision, recall, `citedNotLoaded` (an entry named in the answer but never read), `readNotUsed`. | Whether the answer applies what it read. |
| M5 Task completion and finalization | Terminal status, outcome class, final answer present, grounding outcome marker, plan steps vs budget, refused plan-mismatch calls, recoveries. | MEAI TaskAdherence / IntentResolution. |

A run that never reached the answer turn gets M5 and M4; M1–M3 are `notApplicable`, never 0.

## Scope (P1)

- `tests/bOps.DiagnosticEvaluation/` — console + library, not shipped: reads a campaign, builds one MAF `EvalItem` per run (query = goal,
  response = final answer, conversation rebuilt from the answer-turn request, `Context` = the fact ledger), runs a `LocalEvaluator` whose
  checks are `FunctionEvaluator`s over the bOps scorers, writes `auto-evaluation/<blindCode>.json` and `auto-evaluation/manifest.json`.
- `tests/bOps.DiagnosticEvaluation/keys/*.json` — scenario keys (B01, B02, B03).
- `tests/bOps.DiagnosticEvaluation.Tests/` — offline tests over synthetic captures with known defects.
- `scripts/windows/maf_cli_analysis.py` — reads `auto-evaluation/` when present and adds the P1 metrics (class C/H) to the report per
  variant and pair; absent files leave the report unchanged.
- Runbook section in `docs/maintenance/maf-cli-ab-benchmark-runbook-01.md`.

No runtime, broker, CLI, API, contract or ADR change.

## Acceptance criteria (P1)

- A1 For every executed run of a campaign the evaluator writes one result file keyed by blind code, with M1, M2, M4, M5 and the M3 structural
  signals, each MAF check result (`passed`, `reason`), and `notApplicable` where the run has no final answer.
- A2 The evaluator opens the campaign read-only except `auto-evaluation/`; it copies no evidence text into its output (numbers, fact keys
  and sentence indexes only).
- A3 Offline tests: synthetic captures containing a live dump called a BSOD, a wrong count, a count of the wrong family, an entry cited but
  never loaded, a knowledge read not executed, a run ended `MaxStepsReached`; each defect is detected and the output is identical across
  two executions.
- A4 `maf_cli_analysis.py` merges the files; its existing offline tests still pass and new ones cover the merge.
- A5 The evaluator runs on the campaigns already on disk (B02 pilots, the 69d504c rerun); results stay local; the summary given to the
  operator contains no evidence text.

## Gates

`OFFLINE_TESTS`, `CI_VERIFIED` (PR #135 head and merge commit), `OPERATOR_GATE` (review of P1 results, then the calibration session
together with the operator, condition 2). P2 needs a new authorization.

## Status log

Task state lives only in the register; this section records evidence as it is produced.

- 2026-10-26 — Registered (Issue #136, Project #6 item In Progress, fields read back). P1 implemented on `codex/maf-ab-diagnostic-reliability`:
  `tests/bOps.DiagnosticEvaluation` (+ `keys/B01.json`, `keys/B02.json` for B02/B03), `tests/bOps.DiagnosticEvaluation.Tests` (15 offline tests, both
  projects in `bOps.slnx`), `maf_cli_analysis.py` `auto*` metrics (+4 Python tests), `auto-evaluation` excluded from the integrity hashes like
  `evaluation` and `report`, runbook §6. The MAF check results are read back from `AgentEvaluationResults` (one boolean metric and reason per check).
- 2026-10-26 — Local run on the campaigns already captured (outputs stay in the private campaign directories; nothing uploaded). Calibration of
  the count rules on real answers removed false positives from dates with Unicode hyphens, per-period series, durations, event ids
  (`whea-logger 19`), negated subjects ("live dump, non BSOD") and terms of a neighbouring list item. What remains flagged read as real errors:
  live-dump counts given as kernel crashes (a 0x193 burst reported as "26 kernel crash", "10 kernel live dump" where the evidence holds 27 live dumps
  and 10 kernel crashes), and counts absent from every fact and tool output in the fa933f8 campaign: the run with 7 unverified counts is
  `09-B02-B-r2`, the P0-2 case found by hand in that campaign (PR #135), found here blind and without a model. B02 rerun at 69d504c: counts
  exact in all 6 runs (accuracy 1.0), no live dump called a crash; Knowledge recall of the applicable listed entries 0.095 in C and 0 in D.
  Two executions give identical bytes; `verify` stays OK on an evaluated campaign.
