# AF-TOOLROUTE-01 — tool routing and replanning robustness after BENCH B03

Branch: `fix/af-toolroute-01-tool-routing`, worktree `C:\source\repos\babbubba\bOps-af05h2`.
Code base: `12922661865796efb4cb3bc61b8679f8cab6a2e9` (AF-PLAN-01). No provider adapter, `ToolWireNames`, policy, approval,
entitlement, journal, audit schema, accounting, EvidenceRead, collector or packaging change. No live benchmark, no real LLM call,
no subagent, no push.

## B03 as recorded (archive `20261012T111826Z`, task `b4980fff…`, OpenRouter `nvidia/nemotron-3-super-120b-a12b:free`)

Read locally from the private archive (never copied into the repository): 17 model calls, 73,342 tokens, 4 plan revisions, 10 steps,
terminal `ReplanLimitReached`. The tool names below are the ones in the persisted provider request/response bodies.

| Step | Rev/cursor | Offered (wire) | Model called | Outcome |
|---|---|---|---|---|
| 0–4 | 0 / 0–4 | `system_crashes`, `system_events`, `system_memory`, `system_disk`, `system_cpu` (one per step) | the offered name | 5 read checks succeed (crashes/events: 60-minute window, 0 items) |
| 5 | 0 / exhausted | only `runtime_evidence_read` | 2 evidence reads, then `system_crashes` (reasoning: widen the window to one week, `sinceMinutes` 10080) | refused: not offered → replan |
| — | replan 1 | none | identical 5-step plan, rationale "incorrect tool name (system_crashes)" | accepted, cursor reset |
| 6 | 1 / 0 | **`system_2Ecrashes`** | `system_2Ecrashes`, 60 minutes | **re-executes** step 0 |
| 7 | 1 / 1 | `system_events` | **`system_2Eevents`** (copies the escape) | refused → replan 2 (identical plan) |
| 8 | 2 / 0 | `system_2Ecrashes` | `system.events` | refused → replan 3 (identical plan, 2 calls) |
| 9 | 3 / 0 | `system_crashes` | `system_events` | refused → replan budget exhausted |

## Verified root cause

1. **Protocol/adapter, not the model and not validation.** `ToolWireNames` builds each request's alias map from the offered tools
   plus the history's call names, skipping calls whose `ToolNameError` is set (HARDEN-1 M-1). The AF-02 context projector sends
   historical turns through `MafChatTurnMapper.ToMaf`/`FromMaf`, which dropped `ToolNameError` and `ArgumentsError`. From step 6
   the rejected raw `system_crashes` came back as a canonical call: it was sent verbatim (not as `_bops_invalid_tool`; protected
   replan turns, which skip the projector, show the placeholder correctly) and, being identical to the readable alias of
   `system.crashes`, forced the injective alias `system_2Ecrashes`. The same tool thus changed name between requests (6 and 9),
   and the model reproduced the escape for the next tool. Every refusal was correct: resolution is exact per request.
2. **Step authority prompt.** With the plan exhausted the prompt said "No native tool is authorized … do not call an operational
   tool" and, in the same paragraph, "Propose the concrete tool call for the next unfinished step above". The model proposed a
   genuinely new check (wider window) the only way it could, by calling a tool that was not offered.
3. **Replan.** The replan received only "Unknown tool: 'system_crashes' is not a tool that was offered", the compacted history
   hid which checks had run, so the model could not tell a completed plan from an unfinished one. Each
   accepted revision restarted at its first step, re-ran completed reads and exhausted `MaxReplans` (3).
4. **Immediate replan on a name slip.** A not-offered call at a planned step whose tool *was* offered triggered a full replan
   at once (steps 7–9), although the correct tool was in the request.

## Decision (ADR-0046 AF-TOOLROUTE-01 amendment)

1. **Lossless MAF boundary** (`MafChatTurnMapper`, also used by `GovernedChatClient`): rejections travel as reserved
   `AdditionalProperties` (`bops.toolNameError`, `bops.argumentsError`) and come back unchanged. No alias or fuzzy matching.
2. **In-step name correction**: as a planned step's first deviation, a call refused by name while the step's single tool is
   offered is classified `UnofferedTool` (append-only persisted enum value) and gets one correction in the same step (same single
   tool, fixed sentence). Second refusal, or a refusal after a spent ADR-0047/0050 correction → replan as before. The pure
   persisted fold (`PlannedStepPosition.ToolCorrectionSpent`) carries the budget for resume; AF-05H2 early-final continuation
   treats it as a pending correction (refuses).
3. **Completed plan**: a dedicated, non-contradictory routing instruction; the replan after a proposal from a completed plan gets
   a runtime-authored turn explaining why the call did not run.
4. **Completed checks** (`CompletedChecks`, contract-level, persisted steps only): listed in every replan prompt (bounded, sensitive
   values redacted) as information only. Nothing is refused: a read-only check reflects live state, so sampling it again, with
   identical arguments and as often as the diagnosis needs, is legitimate; an execution count or the identity of a plan is not
   evidence of a loop. A replan cycle spends the existing replan budgets (`MaxReplans`, `MaxLifetimeReplans`) and ends at
   `ReplanLimitReached`; whether stored evidence is still fresh or reusable is AF-EVID-01 scope. (An earlier revision of this
   task refused identical repeats; AF-TOOLROUTE-01-R2 removed that rule as arbitrary.)

`Completed` is never imposed: after a completed plan the model either answers (as before) or proposes a new check that goes
through a validated replan.

## Files

| File | Change |
|---|---|
| `src/core/bOps.AgentFramework/MafChatTurnMapper.cs`, `GovernedChatClient.cs` | Lossless rejection markers across the MAF boundary |
| `src/core/bOps.Abstractions/TaskState.cs` | `PlannedStepExecutionClassification.UnofferedTool` (append-only) |
| `src/core/bOps.Runtime/PlannedStepPosition.cs` | `ToolCorrectionSpent` one-use budget in the semantic fold |
| `src/core/bOps.Runtime/EarlyFinalRecovery.cs` | A spent tool-name correction is a pending correction |
| `src/core/bOps.Runtime/CompletedChecks.cs` | New: completed read checks and the replan prompt section |
| `src/core/bOps.Runtime/AgentRunner.cs` | Classification and in-step correction; plan-complete prompt; replan notice and completed-checks section |
| `tests/bOps.Runtime.Tests/ToolRoutingTests.cs` | New (11 cases) |
| `tests/bOps.AgentFramework.Tests/Regression/ToolRoutingRegressionTests.cs` | New: B03 reproduction on the real runner, broker, MAF projector and OpenAI-compatible adapter (2 cases) |
| `tests/bOps.AgentFramework.Tests/MafContextTests.cs` | Mapper/projector rejection round trip |
| 5 existing runtime test files | Scripts updated only where the in-step correction changes the expected sequence (T4 renamed) |

## Tests

New behavioural guarantees (reusable for ARCH-02, independent of the orchestration engine): one wire name per tool per task;
a rejected name never re-enters as a callable name; a not-offered call never runs and is audited; one in-step correction then
replan; resume keeps the correction budget; completed plan prompt; replan sees completed checks; a wider window, a fresh identical sample and an empty plan
are accepted; a replan cycle without progress ends at the replan budget.

Against `1292266`: the 3 AgentFramework cases fail (`["system_crashes","system_2Ecrashes"]`, `ReplanLimitReached`, rejection lost
in the mapper); with a compile-only stub, 10 of the 11 `ToolRoutingTests` fail — C2 (legitimate new check and empty plan) passes
there by design, as regression coverage.

Windows (`dotnet test`, Debug): `bOps.Runtime.Tests` **2164 passed**; `bOps.AgentFramework.Tests` **101**;
`bOps.Architecture.Tests` **175**; `bOps.Packages.Providers.OpenAiCompatible.Tests` **125**; `bOps.Packages.Providers.Anthropic.Tests`
**60**; `bOps.Policy.Tests` **308**; `bOps.Memory.Tests` **101** (one SQLite transition test failed once, green in three reruns;
untouched code); `bOps.Audit.Tests` **26**; `bOps.Api.Tests` **512**. `bOps.slnx` builds with 0 warnings.

Linux (WSL Ubuntu-24.04, .NET SDK 10.0.401, Release, `git archive` of the staged tree): `bOps.Runtime.Tests` **2164 passed**,
`bOps.AgentFramework.Tests` **101 passed**, `bOps.Architecture.Tests` **173 passed, 2 skipped** (platform-conditional),
`bOps.Packages.Providers.OpenAiCompatible.Tests` **125 passed**.

## MAF compatibility

- **Definitive, reusable:** the lossless mapper (it *is* the MAF adapter contract: whatever orchestrator ARCH-02 chooses must not
  turn a rejected name into a callable one); `CompletedChecks` as a pure contract over persisted steps and plans; the
  `UnofferedTool` classification and fold (step authority stays in bOps per D-048); the behavioural tests above.
- **Temporary, to re-evaluate in ARCH-02:** the wording of the plan-complete instruction and the replan notice; the in-step
  correction living inside `AgentRunner.ContinueAsync`.
- **Dependencies that could complicate convergence:** history must remain projected by a lossless mapper — any MAF-native history
  or compaction must preserve call-level rejection metadata; step-scoped visibility still
  depends on the runner's cursor.

## Residual risks

- Synthetic tests prove the decision, not the live outcome; the gate is the operator's 1×3.
- A model that keeps replaying a completed plan is not refused; it costs one replan per cycle and ends at the replan budget
  (B03 shape: three extra passes of the same reads). Detecting a cycle without verifiable progress, and evidence freshness or
  reuse, are left to AF-EVID-01.
- The in-step correction adds at most one call per planned step.
- A wire alias still depends on the request's name set; with rejected names excluded, a collision now requires two registered
  canonical names differing only by `.`/`_`, which the catalog does not contain.

## Operator command: BENCH 1×3

First with `-PreflightOnly`, then without it, with the same private snapshot as archive `20261012T111826Z` and the full hash of the
AF-TOOLROUTE-01 commit:

```powershell
& 'C:\source\repos\babbubba\bOps\scripts\windows\run-live-benchmark.ps1' -TargetRepoRoot 'C:\source\repos\babbubba\bOps-af05h2' -ConfigSourceRoot 'C:\source\repos\babbubba\bOps' -ConfigSnapshotPath 'C:\Users\fcavallari\AppData\Local\Temp\bOps-BENCH00-config-20261012T103656Z-87973291' -ExpectedTargetCommit '<AF-TOOLROUTE-01 commit>' -RunsPerScenario 1
```
