# ADR-0060 — Planner-facing tool-parameter guidance: an optional, display-only `ToolParameter.PlanningGuidance`

Status: **Accepted — 2026-10-17, by the operator** (proposed the same day). No D-number is assigned and `agentic/06-decisions.md` is not edited.
Date: 2026-10-17
Base: worktree `bOps-knowbench01a`, branch `codex/know-bench-01a`, HEAD `76ac5e4` plus uncommitted changes (LIVE-01B).
Refines: [ADR-0050](0050-semantic-planned-step-execution-and-conditional-follow-ups.md) (what the planner can see of a manifest when it proposes `expectedArguments`).
Carries the [ADR-0022](0022-bops-abstractions-1.0-security-boundaries.md) additive-member note for one `bOps.Abstractions` member (`ToolParameter.PlanningGuidance`), as [ADR-0038](0038-provider-wire-contract.md) did for its three.
Preserves: ADR-0038 (provider wire contract, native-tool-free planning), ADR-0050 (equality-only `expectedArguments`, Runtime-owned validation), every manifest validator, policy, risk, approval and verification rule. No new tool, no configuration key, no audit-schema change.

## 0. Sequence of events (stated plainly)

This ADR is **retrospective with respect to the code**. The change was developed first, as LIVE-01B, after a live smoke run of the Local Agent (tasks `514b91d8…` and `e4554137…`, 2026-10-17), and
exists today as an **uncommitted** modification of the working tree (`Tools.cs`, `AgentRunner.cs`, `SystemToolManifests.cs`, `PlanningContractVisibilityTests.cs`; documented in `HANDOFF.md` and
`docs/system-events.md`). It touches a public type in `bOps.Abstractions`, which `agentic/05-workflow.md` ("When an ADR is required") says needs an ADR *before the code*. That ordering was not
followed; this document does not pretend otherwise. It records the decision, which the operator accepted on 2026-10-17, **before the code is committed**. The accepted status covers the
decision recorded here; the LIVE-01B change itself remains uncommitted until the operator asks for a commit.

## 1. The problem: an information gap between the manifest and the planner

The planner is given the tools of a task as prompt text only, built by `AgentRunner.DescribeToolCatalog` (ADR-0038: plan and replan calls expose no native tools). For each tool the catalog
carries the canonical name, the risk level, **the first sentence of the description, cut at 120 characters**, and for each non-sensitive parameter its name, type, required/optional, allowed
values and numeric/length/item bounds. It carries **no parameter description**.

The model that executes a step does see parameter descriptions (the providers serialize `ToolParameter.Description` into the native tool schema). The model that *plans* does not. Yet the planner
is the actor that commits to a parameter value first: under ADR-0050 it proposes `expectedArguments`, an equality-only subset of the manifest parameters, and the executor's call must then match it.
A choice whose meaning lives only in a parameter description is therefore made, for the whole plan, by a reader who has not seen that description.

Concrete case. `system.events` has `mode` ∈ {`raw`, `aggregate`}. `raw` returns event rows and may return only a subset even when the time window is fully covered (row limit or output cap);
`aggregate` returns groups with counts and first/last times and is required for `windowDays`. That trade-off sits in the `mode` parameter description. In the smoke run the catalog line for
`system.events` showed `mode:Enum optional allowed=["raw","aggregate"]` and nothing else; the plan fixed the mode without the information that distinguishes the two values.

## 2. Context of the decision and motivations

- The gap is structural, not specific to one tool: any tool whose parameter changes *what the result means* (coverage versus detail, summary versus rows) is affected. `system.events` is the first
  observed case, not the only possible one.
- The catalog is deliberately compact (ADR-0038/0050 keep the plan call small). Putting every parameter description into it would answer the problem and defeat that intent (§8).
- The information must come from the **package author**, who owns the manifest, not from the core: the core names no tool, provider or package (CLAUDE.md rule 1). A generic, optional manifest
  field keeps the core ignorant of `system.events`.
- The change is small, reversible at the catalog level (one rendering branch), and independent of any model or provider.

## 3. Contract of the optional `PlanningGuidance` field

```csharp
public sealed record ToolParameter
{
    …
    /// Optional short, planner-facing note on how this parameter's value changes what the result means.
    /// Shown in the planning catalog only and never used for validation; cut at the catalog's guidance budget.
    public string? PlanningGuidance { get; init; }
}
```

1. **Optional, `null` by default, init-only.** It is not a constructor parameter, so every existing `new ToolParameter(...)` call, every `with` expression and every positional deconstruction is
   unaffected. It is additive: source- and binary-compatible for existing manifests and packages (ADR-0022 additive-member note).
2. **Display-only.** The field is read by exactly one consumer: the planning-catalog renderer. It is **not** read by `ArgumentSchema`, `SemanticExpectedArguments`, policy, risk evaluation, approval
   binding, verification, audit or the execution path, and it takes no part in deciding whether an argument is valid.
3. **Prose for the planner, not an instruction to the runtime.** It states how the value changes what the result means. It does not state what the model must choose, and no code parses it.
4. **Author-owned.** A package declares it on its own manifests; the core supplies no text for any tool.
5. **Meaning of absence.** A parameter without guidance renders exactly as before.

## 4. Rendering in the catalog

For each **non-sensitive** parameter, after the type, the required/optional flag, `allowed=[…]` and the numeric/length/item bounds, the catalog appends the guidance in parentheses:

```text
mode:Enum optional allowed=["raw","aggregate"] (Prefer aggregate for overviews, counts, … ; use raw to inspect individual events and full messages. raw may return only a subset … complete=false or truncated=true is not complete evidence.)
```

- Rendering is a single `if (!string.IsNullOrWhiteSpace(parameter.PlanningGuidance))` branch in `DescribeToolCatalog`; ordering, separators and every other segment are unchanged.
- Whitespace runs (including newlines) are collapsed to single spaces, so the guidance cannot add lines to the catalog or break its one-line-per-tool shape.
- A whitespace-only value renders nothing.
- The catalog stays prompt text only. It is not an executable surface and not a provider tool schema.

## 5. Length limit and sensitive parameters

- **Limit.** `MaxCatalogGuidanceCharacters = 320` (a private constant of the renderer, alongside the existing `MaxCatalogSummaryCharacters = 120`). A longer guidance is cut at 320 characters and
  marked with `…`; a cut never splits a UTF-16 surrogate pair. The bound is on the *rendered* text, so an over-long declaration degrades to a prefix instead of failing the catalog.
- **Cost measured.** For `system.events` the catalog line grows from 744 to 1,052 characters (+308). The test asserts the growth stays within 1…330 characters.
- **Sensitive parameters.** The catalog already omits every `Sensitive` parameter (name, type, allowed values and constraints). Guidance is rendered **inside** that same filtered loop, so the
  guidance of a sensitive parameter is never emitted. It is also never hidden by being merged into another field. A manifest author should not put secrets or private values in guidance
  at all: it is plain text in a prompt; the omission for sensitive parameters is a safety net, not a licence.
- **Not sent to providers.** The provider adapters serialize `ToolParameter.Description` into native tool schemas; they do not read `PlanningGuidance`. The API's skills DTO mirrors a fixed set of
  parameter members and does not carry it. The field therefore reaches the model only through the planning catalog.

## 6. Compatibility with existing manifests and providers

- **Manifests.** Every existing manifest compiles and behaves unchanged. Only `system.events` `mode` declares guidance today; no other tool is touched.
- **Persistence and wire.** `ToolParameter` is not persisted in the run store or the audit log, so no stored record changes; a package built against an older `bOps.Abstractions` loads and
  renders without guidance.
- **Providers.** OpenAI-compatible and Anthropic adapters are untouched (see §5). ADR-0038 holds: the plan and replan calls remain native-tool-free; the guidance travels in the system prompt text.
- **Record equality.** `ToolParameter` is a record, so equality now includes the new member. No code relies on equality of parameters from different manifests; noted for completeness.

## 7. Guidance versus deterministic constraints

The two kinds of information are kept separate on purpose:

| | Deterministic constraint | `PlanningGuidance` |
|---|---|---|
| Examples | `AllowedValues`, `Minimum`/`Maximum`, `MinLength`/`MaxLength`, `MinItems`/`MaxItems`, `Required`, ADR-0050 equality | prose on how a value changes what the result means |
| Enforced by | `ArgumentSchema`, `SemanticExpectedArguments`, Runtime | nothing |
| Effect of a violation | the call or plan is rejected | none |
| Authority | the runtime decides | none; it grants and denies nothing |

Consequences of that separation: guidance **never makes a value mandatory or forbidden**; it never widens the manifest; it supplies no argument; and a planner that ignores it is not rejected for
doing so. The `system.events` guidance says `aggregate` is *preferable* for overviews and diagnosis and `raw` is for individual events. It does **not** force `aggregate`, and **this ADR does not claim it
guarantees that the planner chooses `aggregate`**. The most it can claim is that the information required to make the choice is now present in the context the planner reads. The wording rule for
authors: describe the trade-off ("raw may return only a subset"), not the command ("must use aggregate"); the test asserts the second phrase is absent.

## 8. Alternatives considered

1. **Render the full parameter descriptions in the catalog.** Rejected for now. It is the more general fix and needs no new field, but it multiplies the catalog by the parameter count of every
   tool in the task view (~100 tools exist), including text written for the *executor* (argument syntax, examples, defaults) that the planner does not need. It would spend plan-call context
   uniformly rather than only where the choice has consequences, and it removes the author's ability to say something *different and shorter* for the planner. It remains the fallback if guidance
   proves insufficient or if authors start to declare it everywhere (§9); the field can then be superseded without breaking packages.
2. **Lengthen the first-sentence summary of the tool description** (the 120-character cut). Rejected: the tool-level sentence cannot carry a parameter-level distinction without growing
   for every tool, and it would silently change what every other tool line says.
3. **Put the guidance in the tool `Description` and rely on the executor.** Rejected: the executor sees it only after the plan has fixed `expectedArguments` (ADR-0050), which is the point of failure.
4. **Hard-code a `system.events` hint in `AgentRunner`/the standing system prompt.** Rejected: the core would name a tool (CLAUDE.md rule 1, enforced in CI).
5. **Force `mode = aggregate` with a deterministic rule.** Rejected here and out of scope: it would be a constraint, not guidance (§7), would take a real choice away from the planner, and `raw` is the
   right answer for inspecting individual events. Nothing in this ADR decides it.
6. **Extra system-prompt paragraph describing tool trade-offs generally.** Rejected: unscoped, grows with the tool set, and detaches the note from the parameter it explains.

## 9. Risks

- **Redundancy with `Description`.** For `mode` the guidance partly restates the parameter description. Two texts can say the same thing twice, and cost context twice (only the planner pays the
  guidance; only the executor pays the description). Mitigation: guidance is for the *consequence* of a choice, the description for *what the argument is*; review at manifest-change time.
- **Divergence from the validators.** Guidance is prose and nothing checks it against behaviour. If the tool's semantics change (for example `raw` stops being capped), the guidance can become
  false while the code stays green. Mitigation: it is in the same manifest, next to the parameter and the limits it describes; the `system.events` test pins its content. No automatic check exists
  or is proposed here.
- **Catalog growth.** Each declaration adds up to 320 characters to the plan and replan prompts. One declaration costs +308; fifty would be roughly 15 KB, a real share of a 32,768-token window.
  There is no global cap on the number of declarations. Mitigation: the per-item cap, the "only where a choice changes what the result means" rule, and §8.1 as the reconsideration point.
- **Prompt-content risk.** Guidance is author-supplied text injected into a system prompt. Packages are already trusted code (ADR-0022 §5), so this adds no new authority; it is still data in a
  prompt and must contain no secrets (§5).
- **False confidence.** A reader may treat the existence of the field as proof the planner behaves correctly. §10 states what is and is not shown.

## 10. Available offline verification, and the limits of the live evidence

**Offline (deterministic, already in the working tree):**

- `PlanningContractVisibilityTests.ScopedCatalog_CarriesBoundedPlanningGuidanceForSystemEventsMode` asserts: the guidance appears after the `mode` constraints; its key phrases are present
  (aggregate for overviews/counts/distributions/diagnosis/wide windows, raw for individual events and full messages, raw may be a subset, `complete=false`/`truncated=true` is not complete
  evidence); the phrase "must use aggregate" is absent; the added length is within 1…330 characters.
- The existing `ScopedCatalog_ExposesExactlyTheNonSensitiveSchemaAndConstraints` test pins the exact catalog line for parameters **without** guidance, so unchanged rendering is verified, and
  asserts that sensitive parameters leak nothing (that test does not declare guidance on a sensitive parameter; the guarantee of §5 follows from the loop structure and is not separately pinned).
- The full-solution build is clean (0 warnings, 0 errors) per the HANDOFF entry; the unrelated baseline failures recorded there (two `bOps.Runtime.Tests`, three `bOps.Packages.Filesystem.Tests`)
  occur identically on pristine `HEAD`. Linux (WSL) and CI have not been run.

**Live evidence and its limits:**

- `HANDOFF.md` records the change as "verified live by the operator". This ADR does not restate or extend that: it proves that the text **reaches the planner**, which the offline test also proves
  deterministically. It does not prove that the planner then picks the better mode.
- A textual description does not deterministically steer the model (the same HANDOFF entry says so for Qwen). The effect on plan quality is an empirical, per-model, non-guaranteed matter. One
  observation does not establish a rate.
- Nothing here has been measured across models, repeated runs or tools other than `system.events`. No inference or benchmark was run for this ADR.

## 11. Relationship with existing decisions

- **ADR-0022.** Public surface of `bOps.Abstractions` grows by one optional, init-only member: additive under the SemVer promise, no breaking change, no security-boundary change. The ADR
  required by `05-workflow.md` for touching a type in `bOps.Abstractions` is this one (late, §0).
- **ADR-0038.** Plan and replan calls stay native-tool-free; the guidance is system-prompt text. The provider wire contract (tool-name aliasing, valid call history, no `null` members) is untouched.
  The catalog's existing statement that it "is not an executable surface" remains true.
- **ADR-0050.** The planner proposes `expectedArguments`; Runtime validates them against the manifest; constraints are equality-only and non-sensitive. This ADR changes **none** of that. It only
  improves what the planner *knows* when it proposes. It adds no operators, no alternatives, no preference expressions, and no weighting of values.

## 12. Boundary with possible future declarative parameter relations (TOOL-CONTRACT-AUDIT-02)

Some relations in a manifest exist today only as prose or as hand-written validator code: `windowDays` is valid only with `mode = aggregate`; `windowDays` and `windowMinutes` are mutually
exclusive. Whether such relations should become a **declarative, machine-checked** part of the manifest (conditional requirement, mutual exclusion, implication) is a separate design question,
tracked as TOOL-CONTRACT-AUDIT-02.

- This ADR **does not** introduce, reserve a shape for, or pre-empt any such relation. `PlanningGuidance` is not a place to encode relations the runtime should enforce; if a rule must hold, it
  belongs in a validator or in a future declarative contract (§7), not in prose.
- Conversely, a future relation contract would **not make this field redundant**: a relation says what is valid, guidance says what a valid choice means. The two can coexist; neither
  depends on the other.
- If TOOL-CONTRACT-AUDIT-02 produces a contract, it needs its own ADR. Any overlap (for example `system.events` guidance that already mentions `windowDays`-related behaviour) is to be reconciled there,
  not here.

## 13. Consequences

- One additive member in the public SDK, one rendering branch, one constant, one manifest declaration, one test. Nothing else changes behaviourally.
- Package authors gain a bounded channel to the planner; the cost of misuse is bounded per item (320 characters) but not per catalog (§9).
- The ADR is accepted. The HANDOFF and CHANGELOG entries for LIVE-01B are still to be updated to reference ADR-0060 and its status `Accepted`; this is outstanding work, not done by this document.
- If a later decision supersedes or withdraws this one, that decision must state explicitly which components (`PlanningGuidance`, the renderer branch, the `system.events` declaration, the test) are kept, changed or removed; nothing is removed automatically. No stored data depends on them.

## 14. Still open

- Whether to run a repeated live comparison with and without guidance. Acceptance did not wait for it; this ADR does not require it and does not claim the result.
- Whether a global cap on the total guidance text per catalog is wanted before a second tool declares any (§9).
- Whether the field should be documented in a package-author guide; none is edited here.
