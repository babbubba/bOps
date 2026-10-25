# MAF A/B campaign — diagnostic reliability corrections (maintenance 01)

Scope: correct the reliability defects the 18-run MAF CLI A/B campaign (Qwen3.6-35B, baseline `fa933f8`) proved, without changing
the MAF architecture and without new tasks or ADRs. The campaign's raw archive holds data of a real machine and is **not** part of the
repository; every fixture here is synthetic or anonymized (shapes and magnitudes only: no host, user, path, IP, process name or event
message).

## P0-1 — a non-answer finalized as `Completed` (`03-B02-A-r1`)

Root cause (runtime): after three evidence-read rounds the model answered, finish reason `stop`, with the history compaction
placeholder the runtime itself writes into the conversation (`[Compacted tool interactions; persisted result data omitted]` plus a
`- runtime_evidence_read (step 2): result recorded` bullet). `FinalAnswerContent.HasContent` accepts any reply with a letter, so it
passed the unusable-final guard; the grounding check then answered `{"contradictions":[]}`, which only means "nothing cited", and the
runtime appended its own *Evidence limitations* section, so the persisted "answer" looked finished.

Fix: `RuntimeContextMarkers` (shared with the MAF context projector, same bytes) defines the runtime-authored lines;
`FinalAnswerContent.HasContent` skips them and `IsRuntimeContextEcho` tells the retry and the terminal text what was wrong. Everything
else is the existing machinery: one bounded retry (`EmptyFinalResponseRetries`), then `Failed` / `EmptyResponse`, with the same audit,
`TaskState` and token accounting; the grounding check and the disclosure section never run on a non-answer. Short, negative and
uncertain answers are untouched (no length test).

## P0-2 — counts and dates invented for empty queries (`09-B02-B-r2`)

Root cause (runtime/contract): the three `system.events` queries were empty (`observedEvents: 0`, `notRegistered` source filters,
windows in 2026) and, by the settled contract, an empty result emits no typed fact ("absence is never a fact"). The grounding ledger
therefore held nothing about those steps and the check had nothing to contradict, while the answer attributed 196 WHEA events, 1319
Kernel-Power events and 2025 dates to them.

Fix (`AnswerClaimProvenance`, no new fact type, zero results still never facts): only a count or date that the answer *itself attributes*
to a persisted step (by `step N`, also from the nearest heading, or by an exact tool name that ran) is judged, against that step's own
result, arguments and facts (neighbouring step numbers tolerated). A count is an integer followed by a counting noun; units, ids,
versions, windows, zero, derivations and unattributed numbers are never judged. A date is unsupported when its year is neither in the
cited steps' evidence nor the clock's year. Findings join the check's citations in the **same single correction** (never a second
one): accepted only if the correction is a real answer and no flagged value survives, otherwise the attempt fails `RuntimeFailure`.
With no budget left for the correction the attempt also fails rather than persisting the invented answer. New persisted marker
`evidence claims corrected` (additive) when no typed fact existed and no check call was made.

## P1

- Stop codes (`KernelStopCodes`, 8 entries, Microsoft Learn): `system.crashes` adds `stopCodeLegend` for catalogued codes present —
  `0x193` identifies a live dump, never a real bugcheck or restart; `0x101` CLOCK_WATCHDOG_TIMEOUT; `0x116` VIDEO_TDR_FAILURE; `0x50`
  PAGE_FAULT_IN_NONPAGED_AREA; also `0x124`, `0x1e`, `0x7f`, `0xa`. Unknown codes get no entry.
- Tool descriptions state what live dump / bugcheck / application crash mean, that Kernel-Power 41 is an unclean restart and not proof of
  a failing power supply, that WHEA records are corrected or uncorrected per their own message, and that records written by bOps or its
  harness describe its own activity. Harness events are **not** hidden: provenance and content are untouched.

## Residual (not fixed here)

- Causal over-confidence (PSU/CPU/GPU/RAM stated as the cause) is addressed only by the tool descriptions above. A deterministic gate
  would need prose interpretation or a structured claim contract (`DIAG-CLAIMS-01`, BOPS-0111's proposal); the evidence rule E7 already
  asks for hypotheses and is unchanged (any change is an ADR-0042 amendment).
- The provenance check misses unattributed numbers, small counts that coincide with a digit in the cited result (e.g. a schema version),
  and fabricated values with no step or tool attribution; it is a bounded net, not a proof of correctness.
- No filter on bOps-generated events in `system.events` (provenance is preserved by design); a source classification would be a tool
  change of its own.
- `docs/architecture/current/04-evidence-and-diagnosis.md` (C04-R15) should gain one sentence for the two new pipeline rules once the
  operator accepts this design; it is a contract document and was not edited here.
