# AF-05H — Live regression hardening (H2 early final, H4 deterministic disclosure)

| | |
|---|---|
| Governing ADRs | [ADR-0046](../../docs/architecture/adr/0046-step-scoped-tool-routing-and-transactional-replanning.md) "a premature final replans", [ADR-0042](../../docs/architecture/adr/0042-evidence-reasoning-and-limitation-disclosure.md) "deterministic evidence disclosure" |
| Status | **H2 and H4 implemented on `fix/af-05h-live-regression-hardening`** (base `97507aa`, AF-05G). Deterministic-fixture evidence only. Not merged |
| Scope decision (operator, 2026-10-11) | Only the two blockers H2 and H4 in this phase. **H1** (context operating range) is kept isolated on `feat/af-05h-h1-context-operating-range` so it can be left out of a merge while current behaviour is measured and MAF is evaluated as orchestrator. **H3** (correction-class conflict) and **H5** (EvidenceRead guidance) are **not part of this phase**; their implementation is preserved, unmerged, on `backup/af-05h-h1-h5-full` |
| Depends on | AF-05, AF-05G |

## Why

Three live runs on a real Qwen3.6-35B (64k window) after AF-05G:

| Live run | Observed | Root cause |
|---|---|---|
| LIVE-1 `34579793…` short CPU/memory | cpu, memory, info, final; 0 replans, 0 reads, 13,361 tokens | — (negative control) |
| LIVE-2 `3f5b57ac…` long stability | the model answered while `system.time` was still the current planned step and the task completed (the projection then showed that step failed); the disclosure re-ask rewrote "26 kernel live dumps with code 0x193" into "27 … (code 0x193)" (persisted: 26 × 0x193 + 1 × 0x1a1) | a tool-free reply was accepted as completion whatever the plan said; the disclosure phase could rewrite facts |
| LIVE-3 `4888fea6…` focused crash | after the first successful crash query the model answered with five diagnostics left ("cannot determine the root cause without further analysis") and the task completed | premature completion as in LIVE-2 |

## H2 — a premature final replans (ADR-0046 amendment)

Old: `response.IsFinal || ToolCalls.Count == 0` → `Completed`, whatever the plan said. New state transition:

```text
final candidate
  ├─ settled cursor (after ADR-0050 Settle) at/after the end of the plan → disclosure → grounding → Completed (unchanged path)
  └─ unresolved executable step → bookkeeping step (paid call kept: ModelCalls, audit, tokens; no evidence; not a final step)
        → budget stop? → attempt step cap reached? (the replan is then owed to the resume)
        → governed replan (0 native tools, broker, audited, token-counted, replan budget, transactional) with a runtime-authored directive
            ├─ {"steps":[]} → new empty revision; a NEW tool-free step call produces the final → Completed
            └─ non-empty    → execution continues
```

- Bookkeeping step: description `Premature final response; plan re-evaluation required`, runtime-authored observation naming only the
  unresolved planned positions and their catalog-validated tool names; no tool call, result or planned-step index; never addressable
  evidence, never a limitation, never a final-response step. The premature text stays only in the bounded model-call record.
- Resume: `PlannedStepPosition.After` treats the bookkeeping step as `ReplanRequired` for every plan contract, so an attempt that ended
  between the bookkeeping step and the replan replans first, with the same trigger turn; after an accepted replan the new revision starts
  at step 0.
- Projection: the replaced suffix is `Superseded`; while the replan is owed the unattempted step is `Pending`, never `Failed`; an empty active
  revision has no steps. No new status.
- The step prompt says that while planned steps remain, a final answer makes the runtime re-evaluate the plan.
- Stop conditions honoured: no bypass of the normal replan, no planned tool executed without a model call, MAF decides nothing about
  completion, no package semantics in the runtime.

## H4 — deterministic evidence disclosure (ADR-0042 amendment)

`EvidenceDisclosure.Append`: original answer (exact prefix, unchanged) + a closing fence only if the answer ends inside an open fence + a
blank line + `## Evidence limitations` + the fixed `EvidenceDisclosure/v2` line + the persisted digest's typed entry lines (+ its omission
line). No model call, no tokens, bounded by the digest bound (4,608 characters), unforgeable by tool output (typed fields only,
shape-checked tool names). An answer that already has the heading is kept byte for byte; with no limitations nothing is appended. The
grounding check reads the disclosed final. Marker `Final response; evidence limitations appended by runtime`; the historical re-ask markers
stay readable. `Agent:EvidenceDisclosureEnabled` (default true) replaces `Agent:EvidenceDisclosureRetries`, kept as a documented deprecated
alias (0 disables, 1 no-op, other values fail validation); the shipped settings carry only the new key. Normative text updated:
`agentic/01-architecture-rules.md` rule 9 and D-038 in `agentic/06-decisions.md`.

Residual gap, not hidden: unsupported but non-contradictory model claims remain possible; a claim-support checker is a separate task.

## Which change belongs where

| Item | Branch / commit | Files |
|---|---|---|
| **H2** | `fix/af-05h-live-regression-hardening` `6bfdf70`, `ee56580` | `PrematureFinal.cs` (new), `AgentRunner.cs` (final branch, replan trigger turn, step-prompt sentence), `PlannedStepPosition.cs` (fold), `ExecutionPlanProjector.cs` (Pending while owed), ADR-0046 amendment, `EarlyFinalReplanTests` (new) and 77 adapted test scripts |
| **H4** | same branch `380c311` | `EvidenceDisclosure.cs` (append, detector scan), `EvidenceLimitationsDigest.cs` (entries), `FinalResponse.cs` (marker), `AgentRunnerOptions.cs` (`EvidenceDisclosureEnabled`, deprecated alias), `AgentRunner.cs` (re-ask removed), both `appsettings.json`, ADR-0042 amendment, rule 9, D-038, `DeterministicDisclosureTests` (new) and the rewritten disclosure/grounding/fallback tests |
| Fixtures | same branch | `LiveRegressionHarness.cs`, `LiveRegressionTests.cs`, `LiveTokenBenchmarkTests.cs` |
| **H1** (isolated) | `feat/af-05h-h1-context-operating-range` `98044f2` (on `97507aa`) | `ModelContextOperatingRange.cs`, `ModelContext.cs`, `MafContextProjector.cs`, `ModelSummarization.cs` (TriggerRatio removed), broker telemetry, both `appsettings.json` (`Context` ratios), operator docs, ADR-0052 §32, `ContextOperatingRangeTests` |
| H3, H5 (deferred) | `backup/af-05h-h1-h5-full` only | not on either branch above |

## Tests

| Area | Tests |
|---|---|
| H2 | `EarlyFinalReplanTests`: final with empty plan; final after a consumed plan; one step remaining → replan with the paid call kept (persisted, audited, accounted, not evidence); governed tool-free replan with the runtime directive and without the premature text; empty replan → new final call; non-empty replan continues; LIVE-2 equivalent (superfluous step `Superseded`); LIVE-3 equivalent (suffix executed); malformed replan transactional; replan budget bounded; resume before and after the replan; deterministically skipped conditional steps do not block the final; fold for both plan contracts |
| H4 | `DeterministicDisclosureTests`: heading present → byte-identical; no heading → original prefix + deterministic block; zero extra calls; zero tokens; persistence/reload byte-identical and deterministic; forged evidence cannot shape the block; bound and omission line; LIVE-2 "26 × 0x193" kept and the drifted restatement never requested; no limitations → unchanged; open-fence case; switch and deprecated alias; budget exhaustion; delegated Discovery and Diagnostic; historical markers. Plus `EvidenceGroundingLoopTests.G12_*` (the check sees the disclosed final), `TerminalDisclosureFallbackTests`, `EvidenceDisclosureHeadingTests`, Api `AgentEvidenceDisclosureConfigurationTests` |
| Live fixtures | `LiveRegressionTests` (LIVE-1/2/3 on the real runner, broker and MAF projector), `LiveTokenBenchmarkTests` |

77 existing tests scripted a final while plan steps remained (mostly a boilerplate one-step plan); they now plan no step or answer the
re-evaluation with an empty replan, keeping their intent. Tests that asserted the removed re-ask were rewritten. No test was disabled.

## Measurements (DETERMINISTIC FIXTURE)

Full tables: [benchmark results §9](2026-10-11-af-05-benchmark-results.md#9-af-05h-measurements-2026-10-11).

| Fixture (unknown profile) | Before `97507aa` | After (H2+H4) | Check before → after |
|---|---:|---:|---|
| LIVE-1 short | 7,938 | 7,971 (+0.4 %) | unchanged behaviour |
| LIVE-2 long | 74,883 (disclosure 1 call / 3,233) | 76,808 (disclosure 0 / 0; +1 replan, +1 tool-free final call) | FACT DRIFTED (27 × 0x193) → count kept (26 × 0x193) |
| LIVE-3 focused | 23,903 (disclosure 1 / 4,074) | 50,242 (disclosure 0 / 0; the five diagnostics now run) | SUFFIX ABANDONED (1/6) → suffix executed (6/6) |

Disclosure model calls and tokens are 0 in every after row. Live runs: **NOT RUN** (no local model server answered).

## Validation

See the final report of this packet's branch tip; recorded below when complete.

## Deviations

1. The appended section uses a Markdown `## Evidence limitations` heading (the detector accepts it); a plain-text consumer sees two `#`.
2. The H2 rule also applies when the planned tool cannot be offered (for example outside a delegated envelope): such a final replans
   rather than completing; the replan catalog then excludes the tool.
3. H3/H5 were implemented and tested before the scope decision; per the operator they are not on this branch, only on the backup branch.

## Residual debt

- Unsupported but non-contradictory model claims remain possible (H4 residual gap).
- The correction-class ping-pong of LIVE-3 still costs one extra correction turn (H3 deferred).
- H1 is pending the operator's measurement of current behaviour and the MAF-orchestrator evaluation.
