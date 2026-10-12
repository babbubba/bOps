# AF-PLAN-01 — planning robustness after BENCH-01 R2

Branch: `fix/af-plan-01-planning-robustness`, worktree `C:\source\repos\babbubba\bOps-af05h2`.
Code base: `2dbb7b306cafc926503f6022c76b8d16abde293f` (AF-05H2-R2). No provider, model, prompt-catalog, policy, BENCH collector
or packaging change. No live benchmark and no real LLM call were made.

## Verified root cause

BENCH-01 R2 (candidate `2dbb7b3`): B01 Completed, B02 Failed, B03 Completed. The B02 task state, read locally from the private
archive and never copied, has no plan steps, zero tool steps and one synthetic failure step:

| Phase | Calls | Outcome |
|---|---|---|
| Initial plan | 2 (`stop`) | Both `Failure/MalformedResponse`, identical recorded problem: `Plan step 3 has invalid expectedArguments: expectedArguments parameter 'limit' is invalid: Argument 'limit' = 1000 exceeds maximum 500.` |
| Plan-less step 0 | 2 (`length`, 4,096 completion tokens each) | Empty text, no tool call; request offered **0 tools** |
| Terminal | — | `EmptyResponse`, 21,129 tokens |

1. **Why the plans were rejected.** Not the Markdown fence: `TryParsePlan` already sliced from the first `{` to the last `}`, and
   the problem is a validation error, not a parse error. The `system.events` step constrained `limit: 1000`; the manifest declares
   `Maximum = 500` (`SystemEventsLimits.MaximumEvents`) and `SemanticExpectedArguments.Validate` correctly refused it.
2. **Why the correction failed.** The re-ask (`PlanRetryInstructions`) was generic. The runtime knew the exact problem but did
   not send it, so the model reproduced the same plan with the same `limit`.
3. **Why the fallback could not work.** After two malformed replies `CreatePlanAsync` returned an empty plan "proceeding step by
   step". Under ADR-0046 step-scoped routing an empty plan has no cursor step, so `StepToolViewFor` offers no native tool and the
   prompt says "No native tool is authorized for this turn". The model reasoned about that contradiction until the 4,096-token
   output limit, twice (the empty-reply re-ask was the generic "Your last reply was empty").

## Decision

Recorded as an ADR-0046 amendment (AF-PLAN-01); it supersedes the empty-plan degradation of ADR-0014 and ADR-0039 §8 for the
initial plan only.

- **Exact correction.** A rejected initial plan is re-asked with `The runtime rejected that plan: <problem>` (bounded to 512
  characters) followed by the standing format instruction. A reply cut at `length` with no text gets an output-limit notice and no
  empty assistant turn. The replan re-ask carries its problem the same way; its count and the AF-05H2 early-final rules are
  unchanged.
- **Bound and anti-loop.** At most `MaxPlanCorrections = 2` corrections follow the first reply; a correction whose reply leaves the
  identical problem stops further corrections. Worst case 3 planning calls (previously 2); typical repeated failure stays at 2.
  Every call is recorded and audited by the broker as before, the token budget is checked after each call.
- **Explicit failure.** No valid plan → `ModelProtocolException` (`MalformedResponse`) through the existing planning-failure path:
  status `Failed`, `TerminalReason = ModelFailure/MalformedResponse`, synthetic step `Planning failed: the model did not produce a
  valid plan after N attempt(s). Last problem: …` carrying the planning calls, tokens from the recorded calls. No plan is persisted,
  so a resume plans again. An accepted empty plan (`"steps": []`) is unchanged.
- **Fence framing.** `PlanJsonCandidate` takes the first markdown fence whose body is a JSON object, otherwise the outermost braces,
  so prose with braces around a fence no longer corrupts the slice. All validation is unchanged.
- **Step loop.** The single empty-reply re-ask tells an output-limited model to call the offered tool or answer without further
  deliberation. Bound and `EmptyResponse` terminal unchanged.

Policy, step authority, semantic membership, approval, mutation journal, audit schema, `bOps.Abstractions` and the persisted state
shape are untouched. AF-05H2-R2 early-final recovery is untouched.

## Files

| File | Change |
|---|---|
| `src/core/bOps.Runtime/AgentRunner.cs` | Exact, bounded plan correction; explicit planning failure; fence framing; output-limit re-asks |
| `tests/bOps.Runtime.Tests/PlanningCorrectionTests.cs` | New: B02 reproductions and the coverage below |
| `tests/bOps.Runtime.Tests/AgentRunnerTests.cs` | Old degrade-to-empty-plan test now asserts the explicit failure |
| `tests/bOps.Runtime.Tests/ModelCallFailureContainmentTests.cs` | Duplicate-key plan test now asserts the explicit failure |
| `docs/architecture/adr/0046-…-replanning.md` | AF-PLAN-01 amendment |

## Tests

`PlanningCorrectionTests` (16 cases): B02 fenced `limit: 1000` corrected with the exact problem, only the corrected plan runs; the
same invalid plan twice fails explicitly with no tool, no plan, no plan-less step, two audited malformed calls and exact tokens;
fenced plan among prose with braces; `PlanJsonCandidate` table; output-limited empty plan reply → output-limit notice, no empty
assistant turn; reasoning truncated twice → fails after two calls, no third identical request; three different problems → at most
two corrections; token budget stops a correction; resume after a planning failure plans again with lifetime accounting; output-limited
empty step reply; malformed replan re-asked with the exact problem. Against `2dbb7b3` (with a compile-only stub) 14 of 16 fail; the
token-budget case and the no-object table row pass there too, as regression coverage.

Windows (`dotnet test`, Debug): `bOps.Runtime.Tests` **2153 passed**; `bOps.Architecture.Tests` **175**; `bOps.AgentFramework.Tests`
**98**; `bOps.Memory.Tests` **101**; `bOps.Audit.Tests` **26**; `bOps.Policy.Tests` **308**; `bOps.Api.Tests` **512**. `bOps.slnx`
builds with 0 warnings under `TreatWarningsAsErrors`.

Linux (WSL Ubuntu-24.04, .NET SDK 10.0.401, Release, `git archive` of the staged tree): `bOps.Runtime.Tests` **2153 passed**,
`bOps.Architecture.Tests` **173 passed, 2 skipped** (platform-conditional), `bOps.AgentFramework.Tests` **98 passed**.

## Residual risks

- Synthetic tests prove the decision, not the live outcome; the gate is BENCH-01 1×3 by the operator.
- A model that ignores an exact problem still fails, now after 2 calls and with a precise persisted reason instead of a plan-less
  `EmptyResponse`. A planning failure is terminal for the attempt; a resume re-plans.
- The worst case adds one planning call (three different problems in a row).
- The output-limit notice cannot raise the provider's output limit; a model whose reasoning alone exceeds 4,096 tokens on every
  planning call fails after two calls.
- Fence framing picks the first fenced JSON object; a reply with a fenced example before the real plan would be validated on the
  example and rejected, then corrected.

## Operator command: BENCH-01 1×3

First with `-PreflightOnly`, then without it, using the same private snapshot directory as BENCH-01 R2 and the full hash of the
AF-PLAN-01 commit:

```powershell
& 'C:\source\repos\babbubba\bOps\scripts\windows\run-live-benchmark.ps1' -TargetRepoRoot 'C:\source\repos\babbubba\bOps-af05h2' -ConfigSourceRoot 'C:\source\repos\babbubba\bOps' -ConfigSnapshotPath 'C:\Users\fcavallari\AppData\Local\Temp\bOps-BENCH00-config-20261011T224200Z-cff56b9e' -ExpectedTargetCommit '<AF-PLAN-01 commit>' -RunsPerScenario 1
```
