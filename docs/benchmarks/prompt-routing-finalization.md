# PROMPT-ROUTING-FINALIZATION — report

Scope: PRA-R2, PRA-R3, PRA-R4 and PRA-P1, as one coordinated change of the prompts the runtime assembles and of the lifetime of one
temporary instruction; PRA-R5 (the protocol-artifact retry text, §C and §G.5) was added afterwards as a follow-up of the same kind. No audit, no benchmark framework, no live run, no Qwen call, no commit, push, merge, rebase, reset or clean.

**Verdict: the four corrections are implemented and every pertinent test passes (whole `bOps.AgentFramework.Tests`: 615 passed, 0 failed,
8 skipped live tests; the pertinent `bOps.Runtime.Tests` groups, `bOps.Architecture.Tests` and `bOps.ReplayFixtureCapture.Tests`
pass). Whether Qwen3.8 Smart behaves better with the new text is NOT shown by any offline test and is left to the minimal live
validation of section H.**

Sources: `agentic/` rules, ADR-0046 and its amendments, the PA-1 and AE-1 reports (`pa-1-request-assembly-baseline.md`,
`ae-1-scripted-runtime-baseline.md`), the master `bOps_Continuita_Master_2026-10-18.md` §7.3 and the PRA table of
`PROMPT-RUNTIME-AUDIT-01-report.md`. Nothing was re-audited.

---

## A. State

| | |
|---|---|
| Worktree / branch | `C:/source/repos/babbubba/bOps-knowbench01a`, `codex/know-bench-01a` |
| HEAD | `7053ca0` — unchanged |
| Pre-existing local work | preserved: the PA-1 and AE-1 files, the replay/Qwen/T02 test projects and reports, `bOps.AgentFramework.Tests.csproj`, `tests/bOps.Runtime.Tests/EvidenceGroundingLoopTests.cs` (not touched by this task) |
| Other worktrees (including the session's `bOps`, `codex/host-canary-01b`) | not touched |
| Commits / push / merge / rebase / reset / clean | none |

---

## B. Defects corrected, and what is guaranteed by whom

| ID | Defect (as found in the code) | Correction | Guaranteed by the runtime | Depends on the model |
|---|---|---|---|---|
| **R2** | `PlanCompleteInstructions` told the model to "propose the one new check you need: the runtime will ask you to revise the plan", but a prose proposal is accepted as the final answer (`Completed`, no replan). T08 (`system.sensor`) and T06 (deferred step) show the consequence | The text now states what the runtime does: text is the final answer and ends the task, an evidence limit is reported *in* the answer, no check/tool/URL/resource absent from the plan and the evidence is to be presented as done or available, and the single recognized channel for more work is a native call of a tool the plan or the task already names (refused, audited, answered by the governed replan) | prose is never executed, never parsed, never triggers a replan (`PRA_R2_AProposalMadeAsProse…`); a native call to an unoffered/unknown tool is refused, audited `UnknownTool` and replanned (unchanged); plan-complete requests offer no operational tool (D-043) | whether the model stops proposing in prose. **No parser of prose was added** and none is claimed: a prose suggestion can still occur and is then a persisted final answer |
| **R3** | (a) the step prompt ended "a final answer makes the runtime re-evaluate the plan" (an invitation); (b) `KnowledgeControlInstruction` said "make the operational call (or give your final answer)" with two steps pending; (c) the early-final replan reused the generic lead "Your previous plan no longer matches what you have learned", which is not why it runs and pushes the model to add steps; (d) the Knowledge refusal reply said "…or give your final answer" without knowing the plan | (a) "a reply without a tool call is not taken as the final answer and only makes the runtime re-evaluate the plan"; (b) the next reply is the step's operational call while one is offered, the final answer only when none is; (c) a lead of its own for `EarlyFinalWithUnresolvedPlan`: the final was refused only because steps were unresolved, the plan may be right, return an empty `steps` array if what is collected suffices, otherwise only the steps still required, no check the goal does not need; (d) the final answer is named only as the way on when no operational tool is offered | AF-05H unchanged: a final with an unresolved step is never `Completed`; an empty replan is the formal revision that allows finishing early; replan budgets, `MaxReplans`, the AF-05H2 continuations and **LIVE-05** are untouched; no step is forced that the plan does not owe | whether the model proposes the call instead of finalizing, and whether it returns an empty plan when steps are superfluous |
| **R4** | RC1/RC2 were fixed strings ("Either call a tool, or give your final answer"; "call the offered tool now, or…") whatever the state: at plan end they asked for a tool while only a control function was offered (T05: redundant evidence read, then `>`); the instruction also stayed in every later request of the logical call | the text follows the request's own tool surface — `Call the operational tool offered for the current plan step now.` while the step's tool is offered, `No operational tool is offered in this turn: give your final answer … from the evidence already collected.` otherwise (plan complete or empty plan); the recovery turn is removed from the logical call as soon as the retry is answered with a usable reply (call, control call or answer) | one retry (`EmptyFinalResponseRetries`) unchanged; the unusable turn is still not kept; an unusable retry keeps its instruction and **F1 (LIVE-F1-FIX-01) still fails the task** (`Failed/EmptyResponse`, never `Completed`); no loop, no extra call | whether the model answers after the corrected text |
| **P1** | the one-operational-tool routing was stated but never explained while the plan listed several steps; nothing told the model what is available and what is hypothetical | one sentence after the routing line: "Only this step's tool is offered now: the tools of the later planned steps are offered in the turns of their own steps, so do not call them early or treat them as available yet. Do not invent or assume any other tool, URL or resource." (both the plain and the control-function variant) | D-043 and the closed alias map are unchanged: the surface offered is exactly what it was; a call to a later step's tool is still refused and corrected in place (AE1-P1) | whether the model stops calling later steps' tools or inventing resources. Canonical-name/alias reconciliation was **not** done: it was not necessary (the exact-name correction turn already covers a wrong name) |

---

## C. Production changes

Two files, +93 −27.

`src/core/bOps.Runtime/AgentRunner.cs`

| Change | Where |
|---|---|
| `EarlyFinalReplanningLead` (new) and `ReplanningInstructions(evidenceReadExhausted, earlyFinal)`; `ReplanAsync` passes `trigger == EarlyFinalWithUnresolvedPlan`. The `PlanDeviation` lead is byte-identical. The new lead keeps the sentence "Revise it." because the scripted models of six test projects identify a replan request by it | R3 |
| `PlanCompleteInstructions` rewritten (keeps its first two sentences, which Knowledge tests pin) | R2 |
| `OneToolPerStepReason` (new), appended after the routing line when one step tool is offered | P1 |
| step prompt tail: "…a reply without a tool call is not taken as the final answer and only makes the runtime re-evaluate the plan." (the leading "Propose the concrete tool call for the next unfinished step above" is kept) | R3 |
| `KnowledgeControlInstruction(functionName, operationalToolOffered)` | R3 |
| `UnusableFinalRetryInstructions(response, stepToolOffered)` and the RC prefixes; the three static constants are gone. The retry loop passes `StepToolViewFor(plan, position.Cursor, delegation).Count == 1` and removes its recovery turn when the retry is answered with a usable reply | R4 |
| `ProtocolArtifactRetryInstructions` is replaced by `ProtocolArtifactRetryStepPending` ("…Call the operational tool offered for the current plan step with a native tool call, not with text markup.") and `ProtocolArtifactRetryPlanExhausted` ("…No operational tool is offered in this turn. Give your final answer … based on the evidence already collected and stating its limitations."). The call site picks one with the same `StepToolViewFor(plan, position.Cursor, delegation).Count == 1` test as R4; the retry budget, the failure after a repeated artifact and the tool surface are unchanged | R5 |

`src/core/bOps.AgentFramework/KnowledgeReadBridge.cs`: `proceed` of the refusal reply: "…continue with the current step's operational tool; give your final answer only if none is offered." (R3, one constant).

Not touched: LIVE-05's `ContinuationTurn` text ("Propose the call to X now, before giving any final answer" is now consistent with the system prompt), the tool catalog, manifests, fixtures, policy, approval, audit, `MaxOutputTokens`, the model, the Knowledge selection and authority code. `docs/architecture/adr/0046-…` has a new amendment that records the above.

---

## D. Golden and test changes (all deliberate, none to silence a failure)

| Item | Change |
|---|---|
| `Golden/pa1` | re-captured with the PA-1 capture mode, reviewed through the `.md` diff: **10 of 12 scenarios changed** (02B, 03, 04, 05, 06, 07, 08A, 08B, 09, 10); **PA1-01 (planning) and PA1-02A (ordinary replan) are byte-identical** to the baseline, which confirms that only the intended sentences moved. The diffs are exactly: the plan-complete paragraph (06, 08B, 10), the one-tool explanation and the new routing tail (every step request), the early-final lead (02B, 09), RC1 (07) and RC2 (08A, 08B) |
| `Golden/d3da032` (the older "byte-identical to d3da032" baseline) | edited by **exact text substitution** of the same sentences, not re-captured; the knowledge-free and the unbound requests still equal each other (`KnowledgeGoldenRequestTests` 4/4); README of both folders documents the decision |
| `PromptBaselineDefectTests` → `PromptRoutingFinalizationTests` | the 12 defect pins are rewritten as semantic properties of the corrected assembly (15 tests): absent invitations, present rules, unchanged channels. PRA-V1 keeps its observed-behaviour pin; the alias pin is kept and says it is unchanged on purpose |
| `RequestAssemblyInvariantTests` | `PA102_…NotInTheirLead` rewritten (the two replan kinds now differ in their lead when the trigger is an early final) |
| `ScriptedAgentic` | `AeCharacterizationTests` T05: the two `BASELINE` lines are rewritten; a new assertion that the RC2 at plan end asks for the final answer. `AeDiagnosticTests`: the routing sentence, T05 moved from "defect attributed to R4" to "no known defect (PARTIAL)". `AeRubric.RecoveredAnswer` reads the RC text. `AeCases` T05 `Today = Partial`. In `AeSafetyInvariantTests` **only the diagnostic-outcome column** of the finish-reason row changed; no safety check was touched |

---

## E. Tests executed (Windows 11, .NET from `global.json`, Debug; no Qwen, no llama.cpp, no Docker/WSL)

| Command (filter) | Passed | Failed |
|---|---:|---:|
| `bOps.AgentFramework.Tests` — whole project (it contains every consumer of the changed prompts; 8 live tests skipped by design) | **615** | 0 |
| of which `PromptAssembly` (PA-1: 71 → 74, the 12 pins became 15) | 74 | 0 |
| of which `ScriptedAgentic` (AE-1) | 84 | 0 |
| of which `KnowledgeGoldenRequestTests` | 4 | 0 |
| `bOps.Runtime.Tests` — `EarlyFinalReplanTests`, `EarlyFinalValidationRecoveryTests`, `ToolRoutingTests`, `EvidenceIndex*`, `EvidenceGrounding*`, `PlanningCorrectionTests`, `FinalAnswerUsabilityTests`, `EvidenceControlPlaneTests`, `EvidenceAddressabilityTests`, `ModelOutputObservationTests`, `*Recovery*`, `ResumeStateMachine*`, `PrematureFinal`, `Replan*` | 428 | 0 |
| `bOps.Runtime.Tests` — `Delegation*`, `AgentRunner*`, `TaskResume*`, `*Routing*`, `Knowledge*`, `Live05`, `LiveF1`, `Planning*`, `Truncat*`, `Overflow*` | 463 | 0 |
| `bOps.Architecture.Tests` (the core names no package/tool/provider; no generic execution tool) | 210 | 0 |
| `bOps.ReplayFixtureCapture.Tests` | 138 | 0 |

Not run, as instructed: the whole solution, the whole `bOps.Runtime.Tests`, the Linux/WSL tests. The two historical Runtime failures recorded by PA-1/AE-1
(`ToolCallProtocolTests…ToolCatalog…`, `ModelFailureEndToEndTests…TransientProviderFailure…`) were not run here either: "Runtime.Tests passes" is **not** claimed.

---

## F. Comparison with the PA-1 and AE-1 characterizations

**PA-1 (the text).** Frozen goldens → the diff of the `.md` files *is* the measured effect. Before/after, per defect:

| ID | Before (PA-1 pin) | After |
|---|---|---|
| R2 | "…propose the one new check you need instead: it will not run, and the runtime will ask you to revise the plan with it." | "Any text you write is taken as your final answer and ends the task: it cannot request more work. … request it instead with a native call to that tool by its exact name: it will not run in this turn, and the runtime will ask you to revise the plan with it." |
| R3 | step tail "a final answer makes the runtime re-evaluate the plan"; Knowledge "(or give your final answer)"; replan lead generic | "a reply without a tool call is not taken as the final answer and only makes the runtime re-evaluate the plan"; Knowledge "make the current step's operational call in your next reply" (final answer only at plan end); early-final lead of its own, `Revise it.` kept |
| R4 | RC1/RC2 static; RC2 "call the offered tool now" with only `runtime_evidence_read` offered; RC kept in later requests | RC follows the tool surface; RC gone from the evidence-read continuation once the retry is answered |
| P1 | none of the explanatory phrases present | the one-tool explanation present in the plain and in the control-function variant |

**AE-1 (the behaviour).** The scripted models still misbehave exactly as scripted, so the counts that depend on the *model's* replies are, by construction, unchanged:
the metrics table is identical to the AE-1 baseline for 12 of 13 runs (same calls, premature finals, replans, executions, refusals, tokens; the 9 negative controls and all 27 safety
invariants green). The one run whose measured *runtime* behaviour changes is **AE1-T05**: RC2 at plan end now asks for the final answer; the recovery turn is gone from the request that drew `>` (request 5 no longer carries it); the scripted model still reads again and answers `>`, which F1 still fails closed (`Failed/EmptyResponse`) — verdict `BASELINE_DEFECT` → `PARTIAL` (the remaining cause is the model's reply). T02/T10 (`BASELINE_DEFECT`), T06-B/T08-A (`BASELINE_DEFECT`, prose proposals) and AE1-P1 stay as they are: they pin what the runtime does *given* a model that finalizes early, proposes in prose or calls a later tool. That the new text makes a real model do so less often **cannot be shown offline** (AE-1 §J says the same).

---

## G. Residual limits

1. **Prose suggestions at plan end remain possible** (R2). A final that says "please also run X" is a persisted `Completed` answer; the runtime neither parses nor rejects prose (decided: no fragile natural-language parser). A formally coherent channel for "I need one more check" that does not depend on the model naming a tool it cannot see would be a control function — a design and an ADR of its own, outside this task.
2. At plan end the model sees only the tools named in the plan/history, not the catalog, so the native-call channel is useful only for a tool it already knows; for a new capability the honest path is to report the limit (that is what the text now asks for).
3. **No "no change" detection of a replan** (AE-1 §L.1): an early-final replan that returns the same plan is still accepted until `MaxReplans`. The new lead reduces the incentive; the bound is the only guard, unchanged.
4. A recovery turn survives an *unusable* retry (by design: it is the instruction that retry saw). With `EmptyFinalResponseRetries > 1` the earlier turn would be repeated; the default is 1 and the option was not changed.
5. ~~`ProtocolArtifactRetryInstructions` had the same state-blindness as RC1/RC2~~ — resolved by PRA-R5 (§C): the correction now follows the offered tool surface. Covered offline by `TerminalProtocolArtifactTests` T4 (step pending: native call, no final-answer invitation) and T4b (empty plan: final answer, no tool call); like the other texts, its effect on a real model is not shown offline.
6. The step prompt is longer by one sentence on every step (and the plan-complete paragraph by ~70 words); the added tokens are small but real and are not measured by this task.
7. llama.cpp's chat template, the fallback (no-native-tools) mode and Linux byte identity remain uncovered (as in PA-1/AE-1). Reasoning cost (PRA-Q1) and grounding contradictions (PRA-V1) are out of scope.

---

## H. Minimal live validation on Qwen3.8-Flash-Next-Smart (to be run by the operator; nothing was started)

Same build, same corpus, same sampling as `QWEN38-DIRECT-COMPARISON-01`; only these scenarios, one run each first, three if a result is ambiguous. Compare with the stored Qwen3.8 runs, not with Qwen3.6.

| Run | Why | Read from the audit/transcript | Expected direction (not a promise) |
|---|---|---|---|
| T02 | R3 | premature finals, early-final replans (paid, added steps), completion tokens | fewer rejected finals; early-final replans return `[]` or only owed steps |
| T10 | R3 + Knowledge | finals right after `runtime_knowledge_read`; the read count and grants | no final right after the read while steps are pending; one read |
| T08 | R2 | any `x.y` tool name in the final that is not in the registry | no invented tool presented as a next check; limit reported |
| T06 | R2 | deferred check mentioned only in prose | check requested natively or reported as not done |
| T05 | R4 | RC2 text at plan end, redundant evidence reads, `>` | final answer after RC2 instead of a second read |
| One T04-like multi-step run | P1 | calls to a later step's tool (`UnofferedTool`), invented URLs/resources | fewer misrouted calls |

Stop rule: any `Completed` with an owed step, any executed tool outside the plan, any empty/`>` final accepted, or a Knowledge regression (extra reads, ungranted reads) is a defect of this change, not of the model. Efficacy is judged on counts (premature finals, replan calls, redundant reads, invented names) across the runs, never on one.
