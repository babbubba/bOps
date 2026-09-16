# ADR-0023 — Skill/Capability vocabulary, and the Evidence/Finding/ExecutionPlan contracts

Status: Accepted

Written at the start of V1.1 under the now-consolidated roadmap, as required by the earlier plan's
ADR table ("Skill/capability/evidence/execution plan immutabile", minimum version v1.1) and by
`agentic/05-workflow.md`'s trigger list: this alters `bOps.Abstractions` and extends the risk/
policy model.

## Context

Every version through V1.0 authorizes and audits one thing: a single `ITool` call, selected by
the model one step at a time (`ModelToolCall` → policy → approval → execution → verification).
That is deliberately minimal (principle 1) and it is not going away — V1.1 does not replace it.

What V1.1 needs is the layer *above* it that a real operational Skill (a PostgreSQL DBA toolkit,
say) requires and today has nowhere to live:

- A way to say "this operation needs these preconditions, this input shape, this rollback story"
  **before** any tool call happens — richer than one `ToolManifest`, because a single operator-
  facing operation (`postgres.diagnose_bloat`) is routinely several tool calls, not one.
- A way to say "I observed X, and X implies Y" **and have that distinction survive**, instead of
  collapsing into one undifferentiated blob of text the model could later misquote as fact.
- A way to authorize a **whole sequence** of typed tool calls as one unit, with a guarantee that
  what was approved is exactly what runs — not "close enough to what was approved."

Building this wrong is expensive to undo (`bOps.Abstractions` is frozen at `1.0.0` since
ADR-0022 — see D-012/A12), so this ADR is deliberately narrower than the plan's full v1.1 scope.
It settles the **vocabulary** and the **immutable data contracts**. It does not yet settle how a
Skill executes — see "Deferred to a follow-up ADR" below. Committing to an execution interface
before a real runner has exercised it risks getting the shape wrong and being unable to fix it
without a breaking `2.0.0`.

## Decision

### Vocabulary — five terms, not four

| Term | What it is | Exists since |
|---|---|---|
| **Package** | A deployment/loading unit (`PackageId`), assigned by the host, never self-claimed (A11). | V0.1 |
| **Tool** | One atomic, policy-gated, audited runtime action (`ITool`/`ToolManifest`). The runtime's only unit of *execution*. | V0.1 |
| **Capability** | A named, versioned, operator-facing *operation* a Skill declares — richer than one `ToolManifest`, and realized by an `ExecutionPlan` of one or more typed `Tool` calls, never by free commands. | V1.1 (this ADR) |
| **Skill** | A package (A7) that contributes Capabilities, plus the deterministic domain logic to select one, gather Evidence, and produce an `ExecutionPlan`. Domain logic is ordinary package code, **not a second LLM call** — see "Skill logic is deterministic," below. | V1.1 (this ADR) |
| **Agent** | An orchestration role that can delegate to other agents/Skills with a *reduced* privilege context. Named here only to draw the boundary; not built until V1.2 (consolidated roadmap). | Not yet |

A Capability is not a bigger Tool and not a replacement for one. `ITool`/`ToolManifest` stay
exactly what they are — the thing the runtime actually calls, one at a time, through the existing
policy/approval/verification/audit path (`AgentRunner.ExecuteStepAsync`, unchanged by this ADR).
A Capability is the *reason* a sequence of those calls happens, and the unit an operator reasons
about and approves.

### Skill logic is deterministic, not a second model

A Skill's capability-selection and evidence-gathering logic is package code the Skill's author
wrote and tested — the same trust model as a Tool's own implementation, not a new one. This
keeps principle 1 intact without inventing a second flavor of it: *the* model still never touches
the machine; a Skill choosing which Capability applies is no different in kind from a Tool
provider deciding how to format `system.cpu`'s output. Nothing here stops a *planner* (the
existing `AgentRunner` loop, or a future Agent at V1.2) from choosing to invoke a Skill's
Capability the way it chooses a Tool today — that wiring is exactly the deferred work below.

### Evidence and Finding (`bOps.Abstractions/Evidence.cs`)

```csharp
public enum EvidenceKind { Fact, Inference, Recommendation, ExecutedAction, Verification }

public sealed record Evidence(
    string Id, EvidenceKind Kind, string Description, string? Data,
    string SourceTool, DateTimeOffset ObservedAtUtc);

public sealed record Finding
{
    public Finding(string Id, string Summary, IReadOnlyList<string> EvidenceIds, RiskLevel? Severity = null)
    {
        if (EvidenceIds.Count == 0)
            throw new ArgumentException("A finding must cite at least one piece of evidence.", nameof(EvidenceIds));
        ...
    }
}
```

`EvidenceKind` is the plan's own classification (FACT/INFERENCE/RECOMMENDATION/EXECUTED_ACTION/
VERIFICATION) — an `Inference` or `Recommendation` is explicitly *not* the same kind of claim as
a `Fact` observed straight from a tool's output, and collapsing them was exactly the failure mode
this exists to prevent. `Finding.EvidenceIds` cannot be empty — **enforced by the constructor
throwing**, not by a registry-time check like `IToolRegistry.Register` (rule D-006's pattern):
`Finding` has no registration step, so the invariant belongs on the type itself, checked at the
only point it can ever be violated — construction. A Finding that cites evidence the caller never
actually recorded is not preventable by this type alone (that requires the store that will hold
both, deferred below); this ADR guarantees only "not zero," which is the check that is real
*today*.

`Evidence.Data` goes through the same `Sensitive`-parameter redaction path `ToolArguments.Redact`
already provides — no new redaction mechanism (rule S6). This ADR does not introduce a new one;
whatever assembles `Evidence` from a tool's output is responsible for redacting before
construction, exactly as `AgentRunner.RecordAsync` already redacts before writing an audit event.

### `ExecutionPlan` — immutable, versioned, hashed (`bOps.Abstractions/ExecutionPlan.cs`)

Distinct from the existing `AgentPlan`/`PlannedStep` (Planning.cs), which stays exactly what it
is: the model's own stated, natural-language intention, revised freely across replans (rule C8).
`ExecutionPlan` is the *authorizable* artifact — concrete, typed, and immutable once built:

```csharp
public sealed record ExecutionPlanStep(int Index, string ToolName, ToolArguments Arguments, string? Description);

public sealed record ExecutionPlan
{
    public ExecutionPlan(string CapabilityName, string CapabilityVersion, string Rationale, IReadOnlyList<ExecutionPlanStep> Steps)
    {
        if (Steps.Count == 0) throw new ArgumentException(...);
        // Steps.Index must be 0..N-1, contiguous, no gaps or duplicates — checked here.
    }
}

public static class ExecutionPlanHasher
{
    public static string ComputeHash(ExecutionPlan plan); // SHA-256 over a canonical JSON form
}
```

**Canonicalization, not "whatever `JsonSerializer` happens to emit."** `System.Text.Json`'s
default property order follows declaration order, which is an implementation detail, not a
contract — relying on it for a security-relevant hash would make the hash fragile to a harmless
refactor. `ExecutionPlanHasher` instead recursively sorts every JSON object's properties by
ordinal key name before hashing (`CanonicalJson.Sort`), the same "make the representation
deterministic before hashing it" idea the audit chain already uses (`JsonLinesAuditSink`'s
`Hash = SHA256(PrevHash + EventJson)`, D-008) — this ADR reuses that idea, not a new one.

**The hash is computed on demand, never cached on the record.** A C# `record` supports
non-destructive mutation (`plan with { Rationale = "..." }`), which would silently invalidate a
stored hash if one existed as a field. Computing fresh from content every time means there is no
stale-hash bug *by construction* — the type cannot lie about its own contents.

**Approval binds to a hash, not to an instance:**

```csharp
public sealed record ExecutionPlanApproval(string PlanHash, ApprovalDecision Decision);
```

Before a plan executes, the runtime recomputes `ExecutionPlanHasher.ComputeHash(plan)` and
compares it to the `PlanHash` the approval was granted for. A mismatch means the plan changed
after approval — for any reason, benign or not — and the approval no longer applies; a fresh one
must be requested. This is what "a material change invalidates the approval" means concretely:
not a heuristic diff, a hash equality check.

### `SkillReport` — evidence and findings only, never free model text

```csharp
public sealed record SkillReport(IReadOnlyList<Evidence> Evidence, IReadOnlyList<Finding> Findings, ExecutionPlan? Plan);
```

A report is data assembled from what was actually recorded — `Evidence`/`Finding` the Skill
built during its run — never a paragraph the model wrote describing what it thinks happened. This
is the structural half of "never let the model invent proof": there is no field here for
unstructured narrative, so there is nothing for a report renderer to trust *except* recorded
Evidence and Findings.

### Policy stays additive, not rewritten

`PolicyContext` (unchanged constructor — every existing call site, `AgentRunner.ExecuteStepAsync`
included, keeps compiling) gains optional, `init`-only properties: `SkillId`, `CapabilityName`,
`Target`, `Environment`, and `BlastRadius` (a new `enum { Single, Multiple, Fleet }` — a
*magnitude*, never a literal count or a named resource, keeping rule A1 intact: the core still
names no concrete target). All default to `null`/absent for the existing Tool-call path, which
supplies none of them. `Critical` stays unconditionally `Forbidden` regardless of any of these
fields — no code path in `IPolicyEngine` gets a way to raise that ceiling (rule S3 is unaffected
by this ADR).

**`bOps.Policy`'s actual rule evaluation over these new fields is not implemented by this ADR.**
The shape exists so a policy context *can* carry them; teaching `PolicyEngine`/
`PolicyConfigLoader` to evaluate rules keyed on `Target`/`Environment`/`BlastRadius`/maintenance
windows is real policy-semantics design (a YAML schema change) deferred to the follow-up below,
not silently skipped — see "Deferred," next.

## Deferred to a follow-up ADR — real, not silently dropped

This ADR intentionally does **not** define:

- **`ICapability`/`ISkillProvider` execution interfaces.** `CapabilityManifest` (the declarative
  shape: identity, version, risk, required permissions, typed input/output via the existing
  `ToolParameter` shape, timeout, dry-run support, `VerificationSpec?`, rollback description) is
  declared in this ADR's code, alongside Evidence/Finding/ExecutionPlan — but the interface a
  Skill implements to actually *run* is not. Getting an execution contract wrong on a frozen
  `1.0.0` ABI is worse than shipping it one increment later, once a real runner has exercised it.
- **Runtime orchestration.** Nothing in `AgentRunner` changes in this pass. Walking an
  `ExecutionPlan`'s steps through the existing policy/approval/verification/audit pipeline (the
  same `ExecuteStepAsync` every Tool call already goes through — an `ExecutionPlanStep` executes
  exactly like any other tool call, never through a shortcut) is the next increment.
- **`bOps.Policy` rule evaluation** over the new `PolicyContext` fields (above).
- **A sample Skill.** V1.1's Definition of Done in the consolidated roadmap requires one
  working end-to-end; it depends on both deferred items above and is not claimed done here.

## Alternatives considered

- **Fold Capability into `ToolManifest`** (add `Rollback`, `DryRun` etc. directly to the existing
  type). Rejected — `ToolManifest` describes one atomic, directly-executable action; conflating
  it with a multi-step, typed-I/O operator contract would make every existing Tool provider
  implement fields it has no use for, and would make "how many tool calls does this take" an
  unanswerable question from the manifest alone.
- **Model plan text as the source of truth for `ExecutionPlan`**, i.e., re-parse the existing
  `AgentPlan.Rationale`/`PlannedStep.Description` into something hashable. Rejected — those are
  explicitly natural language, revised freely (rule C8), and never typed. Hashing prose the model
  wrote is not "a material change invalidates approval," it is "any rewording invalidates
  approval," which is both too strict (harmless rewording) and too weak (the actual arguments a
  tool call would run with are not in the prose at all).
- **A single flat `Kind` string on `Evidence` instead of an enum.** Rejected — the plan names five
  specific classifications the project has already decided matter; an open string invites five
  different spellings of "fact" across Skills, exactly the inconsistency `RiskLevel`/`ToolOutcome`
  already avoid elsewhere in this contract.

## Consequences

- `bOps.Abstractions` gains three new files (`Evidence.cs`, `ExecutionPlan.cs`, a `Capabilities.cs`
  holding only `CapabilityManifest` for now) and no new project dependency (still zero, rule A7).
- Every new record gets a round-trip test (rule A2), and `Finding`'s and `ExecutionPlan`'s
  constructor-enforced invariants get a dedicated rejection test each, matching the existing
  pattern for `IToolRegistry.Register`'s B3 checks.
- No existing behavior changes: `AgentRunner`, `PolicyEngine`, `bOps.Api`, `bOps.Cli` and the
  Angular UI are all untouched by this ADR. This is additive contract surface only, consumed by
  nothing yet — exactly the state `IVerifiableTool` was in before V0.4 wired it into the loop.
