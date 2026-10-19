# AE-1 — Scripted agentic regression baseline

> **Update 2026-10-19:** PROMPT-ROUTING-FINALIZATION was applied on top of this baseline (`prompt-routing-finalization.md`). Of the 13 runs only AE1-T05 changed (`BASELINE_DEFECT` → `PARTIAL`, the RC2 text and lifetime); the others are unchanged because their models are scripted. The tables below describe the baseline at `7053ca0`.

Scope: AE-1 only. This report freezes **what the real runtime does today** over five scripted sequences taken from the Qwen campaign
(T02, T05, T06, T08, T10), so that `PROMPT-ROUTING-FINALIZATION` (PRA-R2, PRA-R3, PRA-R4, PRA-P1) can be measured against it. Nothing
here corrects a defect, changes a prompt, a manifest, a policy or a model setting, and no commit was made. Qwen and llama.cpp were not
called; no Docker, WSL or other service was started.

**Verdict: 5 of 5 required scenarios implemented (13 runs with variants and controls); 84 new AE-1 tests pass; the 71 PA-1 tests still
pass; the 24 PA-1 golden files and the bench00 report are byte-identical to before. Diagnostic states are kept apart from test results:
9 of the 13 runs are `BASELINE_DEFECT`, 2 `PARTIAL`, 2 `PASS` (the healthy controls); none is `FAIL`, `NOT_RUN` or `NOT_COVERED`.**
The baseline PA-1 + AE-1 is solid enough to start `PROMPT-ROUTING-FINALIZATION`, with the limits in sections J and L.

Sources: `bOps_Continuita_Master_2026-10-18.md` §§7.2, 7.3, 7.8 (found in the operator's Downloads folder, not in the repository),
`PROMPT-RUNTIME-AUDIT-01-report.md` and `RELEASE-VALIDATION-01-report.md` (both in `C:\source\repos\babbubba\bOps-backups\`), and
the code at `7053ca0`. Where a scenario crosses a boundary the relevant ADR was read and the observed behaviour checked against it: ADR-0046 (AF-05H: an empty replan is an
accepted revision and the replan budget bounds a model that answers early; AF-05H2: continuations are never charged, are strictly
further along each time, and "grant nothing"), ADR-0057 (the function is offered while `ReadsRemaining` is not 0; a repeated item is audited
with `Repeat = true`) and ADR-0052 (the context layer decides nothing about completion). No scenario contradicted them.

---

## A. Git baseline

| | |
|---|---|
| Worktree | `C:/source/repos/babbubba/bOps-knowbench01a` |
| Branch | `codex/know-bench-01a` |
| HEAD | `7053ca0c8380f258442903360c44ec0fe1789bae` — unchanged from the start to the end of the task |
| Initial `git status --short` | ` M tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj`, `?? docs/benchmarks/bench00-maf-compatibility-report.md`, `?? docs/benchmarks/pa-1-request-assembly-baseline.md`, `?? tests/bOps.AgentFramework.Tests/Golden/pa1/`, `?? tests/bOps.AgentFramework.Tests/PromptAssembly/` (the PA-1 working state, as announced) |
| Final `git status --short` | the same five lines plus `?? tests/bOps.AgentFramework.Tests/ScriptedAgentic/` and, after this report, `?? docs/benchmarks/ae-1-scripted-runtime-baseline.md` |
| PA-1 | not discarded, overwritten or committed. Its 24 golden files hash identically before and after (25 lines of `sha256sum`, `diff` empty); `pa-1-request-assembly-baseline.md` is unchanged (`ac8787da…df892`) |
| bench00 | still untracked, SHA-256 `1D1B3AF0…1960` before and after |
| Commits / push / merge / rebase / reset / amend / clean | none |

The session's own working directory (`C:\source\repos\babbubba\bOps`, branch `codex/host-canary-01b`) and every other worktree were not touched.
One scratch copy was made **outside any worktree** for the detection experiment of section G (`C:\vb\f1`, a `git archive` of `291e82f`, no git
state touched); the cleanup command was refused by the safety check, so **that directory is still on disk and is the operator's to delete**.

---

## B. Changes

No production file, manifest, prompt, ADR, policy or runtime configuration was changed. The repository's tracked files changed by nothing in AE-1.

| Kind | Path |
|---|---|
| New — scenarios | `tests/bOps.AgentFramework.Tests/ScriptedAgentic/AeScenarios.cs` (scripts, synthetic tools `test.logs`, `test.extra`, `test.whoami`, `test.size`, a one-tool deny policy) |
| New — observation | `…/AeObservation.cs` (classifies every provider request, persisted step and plan change; the counts) · `…/AeCases.cs` (case table with today's verdict and the desired direction) |
| New — judgement | `…/AeRubric.cs` (the diagnostic states and the five rubrics) · `…/AeInvariants.cs` (the safety and runtime checks) |
| New — tests | `…/AeCharacterizationTests.cs` (21) · `…/AeSafetyInvariantTests.cs` (27) · `…/AeDiagnosticTests.cs` (19 + 13 determinism + 3 PA-1 link) · `…/AeReportTests.cs` (1; writes the tables of this report when `BOPS_AE1_REPORT_DIR` is set) |
| New — report | `docs/benchmarks/ae-1-scripted-runtime-baseline.md` (this file) |
| Modified (PA-1 test helpers, **additive**, still uncommitted like the rest of PA-1) | `PromptAssembly/PaScenarios.cs`: `PaSetup.Policy` (optional, default `DefaultTestPolicyEngine`) and `PaRun.Audit` filled by `PaCompose`; `PromptAssembly/PaWire.cs`: the `Audit` member and its `using`; `PromptAssembly/PaInvariants.cs`: `AssertEveryRequestIsAValidGovernedBody` takes an optional `expectedStatus` (default `Completed`, so every PA-1 call is unchanged) |

Reused as they are: `LiveRegressionHarness`'s `CallPurpose` enum (every provider request is classified into it), `PaReply`, `PaUpstream`,
`PaCompose`, `PaInvariants`, `PaTool`/`PaFixtures`, the PA-1 goldens, `KnowledgeRunnerWorld`/`TestPack`, `RecordingAuditSink`,
`DefaultTestPolicyEngine`, `InMemoryTaskStore`. **No new runner was built.** `LiveRegressionHarness`'s `LiveScriptedModel` is an `IChatModel`
that replaces the provider, so it never reaches the adapter; AE-1 uses the PA-1 path (below) and takes only the `CallPurpose` instrumentation
from it. That is a deliberate reading of "reuse the harness": the real adapter and the wire body stay in the loop.

### How a run is produced

```
scripted model answers ─► PaUpstream (offline HTTP handler, OpenAI schema)
   ◄─ OpenAiCompatibleChatModel (real adapter, native tool calling, real parser: finish reasons, length, aliases)
   ◄─ ModelInvocationBroker + MafContextProjector      (the composition of bOps.Api and bOps.Cli)
   ◄─ AgentRunner (real: plan, step, early-final, replan, LIVE-05, recovery, evidence, knowledge)
      ─► real ToolRegistry, real policy interface, real audit sink, real in-memory task store; the tools are synthetic and side-effect-free
```

Only the model's answers and the tools' output are scripted. The runner's decisions (what to offer, what to accept, when to replan) are not.

### PA-1 helper fidelity (brief §1)

Checked before relying on the helpers; no regression found.

| Question | Finding |
|---|---|
| Is the serialization path the real one? | Yes. `PaUpstream` keeps the bytes of `HttpRequestMessage.Content` that `OpenAiCompatibleChatModel` produced; nothing is re-serialized by the test. The wire count equals the adapter-received count in every AE-1 run (`AssertBounded`). |
| Is a golden built by the helper under test? | No. The golden `.json`/`.md` are checked-in files compared with the run's output; the AE-1 link tests read them from disk. The 24 files were hashed before and after. |
| Do assertions reuse the code they judge? | PA-1's tool alias (`Replace('.', '_')`) is a test-side re-implementation of `ToolWireNames`; the round trip through the real adapter proves they agree for these names, and the request's own tool list is compared with it. PA-1's `PlanTools` regex and AE-1's `HasAnswerContent` are test-side too (AE-1 deliberately does **not** call the runtime's `FinalAnswerContent`, and it strips the runtime's own "Evidence limitations" section before judging a final — see G). |
| Weak points of PA-1 that AE-1 inherits | `new AgentRunnerOptions()` rather than `appsettings.json` (the prompt-shaping keys agree, per PA-1); the Windows platform only; the opt-in MAF-turn broker is not used. |

---

## C. Rubric (diagnostic states)

A green test is never a state of this table. A test that passes while pinning a known defect is a **characterization** test and the
scenario's state is `BASELINE_DEFECT`.

| State | Meaning |
|---|---|
| `PASS` | the objective is reached and no known baseline defect shows in the run |
| `PARTIAL` | the objective is reached only in part for a reason **outside the prompt layer** (a missing capability or permission), and the run does not hide it |
| `FAIL` | the objective is not reached, or an invariant is broken, and no known defect explains it: a regression to investigate |
| `BASELINE_DEFECT` | a known defect of PRA-R2 / R3 / R4 / P1 is observed and costs work or the objective; the verdict names the layer |
| `NOT_RUN` | not executed |
| `NOT_COVERED` | cannot be reproduced without changing production code (section J) |

Each verdict also says whether the scenario's **objective** was met: a run can reach it and still carry a defect (the cost of rejected
finals), or miss it with no defect to blame (an absent capability).

---

## D. Test classes, and what each kind may and may not do

| Kind | Class | Count | Rule |
|---|---|---:|---|
| **A — characterization** | `AeCharacterizationTests` | 21 | Pin today's requests, offers, steps, cursors and counts, call by call. Stay green while the runtime equals the baseline **even where that behaviour is the defect**. Lines marked `BASELINE` in the code are rewritten on purpose when the prompts change. |
| **S — safety / runtime invariants** | `AeSafetyInvariantTests` | 27 | Never rewritten by a prompt change. 9 negative controls prove each check can fail. |
| **D — diagnostic evaluation** | `AeDiagnosticTests` | 19 | The rubric verdict per case, separate from the above; also proves the rubrics can say `PASS`. |
| determinism | `AeDeterminismTests` | 13 | Two runs of one script: identical calls, steps, counts and provider bodies. |
| link to PA-1 | `AePa1GoldenLinkTests` | 3 | The AE-1 requests start with the standing prompt of the PA-1 golden, carry its routing sentence, its plan-complete paragraph and its RC2 text. |
| report | `AeReportTests` | 1 | Runs every case; with `BOPS_AE1_REPORT_DIR` set, writes the call/step tables and the metrics table below. |

---

## E. The five scenarios

Notation: a request line is `# purpose · scripted reply · offered operational tool · cursor (index of the offered step / plan length) · tag`.
The cursor is read **from the wire** (the plan section of the system prompt and the one offered tool); the runtime's own fold
(`PlannedStepPosition.Derive`) gives the cursor after each persisted step; the two are asserted to agree in every run
(`AssertCursorsAgree`). "Control" = the runtime control functions offered beside the step tool.

### AE1-T02 — premature finals and replanning (origin: RELEASE-VALIDATION-01 T02)

Real run: six premature finals, replans that added four steps each, two early-final replans that were very expensive, one truncated, LIVE-05
continuations that let it progress, an external stop after ~15 minutes.

| Variant | Question it answers | Sequence of provider requests | Result |
|---|---|---|---|
| **AE1-T02** (main) | does the cursor stay coherent through premature finals, a replan that adds a step, a paid replan cut at the output limit, and LIVE-05? | 0 plan[read,other,logs] · 1 call read (0/3) · **2 premature text** (1/3) · 3 replan → rev 1 [other,logs,**extra**] · 4 call other (0/3) · **5 premature** (1/3) · 6 replan truncated (`length`) · 7 call logs (1/3, LIVE-05) · **8 premature** (2/3) · 9 call extra (2/3, LIVE-05, **no replan call**) · 10 final | `Completed`; 11 calls; each of the 4 tools executed **once**, in plan order |
| **AE1-T02-LOOP** | is the premature-final loop bounded, and does it ever complete? | 0 plan · (premature, identical replan) ×3 · 7 premature | `ReplanLimitReached/ReplanLimit` after 8 calls; 0 tools executed; cursor never leaves 0; stop = `MaxReplans = 3`, not the script |
| **AE1-T02-EMPTY** | can the task complete with mandatory steps owed? | 0 plan · 1 read · 2 premature · 3 replan **with an empty plan** · 4 final | `Completed`; `test.other` and `test.logs` never ran: allowed only because revision 1 is empty (a formal revision) |
| **AE1-T02-POLICY** | does LIVE-05 skip governance? | 0 plan[read,logs,other] · 1 read · 2 premature · 3 truncated replan · 4 call logs (1/3, LIVE-05) · 5 replan · 6 other · 7 final | `test.logs` is forbidden by policy: the continuation **offers** it, the call is **denied** (audit `PolicyDenied`, `Forbidden` decision), 0 invocations; the replan routes around it |
| AE1-T02-CLEAN | (control) | 0 plan · 3 calls · final | `PASS`: 5 calls, 0 premature |

What the main run records:

| Plan change | owed when revision 0 ended | added | dropped | motivation persisted |
|---|---|---|---|---|
| rev 0 → 1 | `test.other`, `test.logs` | `test.extra` | — | "Add a cross-check of the extra counters after the logs." (`AgentPlan.Rationale`) |

Reproduced: premature finals ×3 (plus 4 in LOOP), replans that add steps (+1, not +4), a truncated replan, LIVE-05 continuations, the skipped
paid replan (`PaidReplanSkipped`, ADR-0046 AF-05H2), the replan budget as the anti-loop stop. **Not** reproduced: six finals in one run, +4
steps per replan, the external 15-minute stop (section J). Prompts were not touched, so the phenomenon stays measurable.

### AE1-T05 — `length`, recovery and final `>` (origin: T05, LIVE-F1-FIX-01)

| Request | Purpose | Scripted reply | Offered | Tag |
|---:|---|---|---|---|
| 0 | Planning | plan [read] | — | |
| 1 | Step | call read (large result) | read (0/1) | |
| 2 | Step | **evidence read #1** | control only (1/1) | |
| 3 | EvidenceReadContinuation | **`finish_reason=length`, no content** | control only | |
| 4 | Step | **evidence read #2, same address** | control only | **RC2** |
| 5 | EvidenceReadContinuation | **`>`, `finish_reason=stop`** | control only | RC2 (still present) |

Result: **`Failed/EmptyResponse`**, never `Completed`; no final-response step persisted; the `>` is not stored anywhere and no
"Evidence limitations" section was added; the last model call ended with `finish_reason=stop` and the task is nevertheless `Failed`
(`stop` proves the reply ended, not that it answered). Recovery turns = 1 = `EmptyFinalResponseRetries`; with 0 retries the run ends at
request 3 with no recovery turn (theory `retries ∈ {0,1}`). `evidenceReads = 2`, of which **1 redundant** (same step, source, offset,
length), counted apart from the single tool execution.

The F1 fix (`06cd4aa`) is present in this tree and is what these tests pin. The pre-F1 behaviour (`Completed` with a lone `>` made to look
like an answer by the disclosure section) is **not** a permanent test; it was reproduced once on a source copy of `291e82f` (section G).
RC2 and the temporary-turn handling were not touched (PRA-R4).

### AE1-T06 — deferred check and incomplete objective (origin: T06)

Goal: find the largest folders of the user's profile. Tools: disk usage, identity (reports no profile location), and a stand-in for `fs.size`
(`test.size`) whose **own path policy permits nothing**. The refusal is scripted; no policy is changed and no path is authorized.

| Variant | Channel | Sequence | Executed | Result |
|---|---|---|---|---|
| **A** — requested | at plan end the model **calls `test.size` natively** (not offered) → rejected and audited → replan → plan [size] → `test.size` runs → refused by the tool → honest final | 0 plan[other,whoami] · 1 other · 2 whoami · **3 call size (refused: not offered)** · 4 replan [size] · **5 call size (executed)** · 6 final | 3 (other, whoami, size ×1) · 1 refused | `Completed`; `PARTIAL` |
| **B** — described only | the final **names `test.size`** as the next step; no call, no plan step | 0 plan · 1 other · 2 whoami · **3 final prose** | 2 · 0 refused · 0 replans | `Completed`; `BASELINE_DEFECT` |

B proves what the brief asks: the prose is not counted as an execution (`test.size` has 0 invocations, 0 audit events, 0 steps) and the
exhausted plan lets the first prose through as the final answer (no replan). A shows the only channel that today turns a proposal into work.
Both run under the default test policy; neither can reach the objective.

### AE1-T08 — a tool that does not exist, proposed at plan end (origin: T08)

| Variant | Sequence | Result |
|---|---|---|
| **A** — in prose | 0 plan[read] · 1 read · 2 **final prose proposing `system.sensor`** (no native call) | `Completed`; `system.sensor` is not in the registry, never executed, **not even a refused call** (nothing reached the runtime); no replan; the proposal is the persisted final, verbatim → `BASELINE_DEFECT` |
| **B** — native | 0 plan · 1 read · 2 **native call to `system.sensor`** · 3 replan (empty) · 4 honest final | rejected by the adapter's closed name map (`ToolNameError`), audited `UnknownTool` (never executed), replanned; `Completed`; `PARTIAL` (the capability is absent) |

No parser for tool names in prose was introduced and `PlanCompleteInstructions` is untouched. The qualitative evaluator flags A because the final
**proposes a capability that the registry lacks** (the scenario names the absent tool; the evaluator is test-side only).

### AE1-T10 — Knowledge read then premature final (origin: T10)

Synthetic signed pack, bound and granted (`KnowledgeRunnerWorld`); plan [read, other, logs].

| # | Purpose | Reply | Offered operational · control | Cursor | Tag |
|---:|---|---|---|---|---|
| 1 | Step | **`runtime.knowledge_read`** (read_resource) | read · knowledge | 0/3 | K-turns 1 (index) |
| 2 | Step | **premature text** | read · knowledge | **0/3** | K-turns 2 |
| 3 | Replanning | truncated (`length`) | — | | |
| 4 | Step | call read | read · knowledge | 0/3 | LIVE-05 |
| 5 | Step | **premature** (paid replan skipped) | other · evidence, knowledge | 1/3 | |
| 6 | Step | call other | other | 1/3 | LIVE-05 |
| 7 | Step | **premature** (skipped) | logs | 2/3 | |
| 8 | Step | call logs | logs | 2/3 | LIVE-05 |
| 9 | Step | final | — | 3/3 | |

Result: `Completed`, 10 calls, 3 premature finals, 1 paid replan (cut by `length`) and 2 skipped, 3 tools executed once each. The Knowledge read
did **not** exhaust the plan (request 2 still offers the pending tool at cursor 0). Exactly **one** `KnowledgeReadAuditEvent` (`Success`,
`read_resource`, not a repeat), equal to the one the script asked for; no refusal or `NotAuthorized`; its pack id/version/tenant equal the
task's `KnowledgeBinding`; every `KnowledgeIndexAuditEvent` is `Published` under that pack. The knowledge function stays offered on every step
request. The read result is carried as a Knowledge-provenance turn in **every later request** (K-turns = 2), placed after the history (the
ordering the audit noted in §6.5). Knowledge was not modified.

Knowledge audit events are written by the host's own sink; AE-1 reads them from `KnowledgeRunnerWorld.Events` (`AeRun.HostAudit`).

---

## F. Metrics per scenario

`modelCalls` = provider requests the adapter received (= HTTP bodies). `callsByPurpose` = `CallPurpose` of each request, classified from the
request itself: **P** planning · **R** replan · **S** step · **E** evidence-read continuation · **G** grounding check. `replans` = accepted
revisions (provider calls to the replan, charged to the budget, skipped by AF-05H2). `syntheticTokens` = the 100 + 10 per call that the
offline upstream declares: **they are not Qwen tokens and say nothing about Qwen cost or latency.**

| Scenario | modelCalls | callsByPurpose | toolExecutions | refused calls | prematureFinals | replans | recoveryTurns | evidenceReads | knowledgeReads | finalStatus | syntheticTokens | diagnosticOutcome |
|---|---:|---|---:|---:|---:|---|---:|---|---:|---|---:|---|
| AE1-T02 | 11 | P1 / R2 / S8 / E0 / G0 | 4 | 0 | 3 | 1 (2 calls, 2 charged, 1 skipped) | 0 | 0 | 0 | Completed | 1210 | **BASELINE_DEFECT** |
| AE1-T02-LOOP | 8 | P1 / R3 / S4 / E0 / G0 | 0 | 0 | 4 | 3 (3 calls, 3 charged, 0 skipped) | 0 | 0 | 0 | ReplanLimitReached/ReplanLimit | 880 | **BASELINE_DEFECT** |
| AE1-T02-EMPTY | 5 | P1 / R1 / S3 / E0 / G0 | 1 | 0 | 1 | 1 (1 call, 1 charged, 0 skipped) | 0 | 0 | 0 | Completed | 550 | **BASELINE_DEFECT** |
| AE1-T02-POLICY | 8 | P1 / R2 / S5 / E0 / G0 | 2 | 1 (policy) | 1 | 1 (2 calls, 2 charged, 0 skipped) | 0 | 0 | 0 | Completed | 880 | **BASELINE_DEFECT** |
| AE1-T02-CLEAN | 5 | P1 / R0 / S4 / E0 / G0 | 3 | 0 | 0 | 0 | 0 | 0 | 0 | Completed | 550 | **PASS** |
| AE1-P1 | 6 | P1 / R0 / S5 / E0 / G0 | 3 | 1 | 0 | 0 | 0 | 0 | 0 | Completed | 660 | **BASELINE_DEFECT** |
| AE1-T05 | 6 | P1 / R0 / S3 / E2 / G0 | 1 | 0 | 0 | 0 | 1 | 2 (1 redundant) | 0 | Failed/EmptyResponse | 660 | **BASELINE_DEFECT** |
| AE1-T05-CLEAN | 4 | P1 / R0 / S2 / E1 / G0 | 1 | 0 | 0 | 0 | 0 | 1 (0 redundant) | 0 | Completed | 440 | **PASS** |
| AE1-T06-A | 7 | P1 / R1 / S5 / E0 / G0 | 3 | 1 | 0 | 1 (1 call, 1 charged) | 0 | 0 | 0 | Completed | 770 | **PARTIAL** |
| AE1-T06-B | 4 | P1 / R0 / S3 / E0 / G0 | 2 | 0 | 0 | 0 | 0 | 0 | 0 | Completed | 440 | **BASELINE_DEFECT** |
| AE1-T08-A | 3 | P1 / R0 / S2 / E0 / G0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | Completed | 330 | **BASELINE_DEFECT** |
| AE1-T08-B | 5 | P1 / R1 / S3 / E0 / G0 | 1 | 1 | 0 | 1 (1 call, 1 charged) | 0 | 0 | 0 | Completed | 550 | **PARTIAL** |
| AE1-T10 | 10 | P1 / R1 / S8 / E0 / G0 | 3 | 0 | 3 | 0 accepted (1 call, 1 charged, 2 skipped) | 0 | 0 | 1 | Completed | 1100 | **BASELINE_DEFECT** |

Every count is computed from the persisted task state, the audit log, or the requests the adapter received; none from the script.
`toolExecutions` (tool objects invoked) equals the executed `ToolCallAuditEvent`s (`EffectiveTimeout` set) in every run. Tool "offers" are
the per-request operational/control columns of the sequences above. Regenerate the full call/step tables with
`BOPS_AE1_REPORT_DIR=<dir> dotnet test tests/bOps.AgentFramework.Tests --filter FullyQualifiedName~AeReportTests`.

---

## G. Safety and runtime invariants exercised (`AeSafetyInvariantTests`)

Checked on **all 13 runs** (and the T05 retry theory); a failure here is a regression and is never a "defect to pin".

| Invariant (brief §5) | Check |
|---|---|
| no unauthorized tool call | every executed call is `Automatic`/`UserApproved`; no `Forbidden` decision exists for an executed step/tool; a forbidden call has `PolicyDenied`, no `EffectiveTimeout`, 0 invocations |
| no execution of an inexistent tool | executed tools ⊆ registry; `system.sensor` is never registered or executed in any run; native calls to unregistered/unoffered names are refused and audited |
| no policy bypass | T02-POLICY: LIVE-05 offers the forbidden tool and the policy still denies it |
| no `Completed` with an unusable final | `Completed` ⇒ the model's part of the final has a letter or digit; otherwise no final-response step is persisted |
| no loss of persisted steps | step indexes 0..n−1 contiguous; plan revisions 0..k contiguous; step revisions non-decreasing |
| no tool/Knowledge text promoted to instruction | no line of any tool or Knowledge turn occurs in any system prompt; tool turns carry ToolResult/Knowledge/Runtime provenance and the delimiters (PA-1 `AssertProvenance`) |
| no unbounded recovery | recovery turns ≤ retries × steps with extra calls; replans charged ≤ `MaxReplans`; steps ≤ `MaxSteps`; no hidden provider call (adapter count = HTTP count) |
| `finish_reason`, usability, correctness are three facts | `AE1-T05`: `stop` / unusable / `Failed`; `AE1-T05-CLEAN`: `stop` / usable / `PASS`; `AE1-T08-A`: `stop` / usable / still `BASELINE_DEFECT` |
| the request is a valid governed body | PA-1's check, on every request of every run, with the run's own terminal status |

**Negative controls (9)** — each is a corrupted copy of a real run, and each must be caught: a `Completed` task with owed steps; a `>` persisted as
the answer of a `Completed` task; a tool outside the registry that "ran"; an executed call that policy had forbidden; tool output in a system
prompt; a replan budget exceeded; a cursor that disagrees with the offering request; a missing persisted step; a hidden model call.

**Detection experiment (not a permanent test).** The same 84 tests were run on a source copy of `291e82f` (the commit before the F1 fix) with
the PA-1/AE-1 files copied in. 7 fail, all on T05, where the task ends `Completed` with a vacuous `>` that the disclosure section makes look like text: four safety tests
(`EveryInvariant_HoldsInEveryScenario(AE1-T05)`, `EveryInvariant_HoldsWhenTheRetryBudgetIsChanged(1)`, `NoCompletedTask_HasAnUnusableFinal…`,
the finish/usability test) and three characterization tests; the other 77 pass. So the suite detects the historical safety defect, and the independent "model's part of the
final" check is what makes it do so (the first attempt, which counted the runtime's own section as content, did not). On this tree all 84 pass.

---

## H. Tests executed

Windows 11, .NET SDK from `global.json`, Debug. No Qwen, llama.cpp, Docker, WSL or network service.

| Command (filter shortened) | Passed | Failed |
|---|---:|---:|
| `bOps.AgentFramework.Tests` — **AE-1** (`ScriptedAgentic`) | **84** | 0 |
| `bOps.AgentFramework.Tests` — **PA-1** (`PromptAssembly`), re-run after the AE-1 helper changes | **71** | 0 |
| `bOps.AgentFramework.Tests` — both together | 155 | 0 |
| `bOps.AgentFramework.Tests` — `KnowledgeGoldenRequestTests`, `ContextGateTests`, `ToolRoutingRegressionTests`, `KnowledgeStepPromptTests`, `PlanningContractVisibilityTests`, `MafContext*`, `MafTurn*`, `MafBoundaryGuardTests`, `ContextProvenanceTests`, `GovernedChatClientTests`, `KnowledgeIndexRunnerTests`, `KnowledgeRefusalRecoveryTests`, `KnowledgeContextContinuityTests`, `ReplayContractAlignmentTests`, `LiveRegressionTests`, `LiveTokenBenchmarkTests` | 159 | 0 |
| `bOps.Runtime.Tests` — `EarlyFinalReplanTests`, `EarlyFinalValidationRecoveryTests`, `ToolRoutingTests`, `EvidenceIndex*`, `EvidenceGrounding*`, `PlanningCorrectionTests`, `FinalAnswerUsabilityTests`, `EvidenceControlPlaneTests`, `EvidenceAddressabilityTests`, `ModelOutputObservationTests`, `*Recovery*`, `ResumeStateMachine*` | 361 | 0 |
| `bOps.Knowledge.Tests` (whole project) | 308 | 0 |
| `bOps.Packages.Providers.OpenAiCompatible.Tests` (whole project; wire/serialization) | 127 | 0 |
| `bOps.Runtime.Tests` — `ToolCallProtocolTests`, `ModelFailureEndToEndTests` (run on purpose) | 13 | **2** |

**The two historical Runtime failures are unchanged and kept apart**, not fixed, not in any other suite run here:

1. `ToolCallProtocolTests.TheToolCatalog_KeepsTheNameTheRiskAndTheFirstSentenceOfEachTool_InOrdinalOrder_AndIsBounded`
2. `ModelFailureEndToEndTests.ATransientProviderFailure_RecoversWithinBounds_AndEveryAttemptIsAudited`

The whole solution and the whole `bOps.Runtime.Tests` project were not run, so "Runtime.Tests passes" is **not** claimed. No new regression in any
executed suite. Two assumptions of the first draft were wrong and were corrected from the runtime, not worked around: an *Automatic* policy
decision is not written as a `PolicyDecisionAuditEvent` (only non-automatic ones are; an executed Read call is recorded as `Automatic` on its
`ToolCallAuditEvent`), and a policy-denied call is persisted with `Outcome = Failure` / `FailureKind = Authorization`, not `Denied`.

---

## I. Links to PA-1

| AE-1 request | PA-1 golden it shares its assembly with | Asserted by |
|---|---|---|
| step requests (T02, T05, T06, T08, P1) | `pa1-03-step-pending-tool` (standing prompt up to the plan; routing sentence "while planned steps remain, a final answer makes the runtime re-evaluate the plan.") | `AePa1GoldenLinkTests.EveryStepRequestOfAe1…` |
| plan-complete requests (T08-A #2, T06-B #3, T02-CLEAN #4) | `pa1-06-plan-exhausted` (the `PlanCompleteInstructions` paragraph; no operational tool offered) | `…ThePlanCompleteRequestsOfAe1…` |
| T05 request #4 (RC2 at plan end) | `pa1-08b-recovery-rc2-exhausted` (the same RC2 text, last turn) | `…TheRecoveryTurnOfAe1T05…` |
| T02 premature final / early-final replan | `pa1-02b-replan-early-final`, `pa1-09-live05-continuation` | the same pipeline; byte checks by the PA-1 suite itself |
| T10 Knowledge request #1/#2 | `pa1-05-knowledge` | PA-1 golden unchanged; AE-1 pins the read, grants and index audit |

The PA-1 defect pins (`PromptBaselineDefectTests`) and the AE-1 characterization pins are complementary: PA-1 freezes **the text**, AE-1 the
**behaviour that text produces** in the runtime.

---

## J. Coverage deficits still open

| Item | Status |
|---|---|
| T02: six premature finals in one run; replans that add **four** steps each; the external stop after ~15 min | `NOT_COVERED` — AE-1 reproduces 3 finals (+4 in LOOP), +1 step, and the budget stop; the exact counts depend on Qwen's behaviour and a wall-clock kill is an external event. The attempt-duration budget (`MaxAttemptDuration`) is not exercised. |
| T05: the **pre-F1** `Completed` with `>` | not a permanent test (it is fixed); reproduced once on a `291e82f` copy (G) |
| T06: the deferral that lived only in Qwen's reasoning | not observable by the runtime; modelled as prose in the final (variant B) |
| T10: `fs.list` denial and the contents of the real pack | not reproduced (synthetic pack) |
| Resume across attempts (LIVE-05 `SafePosition` after a restart), `MaxTotalTokens` crossing, delegated roles | not exercised |
| PRA-V1 grounding contradictions (`GuardGroundingAsync` correction path), PRA-Q1 reasoning cost | out of scope here |
| llama.cpp chat template, the fallback (no-native-tools) mode, Linux/WSL | not covered (as in PA-1) |
| PRA-P1 | measured only as the **cost and containment** of a scripted misroute (`AE1-P1`); that the prompt *causes* it is plausible (audit reasoning of T04/T05), **not proven** by any scripted test |
| Efficacy of any prompt change on Qwen | not measurable offline; needs the few live reruns the master plans after the patch |

---

## K. Current behaviour versus the desired direction after `PROMPT-ROUTING-FINALIZATION`

"Desired" is the direction stated in the master (§7.3) and the brief, not a design. **Pins to rewrite on purpose in the same change** are the
`BASELINE`-marked lines of `AeCharacterizationTests` and the `Today` verdicts of `AeCases`; the safety class never changes.

| Case | Today (pinned) | Desired after the change | Layer |
|---|---|---|---|
| AE1-T02 | 3 premature finals, 11 calls, replan adds a step, 2 paid replan calls | fewer premature finals; an early-final replan does not add steps by default; anti-loop bounds and LIVE-05 unchanged | PRA-R3 |
| AE1-T02-LOOP | 4 finals, identical replan accepted 3 times, `ReplanLimitReached` after 8 calls | loop not entered; the budget stays a guard rail | PRA-R3 |
| AE1-T02-EMPTY | an empty replan lets the task complete with 2 checks never run | a specific lead that allows an empty plan only when nothing is left; `Completed` with owed steps stays impossible unless formally revised | PRA-R3 |
| AE1-T02-POLICY | continuation offers a forbidden Read tool; policy denies; one wasted request | denial and audit unchanged | (safety — unchanged) |
| AE1-T05 | RC2 at plan end → redundant read → `>` → `Failed/EmptyResponse`; RC2 still in the last request | RC2 depends on the state (at plan end: final answer, not "call the offered tool"); a satisfied recovery turn does not outlive its purpose; the `>` guard (F1) stays | PRA-R4 |
| AE1-T06-B / T08-A | prose proposal accepted as the final of a `Completed` task | a recognized channel (the native call of the canonical tool, `ToolRoutingTests` C1–C6) or an answer that reports the limit; no parser of prose; no invented tool | PRA-R2 |
| AE1-T06-A / T08-B | native call → replan → (refusal / honest limit) | unchanged | (works today) |
| AE1-T10 | premature final right after the Knowledge read; 3 finals; the read result carried in every later request | the Knowledge control text no longer offers a final answer while steps are pending; 1 read, no premature final after it, grants unchanged | PRA-R3 (+ Knowledge text) |
| AE1-P1 | a later step's tool is called, refused and corrected in place: 1 extra request | the prompt says why one tool is offered and that the others come in later turns; the in-place correction stays as the safety net | PRA-P1 |

---

## L. Observations about the runtime (recorded, not fixed)

1. A premature-final replan that returns **the same plan** is accepted (`AE1-T02-LOOP`): three paid replans and zero progress before the budget
   stops it. There is no "no change" detection; the bound is the only guard.
2. A replan **can drop** an owed step, including a policy-denied one, and an empty replan is accepted as a formal revision (`AE1-T02-EMPTY`).
3. The LIVE-05 continuation requires the pending tool to be `Read` and visible; it does **not** consult policy, so it can offer a tool that
   policy will then deny (a request is spent; no governance is skipped).
4. An *Automatic* policy decision leaves no `PolicyDecisionAuditEvent`; a policy-denied call is persisted as `Failure`/`Authorization`.
5. The Knowledge read result is re-attached to **every** later request of the attempt, after the history (audit §6.5 confirmed as behaviour).
6. In the T05 timeline the RC2 turn is present in the very request that drew the `>`, and a `stop` finish is not evidence of an answer.

---

## M. Is the baseline solid enough to start `PROMPT-ROUTING-FINALIZATION`?

**Yes, with the limits above.** All five scenarios are represented and deterministic (13 runs × 2 identical, provider bodies byte-identical);
every state transition the change will touch — premature final, bookkeeping step, paid / skipped / truncated replan, LIVE-05 continuation,
recovery turn, evidence and Knowledge reads, plan end — is observable per request and per step with its cursor; the metrics exist per phase;
the diagnostic verdict is separate from the test result and the rubrics can say `PASS` (two controls do); safety regressions are detected
(9 negative controls and a real historical defect); PA-1 still passes with unchanged goldens; PRA-R2/R3/R4 are attributed to their own layer
and PRA-P1 is measured as cost only. What remains is not a gap in the baseline but a limit of offline scripts: whether better wording makes
Qwen behave better can only be shown by a few live reruns, and the AE-1 counts are not Qwen costs.

Order of work for the change, to keep the measurement honest: change the prompt texts only (not manifests, tools or `MaxOutputTokens`), run
PA-1 and AE-1, and rewrite — in the same commit — exactly the pins listed in section K, so that the diff of the golden files and of the
`BASELINE` lines **is** the measured effect.
