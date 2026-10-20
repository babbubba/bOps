# ADR-0061 — Disclosing whether a `system.events` source filter names a real source: `sourceFilter`

Status: **Accepted — 2026-10-18, by the operator** (proposed the same day). Implementation follows §9.
Date: 2026-10-18
Base: worktree `bOps-knowbench01a`, branch `codex/know-bench-01a`, HEAD `06cd4aa` (one unrelated untracked file, `docs/benchmarks/bench00-maf-compatibility-report.md`, is not part of this work).
Clarifies: [ADR-0032](0032-bounded-cross-platform-system-events.md) (`system.events`, including its HARDEN-7 and HARDEN-9 amendments) and decision D-028 in [`agentic/06-decisions.md`](../../../agentic/06-decisions.md). It amends neither: ADR-0032 and D-028 are Accepted and keep their meaning (workflow rule: an accepted ADR is superseded, never edited to change its meaning). Where this ADR says "clarifies", it adds a disclosure the earlier text did not provide; it reverses nothing.
Preserves: the meaning of `status`, `complete`, `truncated` and `ToolResultCompleteness` (ADR-0032 HARDEN-7 amendment §6, ADR-0022 HARDEN-6); "unknown names are accepted and echoed" (HARDEN-9 §1); ADR-0042 §7 (a missing fact is never absence; the runtime derives no negative statement); every argument, range and default; the scan ceilings; no new privilege, no new process, no new tool.
Touches `bOps.Abstractions`: no. Runtime, Policy, Memory, Audit schema: no. Package-local contract in `bOps.Packages.System.Core` plus one collector change in each OS package.
No D-number is assigned and `agentic/06-decisions.md` is not edited (the convention ADR-0060 followed). If the operator wants a register entry on acceptance, the next free number is D-051 (D-050 is the last in this worktree).
Origin: finding PRA-M2 of the prompt/runtime audit (live scenario T03) and the operator's design decisions D1–D3 of 2026-10-18, recorded in §2.10.

## 1. Context

### 1.1 The defect

A `system.events` call with an exact `source` that names no real source returns `observedEvents: 0`, `status: complete`, `complete: true`, and the model reports "confirmed: no such events". In T03 the model asked for a short, plausible name of a hardware-error provider; the real provider name is longer. The tool answered the question it was asked (events whose source equals that string: none) completely and truthfully, and the answer was read as an answer to a different question (events of that diagnostic category: none).

Nothing in the result lets a reader tell "the name is right and the source was quiet" from "no source uses this name". That is the failure mode D-028 rejects for other arguments: *a wrong answer that looks right*. D-028 and ADR-0032 close it for gaps in what the host can read (reported, "never as an empty log") and for out-of-range values (rejected, "never clamped"). They say nothing about a syntactically valid name that matches nothing, and HARDEN-9 §1 explicitly accepts such a name ("whether a source exists on a host, or in the window, is not knowable before the read").

### 1.2 What the code does today (verified at `06cd4aa`)

- **Windows** (`WindowsSystemEventsTool`). `ResolveProvider(source)` enumerates `EventLogSession.GetProviderNames()` and returns the registered spelling, or `null`. `null` conflates "enumerated, not registered" with "enumeration failed" (`EventLogException`/`UnauthorizedAccessException` are swallowed). A registered name becomes a native `Provider[@Name='…']` predicate, applied before the 10,000-record scan ceiling. A name that is not registered adds **no** native predicate: the channel is scanned from newest to oldest, and the shared `SystemEventFilter` discards every non-matching record after the read. So an unregistered name is the most expensive request and the one most likely to be `truncated`. `ResolveExcludedProviders` enumerates the catalog a second time for `excludeSources`.
- **Linux** (`LinuxSystemEventsTool`). `source` is never a journald match; the shared filter compares it with the record's canonical `source` (`SYSLOG_IDENTIFIER`, else `_SYSTEMD_UNIT`, else `_COMM`, else `unknown`) **or** with its `unit` (`_SYSTEMD_UNIT`). There is no registry of identifiers or units to consult.
- **Both**: `SystemEventFilter.Matches` is the one definition of a match. `SystemEventFormatting.Format` computes `observed = Matched(snapshot, query)…` — the records that passed every filter — and only then groups, applies `limit` and the byte budget. `observedEvents` is `observed.Length`.
- **Completeness**: `EvidenceCompleteness` reads `complete`/`truncated`/`status` from the emitted JSON and ignores every other key. `complete: true` with zero events is typed `Complete`.
- **Facts**: `SystemEventFacts` emits a count only for a matched (source, event id) pair; it never emits a zero.

Verified on the operator workstation on 2026-10-18 with a read-only probe of `EventLogSession.GetProviderNames()` (1,278 names): classic sources such as `.NET Runtime` and `disk` are listed; `Microsoft-Windows-WHEA-Logger` is listed (it publishes to `System`, from `ProviderMetadata.LogLinks`); the abbreviations `WHEA` and `WHEA-Logger` are not; the catalog lookup is case-insensitive. The product code and the contract below name no provider; these names are only the evidence.

### 1.3 Consumers (verified)

No code outside `bOps.Packages.System.*` and their tests parses the `system.events` output. `bOps.Runtime`, Policy, the API, the CLI and the web UI see it only as the delimited tool-result text and as `ToolCallResult.Completeness` / `Facts`. The web specs and `ToolRoutingRegressionTests` use the tool name or a stub (`{"observedEvents":0,"events":[]}`) and read none of the new fields. The in-package readers are `SystemEventsToolBase.CreateAuditSummary` (reads named keys with `!`; an added key cannot break it), `EvidenceCompleteness` (named keys only) and the tests listed in §6. Schema 2 is in no release tag (`v1.2.0-preview.8` contains no `system.events`).

## 2. Decision

### 2.1 Principle

`complete` keeps answering one question: *was the filtered question read in full?* A source filter that matches no source is read in full and returns zero. A second, independent question needs its own answer: *does the requested name designate something that exists here, and did the records actually examined confirm it?* This ADR adds that second answer as two independent dimensions in one object, and adds nothing to completeness.

### 2.2 Contract

`sourceFilter` is a new top-level key of every `system.events` result, in both modes, placed immediately **after `excludeSources`** and before `coverage`. It is always present: `null` when the call has no `source` argument.

```json
"excludeSources": [],
"sourceFilter": {
  "requested": "WHEA",
  "matching": "exactCaseInsensitive",
  "matchedFields": ["source"],
  "registrationStatus": "notRegistered",
  "observedMatch": false,
  "caution": "noMatchSourceNotRegistered",
  "semantics": "Only records matching requested in matchedFields were kept. Zero matches does not show that nothing of that kind was logged: no source here may use that name, and other sources and logs were not queried."
},
"coverage": { … },
```

| Field | Type / values | Meaning |
|---|---|---|
| `requested` | string | The validated `source` argument in the caller's spelling (≤128 characters, the `source` character set). Never a spelling taken from the host. |
| `matching` | `"exactCaseInsensitive"` | The one comparison rule: `StringComparison.OrdinalIgnoreCase`, whole string, no prefix, substring, alias or similarity. A closed set with one member today; another value needs a new ADR. |
| `matchedFields` | array of strings, ordinal order | The result fields the name is compared with: Windows `["source"]` (the provider name; `unit` is always `null` there); Linux `["source","unit"]`. Names of output fields, as rows and groups show them. |
| `registrationStatus` | `registered` \| `notRegistered` \| `unknown` | What the host's own registry says about the **name**, independently of any record (§2.3). |
| `observedMatch` | boolean | Whether at least one record the collector read satisfied the whole query (§2.4). |
| `caution` | `null` \| `noMatchSourceNotRegistered` \| `noMatchSourceUnverified` | A deterministic, closed code, present only when the name was not confirmed by either dimension (§2.3). |
| `semantics` | constant string | The fixed reading of the object, identical in every result that has a `sourceFilter` (§2.9). Same pattern as `sampleMessageSemantics` (LIVE-04A). |

Without a `source`: `"sourceFilter": null`. The key order inside the object is the table order. All wire values are English identifiers; no value is derived from event text.

`schemaVersion` stays **2**. The key is additive and always present, no existing field changes meaning, no reader parses it, and schema 2 is unreleased (the HARDEN-9 precedent for the `excludeSources` echo). A reader that ignores `sourceFilter` sees exactly the previous result plus a key.

### 2.3 Semantics of the two dimensions and of `caution`

**`registrationStatus`** — a statement about the host's registry, never about events.

| Value | When | What it does **not** mean |
|---|---|---|
| `registered` | The platform could enumerate its source catalog and it lists a name equal to `requested` under `OrdinalIgnoreCase`. Windows only. | That the source logs to a channel this call queried, or that it logged anything in the window. |
| `notRegistered` | The catalog was enumerated, is non-empty, and lists no such name. Windows only. | That the name never logged: a provider uninstalled since, or a writer using an unregistered name, leaves records behind (§2.5). Nor that the name is a typo. |
| `unknown` | The name cannot be verified: always on Linux (no registry of identifiers or units exists); on Windows when the enumeration failed (`EventLogException`, `UnauthorizedAccessException`) or returned no names at all. | That the name is wrong. |

**`observedMatch`** — a statement about the records read, never about the registry. `true` is the only value that confirms the name from data: a record the host itself produced carries it.

**`caution`** is a pure function of the two dimensions (nothing else — not `truncated`, not `status`, not `coverage`):

| `observedMatch` | `registrationStatus` | `caution` | Reading for the model |
|---|---|---|---|
| `true` | any | `null` | The name is confirmed by observed records. |
| `false` | `registered` | `null` | The name is a real source here; nothing in the queried records matched. This is an ordinary empty result and is read under `complete`/`truncated`/`coverage` and `semantics` (§2.7). |
| `false` | `notRegistered` | `noMatchSourceNotRegistered` | The name matches no registered source and no record: likely a wrong or abbreviated name. A historical, unregistered name is possible. |
| `false` | `unknown` | `noMatchSourceUnverified` | Nothing confirms the name. |

`caution` is deliberately silent for `registered`: the name verification is sufficient there. The residual doubt of a registered source (§2.6, case 5) is carried by `semantics`, not by a code that would suggest the name is wrong.

The two dimensions are independent on the wire and in code: a Windows source can be `notRegistered` and `observedMatch: true` (historical records), `registered` and `observedMatch: false` (quiet), and Linux is `unknown` with either value.

### 2.4 Deriving `observedMatch` — from what the collector observed

`observedMatch` is `Matched(snapshot, query).Any()`: the same sequence that yields `observedEvents`, evaluated **before** grouping, `limit` and the byte budget. It is never read from `events`, `groups` or `returnedEvents`/`returnedGroups`.

Consequences, each a test (§6):

- `limit: 1`, an aggregate `limit` that cuts groups, and a `maxOutputBytes` that removes every row or group leave `observedMatch` unchanged, because they act after `observed` is computed.
- `observedMatch == (observedEvents > 0)` whenever `sourceFilter` is not `null`. The two are not independent probes; the field is kept (the contract the operator approved) because it places the reading next to `registrationStatus` and `caution`, is the typed input of `caution`, and costs a few bytes. Its definition is the *whole* query — source together with window, `minSeverity`, `eventId`, `channel`, `text` and `excludeSources` — and `semantics` says "that kind", not "that name".
- On Windows, when `requested` is registered, the collector never reads records of other providers (native predicate), so no source-only count exists; when it is not registered every record is read and filtered afterwards. A "source-only" definition of `observedMatch` would therefore mean different things on the two paths and on the two platforms; one field must not have two meanings (the HARDEN-9 §3 rule), and this ADR does not define it.

### 2.5 Platform behaviour

**Windows.** The collector reads the provider catalog **once per call** and uses that one read for `source` and for `excludeSources` (today it enumerates twice). The read has three outcomes: names, failure, empty. `registered` ⇢ the existing native `Provider[@Name='…']` predicate; `notRegistered` and `unknown` ⇢ no native predicate and the shared post-filter, exactly as today. The scan ceiling, the 20-second budget, the XPath construction and every `truncated` rule are unchanged. A failed enumeration is `unknown` and does not make a source `partial` (it does not reduce what was read). Historical providers: a name that is `notRegistered` whose records exist in the log gives `observedMatch: true`, `caution: null` — the data confirms the name. A historical name whose records lie beyond the scan ceiling gives `observedMatch: false` with `noMatchSourceNotRegistered` **and** `truncated: true`; the two statements coexist.

**Linux.** `registrationStatus` is always `unknown`; no `journalctl` invocation, field query or new switch is added to establish it. `matchedFields` is `["source","unit"]`. `observedMatch` is true when a record read matched on either field. The Linux tool changes only to declare `matchedFields`.

**Shared.** The snapshot (package-local `SystemEventSnapshot`, not an abstraction) carries the platform's `SourceRegistration` (default `Unknown`) and `SourceMatchedFields` (default `["source"]`). `SystemEventFormatting.Format` builds `sourceFilter` from the snapshot, the query and `observed`. The core of the runtime is untouched.

### 2.6 The cases the operator asked to evaluate

| # | Case | Result |
|---|---|---|
| 3 | Envelope budget | §2.8: worst case 3,058 bytes (raw) and 3,510 (aggregate) of 4,096. |
| 4 | Historical provider, no longer registered | `notRegistered`; records present ⇒ `observedMatch: true`, `caution: null`. Records absent in the window, or beyond the ceiling ⇒ `noMatchSourceNotRegistered`, plus `truncated` when applicable. The tool never says the provider "does not exist". |
| 5 | Registered provider that does not publish to the queried logs (the default Windows channels are `System` and `Application`; `ProviderMetadata.LogLinks` can name others) | `registered`, `observedMatch: false`, `caution: null`. The name is real; the channel queried may not carry it. `semantics` ("other sources and logs were not queried") is the disclosure. Checking `LogLinks` is a possible later refinement and is **not** part of this ADR (§3). |
| 6 | Catalog enumeration fails | `unknown`; no exception reaches the caller; `status`/`complete` unchanged; with no match, `noMatchSourceUnverified`. |
| 8 | Partial windows and limited scans | `observedMatch` describes only the records examined. `truncated: true` (ceiling, time bound, `limit`, byte budget), a source `partial`, or `coverage.state` `partial`/`unknown` mean the examined records are not all the records; `observedMatch: false` is then never read as "none exist", whatever `registrationStatus` says. Windows reach is `sources[].examinedFromUtc` (the instant the scan reached); Linux scans the newest 10,000 records and filters after, so a rare source in a long window reports `truncated`, as ADR-0032 already records. `caution` does not change with these; they are read from their own fields. |
| 9 | Aggregate output | `sourceFilter` is envelope, identical in both modes. `observedMatch` comes from the pre-grouping records (§2.4). `matchedFields` is a matching rule, not a grouping key: on Linux a group's `source` may differ from `requested` when the unit matched. Groups, `observedGroups`, `returnedGroups` and `messageDistribution` are unchanged. |

### 2.7 Completeness, facts, runtime and audit

- **D1 — completeness is untouched.** `registrationStatus`, `observedMatch` and `caution` never alter `status`, `complete`, `truncated`, `sources`, `coverage` or `ToolCallResult.Completeness`. A `notRegistered` or unverifiable name does **not** make the result `Partial`: degrading completeness would assert a read gap that did not occur and would blur two dimensions the operator asked to keep apart. A test proves that results differing only in registration state have identical completeness fields.
- **No inference of absence.** The tool does not deduce "no such provider" from zero events, and does not deduce "no problem" from the completeness of a filtered query. `complete: true` with `sourceFilter` means "complete for events whose source matches `requested`", exactly as it means "complete for the filtered question" with `excludeSources` (HARDEN-9 §5).
- **Facts.** `SystemEventFacts` is unchanged. No `EvidenceFact` carries `sourceFilter` and no zero-count fact is ever added; the runtime's limitations digest (ADR-0042) does not see `caution`. The field reaches the model only inside the tool result, which is the channel in which it is read (§2.9).
- **Audit.** The summary from `CreateAuditSummary` gains, when `sourceFilter` is not `null`, `sourceFilter: { registrationStatus, observedMatch, caution }`. It does not repeat `requested` (the redacted arguments already carry `source`) and never carries a message. Denials and rejections are audited as before. The audit schema is unchanged.
- **Untrusted data.** `sourceFilter` contains no text from any record or from the host's catalog: `requested` is the caller's own validated string, every other value is a member of a closed set or a constant. A malicious event cannot influence it. The catalog's names are used for comparison only and are never echoed.

### 2.8 Position and the 4,096-byte budget

`sourceFilter` is part of the envelope the byte budget never cuts, like `excludeSources`. The position (after `excludeSources`) also keeps it in the first part of a result that any later length-based truncation would reach last.

Measured by replicating the serialization with the largest legal values at the 4,096-byte minimum `maxOutputBytes`: 8 `excludeSources` entries (1,024 characters in all), a 128-character `source`, two default Windows channels with 256-character details, two coverage stores with the largest `logMaximumBytes`, no rows, `registrationStatus: notRegistered`, the longest caution code:

| Mode | Envelope before | `"sourceFilter": null` | Worst-case `sourceFilter` object | Worst-case envelope | Left for rows or groups |
|---|---|---|---|---|---|
| raw | 2,514 B | 2,534 B (+20) | 528 B | **3,058 B** | 1,038 B |
| aggregate | 2,966 B | 2,986 B (+20) | 528 B | **3,510 B** | 586 B |

A typical `sourceFilter` is about 390 bytes. Both worst cases are below the existing `4,096 − 512` assertion of `ExcludeSourcesTests`, and `SystemEventFormatting.Format` cannot reach its `InvalidOperationException` ("Event metadata exceeds…") because of this field. The consequence is stated, not hidden: at the 4,096 floor with **every** maximum present at once, 586 bytes remain in aggregate mode, so a group carrying a 512-character sample message may not fit; the budget then removes it, sets `truncated: true`, and `observedEvents`/`observedMatch` still report that records were found. Implementation proves these numbers with the real formatter; if the worst-case envelope exceeds 3,584 bytes, or any combination reaches 4,096, implementation stops and returns to architecture rather than shortening the field (the HARDEN-9 §4 rule).

### 2.9 Minimum guidance so that zero records is not read as certain absence

Three layers, from the one the model always reads to the one it may not see:

1. **In the result — the guaranteed channel.** `sourceFilter.semantics`, a fixed English constant (`SourceFilterEvidence.Semantics`, never derived from data, identical on both platforms, 203 bytes), present whenever `sourceFilter` is not `null`:
   > Only records matching requested in matchedFields were kept. Zero matches does not show that nothing of that kind was logged: no source here may use that name, and other sources and logs were not queried.

   Together with `registrationStatus`, `observedMatch` and `caution`, this is what must not be lost between the tool and the model. It names no domain, provider or diagnostic category (rule 1: the package names no provider).
2. **In the manifest — best effort.** One sentence added to the `system.events` description ("`sourceFilter` echoes the source filter with whether the name is registered, whether records matched and a caution; zero events for a name that no source here uses is not evidence that nothing was logged"), and one to the `source` parameter ("Use the exact, full provider, identifier or unit name; an abbreviated or guessed name matches nothing, and zero events then says nothing about the subject — see `sourceFilter`"). Optionally the same intent as the display-only `PlanningGuidance` of `source` (ADR-0060). The audit already showed that catalogue text can be shortened before the model sees it (PRA-M3), which is why layer 1 exists and layers 2–3 are not relied on.
3. **In the documentation.** `docs/system-events.md` explains the object, the table of §2.3 and the reading rule: a zero result is evidence about the filtered question only, and is read together with `complete`, `truncated`, `coverage` and `sourceFilter`.

The runtime adds no instruction and no heuristic. Whether Qwen then reads the object correctly is an empirical question this ADR does not claim to answer (§8).

### 2.10 Operator decisions this ADR implements, and what it excludes

- **D1.** `ToolResultCompleteness` is not degraded because a provider is unregistered or unverifiable (§2.7).
- **D2.** First version: no similar-provider suggestions, no aliases, no fuzzy matching, no mapping specific to any hardware-error provider. The catalog is used only to answer "is this exact name registered?" and is never listed back. The strings and codes of this contract name no domain.
- **D3.** `sourceFilter` immediately follows `excludeSources`; the envelope budget is verified (§2.8) and the structure tests are updated (§6).
- **Approved correction.** Two independent dimensions (`registrationStatus`, `observedMatch`) instead of one `verification` enum; `requested`, `matching`, `matchedFields` and a deterministic bounded `caution` retained.

## 3. Alternatives considered

**One `verification` enum merging registration and observation.** Rejected by the operator: it conflates "the host knows the name" with "records carried the name" and cannot express a historical provider (`notRegistered` with records) or a quiet registered one.

**Mark the result `Partial` when the name is unregistered or unverifiable.** Rejected (D1). Nothing was left unread; `Partial` would mean "something is missing" where the missing thing is a verification of the question, and typed consumers (runtime digest, audit) would be unable to tell the two apart. It would also make every Linux `source` query `Partial`, because Linux is always `unknown`.

**Reject a name that is not registered (a validation failure).** Rejected: HARDEN-9 §1 decided that existence is not knowable before the read; a historical provider's records are real evidence; and Linux has no registry to validate against.

**Suggest the nearest registered names, aliases or fuzzy matches** (the optional part of PRA-M2). Rejected for the first version (D2): it puts catalog contents and a similarity policy into the contract, invites the model to substitute a different name silently, and needs an ADR of its own for the ranking and the bound.

**A hardware-error-specific mapping.** Rejected (D2, rule 1): the package would name a provider and a diagnostic category.

**`observedMatch` as "a record whose source matched, ignoring the other filters".** Rejected (§2.4): not computable on the Windows native-filter path, so it would have two meanings.

**Make the observation independent of the filters by a second, filter-free probe** (a one-record read per channel to see which sources exist). Rejected: extra I/O against the 20-second budget for a field that `registrationStatus` and the data already cover, and it would broaden what a call reads beyond what the caller asked.

**Verify the name on Linux by listing identifiers or units** (`journalctl -F`, `systemctl list-unit-files`). Rejected: a new invocation per call, and what it would list is journal content — an observation, not a registration — and a command surface ADR-0032 deliberately kept fixed. Linux stays `unknown`.

**Check `ProviderMetadata.LogLinks` to say whether a registered provider publishes to the queried channels** (case 5). Not adopted now: one `ProviderMetadata` construction per call, a new failure mode, and a third verification dimension nobody has approved. Recorded as a candidate follow-up; it would be additive (a fourth `registrationStatus`-like value or a new field) and would need its own ADR.

**Carry the reading in a typed `EvidenceFact`, or push it into the runtime digest.** Rejected: facts never carry absence or zero (ADR-0042); a caution fact would make the runtime interpret package semantics; and the runtime would need to know what `source` means.

**Omit `sourceFilter` when there is no `source`.** Rejected: the present-but-`null` form costs 20 bytes and keeps one rule for every reader and every structure test ("the key is there; `null` means no source filter"), as `excludeSources` is always there (`[]`).

**Bump `schemaVersion` to 3.** Rejected: no field changes meaning, the schema is unreleased, and a bump would invalidate readers that lose nothing by ignoring the key.

**Put the explanatory sentence only in the manifest.** Rejected: the manifest text can be shortened on its way to the planner (PRA-M3); the result is what the model reads at the moment it interprets zero.

## 4. Consequences

- A zero-event answer to a filtered source carries, in the same object, whether the name is a real source here and whether any record confirmed it. The T03 shape — `observedEvents: 0`, `complete: true`, a name no provider uses — now reads `registrationStatus: notRegistered`, `observedMatch: false`, `caution: noMatchSourceNotRegistered` beside the constant that says zero is not absence.
- On Linux the new information is smaller by nature: `registrationStatus` is always `unknown`, and a zero result carries `noMatchSourceUnverified`. That is the honest content, not a gap in the implementation.
- Windows reads the provider catalog once per call instead of up to twice.
- The result grows by 20 bytes without a `source` and by about 390 bytes with one; the worst case is bounded (§2.8).
- `ToolCallResult.Completeness`, `Facts`, policy, approval, risk, verification, the manifest's numeric constraints and every provider schema projection are unchanged. No change to `bOps.Abstractions`, runtime, persistence or the audit schema.
- The tests that pin the top-level key list or the position of `excludeSources` change (§6).

## 5. Compatibility

| Surface | Effect |
|---|---|
| Arguments | None changed. Every call valid before is valid now. |
| `schemaVersion` | Stays 2; one additive, always-present key; unreleased schema. |
| Existing fields | None changes meaning; `complete` and `truncated` identical. |
| `bOps.Abstractions` | No change; `SystemEventSnapshot`'s new members are package-local. |
| Runtime, API, CLI, web | None reads the key (§1.3). |
| Provider schemas | Unchanged; only the `source` and tool descriptions gain a sentence, projected from the manifest automatically. |
| Windows/Linux manifest parity | Preserved (identical apart from `Platforms`). |
| Audit | Additive keys in the aggregate summary. |

## 6. Test plan

Tests first for every behaviour below; a failing test precedes each production change. The package discipline of `agentic/04-testing-rules.md` applies: pure and fake-collector tests in Core, real targets for the OS packages.

**`bOps.Packages.System.Core.Tests` — new `SourceFilterEvidenceTests`**
1. No `source` ⇒ `"sourceFilter": null`, key immediately after `excludeSources`, in raw and aggregate.
2. The full table of §2.3: 3 registration states × `observedMatch` true/false × both modes ⇒ exact `caution`, `matching`, `matchedFields`, key order, `requested` in the caller's spelling (mixed case preserved).
3. `observedMatch` before reduction: `limit: 1` over many matches; an aggregate `limit` that cuts groups; a 4,096-byte budget that removes every row/group — `observedMatch` true, `observedEvents` > 0, `truncated` true.
4. Property: for generated snapshots and queries, `observedMatch == (observedEvents > 0)`.
5. D1: snapshots identical except `SourceRegistration` ⇒ identical `status`, `complete`, `truncated`, `sources`, `coverage` and typed `Completeness`.
6. A `notRegistered` name with records ⇒ `observedMatch: true`, `caution: null`; a collection-truncated snapshot with no match ⇒ `observedMatch: false`, the caution from the table, `truncated: true` and `complete: false` unchanged.
7. Determinism: byte-identical output for identical input; constant `semantics`.
8. Untrusted data: a record whose `source`, unit and message carry an instruction-like string, or the requested name equal to it, leaves every `sourceFilter` value except `requested` unchanged; the object contains no record text.
9. Facts: `SystemEventFacts` output identical with and without `sourceFilter`; no zero-count fact for a no-match query.
10. Audit summary: contains the triple, never `requested` or a message; absent when `sourceFilter` is `null`.
11. Envelope: the worst case of §2.8 (128-character source, 8 maximal exclusions, long details, two stores) ≤ 3,584 bytes with zero rows, ≤ 4,096 with the budget cutting only rows or groups, in both modes; and the existing worst-case tests re-run with a `source`.

**Existing Core tests to update:** `HardenSevenContractTests` (the aggregate top-level key list at the `"excludeSources", "coverage"` assertion gains `"sourceFilter"`); `ExcludeSourcesTests` (the two envelope tests add a `source`); `HardenNineManifestTests` (description sentences, `source` constraints unchanged); `EvidenceCompletenessTests` (the key does not affect completeness); `SystemEventsContractTests`, `SystemEventFactsTests` and `SystemEventsAgentCasesTests` (key-set assertions and a no-match case read with `complete: true`).

**`bOps.Packages.System.Windows.Tests`** — new `WindowsSourceRegistrationTests`: the pure resolver ⇒ `registered` (case-insensitive), `notRegistered` (non-empty catalog, no name), `unknown` (injected failure with `EventLogException`, with `UnauthorizedAccessException`, and an empty catalog); one catalog read per call, shared with `excludeSources` (counting seam); the XPath is byte-identical to today for all three outcomes (`registered` ⇒ `Provider[@Name=…]`, otherwise none). Real Windows (`WindowsOnlyFact`): a name taken from the live catalog ⇒ `registered`; a generated name that cannot exist ⇒ `notRegistered`, `observedMatch: false`, the caution, and `complete`/`truncated` consistent with the scan; a registered name combined with an `eventId` it never uses ⇒ `registered`, `observedMatch: false`, no caution. `WindowsExcludeSourcesTests` keeps `ResolveAgainst`'s signature.

**`bOps.Packages.System.Linux.Tests`** — fake journal: identifier `x` and unit `x.service`; `source: x` and `source: x.service` ⇒ `observedMatch: true`, `matchedFields: ["source","unit"]`, `unknown`, no caution; an absent name ⇒ `false`, `noMatchSourceUnverified`; scan ceiling reached with no match ⇒ `truncated: true`, `observedMatch: false`; `JournalctlArguments` identical with and without the feature. Real journald on Linux (run from a `git archive` copy under WSL Ubuntu-24.04, per the repository's Linux validation note): an identifier read from the live journal ⇒ confirmed; a generated name ⇒ `noMatchSourceUnverified`.

**`bOps.Packages.System.Conformance`** — `SystemEventsConformance`: the envelope assertion checks the key position (`excludeSources` + 1) and the `null` form; a new conformance routine run on both operating systems covers no source, a present name discovered from a baseline row, and a generated absent name, asserting platform-specific `registrationStatus` (Windows `notRegistered`, Linux `unknown`) and the table. `ToolSchemaConstraintTests` (both providers): descriptions equal the manifest; no constraint change.

**Not run here:** no live model inference. A live re-run of T03 with the Local Agent after implementation is an operator-run check, and no gate is declared passed by this ADR.

## 7. Files the implementation will change

Production:

1. `src/packages/bOps.Packages.System.Core/SourceFilterEvidence.cs` — **new**: `SourceRegistration`, wire names, `Semantics` constant, the `caution` function, the `sourceFilter` JSON.
2. `src/packages/bOps.Packages.System.Core/SystemEventSnapshot.cs` — `SourceRegistration` and `SourceMatchedFields` init properties.
3. `src/packages/bOps.Packages.System.Core/SystemEventFormatting.cs` — emit `sourceFilter` after `excludeSources`, from `observed`.
4. `src/packages/bOps.Packages.System.Core/SystemEventsToolBase.cs` — audit summary.
5. `src/packages/bOps.Packages.System.Core/SystemToolManifests.cs` — descriptions (and the optional `PlanningGuidance` of `source`).
6. `src/packages/bOps.Packages.System.Windows/WindowsSystemEventsTool.cs` — single catalog read, tri-state, snapshot values.
7. `src/packages/bOps.Packages.System.Linux/LinuxSystemEventsTool.cs` — declare `["source","unit"]`.

Unchanged on purpose: `SystemEventFilter.cs`, `SystemEventQuery.cs`, `SystemEventsArguments.cs`, `SystemEventsLimits.cs`, `SystemEventFacts.cs`, `WindowsEventXPath.cs`, `JournalctlArguments.cs`, `JournalRecordParser.cs`, `EvidenceCompleteness.cs`, everything in `bOps.Abstractions`, `bOps.Runtime`, `bOps.Policy`.

Tests: `tests/bOps.Packages.System.Core.Tests/SourceFilterEvidenceTests.cs` (new), `HardenSevenContractTests.cs`, `ExcludeSourcesTests.cs`, `HardenNineManifestTests.cs`, `EvidenceCompletenessTests.cs`, `SystemEventsContractTests.cs`, `SystemEventFactsTests.cs`, `SystemEventsAgentCasesTests.cs`; `tests/bOps.Packages.System.Windows.Tests/WindowsSourceRegistrationTests.cs` (new), `WindowsSystemEventsTests.cs`, `WindowsExcludeSourcesTests.cs`; `tests/bOps.Packages.System.Linux.Tests/LinuxSystemEventsTests.cs`, `LinuxExcludeSourcesTests.cs`; `tests/bOps.Packages.System.Conformance/SystemEventsConformance.cs`; the two `ToolSchemaConstraintTests.cs` only if a description assertion needs it.

Documentation: `docs/system-events.md` (the object, the table, the reading rule, the example result), `CHANGELOG.md` (Unreleased), and, if the operator wants it, one line in `docs/operations/troubleshooting-playbook.md`. On acceptance only: this ADR's status line; optionally a "Clarified by ADR-0061" pointer in the ADR-0032 header and a D-051 register entry — both an operator choice, neither edits the meaning of an accepted record.

## 8. Residual risks

1. **The model may still ignore the object.** The contract puts the reading in the result, but a small model can skip a nested key. Only a live run can show whether Qwen now treats T03 correctly; if not, the next step is a runtime-side disclosure (a separate ADR under ADR-0042), not more fields.
2. **`observedMatch` is not an independent probe** (§2.4). With other filters present, zero matches can come from those filters; `semantics` covers it generically, and the name-level evidence is `registrationStatus`.
3. **Registered does not mean reachable.** A registered provider may publish to a channel the call did not query (case 5); the default Windows channels are only `System` and `Application`. Disclosed by `semantics`, not verified; `LogLinks` is the follow-up candidate (§3).
4. **`notRegistered` is not "wrong".** Unregistered writers and uninstalled providers exist; the cautions say "no match" and "unverified", never "does not exist", and the data wins (`observedMatch: true` clears the caution).
5. **Unregistered names remain the costly path on Windows.** They scan to the ceiling and are the likeliest to be `truncated`. This ADR does not change that (it would alter ceiling and completeness semantics); the caution and `truncated` are shown together.
6. **Linux gains little.** `unknown` is permanent there; the only strong Linux signal is a record that matched.
7. **Budget floor.** With every maximum at once and `maxOutputBytes: 4096`, aggregate mode may return no group (586 bytes left). Bounded and disclosed (`truncated`, `observedEvents`); the constant could be shortened if the operator prefers.
8. **Linux `source: unknown`.** `unknown` is also the canonical fallback source of a record with no identifier, unit or command name, so that name matches such records (`observedMatch: true`). Existing behaviour of the shared filter, unchanged.
9. **Catalog cost.** The 1,278-name enumeration (one per call, down from up to two) is outside the call's time budget; a pathological host could make it slow. Unchanged in kind from today.
10. **`excludeSources` gets no such disclosure.** A mistyped exclusion removes nothing, so it errs towards showing more, not less; it is echoed as before. Out of scope here.

## 9. Acceptance

Accepted 2026-10-18 by the operator. The implementation follows the order tests → Core → Windows → Linux → conformance → documentation, each step building warning-free under `TreatWarningsAsErrors`, and the Windows and Linux real-target tests listed in §6 are run on both operating systems before the change is called done. If any stop condition in §2.8 triggers, implementation stops and returns here.
