# AF-05 — MAF real regression, tuning and legacy context cleanup

| | |
|---|---|
| Governing ADR | [ADR-0052](../../docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md), D-048 |
| Status | **Planned** |
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
