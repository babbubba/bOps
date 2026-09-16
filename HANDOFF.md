# Handoff — V1.1 started: Evidence/Finding/ExecutionPlan contracts (ADR-0023), phase 1 of the milestone

V1.0 is complete, committed, and pushed to `origin/main` (verified: CI green on both
`ubuntu-latest` and `windows-latest` after the push). This session began V1.1
(`piano-bops-v0.9.1-v2.0.md` §7 — "Skill/Capability SDK, Evidence e piano immutabile") at the
operator's explicit go-ahead, after pulling forward 95 commits done by a prior agent run
(V0.9.1 → V0.10 → V1.0) that this session had not seen locally until it fetched them.

**This session delivers only the first slice of V1.1 — the immutable data contracts and their
tests — not the whole milestone.** V1.1's own Definition of Done (a working sample Skill,
end-to-end) is explicitly **not** claimed here. See "What remains," below, before starting the
next increment.

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

## Verified for real, this session

- `dotnet build bOps.slnx`: **0 warnings, 0 errors**, full solution (one transient NuGet/CLR
  restore crash on the first attempt, unrelated to any code here — a clean retry built fine).
- `dotnet test bOps.slnx --filter "Category!=LiveModel"`: **every assembly green** except one
  **pre-existing, unrelated** failure —
  `WindowsProcessActionToolsTests.ProcessStop_Succeeds_ForAProcessWithAMainWindow`
  (`bOps.Packages.System.Windows.Tests`, "No process with id N is running") — a real-process
  timing flake in a package this session never touched; reproduced twice in isolation, unrelated
  to ADR-0023. Not fixed here; flagged, not silently ignored.
- `bOps.Runtime.Tests`: 100/100 green, including the 20 new tests above.
- `git status` confirmed clean staging before commit: no stray build artifact
  (`tasks.db`/`audit.jsonl`), and `src/core/bOps.Cli/appsettings.json` untouched — the standing
  security constraint carried since V0.7.

## What remains — real gaps, not silently dropped (see ADR-0023, "Deferred to a follow-up ADR")

In the order a follow-up session should tackle them:

1. **`ICapability`/`ISkillProvider` execution interfaces.** Not designed yet — deliberately, per
   ADR-0023: committing to an execution contract before a real runner has exercised it risks
   getting it wrong on a surface frozen at `1.0.0` (ADR-0022). This needs its own ADR once a
   concrete runner design exists, not a retrofit onto this one (an accepted ADR is never edited
   to change its meaning, agentic/05-workflow.md).
2. **Runtime orchestration.** Nothing in `AgentRunner` changes yet. Walking an `ExecutionPlan`'s
   steps through the *existing* policy/approval/verification/audit pipeline — each
   `ExecutionPlanStep` executing exactly like any other tool call, never through a shortcut — is
   the next real piece of work, and the one most likely to surface a design gap in what this
   session built (exactly like V0.9's UI session found two real `bOps.Api` bugs no earlier test
   caught).
3. **`bOps.Policy` rule evaluation** over `SkillId`/`CapabilityName`/`Target`/`Environment`/
   `BlastRadius` — real policy-semantics design (a YAML schema change), not implemented.
4. **A sample Skill, end-to-end.** V1.1's actual Definition of Done. Blocked on 1–3 above.

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
