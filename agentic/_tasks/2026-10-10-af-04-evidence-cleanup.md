# AF-04 — EvidenceRead soft exhaustion and high-density EvidenceFacts

| | |
|---|---|
| Governing ADR | [ADR-0052](../../docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md), D-048 |
| Status | **Planned** |
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
