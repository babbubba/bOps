# AF-04 — EvidenceRead soft exhaustion and high-density EvidenceFacts

| | |
|---|---|
| Governing ADR | [ADR-0052](../../docs/architecture/adr/README.md#adr-0052), D-048 |
| Status | **Implemented locally** (branch `feat/af-04-evidence-cleanup`) |
| Depends on | AF-02 complete; may run in parallel with AF-03 after shared context APIs stabilize |
| Blocks | AF-05 |
| Effort | **medium**, split package work if needed |
| Primary risk | changing evidence-control semantics or adding package-specific knowledge to Runtime |

## Objective

Return EvidenceRead to its intended role: exact raw-evidence drill-down.

Remove the current failure mode where local EvidenceRead budget exhaustion can end the whole diagnostic task, and add bounded package-owned high-salience `EvidenceFact` values to the largest diagnostic tools.

## Part A — EvidenceRead semantics

Current code to inspect:

- `src/core/bOps.Runtime/EvidenceRead.cs`;
- `AgentRunner.cs` logical-call loop around `EvidenceReadAttempts`;
- `EvidenceControlPlaneTests`;
- HARDEN-8 tests;
- ADR-0046 PRE-3A control-call amendments.

### Required behavior

Separate these concepts:

1. **valid served read count**;
2. malformed supported control call;
3. unsupported `runtime.*` control name;
4. control function not offered in this phase;
5. read whose evidence id/range/source is unavailable/invalid.

Do not silently merge all of them into one “attempt” budget unless an accepted rule explicitly requires it.

For the valid read-service budget:

- keep the current maximum initially unless implementation evidence requires a separate configurable value;
- after exhaustion, do no further evidence read;
- stop offering `runtime.evidence_read` for the remainder of that logical call;
- return/emit deterministic bounded runtime feedback so the model knows it must continue without more reads;
- do **not** append the old synthetic fatal “EvidenceRead/v1 limit exceeded” step;
- do **not** terminate the task as `Failed/RuntimeFailure` solely for exhaustion.

Malformed/unsupported/not-offered behavior remains bounded and auditable, but must be governed as protocol/correction behavior rather than pretending raw evidence capacity was consumed.

Do not create an infinite corrective loop.

### Read response metadata

Where compatible with the existing control contract, improve deterministic feedback so the model can know:

- returned length;
- source total length if safely available;
- next offset/end-of-source;
- whether another read slot remains.

Do not expose another task's evidence and do not put TaskId in model-supplied arguments.

If changing the control response discriminator/version is required, preserve backward parsing only if actually needed by persisted/resumed calls; document the decision rather than introducing an indefinite compatibility protocol.

## Part B — EvidenceFact expansion

Priority tools:

1. `system.stability`;
2. `system.crashes`;
3. `system.dump_analyze`;
4. `storage.health`.

Locate their shared result formatting/base classes first. Facts should be emitted at the package layer that already understands the result, not in Runtime.

### Fact design rules

- Runtime stays opaque to fact Type/Key values.
- Facts are bounded.
- Facts represent diagnostic/high-salience values, not a JSON mirror.
- Stable keys/types must be documented in the package tests/task notes.
- A fact from Partial evidence is still presented under the existing grounding rule as from partial evidence.
- Facts cannot claim absence when collection was unavailable/truncated.
- Prefer counts/statuses/identities/artifact references needed by reasoning and conditional plan steps.

Examples of candidate information — **not mandatory key names**:

`system.stability`:
- aggregate unexpected shutdown count;
- kernel crash count;
- hardware error count;
- display fault count;
- storage error count;
- discovered minidump count;
- time window/completeness where structurally available.

`system.crashes`:
- observed crash count;
- group count;
- presence/count of high-signal bugcheck/WHEA-style classes if the package already deterministically classifies them;
- dump artifact paths/identities where safe and already part of structured output.

`system.dump_analyze`:
- dump path/artifact identity;
- bugcheck code;
- debugger-attributed probable module/cause fields already deterministically parsed;
- analysis completeness/status.

`storage.health`:
- device count;
- unhealthy/degraded/unknown counts where the package's structured result supports those categories;
- device identities requiring follow-up;
- completeness/source availability.

Do not invent domain classifications that the current structured result does not support.

## Conditional-step compatibility

Add tests showing Facts survive JSON persistence and are usable by existing ADR-0050 conditional activation without Runtime knowing package-specific names.

Do not change `EvidenceFact` public shape unless an actual blocker is found.

## Tests

EvidenceRead:

- four/current-limit valid reads still work;
- exhaustion causes no raw read and no terminal RuntimeFailure;
- expected operational tool can still be proposed/executed after exhaustion;
- final response can still complete after exhaustion;
- malformed control call does not consume a successful-read slot;
- unsupported/not-offered remains bounded and audited;
- resume does not resurrect stale in-memory counters incorrectly;
- cross-task evidence id remains rejected.

Facts:

- each priority tool has focused fact tests;
- partial/unavailable data never yields false zero/absence facts;
- fact values are bounded and JSON-round-trippable;
- fact validation rejects malformed package output as today;
- grounding displays authoritative facts even when raw observation is compacted;
- no package-specific strings are added to `bOps.Runtime`.

## Non-goals

- no new generic evidence datastore;
- no semantic/vector memory;
- no increase of raw persisted output size;
- no Runtime parsing of package JSON;
- no package-specific reasoning rules in the system prompt;
- no change to mutation/verification semantics.

## Definition of Done

- EvidenceRead exhaustion is soft and non-terminal;
- protocol errors and valid read budget are not conflated;
- ordinary reasoning can continue after read exhaustion;
- priority diagnostic tools emit useful bounded facts supported by their existing structured data;
- Runtime remains package-agnostic;
- existing evidence/grounding/conditional tests plus new tests are green;
- full non-live solution regression passes.

## Implementation evidence (2026-10-11)

Verified evidence only. Commit and push facts are in the last subsection.

### Part A — EvidenceRead

**Counters (runtime-owned, per logical call).** `LogicalCallState.EvidenceReadAttempts` is replaced by `EvidenceReadBudget` (`bOps.Runtime/EvidenceReadBudget.cs`):

| Concept | Counter | Bound |
|---|---|---|
| valid served raw reads (`EvidenceReadResultCode.Success`) | `ServedReads` | `EvidenceRead.MaxAttempts = 4` (unchanged; **not** raised) |
| control requests that returned no evidence: malformed, unsupported `runtime.*`, not offered, not allowed, invalid id / cross-task / missing step / unavailable source / out-of-range, `EndOfEvidence`, and a read asked for after exhaustion | `ControlRejections` | `EvidenceRead.MaxControlRejections = 4` |

A rejected request never consumes a read slot. The state lives in the logical call, which a live attempt and a resumed attempt both start fresh, so a rebuilt call always begins with the full budget; nothing is carried in memory across calls and no persistence changed.

**Exhaustion is soft.** When `ServedReads` reaches 4: no further raw access; `runtime.evidence_read` is no longer offered for the rest of that logical call (the ContextOverflow K=0 rebuild and provider retries share the state, so they neither reset nor resurrect it); the step system prompt replaces the EvidenceRead instructions with the runtime-authored line "EvidenceRead budget exhausted for this reasoning call. Continue using the evidence already available. No further evidence reads are available in this call."; the fourth read's reply also ends with that line and `readsRemaining=0`. The tool surface is the `ExpectedTool` only (never the full catalog). The task continues: an `ExpectedTool` call is validated and executed normally and a final answer completes the task.

**Fifth read.** A model that still asks for a fifth read (typed call or legacy text directive) gets `EvidenceRead/v1 rejected: LimitExceeded.` plus the exhaustion line, an `EvidenceReadAuditEvent` with `ResultCode = LimitExceeded` (existing code, no contract change) and `ReturnedLength = 0`, no raw read, no `Failed/RuntimeFailure`, no synthetic step. It counts as one control rejection.

**Protocol bound.** The 5th rejected control request of one logical call (for example five malformed requests, or a model that keeps ignoring exhaustion) is a bounded protocol failure: `FailAsync` with `Failed/RuntimeFailure` and an explicit message. It is not read-budget exhaustion. The old "Evidence read limit exceeded" synthetic step is no longer persisted (`TaskResumePolicy.EvidenceReadLimitStepDescription` is kept only so tasks persisted before AF-04 still classify on resume). The replan path uses the same state and the same soft exhaustion; its protocol bound raises `EvidenceControlProtocolExceededException` (renamed from `EvidenceReadLimitExceededException`), also mapped to `FailAsync`. Initial planning is unchanged: EvidenceRead stays `NotAllowedInPhase` / `Malformed` with the existing single corrective re-ask.

**Protocol response.** The discriminator stays `EvidenceRead/v1`; the reply gained deterministic cursor fields, given only where certain: `Success; offset=…; returned=…; sourceLength=…; nextOffset=…; endOfSource=true|false; readsRemaining=N` and `EndOfEvidence; returned=0; sourceLength=…; endOfSource=true; readsRemaining=N`. `sourceLength` is the persisted source's UTF-16 length. No version change: nothing persisted depends on the reply text. TaskId is still never a model argument and cross-task ids are still `CrossTaskRejected`.

**Telemetry.** `bops.evidence_read.events_total{outcome=served|exhausted|malformed|unavailable}` and `bops.evidence_facts.emitted_total{bops.tool}` (a count only). No chunk, id, argument or fact type/key/value.

**Tests.** `EvidenceControlPlaneTests` (+9 `AF04_*`): four served reads then a refused fifth with the `ExpectedTool` still executed and the task completed; final answer after exhaustion; cursor metadata; malformed and invalid-evidence requests not consuming slots; repeated malformed requests as a bounded protocol failure; a model ignoring exhaustion stays bounded; budget fresh per logical call (a later step offers the control again); budget unit test. `HardenEightTests`: the three terminal-limit tests are rewritten (not deleted) to assert bounded reads, soft exhaustion and continuation, including non-reset across provider retry and context-overflow recovery, plus a replan-path soft-exhaustion test; raw persistence, evidence-id, cross-task, chunk-bound and overflow tests are unchanged.

### Part B — high-density EvidenceFacts

Facts are built in the package layer that already owns the result (the shared `*ToolBase` classes), so Windows and Linux share one contract and no OS-specific duplicate exists. The convention follows `system.events`: `Type = <tool>.<what>`, `Key` = stable identity, scalar values; absence is never a fact (no zero-count fact is emitted anywhere). The Runtime validates them with the existing `EvidenceFacts.Validate` (at most 16 facts, 4096 bytes, identifiers up to 128) and presents them under the result's own completeness (a count from a `Partial` result is a lower bound). Each projection drops whole facts to fit and never cuts a value.

| Tool (source) | Fact type → key (value) |
|---|---|
| `system.stability` (`StabilityFacts`, from the formatted `categories`) | `system.stability.category-count` → category wire name, e.g. `hardwareError` (int; only when the category is applicable, status `available` or `partial`, and count > 0); `system.stability.window-days` → `window` (int; only when at least one category fact exists) |
| `system.crashes` (`SystemCrashFacts`, from the in-window snapshot before the group limit and byte budget) | `system.crashes.observed-count` → `all`; `system.crashes.group-count` → `groups`; `system.crashes.kind-count` → a `CrashKinds` value; `system.crashes.kernel-code-count` → `kind=<kind>;code=<code>` (kernel kinds only, top 4); `system.crashes.dump-reference-count` → `all`; `system.crashes.kernel-dump-path` → `newest` (type `Path`: the newest kernel crash's recorded dump reference, at most 260 characters, never cut) |
| `system.dump_analyze` (`DumpAnalysisFacts`, from the formatted output) | `system.dump_analyze.analysis-status` → `status` (`complete`/`partial`/`invalid`); `.dump-path` → `analyzed` (`Path`); `.bugcheck-code` → `bugcheck`; `.debugger-attributed-module` → `module`; `.debugger-attributed-failure-bucket` → `failure-bucket`; `.symbols-status` → `symbols`. Nothing for `unavailable`; values over 256 characters are skipped. The debugger attribution stays qualified in the type; no fact claims a root cause |
| `storage.health` (`StorageHealthFacts`, over all matched devices, not only the returned rows) | `storage.health.devices-observed` → `all`; `storage.health.device-count` → the observed health value (Windows: healthy/warning/unhealthy/unknown; Linux: healthy/critical/unknown — no parity is invented); `storage.health.attention-device` → device identity (string value = its health; critical/unhealthy/warning only, top 6, most severe first, identities over 128 characters skipped) |

The keys above are semantically stable once published; they are also in the XML docs of the four static classes.

**Partial / unavailable.** A category or source that is unavailable, not applicable or not collected yields no count; a zero count yields no fact; a dump analysis that produced no evidence (not-found, access-denied, debugger-unavailable, timeout) yields no facts; storage `unknown` rows are counted as `unknown`, never as healthy; a device with partial reliability evidence makes the result `Partial`. Facts do not make a `Partial` result complete: the runtime still presents them under the result's completeness and limitations.

**Not parsed / not invented.** No fact parses prose or raw text, and no WHEA or other message-pattern class is derived (the crashes DTO has no such class, so none is emitted). Where a fact is read from the tool's own JSON (stability, dump) it uses fields the formatter just wrote, as the audit summary already does.

**Size.** On large fixtures the facts are far smaller than the raw result (tests: 400 stability records give raw output more than 10 times the fact bytes; 400 storage devices give raw output more than 4 times the fact bytes).

**ADR-0050.** `HighDensityFactsActivationTests` (Runtime.Tests, using real `SystemCrashesToolBase` and `SystemDumpAnalyzeToolBase` subclasses): `system.crashes` emits `system.crashes.kernel-dump-path/newest`; the fact persists through a `TaskState` JSON round trip; the conditional `system.dump_analyze` step activates and binds `path` from it; with only an application crash the step is skipped with no call; with unavailable collection no fact exists and the step is skipped. The grounding block carries the authoritative fact. `bOps.Runtime` names none of the tools, types or keys (guard below).

**Architecture guards.** `CoreNamesNoPackageTests` forbidden literals now include the four tool names and the new fact vocabulary (compiled-literal scan of Runtime, Policy, Audit and Memory). `EvidenceReadAndFactsBoundaryTests`: EvidenceRead is not an `ITool` and the registry reserves `runtime.`; no package project references Runtime, AgentFramework, Policy, Audit, Hosting or Memory; `bOps.AgentFramework` sources know no tool, registry, policy, approval, journal, fact or EvidenceRead types; the fact projections are static classes with no process, file or execute surface.

**Package tests.** `HighDensityFactsTests` (System.Core.Tests, 18) and `StorageHealthFactsTests` (Storage.Core.Tests, 7): complete result gives the expected facts; partial gives safe facts under a partial result; unavailable gives none; bounds and JSON round trip; raw vs aggregate parity; long path or identity skipped, not cut.

### Verification

- `dotnet restore bOps.slnx --locked-mode`: passed.
- `dotnet build bOps.slnx --configuration Release --no-restore`: **0 warnings, 0 errors**.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"`: **35 test assemblies, 0 failures, 5244 passed, 109 skipped**. Representative totals: Runtime **2067**, API **504**, CLI **124**, Architecture **174**, AgentFramework **42**, System.Core **387**, Storage.Core **26**, System.Windows **326** (3 skipped), Storage.Windows **23**. The skips are the existing platform/environment-dependent tests (Linux-only System/Storage/Service/Scheduler tests on this Windows host, Docker and Filesystem symlink tests), unchanged by AF-04.
- Regression coverage kept green in the same run: AF-01 broker, AF-02 mapping/profile/compaction, AF-03 governed summarization and summary authority, ADR-0042 grounding/limitations, ADR-0046 tool routing, ADR-0047 correction, ADR-0050 conditional activation, ADR-0051 mutation/reconciliation, resume and provider-failure containment, HARDEN-8 raw persistence/evidence-id/cross-task/chunk-bound/overflow suites.

### Commits and push

- `feat: make evidence read exhaustion non-terminal` (Part A)
- `feat: add high-density diagnostic evidence facts` (Part B)
- `docs: record AF-04 implementation evidence` (this packet and the additive ADR-0052 section 29)
- Push to `origin feat/af-04-evidence-cleanup` was authorized for this task (no pull request, merge, tag, release or force); it was executed after the final validation above and the resulting remote SHA is recorded in the operator report, because a commit cannot contain its own hash.

### Boundaries kept

`bOps.Abstractions` (including `EvidenceFact`, audit and result codes), persistence, MAF summarization (`TriggerRatio`, `RecentGroupsToPreserve`, pipeline order), provider routing, policy/approval/entitlement and the mutation journal are untouched. `appsettings.json` (a pre-existing local modification) is not part of any commit.

### Deviations and notes

1. **Protocol failure is still terminal.** The packet forbids ending the task on exhaustion; it does not say what a model that keeps sending rejected control requests gets. It stays a bounded `RuntimeFailure` (now via `FailAsync`, no synthetic step) after 4 corrected rejections, because an unbounded correction loop would be an abuse vector.
2. **`EndOfEvidence` is a rejection, not a served read.** It returns no evidence, so it spends no slot.
3. **No dedicated audit code for exhaustion.** `EvidenceReadResultCode.LimitExceeded` (existing) is reused so no public contract changes; its XML comment predates AF-04.
4. **Legacy text directive** is still served in step turns whether or not the typed control is offered (pre-existing); after exhaustion it is answered with the same exhaustion rejection.
5. **No WHEA or other "high-signal" class fact** for `system.crashes`, and no extra interpretation for stability categories: the DTOs do not classify them separately.
6. Superseded by review fix R2: the replan system prompt is rebuilt per request from the logical call state; the EvidenceRead invitation is replaced by the same runtime exhaustion feedback once the read slots are spent, so the replan no longer invites a read it will refuse.

### AF-04R/R1 implementation evidence (additive, non-normative)

Review fix R1 makes `ModelCallBudget` govern the whole logical model call, including context projection and MAF summarization. One internal monotonic `ModelCallDeadline` (`bOps.Runtime/ModelCallDeadline.cs`, on the broker's `TimeProvider`) is created when the logical call starts and is read by the projection-time remaining window, retry decisions, the fallback decision and every attempt timeout. `ModelInvocationContext.ForSummarization(deadline)` hands it to the nested summarizer broker (current-candidate or dedicated), which links it to its own local deadline; the more restrictive wins, so a summary, its retries and their delays can never run past the parent. Fallback re-projection keeps the same deadline. `MaxAttemptDuration`, fail-soft summarization, accounting and `bOps.Abstractions` are unchanged. Tests: `GovernedSummarizationTests` ("one logical-call deadline" section).

### AF-05 handoff

- Measure on real tasks whether `MaxAttempts = 4` and `MaxControlRejections = 4` suit the target models, and whether the cursor fields (`nextOffset`, `readsRemaining`) reduce wasted reads; `bops.evidence_read.events_total` gives the served, exhausted, malformed and unavailable rates.
- Measure fact value with `bops.evidence_facts.emitted_total` per tool, and whether grounding with the new facts keeps MAF-compacted history answerable without EvidenceRead.
- Whether a dedicated audit result code for exhaustion is worth a deliberate contract change. (The replan prompt already carries the exhaustion state: review fix R2 rebuilds it from the logical-call state, see deviation 6 above; nothing is left to decide there.)
- Extend facts to other tools only if measurements show repeated raw reads for the same values.
