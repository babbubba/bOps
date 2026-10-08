# ADR-0042 — Evidence reasoning and limitation disclosure

Status: Accepted (2026-10-03, operator decision; independent architecture review of `90bde7e` CHANGES REQUIRED,
resolved in `02e3e19`; independent architecture delta review PASS WITH NON-BLOCKING FINDINGS — see "Operator
acceptance (2026-10-03)")
Date: 2026-10-02
Accepted amendment: "HARDEN-9 implementation-review amendment — delegated Diagnostic structured output" (2026-10-03,
at the end of this document) — no disclosure re-ask for the delegated Diagnostic role's structured JSON
reply; digest and evidence rule still apply.
HARDEN-11 gap: addressed by [ADR-0044](0044-request-dependent-delegation-authority-and-operability.md) §16–§17
(Accepted 2026-10-05, D-041) — typed evidence-limitation metadata in delegation results and the plan approval view;
strict `FindingsOf` (first-complete-object tolerance rejected)

Governs the runtime part of HARDEN-9 of the V1.3.x reliability train
([packet](../../../agentic/_tasks/2026-09-25-v1.3x-harden-09-evidence-reasoning.md); plan
[`2026-09-25-v1.3x-reliability-hardening.md`](../../../agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md) §3 item 4,
finding F-22, hypothesis H-6). The `system.events` part (`excludeSources`) is the
[HARDEN-9 amendment of ADR-0032](0032-bounded-cross-platform-system-events.md#harden-9-amendment--accepted-2026-10-03),
accepted together with this ADR. It builds on, and does not restate, the ADR-0022 HARDEN-6 amendment
(`ToolResultCompleteness`, `ToolFailureKind`), the ADR-0032 HARDEN-7 amendment (modes, horizons, temporal coverage,
the stricter `complete`) and [ADR-0041](0041-typed-cross-platform-stability-evidence.md) (§6 evidence time, §7 identity
and correlation, §10 tool boundary). Decision D-038 in `agentic/06-decisions.md` summarizes it.

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
- Persisted step layout (`AgentRunner.RecordAsync`, `RejectAsync`): a step on a resolved tool has `Description` equal to
  the manifest name and `ToolCall.ToolName` equal to it (the registry resolves names ordinally); every pre-execution
  rejection (policy, operator, entitlement, envelope, unknown tool) has `Description` `Denied`, and only the unknown-tool
  rejection is classified `ToolFailureKind.Validation`. `Result.Output` is the full tool output; `Observation` is
  `TruncateForHistory(Output)`, optionally followed by `\nVerification: …`; `WrapToolOutput` is applied only to the
  history turn and never persisted. The final answer is a step with `Description` `Final response`, read by
  `DelegationRoleData.FinalText`.

### What "evidence reasoning" means in bOps

It is **not** an inference engine. Deterministic code already establishes, labels and bounds the evidence (HARDEN-6,
HARDEN-7). What is missing is (1) a standing rule telling the model how to weigh and describe that evidence, (2) the
runtime telling the model, from facts only the runtime holds, for which steps the requested result was not obtained or
was only partially available to it, and (3) a final answer that states those limitations. The model interprets; the
runtime makes the limits impossible to miss and cheap to disclose; nothing is rewritten.

## Decision

### 1. Placement

HARDEN-9 is a combination of four bounded pieces, each where the boundary rules put it:

| Piece | Lives in | Why there |
|---|---|---|
| Evidence rule (§4) | `bOps.Runtime`, appended to the base system prompt | It is a standing instruction to the model; it is product- and platform-neutral, so the core may carry it (rule A1). |
| Evidence-limitations digest (§5) | `bOps.Runtime`, built from persisted steps | Only typed `ToolCallResult` fields and persisted step metadata are used; the core never parses package JSON (packet stop condition). |
| Final-answer disclosure check (§6) | `bOps.Runtime`, final-response path of the step loop | It changes the loop's final-response contract, which only the runtime owns (packet ADR prerequisite). |
| `excludeSources` (H-6 self-noise) | `bOps.Packages.System.Core` shared reader and filter; per-OS collectors | It is a `system.events` argument (ADR-0032 HARDEN-9 amendment). |

There is **no** new tool, no shared reasoning layer, no relation or confidence field in any package output, no
cross-tool correlation code, no runtime-initiated tool execution and no `bOps.Abstractions` change. Windows and Linux
share everything except the collectors' native exclusion (§15). One package manifest description is also corrected
(HARDEN-7 residual N-2, §16).

### 2. Evidence model

Five classes. Deterministic code produces the first two; the model produces the rest and must label them.

| Class | Definition | Produced by | Authorized by | Not authorized by |
|---|---|---|---|---|
| **Observed** | What a tool result states: values, counts, groups, timelines, coverage, source status, typed time kinds — including what the tool computed deterministically under its accepted ADR (aggregation, exact-identity merges, buckets). | Packages | The tool's contract. | — |
| **Derived** | A statement that follows deterministically from observed evidence under explicitly stated architectural preconditions: "at least N" from a lower bound; the steps the digest lists (§5); the occurrence bound of one event from its own `reported` time; the order or interval of two different events only under the clock preconditions of §8. | Runtime (the digest); the model when it states the statement together with its inputs and preconditions | Arithmetic and order on values of compatible kinds whose preconditions hold (§8). | Values of incompatible kinds; an order whose clock preconditions do not hold or are not shown (§8); summing overlapping evidence (§9). |
| **Hypothesis** | A possible explanation: temporal proximity, co-occurrence, frequency, similarity, a pattern across sources, an order that is not derivable. | The model | Any observed or derived support, stated with what supports it, what contradicts it and what evidence would confirm it. | — (a hypothesis is never presented as observed, derived or as a cause) |
| **Attributed cause** | A causal relation that a source record itself states (a record that names the reason of what it describes — for example an unexpected-shutdown record carrying the bugcheck code that ended the session). | The source; the model reports it | The record's own statement, reported as the record's attribution: "the record attributes Y to X". | Anything else: correlation, temporal order or proximity, frequency, co-occurrence, absence of other evidence, partial coverage, the number of hypotheses ruled out. |
| **Unknown** | What the evidence cannot say: not collected, unavailable, outside retained history, not applicable on this platform, a `null` count, the part of a result that was truncated or not visible to the model, an excluded source. | Packages (statuses, `null`, coverage) and the runtime (digest) | — | Never reported as zero, absence or health. |

**No independently established cause.** bOps has no product contract that verifies a causal fact, and HARDEN-9 adds
none. The model therefore never states "X caused Y" as its own conclusion. When a record attributes a cause, the model
says so as the record's statement ("the record states X as the reason for Y"); every other causal idea is a hypothesis.

### 3. Deterministic code vs model

| Deterministic code (packages and runtime) | Model |
|---|---|
| Normalize, classify and bound evidence; establish exact identity; aggregate; bucket; label time kinds; report coverage, source status and completeness (HARDEN-7, unchanged). | Choose which evidence to collect and in which `mode`/horizon. |
| Remove caller-named noise sources and echo the exclusion (ADR-0032 HARDEN-9 amendment). | Relate evidence across tools and calls in time; form, rank and test hypotheses. |
| List, from typed fields and persisted step metadata only, every step for which the requested result was not obtained or was only partially available (§5). | Explain each limitation in the final answer using what the tool itself reported — or say that the relevant evidence was not visible. |
| Check once, deterministically, that a final answer given under listed limitations contains the disclosure heading, and ask once for a restatement if it does not (§6). | Write the final answer, including the `Evidence limitations` section. |

**Explicitly forbidden inference** (the rule of §4 forbids it to the model; no code produces it): a `reported` or record
time presented as an occurrence time; an order between different events presented as a fact when the clock
preconditions of §8 are not shown; proximity, order, frequency or co-occurrence presented as a cause; a record's causal
attribution restated as the model's own causal conclusion; an empty, partial, unavailable, truncated, shortened,
excluded, `notCollected`, `notApplicable` or `null` result presented as zero, absence or health; any result presented as
proof that something did not occur; a lower bound presented as an exact count; counts added across tools whose
descriptions say they overlap; two records presented as the same incident without an identity the tool established; a
single current sample presented as a historical trend; anything before a store's `oldestAvailableUtc` presented as
known; a descriptive local date (`fileNameLocalDate`) presented as an occurrence time.

**Explicitly not in code:** no confidence score or percentage, no causal or relation field, no ranking of hypotheses, no
summary of findings, no post-processing of the model's text. The packet requires none, and each would put an unprovable
judgement behind a deterministic-looking field.

### 4. The evidence rule (system prompt)

A fixed paragraph appended to `AgentRunner.SystemPrompt`, so every plan, replan and step call carries it. It must state
each clause below; the wording is the implementation's, the clauses are normative:

- **E1 Observation vs inference.** Report what tool results show as observations; label anything concluded beyond them
  as an inference or hypothesis.
- **E2 Current vs historical.** A reading of the current state is weak evidence about a past or intermittent problem;
  prefer evidence covering the period in question; a single sample is not a trend.
- **E3 Bounded evidence.** Evidence a result marks as partial, unavailable, truncated or covering less time than
  requested, or that was not fully visible, is a bounded observation; counts from it are minimums.
- **E4 Absence.** No result proves that something did not happen. The strongest statement is that no matching records
  were observed in the readable sources covering the requested period and filters, and only when the result says it is
  complete; otherwise say that nothing was observed in the evidence read and why it is incomplete. Not collected, not
  available, outside retained history, excluded by a filter, and not applicable on this system are unknowns, never zero.
- **E5 Time.** Some times record when something happened, others when it was recorded or reported, possibly much later;
  a time without a stated kind is the time of the record. A recording time shows only that the thing happened no later
  than that time; never present it as when it happened. Order two different events as a fact only on one comparable
  clock, with no clock change or restart between them and a gap larger than the precision of the times — two seconds
  when a source does not state its precision; otherwise the order is only a hypothesis.
- **E6 Correlation.** Relate independent sources by time to form hypotheses. Closeness in time, co-occurrence,
  frequency or similarity never proves a cause. When tools say their evidence overlaps, one event may appear in both:
  do not add their counts.
- **E7 Cause.** Never state a cause as your own conclusion. When a record itself states a reason, report it as that
  record's statement; otherwise give the most likely explanations as hypotheses, with the evidence for and against and
  what would confirm them.
- **E8 Disclosure.** When the runtime lists evidence limitations, the final answer contains a section with the heading
  `Evidence limitations` — this exact English heading on its own line, whatever the answer's language; the section
  itself may be written in the answer's language. For each listed step it names the tool and what that tool reported as
  partial, missing or cut; when the step failed or the relevant evidence was not visible, it says that it was not
  visible rather than describing content that was not received. When the runtime lists none, no such section is
  required: do not add an empty or boilerplate one; a limitation the model judges material may still be disclosed.

Constraints:

- at most 2,000 characters;
- **no identifier-shaped tokens**: no token containing `.`, `_` or `/` between letters or digits, no token with an
  upper-case letter after its first character (camelCase or PascalCase identifiers), no token mixing letters and digits
  — so no package, schema, field, tool or provider identifier can appear;
- no name from a fixed deny-list of operating systems, products, providers, log stores, event sources and symptom words;
- ordinary English words — including `complete`, `partial`, `coverage`, `truncated`, `reported` — are allowed in their
  normal sense;
- the heading literal `Evidence limitations` is allowed (two ordinary words);
- no example drawn from the incident.

The digest template and the re-ask instruction (§5, §6) are runtime-authored texts outside this rule and may carry their
version identifiers.

### 5. Evidence-limitations digest

**Scope.** The limitations digest lists the steps for which the requested result was not obtained or was only partially
available — to the tool or to the model. That covers evidence reads and actions alike, so a failed non-`Read` action is
listed without any registry lookup or risk parsing.

**Input.** The task's persisted steps, all execution attempts, in ascending `Index` (globally monotonic across attempts,
ADR-0040). Only steps with a `ToolCall` and a `Result` are considered; final-response steps, synthetic failure steps and
verification results are not.

**A step is listed when any of the following holds** (typed fields and persisted step data only):

1. `Result.Completeness` is `Partial` or `Unavailable` (this includes every HARDEN-7 coverage mismatch, which the package
   folds into `Partial`);
2. `Result.Outcome` is not `Success` and `Result.FailureKind` is not `Validation` — an environment, internal, timeout,
   authorization (policy, entitlement, operator, envelope) or unclassified failure;
3. `Result.Outcome` is not `Success`, `Result.FailureKind` is `Validation`, and the step is not superseded (below);
4. the result is shortened: `Result.Outcome` is `Success`, `Result.Output` is neither `null` nor empty, and
   `(Observation ?? "").StartsWith(Result.Output, StringComparison.Ordinal)` is false.

**Shortening predicate (rule 4), against the real layout.** The persisted `Observation` of a successful step is either
`Output` itself, or `Output` followed by the verification suffix `\nVerification: …`, or — when rule C3 cut it — the
first two thirds of the budget, the marker `\n... [truncated N characters] ...\n`, and the last third. The comparison is
therefore "`Observation` starts with the full `Output`", not the reverse and not `Contains`; `WrapToolOutput` is not part
of the persisted value and is not involved. A `null` or empty `Output` is never shortened. Lengths are UTF-16 code units
(`string.Length`). The predicate uses only persisted data, so it is identical live and on resume and independent of the
current `MaxObservationCharacters`. (A false negative would need the tool output to contain the runtime's own truncation
marker at exactly the cut position; the predicate stays deterministic either way.)

**Validation supersession (rule 3) — the only supersession rule.** A resolved-tool `Validation` failure at step *i* is
superseded when a step *j* with `j > i` exists whose `ToolCall.ToolName` equals step *i*'s `ToolCall.ToolName` (ordinal)
and whose `Result.Outcome` is `Success`. Steps are compared by `Index` only, so a later success in the same execution
attempt or in a later one supersedes equally; an earlier success never supersedes a later failure; the superseding
step is itself listed if one of the other rules applies to it. The case it exists for: a model that calls a tool with
invalid arguments and corrects them is not reporting a limitation; a model that never succeeds is (for example an
invalid `system.crashes` call followed directly by a final answer is listed).

**Unknown-tool rejections.** A step with `Description` `Denied` and `Result.FailureKind` `Validation` is an unknown-tool
rejection: the runtime records exactly that combination only for `AuthorizationKind.UnknownTool` (`RejectionKind`).
It has no resolved name, so it is never superseded and is always listed; its tool label is the fixed token
`(unknown tool)` and the caller-supplied name is never used. This is accepted conservative behaviour: an unknown-tool
rejection stays listed even when the model later calls the intended tool successfully, because nothing persisted links
the two. The detection assumes that no registered tool is named `Denied`; the implementation reserves `Denied` as a
runtime description token (registration of a tool with that name fails), so the combination stays unambiguous.

**Non-validation failures are never superseded.** A step listed by rule 2 stays listed even when a later step on the
same tool succeeds: persisted steps carry no identity strong enough to prove that the later call obtained what the
failed one requested (arguments, window and scope may differ), and over-disclosure is preferred to a silently dropped
limitation.

**Entry.** One line per listed step, from a fixed template:
`- step <index>: <tool> — <facts>`. `<tool>` is `(unknown tool)` for an unknown-tool rejection; otherwise the step's
`ToolCall.ToolName` — a resolved, canonical name — when it has at most 128 characters, all of them letters, digits, `.`,
`_` or `-`, and the literal `(tool name omitted)` otherwise (defence in depth). `<facts>` is, in this order and separated
by `; `: `completeness Partial|Unavailable`; `outcome <ToolOutcome>, failure <ToolFailureKind>`;
`observation shortened from <Output.Length> characters`. Enum values are their C# names. An entry never contains
`Output`, `ErrorMessage`, `Observation` text, arguments, or any text a tool or the model produced.

**Order and bounds.** Ascending step index. At most **16** entries — the newest 16 listed steps — so a whole default
attempt (`MaxSteps` 15) always fits; when more steps qualify, one fixed line states how many earlier steps are not
listed. Each entry is at most 256 characters; the fixed text around the entries is at most 512 characters; the digest is
therefore at most 4,608 characters, independent of task length. Computation is O(steps²) in the worst case for the
supersession check over at most `MaxLifetimeSteps` (60) steps.

**Template and placement.** The template is versioned `EvidenceLimitations/v1`; the version string is the first line of
the digest, and any change of the fixed text or entry format is a new version and an amendment of this ADR. When
non-empty, the digest is appended to the **step** system prompt (after the plan section), between the fixed markers
`<<<BOPS_EVIDENCE_LIMITATIONS>>>` and `<<<END_BOPS_EVIDENCE_LIMITATIONS>>>`, with one fixed sentence saying it is
authored by bOps from typed results, that each tool's own result says which sources, periods or items are affected, and
that E8 applies. `WrapToolOutput` neutralizes the two new markers inside tool output exactly as it neutralizes the
tool-output delimiters, so a tool cannot forge a digest. Plan and replan prompts do not carry the digest (the replan
digest is HARDEN-8's).

**Determinism.** The same persisted steps produce a byte-identical digest. It is rebuilt on every step call, so a resumed
attempt sees the same digest the interrupted one would have.

### 6. Final-answer disclosure

**Trigger.** A step call yields a non-empty final answer — the *original answer* — after the existing empty-reply
handling (`EmptyFinalResponseRetries`), the digest is non-empty, `Agent:EvidenceDisclosureRetries` is `1`, and the
heading detector below finds no heading. With an empty digest nothing happens.

**Heading detector (deterministic).** The answer is split into lines (`\n`, a trailing `\r` removed). Fenced code blocks
are skipped: a line whose content, after at most three leading spaces, starts with at least three backticks or at least
three tildes opens a fence; the fence closes at the next line that, after at most three leading spaces, consists only of
at least as many of the same character followed by optional whitespace; an unclosed fence runs to the end of the text;
fence lines and everything inside are not candidates. A line indented by a tab or by four or more spaces (an indented
code block) is not a candidate. Each remaining line is normalized: trim whitespace; remove one leading ATX prefix of one
to six `#` followed by at least one space or tab, and trim; remove one trailing `:` and trim; remove one pair of
surrounding `**` or one pair of surrounding `__` (the same pair on both sides), and trim; remove one trailing `:` and
trim. The line is a heading when the result equals `Evidence limitations` under `StringComparison.OrdinalIgnoreCase`.
So `Evidence limitations`, `Evidence limitations:`, `# Evidence limitations` … `###### Evidence limitations`,
`**Evidence limitations**`, `**Evidence limitations:**`, `__Evidence limitations__` and `EVIDENCE LIMITATIONS` are
headings; `There are no Evidence limitations`, `` `Evidence limitations` ``, `## Evidence limitations ##`, and the phrase
inside a fenced block are not.

**Localization.** The heading stays English on purpose: it is a stable technical marker that makes enforcement
deterministic in every language. The section's content may be written in the language of the answer (an Italian answer
with an English heading is the expected form). This is a conscious UX trade-off, not an oversight.

**The re-ask.** At most **one** per task step that produced an original answer. The request is the step's request (same
system prompt with the digest, same history, same tool view — kept only so the history and provider schema stay
valid) plus the original answer as an assistant turn and one runtime-authored user turn with the fixed instruction
`EvidenceDisclosure/v1`, which says: restate the final answer and include the required `Evidence limitations` section;
keep the conclusions unchanged unless the limitations require qualifying them; do not start a new analysis; do not call
tools. A qualification caused by the limitations is allowed; a new, unrequested line of reasoning is not.

| Re-ask result | Outcome |
|---|---|
| A non-empty final answer in which the detector finds a heading | **Accepted**: persisted as the final answer instead of the original. |
| A non-empty final answer without a heading | Disclosure attempt failed: the **original answer** is persisted. |
| Any tool call (with or without text) | Disclosure attempt failed: the tool is **not executed**, authorized, audited as a tool call or recorded as unexecuted; the **original answer** is persisted. |
| An empty reply | Disclosure attempt failed: the **original answer** is persisted. |
| A model-call failure (any `ModelFailureKind`, after the ADR-0039 retries of that one logical call) | Disclosure attempt failed: the **original answer** is persisted; the failed attempts stay audited and recorded. |
| Caller cancellation | Propagates as for every model call (the task's cancellation semantics, not the re-ask's). |

**Guarantees.** The original answer is held by the runtime until the re-ask is resolved and is the persisted answer
unless the re-ask is accepted. The re-ask creates no step, consumes no step budget, triggers no replan, executes no
tool, never loops, and never changes the task status: the task ends `Completed` exactly as it would have without the
re-ask — including when the final answer came on the last step allowed by `MaxSteps` or `MaxLifetimeSteps`, and when
the re-ask's tokens take the task past `MaxTotalTokens` (they are counted in `TokensUsed`; the final-answer path does not
re-evaluate the budget, as today). The runtime never rewrites, truncates, appends to or annotates the model's text.

**Bounds and budgets.** `Agent:EvidenceDisclosureRetries` is a new additive option, default `1`, valid values `0`
(disabled) and `1`; any other value fails options validation at startup like the existing budget options. The re-ask is
skipped when the task's token budget is already used up or a delegated role's meter reports no budget or time left (the
checks the loop already applies). Its time is the ADR-0039 per-call budget.

**History.** The original answer as an assistant turn and the instruction turn exist only in the re-ask request; they are
not kept in the history afterwards, live or on resume, exactly as for the empty-reply retry, so live and rebuilt
histories stay identical (ADR-0038). A completed task is not resumable (ADR-0040), so no later turn follows.

### 7. Coverage, completeness and negative evidence

Coverage and completeness stay distinct (ADR-0032 HARDEN-7 §5–§6). The reasoning consequences, which E3/E4 express
generically and the HARDEN-7 manifests express per tool:

| Evidence state | What may be said |
|---|---|
| `Completeness.Complete` (every source read, nothing cut, history reaches the request start) | Observed values as observations for the requested period. For an empty result, at most: "no matching records were observed in the applicable, readable sources covering the requested window and filters" — never that the event did not occur. |
| `Partial` — truncated (limit, byte budget, ceiling, time bound) | Observed values are true but incomplete; counts are lower bounds; nothing about records that were not returned. |
| `Partial` — a source unreadable | As above, and the unreadable source is unknown. |
| `Partial` — `coverage.state: partial` | Only `[max(examinedFromUtc, oldestAvailableUtc), requestedToUtc]` per source was observed; before it, unknown — never "nothing happened". |
| `Partial` — `coverage.state: unknown`; directory stores | The reach of the history is unknown. A directory inventory describes the files present now. |
| `Unavailable` | Nothing is known; no negative statement at all. |
| `Unspecified` | The tool declared nothing; no negative statement is supported. |
| `notApplicable` / `notCollected` category, `null` count | Unknown, never zero (ADR-0041 §3, §5). |
| Observation shortened by the runtime | The model did not see all of the result; what it did not see is not visible to it. |
| Source excluded by `excludeSources` | Not looked at; never "no events from that source". |

A result that says it is partial is partial as a whole, even when an individual field (for example a source's
`examinedFromUtc` equal to the request start) looks complete (HARDEN-7 residual N-3).

**Negative evidence.** "No matching records observed" and "the thing did not happen" differ, and no bOps result
establishes the second: a complete read of the retained, readable records still says nothing about what was never
recorded. No HARDEN-9 component computes absence.

### 8. Timestamp semantics

HARDEN-9 preserves ADR-0041 §6 exactly and adds no time and no promotion.

**Per-event semantics.**

- **`occurred`** — when the thing happened, by the source's documented semantics.
- **`reported`** — when it was recorded, processed or written: for the same event, `occurred(A) ≤ reported(A)`. A
  `BlueScreen` Report.wer `EventTime` stays `reported`; Kernel-Power 41, minidump file times and the file-name date are
  never substitutes for an occurrence time.
- **Record times** (`system.events`, which exposes no `timestampKind`) — the instant the record was written. The model
  describes it as "the event record is timestamped at T" and never promotes it to an occurrence time, also not when the
  record seems to describe itself.

**Relations between different events** require the **clock preconditions**, all of them shown by the evidence:

1. both times come from one comparable clock domain (the same host's clock, which local execution gives, D-001);
2. both are normalized UTC instants (the typed tools' format; a local calendar date is never an instant);
3. no clock adjustment is known between them (no evidence of a time change), and **no restart lies between them** (no
   boot, shutdown or unexpected-shutdown evidence in the interval, and the interval is not one the evidence leaves
   uncertain);
4. the difference exceeds the coarser precision of the two timestamps; when a source does not state its precision, two
   seconds — the coarsest common file-time granularity — is assumed.

| Comparing (different events A and B, preconditions met) | Derived fact | Not derivable |
|---|---|---|
| `occurred` A vs `occurred` B | Order and interval. | That one caused the other. |
| `reported` A < `occurred` B | A happened before B. | How long before. |
| `reported` A ≥ `occurred` B | Nothing about the order of occurrence. | That B preceded A, or that they were close. |
| `reported` A vs `reported` B | The order in which they were reported. | The order or proximity of occurrence. |
| Record time A vs any time B | The order of A's **record** and B. | Anything about when what A describes happened. |
| Local calendar date vs any instant | Nothing (no time zone, no time of day). | Same-day claims. |

When any precondition is not met or not shown — the events span a restart, the clock domain is not shown comparable, a
clock adjustment is possible, or the gap is within the timestamp precision — the order is **not** a derived fact; it may
only be an input to a hypothesis, stated as such.

### 9. Correlation vs causation

- **Identity** (two records are one thing) exists only where a tool established it under its ADR: `system.crashes`
  report-GUID merges; exact native record identity within one call (ADR-0041 §7). No identity key spans tools —
  `system.stability` carries no report ids and `system.events` no record ids — and HARDEN-9 adds none.
- **Temporal relation** between records of different results is the model's derived fact (only under §8's
  preconditions) or hypothesis, always stated with both time kinds. It is never a merge, never a count adjustment, and
  never presented as identity.
- **Overlap.** Evidence the tools declare overlapping (bugchecks and display live dumps in `system.stability` and
  `system.crashes`; one incident appearing in several groups or categories) may be described as "probably the same
  incident" as a hypothesis; counts are never added.
- **Causality** follows §2: only a record's own statement, reported as that record's attribution.

The HARDEN-7 rule is unchanged and not weakened: crash records merge only through intersecting report GUIDs; HARDEN-9
introduces no correlation by proximity, name, code or similarity anywhere in code.

### 10. Aggregation and acquisition

HARDEN-9 acquires no evidence: the runtime never executes a tool on its own, and the digest uses only steps the model
chose. Aggregation uses only the HARDEN-7 `mode`; there is no `aggregate` argument and no second switch. When to use
which is tool knowledge and stays in the manifests (aggregate for "how often, since when" over long horizons; raw for the
detail of specific items within 7 days); the neutral rule only says to prefer evidence covering the period asked (E2)
and to treat bounded results as bounded (E3).

### 11. Bounds

| Item | Bound | Reason |
|---|---|---|
| Evidence rule | ≤ 2,000 characters, constant | Added to every model call; small next to the tool catalog. |
| Digest | ≤ 16 entries × 256 characters + 512 fixed = 4,608 characters | One default attempt fits; independent of task length. |
| Re-asks | ≤ 1 per original answer (`EvidenceDisclosureRetries` 0–1), one fixed user turn; no tool execution | Bounded extra cost; never a loop. |
| Relations, groups, candidates, confidence values computed by HARDEN-9 | 0 | None are computed. |
| Evidence records read by HARDEN-9 | 0 new; horizons and ceilings are the tools' own (HARDEN-7) | No `180 days × events × relations` product exists. |
| Time | Re-ask: ADR-0039 per-call budget; digest: over ≤ 60 steps | — |
| `excludeSources` | ≤ 8 entries × 128 characters, ≤ 1,024 characters in total (ADR-0032 HARDEN-9 amendment) | — |

### 12. Security and privacy (rule S5)

Rule S5 says tool output is data and must never alter runtime state or instructions. The digest does not breach it, and
the distinction is normative:

- **Raw tool-result text never modifies system or runtime instructions.** It reaches the model only inside the delimited
  tool-result turn.
- **Tool text never enters the digest.** No `Output`, `ErrorMessage`, `Observation` text, argument value or model text is
  copied into it.
- **Typed metadata the runtime has already extracted may only select fixed runtime-authored tokens.** A
  `ToolResultCompleteness`, `ToolOutcome` or `ToolFailureKind` value selects its enum name; integers are step indexes
  and `Output.Length`; the tool label is a resolved canonical name that passes a fixed shape check, or a fixed token.
  These select text; they never change the goal, the tool list, policy, budgets or any other instruction. A tool that
  declares `Partial` can at most cause a fixed limitation line and one bounded re-ask.

`agentic/03-security-rules.md` (S5) and `agentic/01-architecture-rules.md` (C7) state this general rule. Further:

- The evidence rule and the re-ask instruction are constants; no tool output can alter them.
- The model's original answer is placed in the re-ask only as an assistant turn, never inside a runtime-authored turn.
- The digest markers are neutralized in tool output, so a digest cannot be forged.
- No new read capability, no dump access, no query surface; `excludeSources` only narrows what `system.events` returns
  and is bounded and echoed.

### 13. Audit, persisted marker and telemetry

The three kinds of statement stay distinguishable without new audit types or fields:

- **Tool observation** — `ToolCallAuditEvent` (with `Outcome`, `FailureKind`, `Completeness`, HARDEN-6) and the
  persisted step. A tool call returned by a re-ask is not a tool call of the task and produces no `ToolCallAuditEvent`.
- **Deterministic derivation** — the digest is a pure function of the persisted steps, whose typed fields are audited;
  it is reproducible (versioned template `EvidenceLimitations/v1`) and therefore not stored or audited again.
- **Model interpretation** — `ModelCallAuditEvent` for every call, including the re-ask, and the persisted final answer.

**Persisted marker (existing field `PlanStep.Description`).** The final step's description takes one of three fixed
values:

| `Description` | Meaning |
|---|---|
| `Final response` | No disclosure re-ask was made (unchanged; every existing row keeps its meaning). |
| `Final response; evidence disclosure re-ask accepted` | A re-ask was made and its answer is the persisted one. |
| `Final response; evidence disclosure re-ask result not used` | A re-ask was made and failed; the persisted answer is the original. |

Every runtime consumer that locates the final answer (today `DelegationRoleData.FinalText`, which compares with
`Final response`) recognizes all three through one shared runtime predicate; this is an implementation item. The
predicate is normative: a step is the final-response step only when its `Description` is exactly one of the three
markers above (ordinal) **and** its `ToolCall` is `null`; a tool step whose description happens to equal a marker is
never a final response. The final
step's `ModelCalls` is reconstructed by logical call — a record with `ModelAttempt` 1 (or `null` in legacy rows) starts a
new logical call (ADR-0039): logical call 1 is the normal final-answer call; the following logical calls are the
empty-answer retries; when the description carries a re-ask marker, the **last** logical call is the
evidence-disclosure re-ask. A future model-call-kind field stays the deferred follow-up of ADR-0014.

**Telemetry:** the step span carries `bops.evidence_limitations` (number of digest entries, when non-zero) and
`bops.evidence_disclosure_reask` (`accepted` or `result_not_used`, when a re-ask was made); never content.

### 14. Public contract and compatibility

| Change | Classification |
|---|---|
| New tools | Not required. |
| `bOps.Abstractions` | Not required — no change. |
| Runtime system prompt (evidence rule) | Required — internal behaviour. |
| Digest in step prompts; disclosure re-ask; final-step `Description` markers | Required — loop final-response contract (this ADR; `agentic/01-architecture-rules.md` §C gains a rule with the implementation). |
| `Agent:EvidenceDisclosureRetries` (default 1, valid 0–1) | Required — additive configuration key. |
| `system.events` `excludeSources` argument and `excludeSources` echo | Required — additive (ADR-0032 HARDEN-9 amendment). |
| `system.crashes` manifest description (N-2) | Required — wording only. |
| `schemaVersion` of `system.events`, `system.crashes`, `system.stability` | Not required — 2, 2 and 1 unchanged. |
| `system.crashes`, `system.stability` result contracts | Not required — unchanged. |
| Provider schema projection | Not required — `excludeSources` is a plain `String` with `MinLength`/`MaxLength`, already projected; tests prove it. |
| Audit schema | Not required. |
| Typed coverage field, list parameter type, relation/confidence fields, persisted disclosure field, model-call kind | Deferred (each needs its own ADR; none is needed by HARDEN-9). |

### 15. Windows and Linux

Shared: the evidence rule, the digest, the disclosure check (all platform-neutral runtime code), and the
`excludeSources` contract, parsing, matching on the canonical `source`, and echo. Platform-specific: Windows excludes
natively in the XPath before the scan ceiling and applies the shared post-filter; Linux applies the shared post-filter
only (journald matches cannot be negated), so excluded records count towards its scan ceiling (ADR-0032 HARDEN-9
amendment §2–§3). `system.stability`'s `notApplicable`/`notCollected` categories are the platform asymmetry E4 handles;
HARDEN-9 claims no new parity.

### 16. Boundaries with HARDEN-7 residuals and HARDEN-8

- **HARDEN-7 N-1 (BEX64 classification).** No effect on this architecture: an unclassified kind is still typed,
  observed evidence. Out of scope.
- **HARDEN-7 N-2 — in scope, mandatory HARDEN-9 implementation item.** The `system.crashes` manifest description
  (`SystemToolManifests.Crashes`) says that a Report.wer `EventTime` is an occurrence time without the exception. The
  implementation corrects it so that it states that a `BlueScreen` / `kernel-bugcheck` Report.wer `EventTime` is
  `reported` — written after the restart — and not an occurrence time, while application and `LiveKernelEvent` report
  times stay `occurred`. Wording only: no argument, field or `schemaVersion` change (stays 2). Required with it: the
  manifest/description snapshot or assertion updated, Windows/Linux manifest parity, and the provider projection tests
  (OpenAI-compatible and Anthropic) showing the corrected description is projected unchanged.
- **HARDEN-7 N-3 (mid-read `EventLogException`: `Partial` while `examinedFromUtc` may equal the request start).** No
  effect: the digest uses `Completeness`, which is correct, and §7 makes a partial result partial as a whole. Out of
  scope.
- **HARDEN-8.** The digest is built from persisted steps, so compaction of the model-facing history does not change it,
  and a compacted step is not a limitation by itself. Steps are referenced by step index; HARDEN-8's evidence ids must be
  resolvable to it (or HARDEN-8's ADR-0014 amendment moves the digest to evidence ids). The evidence rule and the digest
  are fixed costs of every step prompt that HARDEN-8's budget must count. HARDEN-9 **detects and discloses** observations
  shortened under the 4,000-character budget (§5 rule 4) and **does not change** that budget; the context-budget economy
  and the projection strategy for full typed evidence stay HARDEN-8's. HARDEN-9 does not touch `ReplanAsync`,
  compaction, evidence retrieval or `ContextOverflow`. Order unchanged: HARDEN-7 → HARDEN-9 → HARDEN-8.

### 17. Test obligations

Deterministic (`FakeChatModel`, fake tools; Windows and Linux CI):

| # | Scenario | Expected |
|---|---|---|
| 1 | All evidence `Complete`, not shortened | No digest in any request; no re-ask; final step `Final response`. |
| 2 | `Partial` (coverage-partial-like result) | Next step prompt holds a delimited, versioned entry with index, tool name, `completeness Partial`. |
| 3 | `Unavailable` | Entry `completeness Unavailable`. |
| 4 | Failures `Environment`, `Timeout`, `Authorization` (policy denied, operator rejected) | Entries with outcome and failure kind. |
| 5 | Failed non-`Read` action | Listed like any other failure. |
| 6 | Non-validation failure followed by a success of the same tool | The failure stays listed. |
| 7 | Validation failure followed by a corrected successful call of the same tool (same attempt; later attempt) | Not listed (superseded) in both cases. |
| 8 | Invalid `system.crashes`-like call, never retried, then a final answer | Listed `outcome Failure, failure Validation`; re-ask triggered. |
| 9 | Unknown tool (including a name that looks like an instruction) | Listed as `(unknown tool)`; the raw name appears nowhere in the digest; never superseded. |
| 10 | Shortening: output longer than the budget; output within the budget; output with a verification suffix; `null` and empty output | Listed with `Output.Length` only in the first case. |
| 11 | Tool output containing the digest markers or text resembling an entry | Markers neutralized in the tool turn; the digest is unchanged. |
| 12 | Digest content | Never contains `Output`, `ErrorMessage`, arguments or model text; first line `EvidenceLimitations/v1`. |
| 13 | 20 qualifying steps | Newest 16 entries in ascending order plus the "not listed" line; size within bound; byte-identical on repeat. |
| 14 | Resume | The digest after resume equals the digest built from the same persisted steps live. |
| 15 | Final without heading, digest non-empty | Exactly one re-ask with the fixed `EvidenceDisclosure/v1` turn and the original answer as assistant turn; re-ask answer with heading persisted; `Description` `…re-ask accepted`; both calls audited. |
| 16 | Re-ask answer without heading; empty; model failure | Original answer persisted; `Description` `…re-ask result not used`; task `Completed`; failure audited. Cancellation propagates. |
| 17 | **Re-ask at the step cap returns a tool call** (final answer on the last step allowed by `MaxSteps`, and by `MaxLifetimeSteps`) | Original answer persisted; the tool is not executed (no tool audit event, no new step, no replan); task status `Completed`, unchanged; no `MaxStepsReached`, `ReplanLimitReached`, `PolicyBlocked` or `BudgetExceeded`. |
| 18 | Final already contains a heading; `EvidenceDisclosureRetries: 0`; token budget used up; delegated role meter without budget | No re-ask. |
| 19 | Invalid `EvidenceDisclosureRetries` (−1, 2) | Options validation fails at startup. |
| 20 | Heading detector | Accepts plain, `:`, `#`–`######`, bold, underscore-bold, case-insensitive, and an Italian answer with the English heading; rejects the phrase in prose, in inline code, in a ```` ``` ```` fence, in a `~~~` fence, in an indented code block, and with closing `#`s. |
| 21 | Delegation | `DelegationRoleData.FinalText` returns the persisted answer for all three final descriptions. |
| 22 | Evidence rule | ≤ 2,000 characters; contains each clause E1–E8; contains no identifier-shaped token and nothing from the deny-list; contains ordinary words such as `complete`, `partial`, `coverage`, `truncated`, and the wording "not visible". |
| 23 | E2E-3 (incident-like) | Partial and shortened evidence → digest delivered → scripted final without heading → re-ask → scripted final with an `Evidence limitations` section naming sources and coverage is the persisted answer. |
| 24 | E2E-3 negative | All complete → no digest, no re-ask; the scripted final is persisted unchanged. |
| 25 | "Event A precedes crash B by a few seconds" | No tool output and no runtime text contains a relation, cause or confidence field (contract snapshot); the rule contains E5–E7. The model's wording is checked only by an optional, non-gating `Category=LiveModel` scenario. |
| 26 | "No matching event found in partial history" | The step is listed `Partial`; the rule contains E4; the package result is `complete: false` (existing HARDEN-7 tests). |
| 27 | Regressions | Existing `system.crashes` GUID-identity tests and `timestampKind` tests (including `BlueScreen` `reported`) pass unchanged. |
| 28 | N-2 | The `system.crashes` description states the `BlueScreen`/`kernel-bugcheck` `reported` exception; manifest snapshot and provider projection tests updated; `schemaVersion` 2. |
| 29 | Architecture | Rule A1 still holds for the new runtime code and constants. |
| 30 | Heading detector, unclosed fence | A heading after an unclosed ```` ``` ```` or `~~~` fence is not found (the fence runs to the end). |
| 31 | Supersession order | A success **before** a validation failure of the same tool does not supersede it; the failure is listed. |
| 32 | Final-step predicate | A tool step whose `Description` equals a final-response marker is not treated as the final response (its `ToolCall` is not `null`); all three markers with `ToolCall` `null` are. |
| 33 | Final-step `ModelCalls` grouping | Records are grouped into logical calls at `ModelAttempt` 1 (or `null`); normal call, empty-answer retries and the re-ask are identified as in §13, including when the re-ask needed several attempts. |
| 34 | Evidence rule E5 | States the restart condition and the two-second fallback precision. |
| 35 | Reserved description | Registering a tool named `Denied` fails; unknown-tool rejections stay listed after a later successful call of the intended tool. |

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

**Executing a tool call returned by the re-ask** (the first proposal). Rejected by the independent review: the re-ask
could then consume the last step or budget and end the task `MaxStepsReached`, `ReplanLimitReached`, `PolicyBlocked` or
`BudgetExceeded`, losing a valid answer. The re-ask is a restatement only.

**Accepting a re-ask answer without the heading.** Rejected: it could replace a good answer with a worse one while still
not disclosing; the original is kept.

**Always excluding `Validation` failures from the digest** (the first proposal). Rejected by the independent review: a
model that never corrects an invalid call would silently lose that evidence.

**Superseding non-validation failures by a later success.** Rejected: no persisted identity proves the later call
obtained what the failed one requested.

**Substring search for the heading** (the first proposal). Rejected by the independent review: "There are no Evidence
limitations" or a code block would pass.

**A localized heading.** Rejected: enforcement would need a per-language vocabulary in the core; the fixed English
marker with localized content is deterministic.

**Re-ask until the section appears.** Rejected: unbounded. **Fail or refuse a final answer without the section.**
Rejected: a correct diagnosis would be lost over formatting.

**Apply the rule only to diagnostic goals** (detected from the goal text). Rejected: a text heuristic in the core; the
rule is cheap and generic.

**Encode current-state vs historical as a manifest field.** Rejected: a `bOps.Abstractions` change; tool descriptions
already say what a tool reads, and E2 is generic.

**A new persisted field for the re-ask.** Rejected for HARDEN-9: a `bOps.Abstractions` change; the final step's existing
`Description` carries a fixed marker and the `ModelCalls` order identifies the call.

## Consequences

- Every model call carries the evidence rule (≤ 2,000 characters); limited tasks also carry the digest (≤ 4,608
  characters) and may cost one extra model call at the end, which can never cost the original answer or change the
  task status.
- Limitations the model could not see for itself — runtime shortening, failed, denied and uncorrected invalid calls —
  are always in front of it, from typed facts.
- E2E-3 tests runtime behaviour (digest and re-ask), not only scripted text; the model's prose quality remains a
  live-model concern.
- No `bOps.Abstractions`, policy, persistence-schema, audit-schema or provider change; no new tool; `system.crashes`
  changes only its description and `system.stability` nothing.
- `agentic/01-architecture-rules.md` §C gains a rule for the digest and the disclosure check when this ADR is accepted and
  implemented; S5 and C7 already state the typed-metadata rule of §12.
- HARDEN-8 inherits the interface of §16.

## Independent review corrections (2026-10-03)

The independent architecture review of `90bde7e` returned CHANGES REQUIRED (HARDEN-9 implementation blocked). Resolved in
this text before any implementation:

- **R1** — the re-ask never executes a tool; any tool call, empty reply, failure or heading-less answer keeps the
  original answer; no step, replan, status change or loop (§6, test 17).
- **R2** — `Validation` failures are listed unless superseded by a later success of the same resolved tool (the only
  supersession rule); unknown-tool rejections are identified by persisted fields and shown as `(unknown tool)`; E8 no
  longer forbids a disclosure the model judges material (§4, §5).
- **R3** — a deterministic heading detector replaces substring search; the English heading is a deliberate, documented
  UX choice (§6).
- **R4** — `Derived` holds under explicit preconditions; ordering between different events requires the clock
  preconditions, including no restart in between; `system.events` record times are never promoted (§2, §8).
- **R5** — HARDEN-7 N-2 is a mandatory HARDEN-9 implementation item (§16).
- **R6** — D-038 was kept out of the decision register until the architecture was accepted (recorded as Accepted on
  2026-10-03).
- **N1** scope wording (§5); **N2** non-validation failures never superseded (§5); **N3** S5 distinction (§12, rules S5
  and C7); **N4** exact shortening predicate (§5); **N5** absence wording (§4 E4, §7); **N6** re-ask wording (§6); **N7**
  `excludeSources` precision (ADR-0032 HARDEN-9 amendment); **N8** persisted marker and template versions (§5, §6, §13);
  **N9** identifier-shaped deny-list and "not visible" (§4); **N10** test additions (§17); **N12** attributed causality
  (§2, §4 E7).

## Operator acceptance (2026-10-03)

Accepted by the operator on 2026-10-03 after the independent architecture review (`90bde7e`, CHANGES REQUIRED, resolved in
`02e3e19`) and the independent architecture delta review of `02e3e19` (PASS WITH NON-BLOCKING FINDINGS). Recorded as
decision D-038. The architecture is unchanged by the acceptance; the delta review's non-blocking findings are folded in
as clarifications and implementation obligations:

- **N1** — `Denied` is reserved as a runtime description token; registering a tool with that name fails (§5, test 35).
- **N2** — E5 states the two-second fallback precision (§4, test 34).
- **N3** — the failed-re-ask marker is `Final response; evidence disclosure re-ask result not used` (§13).
- **N4** — the final-step predicate requires an exact marker and `ToolCall == null` (§13, test 32).
- **N5** — the real-Windows test exercises the largest native XPath (ADR-0032 HARDEN-9 amendment, Tests).
- **N6** — unknown-tool rejections are never superseded; accepted conservative behaviour (§5).
- **N8** — tests 30–34 (unclosed fence, supersession order, final predicate, `ModelCalls` grouping, E5 wording) and the
  maximum-XPath test are implementation obligations.

HARDEN-9 implementation may start; HARDEN-8 follows it.

## HARDEN-9 implementation-review amendment — delegated Diagnostic structured output (Accepted 2026-10-03)

Status: Accepted 2026-10-03 (operator acceptance after independent amendment delta review: PASS; previous R1 resolved;
new blocking findings none; non-blocking findings none). The independent implementation review of `d322ebd` returned
CHANGES REQUIRED with blocker R1; the amendment review of `88c1a10` returned CHANGES REQUIRED on the limitation-wording
rule, addressed in `85dcc66`. This amendment narrows §5–§6 for one case and changes nothing else
in this ADR; where it and §5–§6 differ, this amendment governs that case only.

### Problem

§6 says: a non-empty digest plus a final answer without an `Evidence limitations` heading triggers one disclosure re-ask,
and a re-ask answer that has the heading is persisted instead of the original. The delegated **Diagnostic** role has a
structured role contract (ADR-0030 §2, §4; `DelegationRoleData.DiagnosticInstructions`): its final reply is **only one
JSON object** of the shape `{"findings":[…]}`, with no prose and no code fence, and `DelegationRoleData.FindingsOf` turns
it into `Finding`s that must cite recorded `Evidence` (ADR-0023). The two rules are incompatible: a valid Diagnostic reply
cannot contain an `Evidence limitations` heading line outside the JSON without breaking its own contract, so a Diagnostic
role that worked under limitations always triggers the re-ask, and an *accepted* re-ask replaces a valid payload with one
that no longer satisfies it.

The review reproduced it deterministically: delegated Diagnostic role → valid JSON with one finding → non-empty digest →
re-ask accepted → restated answer with text outside the JSON → `FindingsOf` yields 0 findings. Impact: the diagnosis is
silently emptied — the Diagnostic role's `SkillReport.Findings` is empty, and in a remediation run
`PlanApprovalRequest.Findings` no longer shows the operator why the plan is proposed. A safety-relevant input to a human
approval is lost by a mechanism meant to add information.

### Scope

Exactly one case: a final response produced by `AgentRunner` for a delegated task whose acting agent's role is
`AgentRoleKind.Diagnostic`. The predicate is `delegation is not null && delegation.Correlation.Agent?.Role ==
AgentRoleKind.Diagnostic` — a typed value the orchestrator sets (ADR-0030 §1), never a text heuristic on the goal or the
reply. It is **not** generalized to all delegated roles, to every JSON reply, or to other machine-readable output: the
Discovery role (whose final reply is a plain summary, ADR-0030 §2) and every ordinary task keep §6 unchanged. A future
role or contract with a structured final payload needs its own decision.

### Chosen rule

For the delegated Diagnostic role:

1. **No disclosure re-ask.** The §6 trigger is not evaluated and no re-ask call is made. The original final reply is
   persisted unchanged, with `Description` `Final response` (no re-ask marker), so `FinalText` and `FindingsOf` read
   exactly what the role produced.
2. **The digest still exists.** It is computed and placed in the role's step prompts exactly as in §5 (same listing
   rules, entries, bounds and markers). Only its closing sentence differs: the structured-role variant replaces "E8
   applies" with the instruction of rule 3. No field is added to the payload. Because §5 makes any change of the fixed
   text a new version, the template becomes **`EvidenceLimitations/v2`** for both variants; the ordinary closing
   sentence and the entry format are otherwise unchanged.
3. **The structured-role instruction (normative content of the v2 Diagnostic variant).** It must say, in substance:
   - keep exactly the required structured JSON payload;
   - add no prose, heading or code fence outside it;
   - for this Diagnostic response, this structured-output instruction **replaces** the separate `Evidence limitations`
     section requirement;
   - qualify only findings that a listed limitation materially affects;
   - never create or alter a finding merely to encode a limitation.

   The wording is the implementation's; the five points are normative. The delta review demonstrated architectural
   feasibility with a 498 UTF-16-character formulation. The implementation must verify its definitive Diagnostic
   variant against the §5 bounds: fixed text ≤ 512 UTF-16 characters and digest ≤ 4,608 UTF-16 characters.
4. **Limitation qualification rule (preserves ADR-0023).** A runtime limitation is not a finding. For the Diagnostic
   payload:
   - When a listed limitation **materially affects** a finding that is otherwise supported by recorded evidence, the
     finding's `summary` is qualified accordingly (for example "Repeated display faults were observed, but the
     event-log coverage was partial." — valid only when the finding is supported by its cited evidence and the partial
     coverage is materially relevant to it). The finding stays supported by the `evidenceIds` that already support it.
   - Do not create a finding solely to report a limitation (for example no finding "An unknown tool call occurred."
     because the digest lists an unknown-tool rejection).
   - Do not add, change or invent `evidenceIds` to carry a limitation.
   - Do not change `severity` solely to carry a limitation.
   - Do not attach a limitation to a finding it does not materially affect.
   - Do not drop an otherwise supported finding merely because an unrelated tool or evidence source was limited.
   - **A limitation that affects no finding is not represented in the Diagnostic JSON payload** — for example an
     unknown-tool rejection, a failed unrelated read, a shortened unrelated observation, or partial evidence that led to
     no finding. It is not forced into the payload. It remains in the persisted role task, from which the digest is
     reproducible, and is not visible in the structured delegation result until delegation gains a typed limitation
     channel (gap below).
5. **E8 precedence for the Diagnostic role only.** E1–E7 govern the Diagnostic role's reasoning unchanged (observation
   vs inference, current vs historical, bounded evidence, absence, time kinds and clock preconditions, no causality from
   correlation, attributed cause). E8's semantic obligation — handle limitations honestly — still applies; only its
   separate prose-section **form** does not apply to the Diagnostic JSON response: for `AgentRoleKind.Diagnostic` the
   `EvidenceLimitations/v2` structured-output instruction takes precedence over E8's separate prose-section requirement
   for that role's response. The common evidence-rule constant is identical for ordinary tasks, Discovery and
   Diagnostic; it is not changed by this docs-only amendment. If its E8 wording must state the precedence explicitly
   (for example "unless the runtime's limitation list instructs a structured reply"), that change belongs to the
   implementation fix, stays role-neutral and keeps one common rule. The role-specific direction lives only in the
   runtime-authored digest sentence selected by the typed predicate (rule S5, "Typed metadata is not tool text").
6. **Auditability is unchanged.** Every model and tool call is audited as before; the digest stays reproducible from the
   persisted role task (a `Delegated`-origin task in the task store); telemetry carries `bops.evidence_limitations` as
   usual and never `bops.evidence_disclosure_reask` for this role.

**Role scope (confirmed by the amendment review).** Discovery: prose summary, normal §6 re-ask. Diagnostic: structured
JSON, exempt. Remediation and Verification: no model final-answer path (ADR-0030 §2), so §6 never applies. No generic
"structured output" flag or new abstraction is introduced.

### Parent responsibility — and the gap it exposes

The disclosure obligation belongs to whatever layer produces user-facing text:

- **Ordinary tasks:** the `AgentRunner` final answer — §6 applies unchanged.
- **Delegated runs:** there is no parent *model*. The orchestrator is deterministic runtime code (ADR-0030 §2), there is
  no free-text channel between roles (ADR-0030 §4), and the run's user-facing outcome is the structured delegation result
  — roles, `Evidence`, `Finding`s, the plan, its approval request and the verification verdict — rendered by the API, CLI
  and UI (ADR-0030 §9). No component writes a prose answer for a delegated run, so there is no prose final answer on
  which §6 could be enforced.

**Gap (accepted, assigned to HARDEN-11).** The structured delegation result carries no typed limitation metadata today:
`DelegationRoleData.EvidenceOf` turns only successful Read steps into `Evidence` (failed, denied and shortened steps are
not visible as such), `Evidence` has no completeness field, and `SkillReport`, `PlanApprovalRequest` and the delegation
view show none. The limitations remain recoverable — the digest is a pure function of each role's persisted task — but
only those that materially affect a finding reach the operator, as a qualified finding summary (rule 4). Closing the gap
needs a new contract and therefore its own decision; none is invented here. The owner is **HARDEN-11 — delegation
operability**, which already owns `DelegationRoleData`, the delegation API/UI, readiness and the related structured
delegation contract follow-ups: HARDEN-11 must evaluate typed limitation metadata in delegation results and in the plan
approval view.

### Rejected alternatives

- **Re-ask the Diagnostic role, then accept the reply only if its findings are equivalent** (parse both replies with
  `FindingsOf` and compare). Rejected: it parses twice; it needs a new semantic-equivalence rule for findings (summary
  wording, evidence ids, severity) that does not exist; such a rule can hide changes it fails to detect; it is more
  complex than needed; and the role contract is already structured and sufficient — the re-ask adds nothing the contract
  can carry.
- **Make `FindingsOf` tolerant** (read the first complete JSON object and ignore the rest). Rejected as the fix: it would
  make the parser accept replies that break the role contract instead of preserving the conforming payload. Classified
  **FOLLOW-UP (defence in depth), owned by HARDEN-11**, not part of HARDEN-9; if adopted later it must not weaken the evidence-citation
  invariant of ADR-0023.
- **Exempt every delegated role or every JSON reply.** Rejected: broader than the conflict; Discovery's reply is prose,
  and ordinary tasks keep §6.
- **Add an `evidenceLimitations` field to the Diagnostic payload or to `Finding`.** Rejected here: a contract change
  (and, for `Finding`, a `bOps.Abstractions` change) that belongs to the gap's own decision.
- **Keep the re-ask but ask for the heading inside the JSON.** Rejected: the payload has no such field, and §6's
  detector works on lines, not on data.

### Test requirements

Added to §17 as rows 36–42; deterministic, `FakeChatModel`:

| # | Scenario | Expected |
|---|---|---|
| 36 (A) | Delegated Diagnostic role; a `Partial` tool result; final reply is valid JSON with one finding citing recorded evidence; digest non-empty | No disclosure re-ask (one model call in the final step); the original JSON persisted unchanged with `Description` `Final response`; `FindingsOf` returns exactly 1 finding; the role's step prompt carries the `EvidenceLimitations/v2` digest with the structured-role closing sentence. |
| 37 (B) | Same, through `DelegationRunner` | The Diagnostic role's `SkillReport.Findings` is non-empty (the finding survives). |
| 38 (C) | Same, with a remediation request reaching plan approval | `PlanApprovalRequest.Findings` contains the Diagnostic finding. |
| 39 (D) | Ordinary (non-delegated) task; digest non-empty; final answer without heading | The disclosure re-ask still occurs exactly as in §6 (proves the exemption is narrow). |
| 40 (E) | Delegated Discovery role; digest non-empty; plain-summary final reply without heading | §6 applies unchanged (re-ask made, outcome per §6); no other role or structured output changes behaviour. |
| 41 (F) | `EvidenceLimitations/v2` Diagnostic variant | Contract assertions (not necessarily byte-for-byte) that the closing instruction states: the separate prose section does not apply to this response; only materially affected, otherwise supported findings are qualified; no finding is created solely for a limitation; `evidenceIds` and `severity` are not modified solely for a limitation; unrelated limitations do not alter findings. The Diagnostic variant's fixed text is within 512 characters and the digest within 4,608. |
| 42 (G) | Common evidence rule | The evidence-rule text in the system prompt is identical for an ordinary task, a Discovery role and a Diagnostic role; the Diagnostic exception exists only in the final-disclosure handling and the v2 closing sentence, never as a separate taxonomy. |

The existing digest tests (§17 rows 2–14) and the evidence-rule test (row 22) move to `EvidenceLimitations/v2`.

### Implementation obligations after acceptance

- **`agentic/01-architecture-rules.md` §C rule 9** (added by the HARDEN-9 implementation on `d322ebd`; it does not exist
  at this branch's base `b1b07bb`, so it is not edited here) currently says that every final answer under a non-empty
  digest without the heading is re-asked. The implementation fix must add the typed exception: except the final
  response of a delegated `AgentRoleKind.Diagnostic` role, whose structured JSON payload is never re-asked, and must name
  `EvidenceLimitations/v2`.
- **`docs/system-maintenance.md`** does not describe the disclosure re-ask (checked at `b1b07bb` and `d322ebd`); no
  update is required there.
- **Runtime:** the typed predicate, the v2 closing-sentence variant with the five points of rule 3, and — only if needed
  to state E8 precedence — a role-neutral wording change of the common evidence rule (rule 5).
- **Tests:** rows 36–42, plus the v2 updates of the existing digest and evidence-rule tests.

### Compatibility impact

- **Runtime:** one typed predicate on the final-response path and one closing-sentence variant of the digest; template
  version `EvidenceLimitations/v2`. `Agent:EvidenceDisclosureRetries` keeps its meaning and never enables a re-ask for the
  Diagnostic role.
- **Delegation:** the Diagnostic role contract (ADR-0030 §2, §4; `DiagnosticInstructions`) is preserved exactly;
  `FindingsOf` is unchanged.
- **API, schemas, persistence, audit schema:** no change, no new field; the final-step `Description` vocabulary of §13 is
  unchanged.
- **`bOps.Abstractions`:** no change.

### Decision record

`agentic/06-decisions.md` says that a decision is changed by an ADR, never by an edit, and that its entries are Accepted.
D-038 is therefore unchanged. D-039 records this amendment directly as Accepted: disclosure enforcement (§6)
applies to user-facing prose final answers, not to the delegated Diagnostic role's structured JSON payload; the digest
and the evidence rule still apply to that role.

### Implementation-review non-blocking findings (follow-up only, not part of this amendment)

- **N1** — the evidence rule should state explicitly "never present a reported time as when it happened".
- **N2** — heading-indentation edge case: a line indented by a space followed by a tab.
- **N3** — the logical grouping of a final step's `ModelCalls` has no production consumer yet.
- **N4** — test gaps for the Windows native exclusion and for the delegated and lifetime-cap re-ask paths.

Only R1 is architecture-blocking; these stay with the implementation follow-up.

## Amendment — terminal fallback after a failed correction (PRE-3B2)

A failed disclosure re-ask may fall back only to an already-valid user-facing original response. An invalid protocol/control artifact is never a valid fallback. If bounded terminal correction fails, the task fails.

- **Valid original** — non-empty prose that is not a protocol/control artifact (`TerminalProtocolArtifact`). When it lacks the disclosure heading, the single bounded re-ask runs. A reply is adopted only if it is itself a valid terminal candidate (non-empty, not an artifact, not a tool call) and carries the heading. Any other outcome — timeout, provider failure, empty reply, heading-less reply, tool call, artifact — keeps the original byte-for-byte; the step is marked `Final response; evidence disclosure re-ask result not used` and the task is `Completed`.
- **Invalid original** — a protocol/control artifact or an empty/whitespace response is not a candidate answer and is never persisted or used as a fallback. It gets the one bounded terminal correction (no new step, no extra retry budget). A valid correction proceeds normally (including the disclosure re-ask if it lacks the heading); a failed, empty or repeated-artifact correction ends the task `Failed` (`RuntimeFailure` for an artifact, `EmptyResponse` for an empty reply, the model failure kind for a provider error).
- No retry, model-call timeout or provider policy changes: the re-ask budget stays `EvidenceDisclosureRetries = 1`.
