# ADR-0042 — Evidence reasoning and limitation disclosure

Status: Proposed (2026-10-02, HARDEN-9 architecture gate; independent architecture review and operator acceptance
pending)
Date: 2026-10-02

Governs the runtime part of HARDEN-9 of the V1.3.x reliability train
([packet](../../../agentic/_tasks/2026-09-25-v1.3x-harden-09-evidence-reasoning.md); plan
[`2026-09-25-v1.3x-reliability-hardening.md`](../../../agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md) §3 item 4,
finding F-22, hypothesis H-6). The `system.events` part (`excludeSources`) is the
[HARDEN-9 amendment of ADR-0032](0032-bounded-cross-platform-system-events.md#harden-9-amendment--proposed-2026-10-02),
proposed together with this ADR. It builds on, and does not restate, the ADR-0022 HARDEN-6 amendment
(`ToolResultCompleteness`, `ToolFailureKind`), the ADR-0032 HARDEN-7 amendment (modes, horizons, temporal coverage,
the stricter `complete`) and [ADR-0041](0041-typed-cross-platform-stability-evidence.md) (§6 evidence time, §7 identity
and correlation, §10 tool boundary).

## Context

In task 88f97dda the agent answered "why has this PC been freezing for months?" from a 0 % CPU sample, a memory reading
and a disk free-space figure, did not weigh them against the historical evidence it had partly collected, and did not
say that two of its sources were partial and one truncated. HARDEN-7 fixed the evidence itself; this ADR fixes how the
evidence is used.

### What HARDEN-7 left in place (verified at `46fbf47`)

- `system.events`: schema 2, `mode` `raw` (default) | `aggregate`; `windowMinutes` 1–10080 in both modes, `windowDays`
  1–180 only with an explicit `mode: aggregate`; cross-field rules in the shared System.Core reader; `coverage`;
  `sources[].examinedFromUtc`; record times with no `timestampKind`.
- `system.crashes`: schema 2, `mode` `aggregate` (default) | `raw`; `sinceDays` 1–180 in aggregate mode only; one record
  per crash, merged only through intersecting WER report GUIDs; `timestampKind` in every group key and row; a
  `BlueScreen` Report.wer `EventTime` is `reported`; `uncorrelatedCount`; fixed 65,536-byte budget.
- `system.stability`: schema 1, aggregate-only; eight fixed categories with `applicability` (`applicable`,
  `notApplicable`, `notCollected`) and a `status`; `null` for unknown counts, lower bounds for `partial`; a timeline per
  category and `timestampKind`; fixed 32,768-byte budget; no hint for another tool.
- `coverage` (all three): per-store `oldestAvailableUtc` and `state` `complete` | `partial` | `unknown`, directory stores
  always `unknown`; `complete = status complete AND NOT truncated AND coverage.state complete`; the typed
  `ToolCallResult.Completeness` is derived from that JSON inside the package (`EvidenceCompleteness`), so a retention gap
  is `Partial`.
- `ToolCallResult` (in `bOps.Abstractions`) carries `Outcome`, `FailureKind` and `Completeness`; `ToolCallAuditEvent`
  audits `Outcome`, `FailureKind` and `Completeness` for every call. There is **no** typed coverage field: a coverage
  mismatch reaches the runtime only as `Completeness.Partial`.
- The runtime system prompt (`AgentRunner.SystemPrompt`) contains only the tool-output-is-data rule. Tool output is cut
  to `Agent:MaxObservationCharacters` (default 4,000) before it enters the model history (rule C3), while the typed
  evidence tools emit up to 32,768 (`system.events` default, `system.stability`) or 65,536 bytes (`system.crashes`):
  with defaults the model sees only the head and tail of those results.

### What "evidence reasoning" means in bOps

It is **not** an inference engine. Deterministic code already establishes, labels and bounds the evidence (HARDEN-6,
HARDEN-7). What is missing is (1) a standing rule telling the model how to weigh and describe that evidence, (2) the
runtime telling the model, from facts only the runtime holds, which evidence was incomplete, failed, or shortened before
it reached the model, and (3) a final answer that states those limitations. The model interprets; the runtime makes the
limits impossible to miss and cheap to disclose; nothing is rewritten.

## Decision

### 1. Placement

HARDEN-9 is a combination of four bounded pieces, each where the boundary rules put it:

| Piece | Lives in | Why there |
|---|---|---|
| Evidence rule (§4) | `bOps.Runtime`, appended to the base system prompt | It is a standing instruction to the model; it is product- and platform-neutral, so the core may carry it (rule A1). |
| Evidence-limitations digest (§5) | `bOps.Runtime`, built from persisted steps | Only typed `ToolCallResult` fields and runtime facts are used; the core never parses package JSON (packet stop condition). |
| Final-answer disclosure check (§6) | `bOps.Runtime`, final-response path of the step loop | It changes the loop's final-response contract, which only the runtime owns (packet ADR prerequisite). |
| `excludeSources` (H-6 self-noise) | `bOps.Packages.System.Core` shared reader and filter; per-OS collectors | It is a `system.events` argument (ADR-0032 HARDEN-9 amendment). |

There is **no** new tool, no shared reasoning layer, no relation or confidence field in any package output, no
cross-tool correlation code, no runtime-initiated tool call and no `bOps.Abstractions` change. Windows and Linux share
everything except the collectors' native exclusion (§15).

### 2. Evidence model

Five classes. Deterministic code produces the first two; the model produces the rest and must label them.

| Class | Definition | Produced by | Authorized by | Not authorized by |
|---|---|---|---|---|
| **Observed** | What a tool result states: values, counts, groups, timelines, coverage, source status, typed time kinds — including what the tool computed deterministically under its accepted ADR (aggregation, exact-identity merges, buckets). | Packages | The tool's contract. | — |
| **Derived** | A statement that follows necessarily from observed values without any assumption: ordering of two occurrence times, an interval, a bound from a reported time (§8), "at least N" from a lower bound, which steps were incomplete (§5). | Runtime (the digest, §5); the model when it states it with its inputs | Arithmetic and order on values of compatible kinds (§8). | Values of incompatible kinds; summing overlapping evidence (§9). |
| **Hypothesis** | A possible explanation: temporal proximity, co-occurrence, frequency, similarity, a pattern across sources. | The model | Any observed or derived support, stated with what supports it, what contradicts it and what evidence would confirm it. | — (a hypothesis is never presented as observed or as a cause) |
| **Cause** | A causal relation stated as established. | The model | Only evidence that itself records the relation (a record that names the reason of what it describes — for example an unexpected-shutdown record carrying the bugcheck code that ended the session). | Correlation, temporal proximity or order, frequency, co-occurrence, absence of other evidence, partial coverage, or the number of hypotheses ruled out. |
| **Unknown** | What the evidence cannot say: not collected, unavailable, outside retained history, not applicable on this platform, a `null` count, the part of a result that was truncated or shortened. | Packages (statuses, `null`, coverage) and the runtime (digest) | — | Never reported as zero, absence or health. |

### 3. Deterministic code vs model

| Deterministic code (packages and runtime) | Model |
|---|---|
| Normalize, classify and bound evidence; establish exact identity; aggregate; bucket; label time kinds; report coverage, source status and completeness (HARDEN-7, unchanged). | Choose which evidence to collect and in which `mode`/horizon. |
| Remove caller-named noise sources and echo the exclusion (ADR-0032 HARDEN-9 amendment). | Relate evidence across tools and calls in time; form, rank and test hypotheses. |
| List, from typed fields only, every step whose evidence was incomplete, failed, or shortened before it reached the model (§5). | Explain each limitation in the final answer using what the tool itself reported (which source, which period, which items). |
| Check once that a final answer given under listed limitations contains the disclosure section, and ask once if it does not (§6). | Write the final answer, including the "Evidence limitations" section. |

**Explicitly forbidden inference** (the rule of §4 forbids it to the model; no code produces it): a `reported` or record
time presented as an occurrence time; proximity, order, frequency or co-occurrence presented as a cause; an empty,
partial, unavailable, truncated, shortened, excluded, `notCollected`, `notApplicable` or `null` result presented as zero,
absence or health; a lower bound presented as an exact count; counts added across tools whose descriptions say they
overlap; two records presented as the same incident without an identity the tool established; a single current sample
presented as a historical trend; anything before a store's `oldestAvailableUtc` presented as known; a descriptive local
date (`fileNameLocalDate`) presented as an occurrence time.

**Explicitly not in code:** no confidence score or percentage, no causal or relation field, no ranking of hypotheses, no
summary of findings, no post-processing of the model's text. The packet requires none, and each would put an unprovable
judgement behind a deterministic-looking field.

### 4. The evidence rule (system prompt)

A fixed paragraph appended to `AgentRunner.SystemPrompt`, so every plan, replan and step call carries it. It must state
each clause below in product-, OS-, tool- and symptom-neutral words; the wording is the implementation's, the clauses are
normative:

- **E1 Observation vs inference.** Report what tool results show as observations; label anything concluded beyond them
  as an inference or hypothesis.
- **E2 Current vs historical.** A reading of the current state is weak evidence about a past or intermittent problem;
  prefer evidence covering the period in question; a single sample is not a trend.
- **E3 Bounded evidence.** Evidence a result marks as partial, unavailable, truncated, shortened or covering less time
  than requested is a bounded observation; counts from it are minimums.
- **E4 Absence.** "Not found" proves absence only when the result is complete for the whole period asked; otherwise
  say "not observed in the evidence read" and why. Not collected, not available, outside retained history, excluded by a
  filter, and not applicable on this system are unknowns, never zero.
- **E5 Time.** Some times record when something happened, others when it was recorded or reported, possibly much later.
  A recording time shows only that the thing happened no later than that time; never present it as when it happened.
- **E6 Correlation.** Relate independent sources by time to form hypotheses. Closeness in time, co-occurrence,
  frequency or similarity never proves a cause. When tools say their evidence overlaps, one event may appear in both:
  do not add their counts.
- **E7 Cause.** State a cause only when the evidence itself records that relation; otherwise give the most likely
  explanations as hypotheses, with the evidence for and against and what would confirm them.
- **E8 Disclosure.** When the runtime lists evidence limitations, the final answer contains a short section headed
  exactly `Evidence limitations` (this heading in English whatever the answer's language) naming each affected step's
  tool and what that tool reported as partial, missing or cut. When the runtime lists none, the answer has no such
  section.

Constraints: at most 2,000 characters; no OS, product, provider, package, tool name, event source, event id or symptom
term (a deny-list test, §17); no field name of any package schema; no example drawn from the incident.

### 5. Evidence-limitations digest

A runtime-authored, deterministic list of the steps whose evidence the model must treat as limited.

**Input.** The task's persisted steps, all execution attempts, in step-index order. Only steps with a `ToolCall` and a
`Result` are considered; final-response steps, synthetic failure steps and verification results are not.

**A step is listed when any of the following holds** (typed fields and persisted step data only):

1. `Result.Completeness` is `Partial` or `Unavailable` (this includes every HARDEN-7 coverage mismatch, which the package
   folds into `Partial`);
2. `Result.Outcome` is not `Success` and `Result.FailureKind` is not `Validation` — an environment, internal, timeout,
   authorization (policy, entitlement, operator, envelope) or unclassified failure: evidence that was not collected;
3. the result succeeded and its persisted `Observation` does not contain its `Output` in full (ordinal) — the runtime
   shortened it under rule C3. Using the persisted step, not the current option value, makes this identical live and on
   resume.

`Validation` failures are not listed: the call broke the tool contract (including an unknown tool name), no evidence
collection was attempted, and the model already sees the error and corrects it (E2E-2). `Completeness.Unspecified` with
`Success` is not listed: the tool declared nothing, and E4 already forbids absence claims from it.

**Entry.** One line per listed step, from a fixed template:
`- step <index>: <tool> — <facts>`, where `<tool>` is the step's `ToolCall.ToolName` when it has at most 128 characters,
all of them letters, digits, `.`, `_` or `-` (the shape of a canonical tool name; unresolved names are `Validation`
failures and never listed anyway), and otherwise the literal `(tool name omitted)` — a pure function of the persisted
step, independent of what the registry holds later; and `<facts>` is, in this order and
separated by `; `: `completeness Partial|Unavailable`; `outcome <ToolOutcome>, failure <ToolFailureKind>`;
`observation shortened from <length of Output> characters`. Enum values are their C# names. An entry never contains
`Output`, `ErrorMessage`, `Observation` text, arguments, or any text a tool or the model produced.

**Order and bounds.** Ascending step index. At most **16** entries — the newest 16 listed steps — so a whole default
attempt (`MaxSteps` 15) always fits; when more steps qualify, one fixed line states how many earlier steps are not
listed. Each entry is at most 256 characters; the fixed text around the entries is at most 512 characters; the digest is
therefore at most 4,608 characters, independent of task length. Computation is O(steps) over at most
`MaxLifetimeSteps` (60) steps.

**Placement.** When non-empty, the digest is appended to the **step** system prompt (after the plan section), between
the fixed markers `<<<BOPS_EVIDENCE_LIMITATIONS>>>` and `<<<END_BOPS_EVIDENCE_LIMITATIONS>>>`, with one fixed sentence
saying it is authored by bOps from typed results, that each tool's own result says which sources, periods or items are
affected, and that E8 applies. It is runtime-authored text in the trusted turn, so it may contain nothing that came from
outside the runtime (rule S5): only step indexes, registry tool names, enum names and integers. `WrapToolOutput`
neutralizes the two new markers inside tool output exactly as it neutralizes the tool-output delimiters, so a tool cannot
forge a digest. Plan and replan prompts do not carry the digest (the replan digest is HARDEN-8's).

**Determinism.** The same persisted steps produce a byte-identical digest. It is rebuilt on every step call, so a resumed
attempt sees the same digest the interrupted one would have.

### 6. Final-answer disclosure

When a step call yields a non-empty final answer (after the existing empty-reply handling,
`EmptyFinalResponseRetries`) and the digest is non-empty, the runtime checks whether the text contains
`Evidence limitations` (ordinal, case-insensitive). If it does, the answer is accepted. If it does not, the runtime asks
**once**: the same request plus the model's draft answer as an assistant turn and one runtime-authored user turn with a
fixed instruction (restate the complete final answer with an `Evidence limitations` section covering the listed steps,
or call a tool if more evidence is needed). The re-ask uses the same tool view, is an ordinary audited model call
recorded in the step's `ModelCalls`, and is not a step. The draft and the instruction turn are not kept in the history
afterwards, live or on resume, exactly as for the empty-reply retry, so live and rebuilt histories stay identical
(ADR-0038).

| Re-ask result | Outcome |
|---|---|
| Non-empty final answer (with or without the section) | Accepted unchanged as the final answer; no second re-ask. |
| A tool call | Executed as this step's call; the loop continues, and a later final answer is checked again. |
| Empty reply | The original final answer is accepted. |
| Model-call failure (any `ModelFailureKind`, after ADR-0039 retries) | The original final answer is accepted; the failed call stays audited and recorded. Caller cancellation still propagates. |

Bounds and budgets: at most `Agent:EvidenceDisclosureRetries` re-asks per final answer — a new additive option, default
`1`, valid `0` (disabled) or `1`; the re-ask is skipped when the task's token budget is already used up or a delegated
role's meter reports no budget or time left (the checks the loop already applies); its tokens count as usual; its time is the ADR-0039 per-call budget. Since each re-ask either ends the task or
consumes a step through a tool call, the number of re-asks is bounded by the step budgets.

The runtime never rewrites, truncates, appends to or annotates the model's text, and never refuses a final answer: the
check can only add one model turn. Whether the section is present does not change the task status.

### 7. Coverage, completeness and negative evidence

Coverage and completeness stay distinct (ADR-0032 HARDEN-7 §5–§6). The reasoning consequences, which E3/E4 express
generically and the HARDEN-7 manifests express per tool:

| Evidence state | What may be said |
|---|---|
| `Completeness.Complete` (every source read, nothing cut, history reaches the request start) | Observed values as facts for the requested period; an empty result as absence **for that period and those filters**. |
| `Partial` — truncated (limit, byte budget, ceiling, time bound) | Observed values are true but incomplete; counts are lower bounds; absence cannot be claimed. |
| `Partial` — a source unreadable | As above, and the unreadable source is unknown. |
| `Partial` — `coverage.state: partial` | Only `[max(examinedFromUtc, oldestAvailableUtc), requestedToUtc]` per source was observed; before it, unknown — never "nothing happened". |
| `Partial` — `coverage.state: unknown`; directory stores | The reach of the history is unknown; absence cannot be claimed. A directory inventory describes the files present now. |
| `Unavailable` | Nothing is known; no negative inference at all. |
| `Unspecified` | The tool declared nothing; treat absence claims as not supported. |
| `notApplicable` / `notCollected` category, `null` count | Unknown, never zero (ADR-0041 §3, §5). |
| Observation shortened by the runtime | The model did not see all of the result; what it did not see is unknown to it. |
| Source excluded by `excludeSources` | Not looked at; never "no events from that source". |

A result that says it is partial is partial as a whole, even when an individual field (for example a source's
`examinedFromUtc` equal to the request start) looks complete (HARDEN-7 residual N-3).

**Negative evidence.** "No evidence found" and "evidence proves absence" differ. Only a `Complete` result supports
absence, and only for its window and filters. No HARDEN-9 component computes absence.

### 8. Timestamp semantics

HARDEN-9 preserves ADR-0041 §6 exactly and adds no time and no promotion:

- **`occurred`** — when the thing happened, by the source's documented semantics.
- **`reported`** — when it was recorded, processed or written; the thing happened **no later than** that instant.
  A `BlueScreen` Report.wer `EventTime` stays `reported`; Kernel-Power 41, minidump file times and `fileNameLocalDate` are
  never substitutes for an occurrence time.
- **Record times** (`system.events`, no kind) — the instant the OS recorded the record; for reasoning they are treated as
  `reported` unless the record is itself the event it describes.

| Comparing | Derivable fact | Not derivable |
|---|---|---|
| `occurred` A vs `occurred` B | Order and interval. | That one caused the other. |
| `reported` A < `occurred` B | A happened before B. | How long before. |
| `reported` A ≥ `occurred` B | Nothing about the order of occurrence. | That B preceded A, or that they were close. |
| `reported` A vs `reported` B | The order in which they were reported. | The order or proximity of occurrence. |
| Local calendar date vs any instant | Nothing (no time zone, no time of day). | Same-day claims. |

All instants are UTC from one host clock (local execution, D-001). bOps does not detect clock changes; second-level
proximity between different stores is approximate and only ever a hypothesis input.

### 9. Correlation vs causation

- **Identity** (two records are one thing) exists only where a tool established it under its ADR: `system.crashes`
  report-GUID merges; exact native record identity within one call (ADR-0041 §7). No identity key spans tools —
  `system.stability` carries no report ids and `system.events` no record ids — and HARDEN-9 adds none.
- **Temporal relation** between records of different results is the model's derived fact or hypothesis, always stated
  with both time kinds (§8). It is never a merge, never a count adjustment, and never presented as identity.
- **Overlap.** Evidence the tools declare overlapping (bugchecks and display live dumps in `system.stability` and
  `system.crashes`; one incident appearing in several groups or categories) may be described as "probably the same
  incident" as a hypothesis; counts are never added.
- **Causality** follows §2: only a record that itself states the relation establishes a cause.

The HARDEN-7 rule is unchanged and not weakened: crash records merge only through intersecting report GUIDs; HARDEN-9
introduces no correlation by proximity, name, code or similarity anywhere in code.

### 10. Aggregation and acquisition

HARDEN-9 acquires no evidence: the runtime never calls a tool on its own, and the digest uses only steps the model chose.
Aggregation uses only the HARDEN-7 `mode`; there is no `aggregate` argument and no second switch. When to use which is
tool knowledge and stays in the manifests (aggregate for "how often, since when" over long horizons; raw for the detail of
specific items within 7 days); the neutral rule only says to prefer evidence covering the period asked (E2) and to treat
bounded results as bounded (E3).

### 11. Bounds

| Item | Bound | Reason |
|---|---|---|
| Evidence rule | ≤ 2,000 characters, constant | Added to every model call; small next to the tool catalog. |
| Digest | ≤ 16 entries × 256 characters + 512 fixed = 4,608 characters | One default attempt fits; independent of task length. |
| Re-asks | ≤ 1 per final answer (`EvidenceDisclosureRetries` 0–1), one fixed user turn | Bounded extra cost; never a loop. |
| Relations, groups, candidates, confidence values computed by HARDEN-9 | 0 | None are computed. |
| Evidence records read by HARDEN-9 | 0 new; horizons and ceilings are the tools' own (HARDEN-7) | No `180 days × events × relations` product exists. |
| Time | Re-ask: ADR-0039 per-call budget; digest: O(steps ≤ 60) | — |
| `excludeSources` | ≤ 8 entries × 128 characters, ≤ 1,024 characters in total (ADR-0032 HARDEN-9 amendment) | — |

### 12. Security and privacy

- The digest contains only step indexes, registry tool names, enum names and integers; no tool output, error message,
  argument, path, payload or model text, so it cannot leak more than the audit already holds and cannot carry an
  injection into the trusted turn. Its markers are neutralized in tool output.
- The evidence rule is a constant; no tool output can alter it (rule S5).
- The re-ask instruction is a constant; the model's earlier text is never echoed into a runtime-authored turn.
- No new read capability, no dump access, no query surface; `excludeSources` only narrows what `system.events` returns
  and is bounded and echoed.

### 13. Audit and telemetry

The three kinds of statement stay distinguishable without new audit types:

- **Tool observation** — `ToolCallAuditEvent` (with `Outcome`, `FailureKind`, `Completeness`, HARDEN-6) and the
  persisted step.
- **Deterministic derivation** — the digest is a pure function of the persisted steps, whose typed fields are audited;
  it is reproducible and therefore not stored or audited again.
- **Model interpretation** — `ModelCallAuditEvent` for every call, including the disclosure re-ask, and the persisted
  final answer.

Telemetry: the step span carries `bops.evidence_limitations` (number of digest entries, when non-zero) and
`bops.evidence_disclosure_reask` (when a re-ask was made); never content. A call-kind field on `ModelCallAuditEvent`
(plan, replan, step, re-ask) remains the deferred follow-up of ADR-0014.

### 14. Public contract and compatibility

| Change | Classification |
|---|---|
| New tools | Not required. |
| `bOps.Abstractions` | Not required — no change. |
| Runtime system prompt (evidence rule) | Required — internal behaviour. |
| Digest in step prompts; disclosure re-ask | Required — loop final-response contract (this ADR; `agentic/01-architecture-rules.md` §C gains a rule with the implementation). |
| `Agent:EvidenceDisclosureRetries` (default 1) | Required — additive configuration key. |
| `system.events` `excludeSources` argument and `excludeSources` echo | Required — additive (ADR-0032 HARDEN-9 amendment). |
| `schemaVersion` of `system.events` | Not required — stays 2 (the amendment explains why). |
| `system.crashes`, `system.stability` contracts | Not required — unchanged. |
| Manifest changes | Required for `system.events` only (one parameter, one description sentence); constraint snapshot updated. |
| Provider schema projection | Not required — `excludeSources` is a plain `String` with `MinLength`/`MaxLength`, already projected. |
| Audit schema | Not required. |
| Typed coverage field, list parameter type, relation/confidence fields, persisted disclosure field | Deferred (each needs its own ADR; none is needed by HARDEN-9). |

### 15. Windows and Linux

Shared: the evidence rule, the digest, the disclosure check (all platform-neutral runtime code), and the
`excludeSources` contract, parsing, filter definition and echo. Platform-specific: Windows may exclude natively in the
XPath on the registered provider spelling and post-filters the rest; Linux post-filters after parsing (journald matches
cannot negate "identifier or unit"). The scan-ceiling consequence is the one `source` already has (ADR-0032).
`system.stability`'s `notApplicable`/`notCollected` categories are the platform asymmetry the rule's E4 handles; HARDEN-9
claims no new parity.

### 16. Boundaries with HARDEN-7 residuals and HARDEN-8

- **HARDEN-7 N-1 (BEX64 classification).** No effect on this architecture: an unclassified kind is still typed,
  observed evidence. Out of scope.
- **HARDEN-7 N-2 (`system.crashes` description says Report.wer `EventTime` is `occurred` without the `BlueScreen`
  exception).** Weak dependency: the model reads that description, but every row and group carries the authoritative
  `timestampKind`, and E5 tells the model to use the kind each result states. HARDEN-9's correctness does not depend on
  it. Minimal treatment: a one-sentence manifest-description correction; it may be done in the HARDEN-9 implementation
  only if the operator says so at review, as a separately listed item; otherwise it stays a HARDEN-7 follow-up.
- **HARDEN-7 N-3 (mid-read `EventLogException`: `Partial` while `examinedFromUtc` may equal the request start).** No
  effect: the digest uses `Completeness`, which is correct, and §7 makes a partial result partial as a whole. Out of
  scope.
- **HARDEN-8.** The digest is built from persisted steps, so compaction of the model-facing history does not change it,
  and a compacted step is not a limitation by itself. Steps are referenced by step index; HARDEN-8's evidence ids must be
  resolvable to it (or HARDEN-8's ADR-0014 amendment moves the digest to evidence ids). The evidence rule and the digest
  are fixed costs of every step prompt that HARDEN-8's budget must count. The 4,000-character observation budget against
  32–64 KiB typed evidence results is disclosed by HARDEN-9 (§5 rule 3) and fixed by HARDEN-8. HARDEN-9 does not touch
  `ReplanAsync`, compaction, evidence retrieval or `ContextOverflow`.

### 17. Test obligations

Deterministic (`FakeChatModel`, fake tools; Windows and Linux CI):

| # | Scenario | Expected |
|---|---|---|
| 1 | All evidence `Complete`, not shortened | No digest in any request; no re-ask; one model call in the final step. |
| 2 | `Partial` (coverage-partial-like result) | Next step prompt holds a delimited entry with index, tool name, `completeness Partial`. |
| 3 | `Unavailable` | Entry `completeness Unavailable`. |
| 4 | Failures: `Environment`, `Timeout`, `Authorization` (policy denied, operator rejected) | Entries with outcome and failure kind. |
| 5 | `Validation` failure (bad argument, unknown tool) | No entry. |
| 6 | Output longer than the observation budget, `Complete` | Entry `observation shortened from <n> characters`. |
| 7 | Tool output containing the digest markers or text resembling an entry | Markers neutralized in the tool turn; the digest is unchanged. |
| 8 | Digest content | Never contains `Output`, `ErrorMessage`, arguments or model text. |
| 9 | 20 qualifying steps | Newest 16 entries in ascending order plus the "not listed" line; size within bound; byte-identical on repeat. |
| 10 | Resume | The digest after resume equals the digest built from the same persisted steps live. |
| 11 | Final without the section, digest non-empty | Exactly one re-ask with the fixed turn; its final answer accepted; two `ModelCalls`, both audited. |
| 12 | Re-ask final still without the section | Accepted unchanged; no second re-ask. |
| 13 | Re-ask empty; re-ask model failure | Original final accepted, task `Completed`; failure audited. Cancellation propagates. |
| 14 | Re-ask returns a tool call | Executed as the step's call; the loop continues. |
| 15 | Final already contains the section; `EvidenceDisclosureRetries: 0`; token budget exhausted | No re-ask. |
| 16 | Evidence rule | ≤ 2,000 characters; contains each clause E1–E8; contains none of the deny-list (OS names, products, providers, package and tool names or name-shaped tokens, event sources and ids, symptom words). |
| 17 | E2E-3 (incident-like) | Partial and shortened evidence → digest delivered → scripted final without the section → re-ask → scripted final with an `Evidence limitations` section naming sources and coverage is the persisted answer. |
| 18 | E2E-3 negative | All complete → no digest, no re-ask; the scripted final has no section and is persisted unchanged. |
| 19 | "Event A precedes crash B by a few seconds" | No tool output and no runtime text contains a relation, cause or confidence field (contract snapshot); the rule contains E6/E7. The model's wording is checked only by an optional, non-gating `Category=LiveModel` scenario. |
| 20 | "No matching event found in partial history" | The step is listed `Partial` in the digest and the rule contains E4; the package result is `complete: false` (existing HARDEN-7 tests). |
| 21 | Architecture | Rule A1 still holds for the new runtime code and constants. |

`excludeSources` tests are listed in the ADR-0032 HARDEN-9 amendment.

## Alternatives considered

**A new reasoning or correlation tool** (for example one that reads other tools' results and emits relations).
Rejected: it would read task evidence through a generic accessor (HARDEN-8's territory and a stop condition there),
depend on other tools' schemas, and either correlate fuzzily — what ADR-0041 §7 forbids — or duplicate HARDEN-7's
exact-identity merge.

**Relation fields in package outputs** (`precededBy`, `coOccurred`, `possibleCause`). Rejected: within one tool, groups
and timelines already carry the distribution; across tools, the core would have to parse package JSON; and any
"possible cause" in a typed field turns a hypothesis into something that looks observed.

**Confidence scores.** Rejected: not required by the packet and not derivable without arbitrary weights.

**A typed coverage field on `ToolCallResult`** so the digest could say "coverage shorter than requested". Rejected for
HARDEN-9: a `bOps.Abstractions` change for information that `Completeness.Partial` already signals and the tool output
already explains; the digest points the model to it.

**Parse package JSON in the runtime to name sources and coverage in the digest.** Rejected: packet stop condition and
rule A1.

**Appending the limitations to the model's answer, or storing them in a new `TaskState` field.** Rejected: the first is
the post-processing the packet puts out of scope; the second is a `bOps.Abstractions` and UI change the packet does not
need. The digest is reproducible from the persisted steps whenever a consumer wants it.

**Digest only in the re-ask, not in step prompts.** Rejected: a compliant model could not include the section on its
first final answer, so every limited task would pay one extra call, and the model could not seek better evidence while
it still can.

**Digest as a tool-result turn.** Rejected: it is runtime-authored, not tool output; placing it among tool data would blur
rule S5's boundary in both directions.

**Re-ask until the section appears.** Rejected: unbounded; one re-ask is enough to make the requirement visible to the
model and to make E2E-3 test runtime behaviour rather than scripted text.

**Fail or refuse a final answer without the section.** Rejected: a correct diagnosis would be lost over formatting.

**Apply the rule only to diagnostic goals** (detected from the goal text). Rejected: a text heuristic in the core; the
rule is cheap and generic.

**Encode current-state vs historical as a manifest field.** Rejected: a `bOps.Abstractions` change; tool descriptions
already say what a tool reads, and E2 is generic.

## Consequences

- Every model call carries the evidence rule (≤ 2,000 characters); limited tasks also carry the digest (≤ 4,608
  characters) and may cost one extra model call at the end.
- Limitations the model could not see for itself — runtime shortening, failed and denied evidence calls — are always in
  front of it, from typed facts.
- E2E-3 tests runtime behaviour (digest and re-ask), not only scripted text; the model's prose quality remains a
  live-model concern.
- No `bOps.Abstractions`, policy, persistence, audit-schema or provider change; no new tool; `system.crashes` and
  `system.stability` unchanged.
- `agentic/01-architecture-rules.md` §C gains a rule for the digest and the disclosure check when this ADR is accepted and
  implemented.
- HARDEN-8 inherits the interface of §16.
