# Handoff — V1.1 in progress: contracts (ADR-0023) + ExecutionPlan orchestration (ADR-0024)

V1.0 is complete, committed, and pushed to `origin/main` (verified: CI green on both
`ubuntu-latest` and `windows-latest` after the push). This session began V1.1
(the Skill/Capability SDK, Evidence and immutable execution-plan tranche) at the
operator's explicit go-ahead, after pulling forward 95 commits done by a prior agent run
(V0.9.1 → V0.10 → V1.0) that this session had not seen locally until it fetched them. It then
continued through a second increment in the same session — real `ExecutionPlan` orchestration —
after the operator said to push the first increment and keep going.

Current tracked scope is recorded in
`agentic/_plans/2026-09-16-consolidated-roadmap.md`; the immediate continuation is
`agentic/_tasks/2026-09-16-v1.1-a-skill-sdk-completion.md`. `agentic/00-bootstrap.md` is the
stable routing entry point for future sessions. Earlier plan/task snapshots are archived under
`agentic/obsolete/` and must not be resumed.

**This session delivers two of V1.1's pieces — the immutable data contracts, and the runtime
orchestration that executes them — not the whole milestone.** V1.1's own Definition of Done (a
working sample Skill, end-to-end) is explicitly **not** claimed here; it is still blocked on
`ICapability`/`ISkillProvider`, which this session deliberately did not build. See "What
remains," below, before starting the next increment.

## What this session delivers

**ADR-0023** (`docs/architecture/adr/0023-skill-capability-evidence-execution-plan.md`) —
required before any code, per the plan's own ADR table. Settles:

- **Vocabulary**: Package / Tool / Capability / Skill / Agent, and how each relates to the
  existing V0.1–V1.0 contract. A Capability is not a bigger Tool; it is realized by an
  `ExecutionPlan` of ordinary, already-governed Tool calls. A Skill's capability-selection logic
  is deterministic package code, **not a second LLM call** — this is the decision that keeps
  principle 1 ("the LLM never touches the machine") intact without inventing a parallel version
  of it.
- **Evidence/Finding** (`bOps.Abstractions/Evidence.cs`): `EvidenceKind` (Fact/Inference/
  Recommendation/ExecutedAction/Verification), `Evidence`, `Finding` (constructor throws on empty
  `EvidenceIds` — a claim with no cited evidence is structurally impossible, not just
  discouraged), `SkillReport` (assembled only from recorded Evidence/Findings, no free-text field
  — the structural half of "never let the model invent proof").
- **`ExecutionPlan`** (`bOps.Abstractions/ExecutionPlan.cs`): immutable, typed, versioned;
  `ExecutionPlanHasher.ComputeHash` over a **canonicalized** JSON form (`CanonicalJson.Sort`
  recursively sorts every object's keys before hashing) so the hash never depends on property
  declaration order or on how a `ToolArguments` object happened to be built — proven by a test
  that builds the same arguments with keys inserted in two different orders and asserts equal
  hashes. `ExecutionPlanApproval` binds a decision to a specific hash; a plan that no longer
  matches its approval's hash is, by construction, unapproved — no separate invalidation logic
  needed.
- **`CapabilityManifest`** (`bOps.Abstractions/Capabilities.cs`): the declarative shape only
  (identity, version, risk, required permissions, typed input/output reusing `ToolParameter`,
  timeout, dry-run support, verification, rollback description). **No `ICapability`/
  `ISkillProvider` execution interface yet** — deliberately deferred; see below.
- **`PolicyContext` extension** (`bOps.Abstractions/Policy.cs`): five new optional `init`-only
  properties (`SkillId`, `CapabilityName`, `Target`, `Environment`, `BlastRadius` — a new
  `enum { Single, Multiple, Fleet }`, a magnitude never a literal count, rule A1). The existing
  positional constructor and its one call site (`AgentRunner.ExecuteStepAsync`) are untouched;
  every existing tool-call path leaves these fields `null`. `bOps.Policy`'s actual rule
  evaluation over these fields is **not implemented** — the shape exists, the semantics do not
  yet.

**Tests** (TDD, per `agentic/04-testing-rules.md` — core component, no exceptions):
- `tests/bOps.Runtime.Tests/EvidenceTests.cs` — `Finding` rejects empty evidence, accepts one.
- `tests/bOps.Runtime.Tests/ExecutionPlanTests.cs` — rejects empty/non-contiguous/duplicate step
  indices; hash is deterministic for identical content; hash differs when an argument or the
  rationale changes; **hash is insensitive to JSON property insertion order** (the canonicalization
  proof); `ExecutionPlanApproval` binds a hash to a decision.
- `tests/bOps.Runtime.Tests/JsonRoundTripTests.cs` — one round-trip test per new record
  (`Evidence`, `Finding`, `SkillReport`, `ExecutionPlanStep`, `ExecutionPlan`,
  `ExecutionPlanApproval`, `CapabilityManifest`) plus two for `PolicyContext` (fields absent,
  fields present), matching rule A2 and this file's existing hand-listed pattern.
- One new suppression recorded in `docs/architecture/suppressions.md` (`CA1720` on
  `BlastRadius.Single`, same justification already on record for `ToolParameterType`).

### Second increment: `ExecutionPlan` orchestration (ADR-0024)

**ADR-0024** (`docs/architecture/adr/0024-execution-plan-orchestration.md`) — required before
this code, since it changes `AgentRunner`'s public surface. Decision, in short: reuse the
existing private `ExecuteStepAsync` for every `ExecutionPlanStep` — the one place a tool call is
authorized and audited (rule A5) — rather than writing a second implementation of
policy/approval/verification/audit in a new "Skill runner" component.

- **`AgentRunner.ExecuteExecutionPlanAsync(taskId, actor, plan, approval, ct)`** (new public
  method, `src/core/bOps.Runtime/AgentRunner.cs`): checks `approval`'s hash against
  `ExecutionPlanHasher.ComputeHash(plan)` before anything runs (a mismatch produces a single
  refusal `Evidence` entry and executes nothing); then walks `plan.Steps` in `Index` order,
  converting each to a `ModelToolCall` and running it through the *unmodified*
  `ExecuteStepAsync` — same `IPolicyEngine.Evaluate`, same possible `IApprovalProvider` call, same
  verification, same audit events a model-proposed tool call would produce. **Capability-level
  approval does not replace per-step policy** — a step still needing its own approval still gets
  one; an `ExecutionPlanApproval` only gates whether the plan runs at all, not each of its steps
  (ADR-0024's central decision: collapsing the two would have let one operator glance at a plan's
  rationale silently pre-authorize a `Critical`-adjacent step inside it).
- Each step's outcome becomes one `EvidenceKind.ExecutedAction` entry (plus a
  `EvidenceKind.Verification` one when the step was verified). A denied/rejected/unknown-tool
  step stops the plan — later steps are never attempted, mirroring `ContinueAsync`'s own
  `deviated` check.
- `SkillReport.Findings` is always empty from this method — turning Evidence into Findings is
  domain interpretation only a Skill's own logic can do, and no Skill exists yet (next item).
- **No `ITaskStore` persistence for a plan run** — a deliberate, recorded gap (ADR-0024): an
  `ExecutionPlan` is bounded and pre-computed, unlike the model-driven loop `RunAsync` persists
  after every step; what "resuming" a partially-run plan would even mean is its own design
  question, not attempted here.
- **Tests**: `tests/bOps.Runtime.Tests/ExecutionPlanOrchestrationTests.cs` — executes every step
  and records evidence; records verification evidence for a non-`Read` step; writes the same
  `ToolCallAuditEvent`s an ordinary tool call would; stops at the first denied step without
  running later ones; refuses when the approval's hash does not match the plan; runs when it does.
  `AgentRunner`'s existing 76 tests (`RunAsync`/`ResumeAsync`/`ContinueAsync`) are untouched and
  still pass — this method adds, it does not modify.

## Verified for real, this session

- `dotnet build bOps.slnx`: **0 warnings, 0 errors**, full solution, after both increments (one
  transient NuGet/CLR restore crash on the very first attempt, unrelated to any code here — a
  clean retry built fine).
- `dotnet test bOps.slnx --filter "Category!=LiveModel"`: **every assembly green** except one
  **pre-existing, unrelated** failure —
  `WindowsProcessActionToolsTests.ProcessStop_Succeeds_ForAProcessWithAMainWindow`
  (`bOps.Packages.System.Windows.Tests`, "No process with id N is running") — a real-process
  timing flake in a package this session never touched; reproduced twice in isolation. Not fixed
  here; flagged, not silently ignored.
- `bOps.Runtime.Tests`: **106/106 green** (100 after increment one, 106 after increment two).
- `git status` confirmed clean staging before each of the two commits: no stray build artifact
  (`tasks.db`/`audit.jsonl`), and `src/core/bOps.Cli/appsettings.json` untouched — the standing
  security constraint carried since V0.7.
- Current merged HEAD `066b932` is present on `origin/main`; CI run `35123208276` completed
  successfully on the Windows/Linux matrix, including the Angular build and headless tests.

## What remains — real gaps, not silently dropped (see ADR-0023 and ADR-0024's "Deferred"/"Still deferred" sections)

In the order a follow-up session should tackle them:

1. **`ICapability`/`ISkillProvider` execution interfaces, plus `IToolInvoker`** (rule A10's
   already-named, still-never-implemented host service: read-only tools of the same package
   only). A Skill needs `IToolInvoker` to gather diagnostic evidence *before* it can decide what
   `ExecutionPlan` to build — this is real new infrastructure, not a small addition, and is what
   everything below is blocked on. Needs its own ADR once designed (an accepted ADR is never
   edited to change its meaning, agentic/05-workflow.md) — do not retrofit ADR-0023 or ADR-0024.
2. **Consolidating capability-level and per-step approval**, if an operator being asked twice for
   the same effective risk turns out to matter in practice — deliberately not attempted in
   ADR-0024, to avoid weakening rule A5 without a validated design for doing so safely.
3. **Persistence/resumability of a Skill run** — deliberately out of ADR-0024's scope; see its
   "Decision" section for why this is harder than it looks (steps 1–3 already having side effects
   when step 4 needs to resume).
4. **`bOps.Policy` rule evaluation** over `SkillId`/`CapabilityName`/`Target`/`Environment`/
   `BlastRadius` — real policy-semantics design (a YAML schema change), not implemented; the
   `PolicyContext` fields exist, `PolicyEngine` does not yet read them.
5. **A sample Skill, end-to-end.** V1.1's actual Definition of Done. Blocked on 1 above.

**Do not start V1.2** (multi-agent) before V1.1's own Definition of Done is met — per the plan's
own checklist ("stop at the first unmet gate"), and per this project's standing scope-discipline
rule.

## Carried forward, unrelated to this session

- The release pipeline (`release.yml`) still has not had its first real execution — still an open
  decision for the operator (tag `v1.0.0-rc.1` or `workflow_dispatch`), not something to do
  automatically.
- Everything V1.0's own handoff already listed as a deliberate scope boundary (in-process plugins
  remain trusted code; no Windows file-permission hardening equivalent to Unix's `0600`; no V1.1
  Skill/Capability contract *was* introduced there — it now partially is, here) stays as recorded
  in ADR-0022 and the V1.0 threat model.
