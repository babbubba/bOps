# AF-05 — MAF real regression, tuning and legacy context cleanup

| | |
|---|---|
| Governing ADR | [ADR-0052](../../docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md), D-048 |
| Status | **Implemented; branch pushed to `origin/feat/af-05-maf-regression-tuning-cleanup`, not merged. Two review findings (evidence addressability, grounding diversity) closed by AF-05G on `fix/af-05g-evidence-addressability-grounding`. The premature-completion and disclosure-drift live regressions closed by AF-05H ([packet](2026-10-11-af-05h-live-regression-hardening.md)). Live-provider runs and Windows/Linux CI NOT RUN** |
| Depends on | AF-01, AF-02, AF-03, AF-04 complete |
| Effort | **medium** |
| Primary risk | declaring success from token reduction while diagnostic quality or safety regresses |

## Objective

Validate the completed MAF context path on representative real workloads, tune configuration from measurements, then remove obsolete custom model-facing context code that has no remaining runtime purpose.

MAF adoption is already decided. AF-05 decides **configuration and cleanup**, not whether to revert architecture to a custom framework.

## Baseline scenarios

Reconstruct or preserve fixtures for the real failure shape that motivated ADR-0052, especially task:

`c508f188-3306-4e29-bf86-118925ced418`

Known baseline characteristics to preserve in the regression notes:

- successful `system.info`;
- `system.stability` with substantial/high-density evidence;
- `system.crashes` large/partial output;
- `system.events` large grouped output;
- repeated EvidenceRead leading to fatal exhaustion before later CPU/memory/storage work;
- roughly 71k cumulative model tokens in the observed run.

Do not encode those values as product constants. They are regression evidence.

Also use at least one shorter successful diagnostic so tuning is not optimized only for pathological long sessions.

## Provider matrix

Run/test at least:

1. a local OpenAI-compatible model path representative of Qwen/local operation;
2. OpenRouter or another external provider family already supported by bOps;
3. if readily available, Anthropic native adapter as a third compatibility check.

Correctness must remain covered by deterministic fake/recorded-provider tests even when live credentials are unavailable.

## Required measurements

For each scenario/configuration record:

- provider/requested model/actual model where reported;
- context profile;
- primary model call count;
- primary prompt/completion tokens;
- summarizer call count;
- summarizer prompt/completion tokens;
- all-in tokens;
- estimated versus actual prompt tokens where available;
- history groups before/after compaction;
- EvidenceRead calls and exhaustion events;
- context-overflow events/retries;
- wall-clock;
- number of operational steps;
- replans;
- terminal status;
- expected fact/evidence retention;
- final diagnostic usefulness/grounding checks.

Do not compare only primary prompt tokens. A summarizer that reduces primary tokens but increases all-in work/latency materially must be tuned.

## Tuning targets

Tune, based on measured evidence:

- context safety reserve;
- history budget;
- tool-result compaction threshold;
- summarization trigger;
- minimum/recent group preservation;
- sliding-window threshold;
- truncation backstop;
- summary model choice/profile examples where relevant.

Record chosen values and rationale in operator configuration documentation and task evidence.

Do not hard-code one model's context window as a global default.

## Functional acceptance

For the c508-equivalent scenario:

- EvidenceRead exhaustion alone must never terminate the task;
- later diagnostic work remains possible;
- high-salience known evidence is available through Facts/grounding or exact drill-down;
- MAF summary never overrides authoritative facts/completeness/verification;
- exact step-scoped tool routing is unchanged;
- no policy/approval/entitlement/mutation behavior changes;
- raw persisted tool results remain intact.

The final answer must not be considered “better” merely because it is shorter. Validate required known evidence from the fixture and limitation disclosure.

## Safety regression

Run focused suites for:

- step-scoped tool routing;
- argument correction;
- policy denial;
- approval;
- entitlement;
- mutation journal crash/reconcile;
- resume/fencing;
- grounding/limitations;
- prompt injection through tool output and summary;
- provider fallback;
- model failure containment.

Any regression in authority/safety blocks cleanup.

## Legacy cleanup

After MAF path and AF-04 evidence behavior pass the gate, remove custom model-facing context code that no longer has a runtime purpose.

Candidate cleanup includes:

- `BoundedHistory` compact/archive machinery;
- HARDEN-8-specific aggressive K=0 projection logic superseded by MAF;
- obsolete options such as `VerbatimHistorySteps` **only if no remaining runtime behavior requires them**;
- obsolete tests that assert implementation-specific legacy compaction rather than preserved product semantics;
- stale documentation/configuration for removed options.

Preserve/rewrite tests for the actual invariant before deleting legacy tests.

Do **not** remove:

- raw evidence persistence;
- EvidenceRead exact raw drill-down;
- EvidenceFacts;
- evidence limitations/grounding;
- task token/duration budgets;
- context-overflow failure classification;
- model-call payload recording;
- mutation/reconciliation notices.

## Documentation reconciliation

Update, based on verified implementation:

- README/operator configuration for MAF context/summarizer settings;
- `HANDOFF.md`;
- `CHANGELOG.md`;
- ADR-0014 amendment pointers if cleanup is complete;
- architecture suppressions/third-party notices/SBOM references;
- task index status;
- any appsettings defaults actually changed.

Do not claim live-provider validation that was not run.

## Validation

Required final gate:

```bash
dotnet restore bOps.slnx --locked-mode
dotnet build bOps.slnx --configuration Release --no-restore
dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"
```

Also require Windows and Linux CI because the full solution contains platform packages and architecture guards.

Run Angular build/tests only if API/UI/configuration surfaces were changed in a way that touches the web Settings UI.

## Definition of Done

AF-05 closes the MAF migration train only when:

- deterministic regression tests are green;
- real/local Qwen-compatible run has been exercised or explicitly recorded as unavailable;
- at least one external provider path has been exercised or recorded as blocked by credentials, while recorded fixtures still pass;
- context/summarization tuning values are evidence-based and documented;
- c508-equivalent failure no longer dies from EvidenceRead exhaustion;
- safety/authority regression suites are green;
- obsolete legacy context code/options/tests are removed or each retained item has a documented active reason;
- full non-live solution suite and required CI are green;
- documentation accurately distinguishes measured/live evidence from fixture-only evidence.

## Stop conditions

Stop cleanup if:

- MAF path still depends on legacy compaction for normal operation;
- safety tests differ between legacy and MAF paths;
- provider usage accounting cannot include summary calls;
- raw evidence/persistence differs because of projection;
- live/fixture diagnostics lose materially required evidence without a compensating authoritative Fact/drill-down path.

## Implementation evidence (2026-10-11)

Verified evidence only. Measurements, their classes (DETERMINISTIC FIXTURE / LIVE LOCAL / LIVE EXTERNAL / NOT RUN) and the full tables are in
[2026-10-11-af-05-benchmark-results.md](2026-10-11-af-05-benchmark-results.md). **No live model was exercised.** Branch
`feat/af-05-maf-regression-tuning-cleanup`, based on `a723092` (verified `fix/af-04r-pre-af05-review`).

### Phase 1 — instrumentation verified (and the gaps found)

Every measure the packet requires was checked against the code before any tuning:

| Measure | Where it is observable | Result |
|---|---|---|
| provider, requested model | `bops.model_context` tags `bops.model.provider/id`; `ModelCallRecord`; `ModelCallAuditEvent` | available |
| actual model | `ModelCallRecord.ActualModel`, audit `ActualModel` (reported by the provider) | available in records, not a metric dimension (it can differ per call) |
| profile source, max context, reserved output, history budget | `bops.context.profile_source/max_tokens/reserved_output_tokens/input_budget_tokens` | available; `unknown-structural` replaces the former `unknown-legacy-safe` |
| fixed / protected / tool / history estimates, history before/after (groups, messages, characters), strategies, tool groups compacted, turns/groups dropped | `bops.context.*` | available; **added** `estimated_prompt_tokens_before/after` |
| summary requested / applied / skipped / failed / discarded, input and output estimate, provider, model, dedicated | `bops.summary.events_total`, `bops.summary.input_estimated_tokens`, `bops.model_summary` | available |
| primary, summary and all-in tokens | `bops.task.call_tokens{part}`, `bops.summary.tokens` | available |
| provider-reported prompt tokens, estimate delta | `bops.context.actual_prompt_tokens`, `bops.context.estimate_delta` | **gap fixed**: the delta compared the provider prompt with `fixed + history`, and `fixed` included the *safety reserve* (headroom, not prompt), biasing every delta by `SafetyReserveTokens`. It now uses `EstimatedPromptTokensAfter` (protected + tools + history). |
| EvidenceRead served / exhausted / malformed / unavailable | `bops.evidence_read.events_total`, `EvidenceReadAuditEvent` | available |
| retry / fallback transitions | `ModelCallRecord` / audit `RetryDecision`, `FallbackOrdinal` | available in records (no counter; not needed) |
| context overflow | `bops.context.overflow_retries_total`, `FailureKind` | available |
| wall-clock | `bops.step.duration_ms`, `bops.summary.duration_ms` | available at runtime; **not measurable in the fixture** (no model latency) |

No raw history, evidence, argument, secret or summary text was added to any attribute.

### Phases 2 and 2B — regression fixtures

`tests/bOps.AgentFramework.Tests/Regression/` drives the real `AgentRunner`, `ModelInvocationBroker` and `MafContextProjector` with a scripted model and
the real package fact builders. `c508-equivalent` reproduces the semantic pressure of task `c508f188-3306-4e29-bf86-118925ced418` (large
partial/truncated outputs, high-density facts, six requested evidence reads against an allowance of four, CPU/memory/storage after the pressure);
`c508-extended-10` adds ten more large steps so the final call is compacted; `short-diagnostic` is `system.info`, `system.cpu`, `system.memory`.
`MafRegressionTests` asserts: exhaustion is soft in every configuration and the later steps run once each; the c508 quality gate (facts and
limitations visible, `Evidence limitations` in the answer) for configured windows of 10,240–32,768; raw persisted results and the exact step tool
surface are unchanged by projection; the short diagnostic pays no summary and no compaction; a window too small for the protected reads fails as a
classified `ContextOverflow`; the unprofiled default keeps every fact of the short diagnostic.

### Phase 3 — provider matrix: NOT RUN

| Path | Status |
|---|---|
| Local OpenAI-compatible / Qwen-like | **NOT RUN — unavailable credentials/environment**: nothing listening on 8080 / 11434 / 1234, no `llama-server`, `ollama` or LM Studio present. No service was installed. |
| OpenRouter / external | **NOT RUN — unavailable credentials/environment**: `BOPS_MODELPROVIDER_API_KEY` is unset; the vault was not opened and no credential was looked for. |
| Anthropic native | **NOT RUN — unavailable credentials/environment** (`ANTHROPIC_API_KEY` unset). |

Correctness for these providers rests on the unchanged recorded-wire suites (OpenAI-compatible, Anthropic, `ProviderWireEndToEndTests`) and the
fallback/failure-containment suites. Nothing in this packet is "validated on OpenRouter/Qwen/Anthropic".

### Phases 4 and 5 — measurements and tuning

See the benchmark record. Changed defaults (each with old/new/scenario/before/after/rationale there): `Agent:Context:MinimumPreservedToolGroups`
2 → 3; `Agent:Summarization:TriggerRatio` 0.75 → 0.9. Unchanged after measurement: `SafetyReserveTokens` (1,024), `MinimumPreservedTurns`, `RecentGroupsToPreserve`,
`EvidenceRead.MaxAttempts` / `MaxControlRejections` (4 / 4), summarization `Enabled=false`, no dedicated summarizer by default. The ADR-0052 §11
pipeline order was **measured, not changed**: the normative order offers the summarizer 1,320 characters and no raw tool data; the reverse offers
13,765 characters and all five raw fact markers; the authoritative facts are not at stake in either (they live in grounding and limitations).

### Phase 6 — operability items

| Item | Outcome |
|---|---|
| **O1** profile activation and routers | Closed. One path: exact profile → budget-driven MAF projection; unlisted pair → MAF structural tool-result bound (`unknown-structural`), no capacity invented, visible in telemetry. `UseLegacySafeProjectionWhenUnknown` removed (a capacity-less entry is now a validation error; leaving a model out is how its capacity stays unknown). A router window can be stated only by the operator as a guarantee they own (documented with the risk). `docs/operator-configuration.md` §13 documents exact matching, local / OpenAI-compatible / external / fallback / router cases. |
| **O2** `BoundedHistory` | Classified and removed — see the table below. |
| **O3** K=0 overflow recovery | Superseded and removed. `ModelFailureKind.ContextOverflow` is preserved; after a provider-reported overflow the runner asks for **one** aggressive projection (`ModelContextRequest.Aggressive`: nothing preserved, history allowance halved, no summarization; goal and plan prompt caps kept). A second overflow, or a call with no historical context, is terminal as before. No loop. |
| **O4** CLI dedicated summarizer cross-provider | **Not done; documented blocker with an active reason.** The API composes a dedicated summarizer from stored provider profiles (`ProviderConfigurationCoordinator.ResolveCandidate`) and per-invocation vault credentials (`ProviderCredentialResolver`), both API-internal and bound to the Settings store and the vault. The CLI composes neither; making it cross-provider means moving or duplicating them into a shared assembly and adding the vault/settings to the CLI composition — a new provider/secret framework (stop condition 13). The CLI therefore accepts a different *model* of its single `ModelProvider:Provider`, and rejects another provider with an explicit message. |
| **O5** operator documentation | Closed (`docs/operator-configuration.md` §13, README). |
| **O6** stale AF-04 hand-off | Closed: the `ReplanningInstructions` item is corrected (R2 already solved it). |
| **O7** estimated cost | Closed: `ModelSummarizationLedger` accumulates the summary calls' reported `EstimatedCostUsd`; the all-in cost is the known primary cost plus the known summary cost, a part with no reported cost contributes nothing (never estimated), `null` only when neither reports. Tested (4 cases). |
| **O8** caller-cancellation hardening | **Evaluated, deliberately not applied.** Linking the caller's token to the broker's wait makes an adapter that ignores its token stop waiting, but it (a) abandons an in-flight response whose usage the task must still account (`ACancelledAttempt_KeepsTheTokensItAlreadySpent`, ADR-0040 §5.2 — the change broke it) and (b) changes the exception type surfaced to callers. A conforming adapter already cancels (it observes the attempt token); a non-conforming one is bounded by the attempt timeout and the execution budget. The cancellation/timeout distinction is now covered by two regression tests. |
| **EvidenceRead addressing** (new, found by E2E-13) | Without the HARDEN-8 compact records the model could not learn the `ev1:<task>:<n>` id the text directive needs (the replan has only the text directive). A runtime-authored **evidence index** (newest 16 truncated steps with id and lengths, plus the rule that makes any step addressable) is added to the step and replan prompts while the control function is offered. It names no evidence text, is rebuilt from persisted steps on resume, and its markers are neutralized inside tool output. |

### Phase 7 — legacy cleanup (O2)

| Class | Item | Disposition |
|---|---|---|
| A — obsolete model-facing legacy | `BoundedHistory.Build / CompactRecord / ArchiveSummary` and their limits, `ModelFacingHistory`, the `<<<BOPS_HISTORY/v1>>>` block, `Agent:VerbatimHistorySteps`, `ModelContextRequest.LegacySafeRequest`, `ModelContextProfile.UseLegacySafeProjectionWhenUnknown`, `LogicalCallState.HasCompactableHistory`, `RebuildHistory` and the dead `history` plumbing of `ContinueAsync` | **Deleted** |
| B — unknown-profile fallback | the conservative legacy request | **Replaced by MAF** (structural tool-result bound); no second engine remains |
| C — tests of implementation detail | 10 `B3_*` compact-record/archive grammar tests and the `BoundedHistory_EnforcesK_M…` test | **Deleted after naming the invariant**: bounded size and whole-field fail-closed → `EvidenceIndexTests`; tool-name distrust → `EvidenceIndexTests`; determinism/plateau → MAF regression and projector tests |
| C — rewritten | E2E-13 (40 steps, two resumes: now proves completion, raw evidence and retrieval of step 0 through the index on the new path), `ContextOverflow_RetriesOnceAtK0…` (→ one aggressive request, same authority and tools), `B4_*` (goal/plan caps), Windows-freeze scenario (digest assertions kept), `AgentContextBudgetConfigurationTests` | **Rewritten on the MAF path** |
| D — unrelated, kept | `EvidenceRead.EvidenceId`, `EvidenceRead.TakeUtf16` (moved out of `BoundedHistory`), `AggressiveGoal/PlanMaxCharacters`, `ReplanPlanMaxCharacters`, `ProjectForPrompt` | **Kept** — active reason: bounds on runtime-authored goal/plan *prompt text* during the one overflow recovery (HARDEN-8 §§3–4), not history management |
| Retained shim | `ModelContextRequest.ToUnprojectedRequest()` | **Kept** — a host that composes no projector sends protected context plus verbatim history (each observation is already capped at `MaxObservationCharacters`). Every host in this repository composes the MAF projector. Not a context engine. |

Not removed (as required): raw evidence persistence, `EvidenceRead`, `EvidenceFacts`, evidence ids, grounding, limitations, completeness, verification, `TaskState`,
execution-attempt fencing, the mutation journal and reconciliation notices, model-call records and payload bounds, task/model token budgets, the logical call
deadline, `ContextOverflow` classification, exact tool routing.

### Stop conditions — evaluated

| # | Condition | Result |
|---|---|---|
| 1 | c508-equivalent loses materially required evidence | **Judged not a stop for the cleanup; operator decision requested.** With an exact profile (10,240–32,768) every expected fact and limitation is visible (9/9, 3/3). The *default* unprofiled path keeps 1/9 facts directly visible — identical to the HARDEN-8 request it replaces (1/9), with a compensating drill-down path (digest, evidence index, `EvidenceRead`, raw evidence persisted). Cause: the ADR-0042 grounding block shows at most 12 facts, newest steps first, and `system.events` alone emits 12 (see below). The cleanup removes no evidence path and does not worsen any measured row; it does not fix this either. |
| 2 | a summary prevails over Facts / limitations | No — `SummaryAuthorityTests` (contradicting fact, completeness, mutation journal, tool steering) pass unchanged. |
| 3 | MAF changes tool authority | No — exact tool surface asserted on every projected request. |
| 4 | raw persistence changes | No — fixture results equal persisted results after projection with summarization on. |
| 5 | token accounting excludes summarization | No — all-in tokens and (now) cost include it. |
| 6 | logical `ModelCallDeadline` regresses | No — the AF-04R/R1 deadline suite is green and `ModelCallDeadline` is untouched. |
| 7 | EvidenceRead exhaustion becomes terminal | No — soft in all configurations. |
| 8 | the normal path needs the old engine | No — removed. |
| 9 | an unknown router needs an invented capacity | No — structural bound only. |
| 10 | provider matrix shows unexplained semantic differences | Not applicable — no provider run. |
| 11 | a safety regression fails | No — see validation. |
| 12 | ADR-0052 must change | No — additive evidence only (§30). |
| 13 | CLI needs a new provider/secret framework | **Yes, for cross-provider only** — not done (O4); the rest of the task did not depend on it. |
| 14 | a persistence migration is needed | No. |
| 15 | benchmark vs fixture cannot be told apart | No — every number is labelled DETERMINISTIC FIXTURE; the synthetic parts are listed. |

### Recommendation: ADR-0042 grounding salience — **applied by AF-05G (2026-10-11)**

`EvidenceGrounding/v1` ranks facts "activation-referenced first, then the newest steps first" and shows 12. On the c508 shape the newest fact-bearing step
(`system.events`, 12 facts) fills the block ("18 additional observed fact(s) are not shown"), so the AF-04 facts of `system.stability` and `system.crashes` never reach
the final prompt through grounding; once the raw results are compacted they are reachable only by drill-down. An **uncommitted experiment** replacing the order by "referenced first,
then fact position, then newest step" (round-robin across steps, same 12-fact bound) changed all-in tokens by at most ±0.3 % and raised directly visible facts from
**1/9 to 7/9** for the unprofiled default and for 12,288/16,384 windows under pressure (c508-extended-10), and left the 9/9 configured cases at 9/9. This changes an Accepted
rule and its ordering tests, so it needs its own ADR-0042 amendment and the operator's decision; the experiment was reverted. **AF-05G then applied it as the ADR-0042 "step-diverse grounding selection" amendment** (referenced first, then a newest-to-oldest round-robin; see the AF-05G section below).

### Deviations and residual technical debt

1. **No live runs** (local, external, Anthropic): environment, not choice. All claims are fixture-class.
2. **Token counts are a synthetic proxy**; provider-prompt-vs-estimate calibration is unavailable and no estimator tuning was done.
3. **Summarization quality is unmeasurable** with a scripted summarizer; the default stays off because cost is never lower all-in on the fixtures.
4. **O4 and O8** not applied (reasons above).
5. **Small windows** (≈ 9k and below with four protected evidence reads) fail safely as `ContextOverflow` rather than degrading: reads could be made capacity-aware in a future packet.
6. **Evidence index duplicates the digest's step number** for truncated steps (by design: it adds the stable id and lengths); both are bounded.
7. `MafRegressionTests` serialize with `GovernedSummarizationTests` because that class's telemetry test listens globally for summary activities.
8. The local, uncommitted edit of `src/core/bOps.Api/appsettings.json` (model-call timeouts) predates this task and is **not** in any commit; only the removal of the obsolete `VerbatimHistorySteps` key from the committed file is.

### Validation

Run from a **clean checkout** of the committed code (git worktree at a short path, so the operator's local `appsettings.json` edit is not in the tree) at `3698181`; every later commit is documentation only.

| Gate | Command | Result |
|---|---|---|
| Restore | `dotnet restore bOps.slnx --locked-mode` | **PASS** (exit 0) |
| Build | `dotnet build bOps.slnx --configuration Release --no-restore` | **PASS — 0 warnings, 0 errors** |
| Non-live suite | `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"` | **PASS — 35 assemblies, 0 failures, 5,255 passed, 147 skipped** |

Representative assemblies: Runtime 2,069, Api 509, Cli 124, Architecture 175, AgentFramework 83, Policy 308, Memory 99, System.Core 387, Storage.Core 26, OpenAI-compatible provider 125, Anthropic provider 60 — all passed. The 147 skips are the existing platform/environment-dependent tests (Linux-only System/Storage/Service/Scheduler tests on this Windows host, Docker daemon and Filesystem symlink tests), unchanged by AF-05. `LiveModel` tests were **excluded by the filter and not run**; nothing here claims they pass.

**Safety / authority regression gate (green before the cleanup and again after):** exact step-scoped tool routing and argument correction (`RequestDependentAuthorityTests`, `PlannedStepValidationRetryTests`, `SemanticPlannedStepTests`, `ToolCallProtocolTests`), policy denial, approval and entitlement (`EntitlementExecutionTests`, `DelegatedExecutionTests`, Policy tests, Api approval tests), ADR-0050 conditional facts (`ConditionalFollowUpTests`, `HighDensityFactsActivationTests`), the mutation journal, unknown outcome and reconciliation (`OrdinaryMutationJournal*`, `OrdinaryMutationNoDoubleExecutionTests`, `HardenFourteenMutationCompositionTests`, `MutationJournalPolicyTests`), resume and fencing (`ResumeStateMachineTests`, Api `TaskResumeEndpointTests`), grounding and limitations (`EvidenceGrounding*`, `EvidenceLimitation*`, `EvidenceDisclosure*`), prompt injection and summary authority (`SummaryAuthorityTests`, `GovernedSummarizationTests`, `TerminalProtocolArtifactTests`), provider fallback (`FallbackHandshakeTests`, Api `FallbackExecutionTests`/`FallbackChatModelTests`), model failure containment (`ModelCallFailureContainmentTests`, `ModelFailureEndToEndTests`), the logical `ModelCallDeadline` suite and EvidenceRead soft exhaustion (`EvidenceControlPlaneTests`, `HardenEightTests`). The pre-cleanup run at the AF-05 baseline plus the first fixes was 35 assemblies / 0 failures / 5,253 passed; no test was disabled to reach green.

**Linux (local, not CI):** WSL Ubuntu 24.04, .NET 10.0.401, tree copied with `git archive` at `3698181`: Architecture 173 passed / 2 skipped, AgentFramework 83, Runtime 2,069, Policy 308, Memory 99 — all passed. `bOps.Api.Tests` was **not** run on Linux: its build needs the Windows-only Firewall helper project restored, which this ad hoc copy did not set up (environment, not a failure); Linux CI covers it. This is **not** the Linux CI job.

| Item | Status |
|---|---|
| Windows CI | **NOT RUN** — the branch is pushed, but `ci.yml` triggers only on pull requests to and pushes to `main`, and no pull request is open. |
| Linux CI | **NOT RUN** — same reason (the WSL run above is local evidence only). |
| LiveModel / live local / live external / Anthropic | **NOT RUN** — see Phase 3. |
| Angular build/tests | Not run: no API/UI/Settings surface changed (the shipped `appsettings.json` lost one obsolete key). |

### Commits (pushed with the branch; tip `4f83329`)

| SHA | Subject |
|---|---|
| `cc561ef` | test: add MAF regression benchmark scenarios |
| `7db054d` | fix: close MAF context operability gaps |
| `83e8fcf` | refactor: remove obsolete legacy context projection |
| `549d9e8` | perf: tune MAF context configuration from measurements |
| `a30c51d` | test: measure summarization at the product default settings |
| `83ca757` | docs: document MAF context configuration and AF-05 evidence |
| `aedd10c` | refactor: keep the overflow recovery decision out of an exception filter |
| `34371f8` | test: bind and validate the documented context profile configuration |
| `3698181` | test: guard against a second model-facing history engine |


## AF-05G — evidence addressability and grounding diversity (2026-10-11)

Follow-up on `fix/af-05g-evidence-addressability-grounding`, created from `4f83329`. It closes two findings of the direct review of AF-05 and
does nothing else: no new milestone, no MAF tuning (every default measured by AF-05 is unchanged: `MinimumPreservedToolGroups` 3,
`MinimumPreservedTurns` 1, `SafetyReserveTokens` 1024, `Summarization.TriggerRatio` 0.9, `RecentGroupsToPreserve` 4). Evidence class:
**DETERMINISTIC FIXTURE**; no live model was exercised. Tables: [benchmark results §8](2026-10-11-af-05-benchmark-results.md#8-af-05g-post-fix-measurements).

### G1 — raw evidence stays exactly addressable after MAF compaction

**Root cause.** `AgentRunner` offered `runtime.evidence_read`, and `EvidenceIndex.Build` listed a step, only when a persisted result or
observation was longer than `MaxObservationCharacters` (4,000). That number decides when the *runtime* shortens an observation. It says nothing
about when the *MAF context layer* removes an old tool result from the projected history (the oldest groups beyond `MinimumPreservedToolGroups = 3`).
With the unknown profile, four 2,000-character results compact the oldest group while the control function and the index are absent: the
evidence is persisted and the reasoning model can no longer reach it, unless the tool also produced Facts. That broke the ADR-0052 invariant
that persisted raw evidence the context may drop stays recoverable.

**Rule now.** A persisted step is addressable when it is an executed tool step (`ToolCall` and `Result` present) whose result output or
observation is not empty (`EvidenceIndex.IsAddressable`). Size, completeness, facts and whether MAF compacted it play no part. Whenever any
such step exists and the logical call's read budget is not exhausted, `runtime.evidence_read` is offered beside (never instead of) the single
step tool, and the protected `EvidenceIndex` block is added to the step prompt and to the replan text prompt. The index lists the newest 16
addressable steps with id and lengths, no evidence text, and states the rule that any persisted tool step `n` of the task is addressable as
`ev1:<task>:n`. Everything else is unchanged: `MaxAttempts` 4 served reads, `MaxControlRejections` 4, soft exhaustion (the control function and
the index disappear and `ExhaustedFeedback` is shown, the step tool stays), exact step-scoped tool authority, no registry entry, no policy,
approval or entitlement for the control function, the model never has to read anything (a small result costs no extra model call), and the
replan keeps the text protocol. No persistence schema, MAF authority, `BoundedHistory` or second copy of the evidence is involved.

**Tests.** `EvidenceAddressabilityTests` (runtime: small result offered and indexed with no automatic read and the expected tool intact - T3; fact-less
exact drill-down - T2; four reads then soft exhaustion - T4; replan text protocol on an old `<4000` result - T5; persist, reopen, resume gives the same
index and the same drill-down - T6; cross-task, malformed and out-of-range unchanged - T7), `EvidenceIndexTests` (addressability independent of size,
fact-less steps listed, newest-16 window and the rule line, deterministic and text-free), and `MafRegressionTests.G1_*` (T1: four 2,000-character groups, unknown
profile, MAF really compacts the oldest, the raw sentinel is gone from the projected history, the control function is offered, the index names step 0 and an exact
read returns the raw fragment). T1 **fails at `4f83329`** (control function not offered). Test expectations that fixed the tool surface to the single step tool now
filter the runtime control function out of the *operational* surface, which is what they are about.

**Cost.** The control function declaration, one instruction sentence and the index (about 380 synthetic tokens per call) now ride in every step prompt once a tool
step exists, so a very short diagnostic pays for it (short diagnostic, unknown profile: 5,705 to 7,599 prompt synthetic tokens, +33 %, five calls). On the c508 shape
it is +1.4 % (69,600 to 70,544). This follows directly from making addressability independent of size; the alternative (predicting MAF's compaction in the
runtime) was rejected by the task and would give the runtime a second, unreliable copy of the context layer's decision.

### G2 - grounding diversity

ADR-0042 amendment "step-diverse grounding selection (AF-05G)". Referenced facts keep absolute priority (newest step, then position); the remaining slots are filled
by a deterministic round-robin over steps newest to oldest, each step's facts in list order (pass 1 takes every step's first remaining fact, pass 2 the second, and so on), up
to `MaxShownFacts = 12`. Display order, fact ids, markers, entry format, checker, bounds and `MaxShownFacts` are untouched, so `EvidenceGrounding/v1` is not bumped
(rationale in the amendment). Tests: exact selection, the documented `A1 B1 C1 A2 B2 C2 A3` example, referenced precedence, stable ids, byte-identical rebuild,
persist/reopen/resume, and the c508 quality gate (`MafRegressionTests.G2T6_*`): for the unknown profile, 10,240 and 16,384 every expected authoritative signal is
directly visible, a grounded typed fact, or exactly readable through `runtime.evidence_read`, and the grounding block covers `system.stability`, `system.crashes`
and `system.events`; the coverage test **fails at `4f83329`** (events took all 12 slots). The harness classifies each signal as visible / grounded / exact-addressable
(`EvidenceAvailability`); a "result recorded" stub counts as none.

### Measured effect (DETERMINISTIC FIXTURE)

| Workload | Visible / grounded / exact-addressable / available | ER served | Synthetic tokens |
|---|---|---:|---:|
| c508, unknown profile - before (`4f83329`) | 0 / **1** / 9 / 9 of 9 | 4 | 69,600 prompt |
| c508, unknown profile - after | 0 / **7** / 9 / 9 of 9 | 4 | 70,544 prompt (70,871 all-in) |
| c508, 10,240 - after | 9 / 7 / 9 / 9 of 9 | 4 | 70,812 prompt (71,152 all-in) |
| c508, 16,384 - after | 9 / 7 / 9 / 9 of 9 | 4 | 81,951 prompt (82,291 all-in) |
| compacted small results (5 x 2,000 chars), unknown - before | n/a (read impossible: 0 served) | 0 | 14,909 prompt |
| compacted small results, unknown - after | n/a (exact read served) | 1 | 22,434 prompt (22,587 all-in) |
| short diagnostic, unknown - before / after | 3/0/0/3 to 3/0/3/3 of 3 | 0 | 5,705 to 7,599 prompt (7,714 all-in after) |

### Deviations

1. The index and the control function are now present in every step prompt after the first tool step (see Cost) instead of only after oversize results.
2. No `EvidenceGrounding` version bump (nothing in the repository ties the selection algorithm to the version; recorded in the ADR-0042 amendment).
3. The tool-surface assertions of seven existing test classes were adjusted to ignore the runtime control function (see G1 tests); no assertion about the *operational* surface was weakened.
