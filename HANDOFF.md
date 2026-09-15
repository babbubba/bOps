# Handoff — V0.2 (explicit agent loop with replanning) complete

Written at the end of the session that implemented V0.2 on top of the completed V0.1 scaffolding.
Everything below is exact, not a summary — follow it literally to resume.

## State right now

**`dotnet build bOps.sln` builds clean end to end — 0 warnings, 0 errors** (verified with a full
clean of every `bin`/`obj` and a from-scratch rebuild). `dotnet test bOps.sln`: **59 passing, 6
skipped** (the Linux package tests, correctly and visibly skipped — no Linux host exists in this
dev environment; see the previous handoff's note, still accurate). Nothing failing.

CLI smoke test (`cd src/core/bOps.Cli && dotnet run -- "how is this machine doing?"`): DI wiring,
config binding, tool registry and provider registry all succeed; the task now opens with a
planning call, which fails cleanly at the HTTP call with `ApiKey` empty (rule S6 — never put a
real key in `appsettings.json`), audited as a `ModelCallAuditEvent` with `Outcome: Failure` and
`StepIndex: -1`, and the task ends as `Failed` with a readable error — no crash, no change from
V0.1's behavior here except that the failure now happens during planning instead of step 0.

## What this session did

Implemented V0.2 per `agentic/00-project-spec.md`'s roadmap: **"Explicit agent loop with
replanning."** Full design rationale is in
[`docs/architecture/adr/0014-explicit-planning-and-replanning.md`](docs/architecture/adr/0014-explicit-planning-and-replanning.md)
— read that before touching this area again. Summary:

- **New contract types** (`bOps.Abstractions/Planning.cs`): `PlannedStep` (a stated intention —
  description + optional expected tool, no arguments) and `AgentPlan` (a revision number, a
  rationale, and a list of `PlannedStep`s).
- **`TaskState` gains `Plans`** (every plan revision produced, distinct from `Steps`, which is
  tool-call iterations, not planning). **`PlanStep` gains `PlanRevision`** (nullable, defaults to
  `null`, but no code path leaves it null in practice).
- **`AgentTaskStatus` gains `ReplanLimitReached`** — distinct from `PolicyBlocked` (stuck on the
  same tool) and `MaxStepsReached` (no terminal state reached at all).
- **`AgentRunnerOptions` gains `MaxReplans`** (default 3).
- **`AgentRunner.RunAsync`** now opens every task with `CreatePlanAsync` — a dedicated,
  non-tool-calling model call producing `AgentPlan` revision 0, with one bounded retry on a
  malformed reply (same pattern as the existing JSON-schema tool-call fallback). Every per-step
  call includes the current plan's remaining steps in its system prompt. After a step executes,
  `RunAsync` calls `ReplanAsync` — one more model call producing the next plan revision — when
  the step's authorization was `PolicyDenied`/`UnknownTool`, its outcome was `Timeout`, or the
  model proposed a tool call after every step the current (non-empty) plan named had already
  been attempted. **A plain tool `Failure` does not trigger a replan** — see the ADR for why.
  `CallModelAsync` was refactored to take a prebuilt `ModelRequest` so planning, replanning and
  step calls all share the one audited call path (rule S9).
- **Telemetry**: `BOpsTelemetry.ReplansTotal` (a counter) and `bops.plan_revision` span tags on
  both the task and step activities.
- **Tests**: every existing `AgentRunnerTests` scenario was updated for the new leading planning
  call (and, where the scenario now triggers a replan, one more canned response for it) — this
  was expected, real churn from changing the loop's shape, not test fragility. Six new tests
  cover the V0.2 behavior itself: the initial plan is recorded, a malformed plan degrades
  gracefully after one retry, a retry that succeeds is recovered, replanning triggers on plan
  exhaustion, replanning triggers on an unknown tool, and `ReplanLimitReached` is reached when
  replanning keeps being needed. Two new JSON round-trip tests cover `PlannedStep`/`AgentPlan`
  (rule A2), and `TaskState`/`PlanStep`'s existing round-trip tests were extended to cover
  `Plans`/`PlanRevision`. Total: 44 → 51 tests in `bOps.Runtime.Tests`.
- **`agentic/01-architecture-rules.md`** gained §B9 (the `PlannedStep`/`AgentPlan`/`TaskState`
  contract shapes) and rule C8 (the replan-trigger conditions and the bound), matching the ADR.

None of this was worked around or deferred — every fix and every new behavior is real, tested,
in the diff.

## Design choices worth knowing before extending this further

- **Planning and replanning stayed inside `AgentRunner`, not a new `AgentPlanner` class or
  project.** The roadmap asked for planning to become an *explicit phase*, not for planning and
  execution to become physically separate components — that would be scope creep per
  `agentic/05-workflow.md`'s scope-discipline rule. `CreatePlanAsync`/`ReplanAsync` are private
  methods, as explicit and independently testable as the rest of the loop.
  `agentic/01-architecture-rules.md` §C's note ("`AgentRunner`, not `AgentPlanner`... planning
  and execution are not yet separated") is still accurate and was left as-is, not corrected —
  that split is not what happened here.
- **The model contract (`IChatModel`, `ModelRequest`, `ModelResponse`) is completely
  untouched.** Planning and replanning are just more calls to the existing
  `CompleteAsync(ModelRequest, ...)`, with a different prompt and a different way of interpreting
  the response. No provider package (`OpenAiCompatibleChatModel` included) needed to change.
  This was a deliberate choice over adding a `Plan` field to `ModelResponse` — see the ADR's
  "Alternatives considered" for the reasoning, which also explains why this could not have been
  meaningfully end-to-end tested against a real provider in this environment anyway (no real API
  key is or should be configured here, per rule S6 — `FakeChatModel` is the only thing that
  exercises this code today).
- **`ModelCallAuditEvent` does not say *why* a model was called** (plan vs. replan vs. step) —
  only `StepIndex` hints at it (`-1` for the initial plan, the triggering step's index for a
  replan). This is a real, known gap, deliberately deferred rather than fixed as a side effect
  here (see the ADR's last "alternative considered"). If it becomes a real operational pain,
  fixing it needs an ADR (it touches `bOps.Abstractions`).

## What V0.2 deliberately does NOT have yet (unchanged from V0.1, still correct)

- No `bOps.Policy` project — V0.3. `AgentRunner` still fails closed on any non-`Read` tool.
- No `bOps.Memory` project / SQLite — V0.7. Plans and steps are still in-memory only, one
  `AgentRunner.RunAsync` call at a time.
- No dynamic plugin loading — V0.10.
- No `fs.*` (Filesystem) package — V0.5.
- No post-action verification service — V0.4. `IVerifiableTool` exists and is enforced at
  registration, but nothing calls `EvaluateVerificationAsync` yet.

## Next steps

V0.2 is genuinely done: the loop has an explicit, inspectable, bounded PLAN/REPLAN phase: full
solution build clean, 59 tests passing, the CLI smoke-tested end to end, and the architecture
docs updated to match the code (ADR-0014, `agentic/01-architecture-rules.md` §B9/§C8). **V0.3 —
"Policy engine and approval flow; analyzers escalate to `all`" — has not been started.** Per the
scope-discipline rule, the next session should begin by reading `agentic/00-project-spec.md`'s
roadmap entry for V0.3, `agentic/03-security-rules.md` (the policy-related rules, especially S3
and the `Critical`-is-always-forbidden invariant), and `agentic/06-decisions.md` D-011 (the
`AnalysisLevel` escalation this version also calls for), before writing any code. Two things to
watch for going in:

1. `AgentRunner.ExecuteStepAsync`'s current "no policy engine exists yet, refuse everything above
   `Read`" block (rule S3) is the exact seam where `IPolicyEngine.Evaluate(...)` needs to be
   wired in — replacing the hardcoded refusal, not loosening it.
2. The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
   0002, 0005, 0006, 0011, 0012) are still unwritten — flagged again from the last handoff,
   still a real but non-urgent documentation gap, still judged out of scope for a session that
   is implementing code, not backfilling retroactive ADRs. Worth doing opportunistically if a
   V0.3 session has spare budget, since 0002 ("Five risk levels, and `Forbidden` as an
   unbypassable invariant") is directly relevant to the policy engine it's about to build.
