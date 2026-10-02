# ADR-0032 — Bounded cross-platform system events (`system.events`)

Status: Accepted
Date: 2026-09-21
Amended by: HARDEN-7 amendment (2026-10-02, at the end of this document) — aggregate and raw modes (`system.events`
keeps raw as its default, `system.crashes` defaults to aggregate), aggregate-only long horizons, temporal coverage
metadata, schema 2 for both tools; the new `system.stability` tool is
[ADR-0041](0041-typed-cross-platform-stability-evidence.md).

## Context

A diagnosis that stops at "the service is down" cannot say why. The reason is usually in the operating
system's own log: a service that failed to start, the kernel killing a process for memory, a disk or
filesystem error, a driver fault, an application crash. The original design had a system-log capability;
it was lost in the V0.11 consolidation. V1.3-A restores it as one read-only tool.

Both operating systems already keep this data in a structured store (Windows Event Log, journald), and
both stores are large, shared with other identities and full of text that anyone able to log can write.
The tool therefore has to be bounded in time, count and bytes, has to say when it could not see
something, and must treat every message as untrusted data. It must not become a way to run a shell or
to widen what bOps can read.

## Decision

### One tool, one manifest, two collectors

`system.events` is a `Read` tool contributed by `bOps.Packages.System.Windows` and
`bOps.Packages.System.Linux`, each declaring its own platform (architecture rule A8). The manifest,
argument parsing, filter semantics, ordering, bounds and JSON output live in `bOps.Packages.System.Core`;
each OS package implements only collection. The two manifests are identical apart from `Platforms`.
No other public name is added: there is no `service.logs`, `journalctl.*` or `eventlog.*`. A service is
diagnosed by filtering `system.events` on its `source`.

### Arguments

Every argument is optional, and an out-of-range value is rejected, never clamped.

| Name | Type | Meaning |
|---|---|---|
| `windowMinutes` | Integer, 1 to 10080, default 60 | How far back to look, counted from now. |
| `minSeverity` | Enum: `critical`, `error`, `warning`, `information`, `verbose` | Only events at least this severe. Absent means no severity filter. |
| `source` | String, up to 128 characters | Windows: the provider name. Linux: the syslog identifier or the systemd unit. Exact, case-insensitive. |
| `eventId` | String, up to 64 characters | Windows: the numeric event id. Linux: the 32-hex-digit `MESSAGE_ID`. |
| `channel` | String, up to 256 characters | Windows: a channel name (default: `System` and `Application`). Linux: the journal transport (`kernel`, `journal`, `syslog`, `stdout`, `driver`, `audit`; default: all). |
| `text` | String, up to 256 characters | Case-insensitive substring of the first 2000 characters of the message (what the result can show, on both platforms). |
| `limit` | Integer, 1 to 500, default 50 | Events returned. |
| `maxOutputBytes` | Integer, 4096 to 65536, default 32768 | UTF-8 size of the result. |

`source`, `channel`, `eventId` and `text` accept only a small, fixed character set (letters, digits and
`space _ . @ : / ( ) -` for names; no control characters in `text`), so a value can never carry query
syntax. `eventId` and `channel` are further checked by each platform, because their shape differs.

### Result

Deterministic JSON, newest first (ties broken by source, channel, event id and message):

```json
{
  "schemaVersion": 1,
  "status": "complete | partial | unavailable",
  "complete": true,
  "window": { "fromUtc": "...", "toUtc": "..." },
  "observedEvents": 12,
  "returnedEvents": 12,
  "truncated": false,
  "sources": [ { "name": "windows.channel.System", "status": "available", "detail": null } ],
  "events": [
    {
      "timestampUtc": "2026-09-21T10:00:00.0000000Z",
      "severity": "critical | error | warning | information | verbose | unknown",
      "source": "...", "unit": null, "eventId": null, "channel": null,
      "message": "...", "messageTruncated": false,
      "processId": null, "processName": null
    }
  ]
}
```

- `sources` reuses the inventory vocabulary (`available`, `partial`, `unavailable`, `unsupported`,
  `notApplicable`). A source that could not be read because of permissions, a missing log service or an
  I/O failure is `unavailable`; it is never reported as an empty healthy result. `status` is `unavailable`
  when no applicable source could be read and `partial` when some could not.
- `truncated` is true when the limit, the byte budget or the scan ceiling cut the result. `complete` is
  true only when `status` is `complete` and `truncated` is false, so a reader can trust an empty `events`
  only when `complete` is true.
- `unit` is the systemd unit (Linux only, `null` on Windows). `source` is the Windows provider or the Linux syslog identifier, then the
  unit when there is no identifier, then the process name, then the literal `unknown`.
- Unknown levels and providers stay explicit (`unknown`, the raw provider name) rather than guessed.
- Not returned: raw Windows event XML, the journald field bag, or any native metadata beyond the fields
  above. Messages are bounded to 2000 characters, per-field strings to their own limits.

### Windows collection

`EventLogQuery` and `EventLogReader` from `System.Diagnostics.EventLog` (already a dependency of the
package through the performance counters; no new package), in reverse direction. The query is an XPath
built from validated values; time, level, provider and event id are filtered natively, the message text
after `FormatDescription`. Only channels the current identity can read are queried; a denied or missing
channel is a source with status `unavailable` (`notApplicable` for a default channel that does not
exist). There is no elevation, no identity switch, no PowerShell and no `wevtutil`. A record that cannot
be read or formatted is skipped and makes its source `partial`. A provider that registers no message text on
this machine gives its data values as the message, and an event with neither has an empty message rather than an
invented one. A native provider filter is case-sensitive, so `source` is first matched to the spelling the provider
registered; a provider that is not registered here is filtered afterwards by the shared, case-insensitive filter.
Cancellation calls `CancelReading`, and a 20-second bound (shorter than the runner default tool timeout of 30 seconds) stops a read that does not finish and marks it `partial`.

### Linux collection

The tool runs `journalctl` directly: `ProcessStartInfo` with `UseShellExecute = false`, every switch fixed
in code, every value passed as its own `ArgumentList` item, and a fixed environment (`LC_ALL=C`). No
argument reaches the command line except through a validated, single-purpose value (a time, a priority
number, `_TRANSPORT=` or `MESSAGE_ID=` matches). The switches are `--no-pager --output=json --reverse
--utc --since --until --lines --priority --output-fields`, and `--output-fields` limits what journald
returns to the fields the tool uses. Source and text are matched by the shared filter after parsing,
because journald combines matches for different fields with AND and a "unit or identifier" test would
need a disjunction that does not compose with the other matches.

A scan ceiling (10,000 records per source) bounds the work; hitting it with a filter still unsatisfied makes the
result truncated. Standard output is read line by line and stops at the ceiling, the child is killed on
cancellation or timeout, and standard error is read up to a small cap. A missing executable, a non-zero
exit or an unreadable journal is `unavailable`. When journald says the identity does not see other users'
messages (the `adm` and `systemd-journal` groups), the source is `partial` with that reason, because the
result is real but not the whole journal. Non-JSON lines and records that do not parse are skipped and
make the source `partial`.

### Untrusted data and privacy

Event messages are attacker-influenceable text. They cross the tool boundary as data inside the
delimited tool-result turn, like every tool output, and never alter runtime state. Audit records the
already-redacted arguments and, through `IToolAuditSummaryProvider`, an aggregate summary: status,
completeness, observed and returned counts, counts by severity and the status of each source. It never
records a message, and neither does telemetry. Storing the bounded tool result in the task, as for every
read tool, is unchanged.

### No new privilege

bOps asks for no extra privilege to read more. What the host identity cannot read is reported as a gap.
The documentation says which groups or rights widen visibility and leaves that choice to the operator.

## Alternatives considered

- **A libsystemd binding** (`sd_journal_*`, through P/Invoke or a NuGet package). Rejected for V1.3-A:
  it adds a native dependency that is absent on minimal images and a package to review, for a query that
  `journalctl` already answers with structured JSON. The choice is isolated behind the Linux tool, so a
  binding can replace it later without a contract change.
- **PowerShell `Get-WinEvent` or `wevtutil`.** Rejected: a second process and a command surface for what
  the .NET API already does natively, and exactly what the tool must not resemble.
- **Separate `service.logs`, `journalctl.*` and `eventlog.*` tools.** Rejected: two vocabularies for one
  concept, and a model that has to know which operating system it is on.
- **A `minSeverity` default of `warning`.** Rejected: hiding informational events by default would make
  "what happened around 10:00" harder to ask. The window and the limit already keep the default narrow.
- **Clamping out-of-range arguments.** Rejected: a silently widened or narrowed request is a wrong
  answer that looks right.
- **Returning raw native records for flexibility.** Rejected: unbounded, OS-shaped and a larger
  prompt-injection surface.

## Consequences

- One `Read` tool answers "what happened recently" on both systems with the same shape, and the agent
  can correlate service and process state with native events.
- An empty result can be trusted only when `complete` is true; a permission gap is visible instead of
  looking like a healthy machine.
- The Linux collector depends on `journalctl` being installed and on the journal being readable by the
  identity. The Windows collector sees only channels the identity can read (for example, not `Security`
  without the right).
- Source and text filtering on Linux happens after the scan ceiling, so a rare source in a long window
  can be reported as truncated rather than missed silently.
- No change to `bOps.Abstractions`, policy, runtime or persistence.

## HARDEN-7 amendment — Accepted 2026-10-02

Status: Accepted (2026-10-02, operator decision through the HARDEN-7 architecture gate, with the independent-review
corrections recorded at the end of this amendment).

Governs the `system.events` and `system.crashes` part of HARDEN-7 of the V1.3.x reliability train
([packet](../../../agentic/_tasks/2026-09-25-v1.3x-harden-07-windows-stability-evidence.md); plan
[`2026-09-25-v1.3x-reliability-hardening.md`](../../../agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md) §2.3, §2.4,
findings F-08, F-10, F-22, defect C-16). The new `system.stability` tool, the evidence-time rules and the crash
correlation rules are [ADR-0041](0041-typed-cross-platform-stability-evidence.md), accepted together with this amendment.
Everything in the 2026-09-21 decision that this amendment does not name stays as it is: one tool and one manifest per
platform, collection through `EventLogReader` and a directly started `journalctl`, untrusted messages, no new privilege.
Completeness semantics are those of the ADR-0022 HARDEN-6 amendment (`ToolResultCompleteness`).

### Context

The operator question "why has this PC been freezing for months?" failed in three ways that are contract problems, not
collector bugs:

1. **Flooding.** Both tools return one row per native record. In the incident `system.crashes` returned 100 rows that
   were 7 distinct signatures (76 × LiveKernelEvent `193`), and `system.events` output was dominated by the development
   host's own `.NET Runtime` errors (H-6). Neither tool can say "how many, of what, between when" without spending the
   context budget on duplicates.
2. **Horizon.** `windowMinutes` and `sinceMinutes` stop at 10080 (7 days). A months-long question cannot be asked.
3. **Invisible retention.** On the workstation the System log reached back to 2026-07-31 and the Application log to
   2026-09-02. No tool reports how far back a log reaches, so a long request against a short log returns a result that
   looks like complete history. `complete: true` today means only "every source was read and nothing was cut", which a
   reader can mistake for "nothing happened before the oldest record".

HARDEN-6 made numeric bounds manifest constraints (`ToolParameter.Minimum`/`Maximum`), projected into every provider
schema and enforced by the runtime before execution. A "7 days in raw mode, 180 days in aggregate mode" rule is a
cross-field condition those constraints cannot express.

### Decision

#### 1. Two output modes; a default per tool

`system.events` and `system.crashes` each gain one optional argument, `mode`, the single canonical way to choose the
output shape (there is no boolean `aggregate` argument or any other second switch):

| Tool | Name | Type | Allowed values | Default |
|---|---|---|---|---|
| `system.events` | `mode` | Enum | `raw`, `aggregate` | `raw` |
| `system.crashes` | `mode` | Enum | `aggregate`, `raw` | `aggregate` |

- `aggregate` returns **groups**: one entry per distinct aggregation key (§3, §4) with `count`, `firstSeenUtc` and
  `lastSeenUtc`, instead of one entry per native record.
- `raw` returns **rows**, one per record, in the schema-1 row shape plus the additive fields listed below.
- **`system.events` keeps `raw` as its default.** A call without `mode` returns rows exactly as before (schema 2 adds
  fields and makes `complete` stricter, §3, §6), so every existing invocation keeps the same kind of output; aggregation is an additive capability
  the caller chooses explicitly with `mode: aggregate`, and long horizons (§2) are available only through that explicit
  choice.
- **`system.crashes` defaults to `aggregate`** (packet scope 5: aggregate is the default model-facing output for crash
  evidence). This is an intentional default behavioural change of that tool, versioned by schema 2 (§9); `mode: raw` is
  chosen explicitly for per-crash detail and dump references.
- Every filter applies before aggregation, in both modes, with unchanged meaning. `limit` keeps its name, range and
  default, and bounds **groups** in aggregate mode and **rows** in raw mode.
- The result always states the mode it was produced in (`"mode": "aggregate"` or `"raw"`).

#### 2. Horizons: a separate day-granular argument, aggregate only

The existing minute arguments keep their manifest bounds unchanged. Long horizons are a **new, separate argument**
with its own manifest bounds, valid only in aggregate mode:

| Tool | Existing argument (unchanged) | New argument | Manifest constraint | Valid in |
|---|---|---|---|---|
| `system.events` | `windowMinutes`, 1–10080, default 60 | `windowDays` | Integer, `Minimum = 1`, `Maximum = 180` | `aggregate` only |
| `system.crashes` | `sinceMinutes`, 1–10080, default 1440 | `sinceDays` | Integer, `Minimum = 1`, `Maximum = 180` | `aggregate` only |

The minute argument is valid in both modes. The window is `[now − N minutes, now]` or `[now − N × 1440 minutes, now]`,
in UTC. When neither argument is present the minute default applies (60 for `system.events`, 1440 for
`system.crashes`), in either mode.

The mode is resolved first: the explicit `mode`, else the tool's default (§1). For `system.events` a day argument
therefore requires an explicit `mode: aggregate`; `windowDays` without `mode` is a raw request and is rejected by rule 2.
For `system.crashes` `sinceDays` without `mode` is valid, because its default is `aggregate`.

Two cross-field rules are enforced by the shared argument reader in `bOps.Packages.System.Core`, before any collection,
as a `ToolFailureKind.Validation` failure whose message names the rule. Nothing is clamped and the tool never switches
mode on its own. The reader evaluates them in this fixed order and reports only the first rule that fails:

1. The minute argument and the day argument together → rejected, in either mode: "Use either windowMinutes or
   windowDays, not both." (`sinceMinutes`/`sinceDays` for `system.crashes`).
2. The day argument in raw mode (explicit `mode: raw`, or the `system.events` default) → rejected: "windowDays
   applies only to mode aggregate; raw mode is limited to windowMinutes up to 10080 (7 days)." (the
   `sinceDays`/`sinceMinutes` equivalent for `system.crashes`).
3. Every remaining argument check (the existing per-argument and per-platform validation).

A request carrying both arguments with `mode: raw` is therefore rejected by rule 1. The runtime's manifest checks
(ranges, enum values, unknown arguments) run before the tool and are unaffected by this order.

Every numeric bound therefore stays a HARDEN-6 manifest constraint, and the manifest stays the sole source of numeric
bounds: a raw request above 7 days can only be expressed as `windowMinutes > 10080`, which the runtime rejects from the
manifest before execution, in every mode. The only rules a provider schema cannot carry are "not both" and "day
argument only in aggregate"; each manifest description states them, and they are enforced in exactly one place, the
shared System.Core reader. No conditional-schema machinery is added to `bOps.Abstractions`. Because these two rules are
checked inside the tool, the policy decision for the call is evaluated before they reject it; for these `Read` tools
that changes nothing, and the rejection is audited like any validation failure.

Aggregate mode over a long horizon runs under the **same scan ceilings and time bounds** as raw mode (§7). A long
horizon that hits a ceiling is reported truncated together with the instant the scan actually reached (§5); a ceiling
is never raised silently.

#### 3. `system.events` result, schema 2

An explicit aggregate request (`mode: aggregate`, `windowDays: 180`):

```json
{
  "schemaVersion": 2,
  "mode": "aggregate",
  "status": "complete",
  "complete": false,
  "truncated": false,
  "window": { "fromUtc": "2026-04-05T10:00:00.0000000Z", "toUtc": "2026-10-02T10:00:00.0000000Z" },
  "coverage": { "see": "§5" },
  "observedEvents": 812,
  "sources": [
    { "name": "windows.channel.System", "status": "available", "detail": null,
      "examinedFromUtc": "2026-04-05T10:00:00.0000000Z" }
  ],
  "observedGroups": 9,
  "returnedGroups": 9,
  "groups": [
    {
      "channel": "System", "source": "Microsoft-Windows-Kernel-Power", "unit": null, "eventId": "41",
      "severity": "critical",
      "count": 16,
      "firstSeenUtc": "2026-08-01T07:12:44.0000000Z", "lastSeenUtc": "2026-09-25T06:01:02.0000000Z",
      "sampleMessage": "The system has rebooted without cleanly shutting down first. ...",
      "sampleMessageTruncated": false
    }
  ]
}
```

- **Aggregate** (`mode: aggregate`): the aggregation key is the tuple (`channel`, `source`, `unit`, `eventId`,
  `severity`), compared ordinally on the normalized values a raw row would carry (`null` is a value of its own).
  `count` is the number of matched records with that key; `firstSeenUtc`/`lastSeenUtc` are the oldest and newest record
  times in the group. `sampleMessage` is the message of the group's newest record (ties broken by the raw ordering of
  ADR-0032), cut to 512 characters, with `sampleMessageTruncated`. `processId` and `processName` are not carried.
  Groups are ordered by `count` descending, then `lastSeenUtc` descending, then `channel`, `source`, `unit`, `eventId`,
  `severity` — each compared case-insensitively ordinal, then ordinal, `null` before any value. `observedGroups` is the
  number of distinct keys; `returnedGroups` the number returned after `limit` and the byte budget.
- **Raw** (`mode: raw`, the default): `returnedEvents` and `events` exactly as in schema 1 (same row fields, ordering
  and bounds); no `observedGroups`, `returnedGroups` or `groups`. Against schema 1 a raw result only adds `mode`,
  `coverage` and `sources[].examinedFromUtc`, and `complete` is stricter (§6).
- Both modes carry `schemaVersion`, `mode`, `status`, `complete`, `truncated`, `window`, `coverage`, `observedEvents`
  and `sources`. `observedEvents` counts matched records before grouping in both modes. Each `sources` entry gains
  `examinedFromUtc` (§5).
- `timestampUtc`, `firstSeenUtc` and `lastSeenUtc` are **record times**: the instant the operating system recorded the
  event. `system.events` does not interpret them. For some events (Kernel-Power 41, the System-log bugcheck 1001, WER
  1001) the record time is later than the incident the event describes; `system.crashes` and `system.stability` carry
  the typed `timestampKind` for that (ADR-0041 §6). The manifest description says so.
- The byte budget (`maxOutputBytes`, unchanged) removes groups (or rows) from the end of the order and sets
  `truncated: true`; nothing else is removed.

#### 4. `system.crashes` result, schema 2

Arguments after this amendment: `mode`, `sinceMinutes` (1–10080, default 1440), `sinceDays` (1–180, aggregate only),
`limit` (1–1000, default 100; groups or rows). The result's UTF-8 size is bounded by a fixed budget of 65,536 bytes in
both modes (schema 1 had no byte bound); no argument is added for it. The budget removes groups (or rows) from the end
of the order and sets `truncated: true`.

```json
{
  "schemaVersion": 2,
  "mode": "aggregate",
  "status": "partial",
  "complete": false,
  "truncated": false,
  "window": { "fromUtc": "2026-09-24T10:00:00.0000000Z", "toUtc": "2026-09-25T10:00:00.0000000Z" },
  "coverage": { "see": "§5" },
  "observedItems": 101,
  "catalogAge": null,
  "sources": [
    { "name": "windows-event-wer", "status": "available", "detail": null,
      "examinedFromUtc": "2026-09-24T10:00:00.0000000Z" }
  ],
  "warnings": [],
  "observedGroups": 7,
  "returnedGroups": 7,
  "groups": [
    {
      "kind": "kernel-live-dump", "eventName": null, "code": "0x193",
      "application": null, "module": null, "timestampKind": "reported",
      "count": 76, "uncorrelatedCount": 0, "dumpReferenceCount": 76,
      "firstSeenUtc": "2026-09-25T06:09:36.0000000Z", "lastSeenUtc": "2026-09-25T06:09:42.0000000Z",
      "evidenceSources": ["windows-event-wer"]
    }
  ]
}
```

- **Crash record.** Before formatting, the collector produces one normalized crash record per crash: native records
  that share a report identity are merged into one (ADR-0041 §7); records without one are never merged.
  `observedItems` counts these crash records inside the window in both modes.
- **Raw rows** keep every schema-1 field (`timestampUtc`, `process`, `pid`, `kind`, `dumpPath`, `eventIdOrCrashId`,
  `summary`, `source`) and add: `timestampKind` (`occurred` or `reported`, ADR-0041 §6 — a `BlueScreen` Report.wer `EventTime` is
  `reported`, by the evidence-driven correction of 2026-10-02 recorded in ADR-0041), `reportedUtc` (the WER
  processing time when the record also has an occurrence time, else `null`), `reportId` (lower-case GUID or `null`),
  `eventName`, `code`, `bugcheckCode`, `liveDumpCode`, `exceptionCode`, `faultModule`, `bucket` and `evidenceSources`
  (sorted names of the sources the record was merged from). `source` is the source that supplied `timestampUtc`.
  `dumpPath` keeps its name and now also carries the dump reference from WER `AttachedFiles` (the first `.dmp` path);
  it is a path string only and the file is never opened. The plan's provisional field name `dumpReference` is **not**
  added, so one field carries one meaning. `summary` is no longer a raw native field: it is the non-null parts of
  `eventName`, `code` and `faultModule` joined with `"; "`, or `null`. `kind` gains the values `kernel-bugcheck` and
  `kernel-live-dump` (the field mapping is the packet's scope item 1 and needs no further decision). Rows are ordered
  newest `timestampUtc` first, then `process`, then `source` (case-insensitive ordinal, then ordinal), then `reportId`.
- **`code`** is one canonical text per kind: bugcheck, live-dump and Windows exception codes are lower-case hexadecimal
  with a `0x` prefix and no leading zeros (`0x50`, `0x1a1`, `0xc0000005`); a Linux core dump's `code` is the
  terminating signal number in decimal (`"11"`); otherwise `null`. `bugcheckCode`, `liveDumpCode` and `exceptionCode`
  carry the same canonical value in the field that names its meaning; the other two are `null`.
- **Aggregate key** is the tuple (`kind`, `eventName` — only when `kind` is `wer`, else `null` —, `code`,
  `application`, `module`, `timestampKind`). `application` is the record's `process`, `module` its `faultModule`; both
  are compared case-insensitively ordinal, and the group shows the ordinally smallest spelling among its members.
  `timestampKind` is part of the key, so a group never mixes occurrence and reporting times and `firstSeenUtc`/
  `lastSeenUtc` are always of the group's kind. `uncorrelatedCount` is the number of members without a `reportId` —
  members that may describe a crash another source also recorded, which bOps does not guess (ADR-0041 §7).
  `dumpReferenceCount` counts members with a `dumpPath`; the paths themselves are raw-mode detail. `evidenceSources` is
  the sorted union of the members' sources (at most 8). Groups are ordered by `count` descending, `lastSeenUtc`
  descending, then the key fields in key order (case-insensitive ordinal, then ordinal, `null` first).
- **Window rule.** A crash record belongs to the window when its primary `timestampUtc` (the highest-precedence time of
  ADR-0041 §6) lies in it. Report.wer metadata is parsed within its bounds whatever its time, so a report processed
  inside the window can be merged with an occurrence time outside it; that crash is then outside the window, which is
  what "crashes in this window" means.
- **Sources.** The two WER report roots get distinct names: `windows.wer.programdata.reportarchive`,
  `windows.wer.programdata.reportqueue`, `windows.wer.localappdata.reportarchive`,
  `windows.wer.localappdata.reportqueue`. Event Log sources keep `windows-event-application-error` and
  `windows-event-wer`; Linux keeps `linux-coredumpctl` and `linux-core-pattern`.
- **Bounds.** The existing ceilings stay: 512 Event Log records per provider, 512 report directories per WER directory,
  2,048 normalized records, a 10-second bound per Event Log read. Report.wer is read by a streaming key scan of at most
  256 KiB per file (replacing the 32 KiB whole-file skip) and at most 32 MiB of Report.wer data per call. A file whose
  needed keys are not found within its cap is skipped and makes its source `partial`; reaching the per-call cap stops
  the scan with the source `partial` and the result truncated.

#### 5. Temporal coverage — a separate object, never a synonym for completeness

`system.events`, `system.crashes` and `system.stability` each return a `coverage` object that answers one question:
**how far back does each data store actually reach, compared with what was asked?** It says nothing about whether the
read succeeded; that is `status`, `truncated`, the per-source `status` and `ToolResultCompleteness`.

```json
"coverage": {
  "requestedFromUtc": "2026-04-05T10:00:00.0000000Z",
  "requestedToUtc": "2026-10-02T10:00:00.0000000Z",
  "state": "partial",
  "stores": [
    {
      "name": "windows.channel.System",
      "basis": "eventLog",
      "oldestAvailableUtc": "2026-07-31T06:12:09.0000000Z",
      "logMaximumBytes": 20971520,
      "state": "partial"
    }
  ]
}
```

- **Unit and form.** Instants are UTC in the format the tools already use (`yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`).
  `requestedFromUtc`/`requestedToUtc` equal the result's `window`. `state` is `complete`, `partial` or `unknown`;
  `basis` is `eventLog`, `journal` or `directory`; `logMaximumBytes` is an integer number of bytes or `null`.
- **Stores** are the history-bearing data stores behind the result's sources, one entry each, ordered by `name`
  (case-insensitive ordinal, then ordinal). A store is listed when at least one source it backs is not
  `notApplicable`. Store names: `windows.channel.<Channel>` (an Event Log channel), `linux.journald` (the journal), and
  the directory source names (`windows.wer.<root>.<directory>` in `system.crashes`, `windows.minidump` in
  `system.stability`). A source that reads no history has no store; the only such source today is
  `linux-core-pattern`, a configuration probe. Mapping: a `system.events` source `windows.channel.<Channel>` is backed by the store of the same name and
  `linux.journald` by `linux.journald`; the `system.crashes` sources `windows-event-application-error` and
  `windows-event-wer` are backed by `windows.channel.Application`, each WER directory source by itself, and
  `linux-coredumpctl` by `linux.journald` (systemd-coredump records live in the journal); `system.stability` is mapped
  in ADR-0041 §4.
- **`basis: eventLog`** (a Windows channel). `oldestAvailableUtc` is the record time of the oldest record in the live
  channel, read with one forward read of one record; `logMaximumBytes` is the channel's configured maximum size
  (`EventLogConfiguration.MaximumSizeInBytes`). Either is `null` when it cannot be read.
- **`basis: journal`** (Linux). `oldestAvailableUtc` is the realtime timestamp of the oldest journal entry **visible to
  the host identity**, read by one fixed `journalctl` invocation in the ADR-0032 model (`--no-pager --output=json --utc
  --output-fields=_TRANSPORT`, forward order, first line only, after which the child is stopped). `logMaximumBytes` is
  always `null`: journald derives its effective cap from configuration and file-system size, and bOps does not
  reproduce that calculation.
- **`basis: directory`** (WER report directories, the minidump directory). A directory has no retention guarantee:
  the absence of older files proves nothing about older crashes. Its `state` is therefore always `unknown`;
  `oldestAvailableUtc` is the oldest item time observed in it (or `null`) and `logMaximumBytes` is `null`.
- **Store `state`** for `eventLog` and `journal`: `complete` when `oldestAvailableUtc ≤ requestedFromUtc`; `partial`
  when `oldestAvailableUtc > requestedFromUtc` (the store does not reach back to the start of the request); `unknown`
  when `oldestAvailableUtc` could not be determined (metadata unreadable, probe failed or timed out, store empty).
  An undetermined value is `null` with `unknown`, never an estimate.
- **Global `state`** is computed over the `eventLog` and `journal` stores only: `partial` if any is `partial`; else
  `unknown` if any is `unknown` or there is none; else `complete`. Directory stores never change the global state: the
  crashes and bugchecks they describe are also recorded by a retention-bearing store (WER 1001 for each report, the
  System-log 1001 for each bugcheck), and their own coverage is unknowable.
- **Scan reach** is per source, not per store: each `sources` entry carries `examinedFromUtc`, the oldest instant this
  call's scan of that source actually reached — `requestedFromUtc` when the scan ran to the start of the window, the
  record time of the oldest record read when a ceiling or time bound stopped it, and `null` when the source was not
  read or stopped before its first record. A directory source has `requestedFromUtc` when its enumeration finished
  within its ceiling and `null` otherwise, because directory order is not time order. The span a source's evidence
  really covers is `[max(examinedFromUtc, oldestAvailableUtc of its store), requestedToUtc]`.
- Time bounds of the coverage reads: in `system.events` they share the call's 20-second bound; in `system.crashes`
  the Windows probe is one more Event Log read under its own 10-second bound and the Linux probe runs under the same
  15-second bound as the `coredumpctl` run; in `system.stability` they share the call's 20-second bound (ADR-0041 §4).
  A probe that does not finish leaves `oldestAvailableUtc: null` and `state: unknown`.

A request for 180 days against a System log that reaches back two months therefore reads `coverage.state: partial`
with `oldestAvailableUtc` two months ago, whatever the scan found.

#### 6. Completeness and the `complete` field

`complete` (JSON) is redefined, for all three tools, as

`complete = (status == "complete") AND (truncated == false) AND (coverage.state == "complete")`

and `ToolCallResult.Completeness` keeps being derived from the emitted JSON by the existing System.Core rule
(`EvidenceCompleteness`, ADR-0022 HARDEN-6 amendment), which needs no change:

| Situation | `status` | `truncated` | `coverage.state` | `complete` | `Completeness` |
|---|---|---|---|---|---|
| Every source read fully; every store reaches the request start | `complete` | false | `complete` | true | `Complete` |
| A scan ceiling, time bound, `limit` or the byte budget cut the result | not `unavailable` | true | any | false | `Partial` |
| One source unreadable (denied, missing, failed) while another produced evidence | `partial` | any | any | false | `Partial` |
| No applicable source could be read | `unavailable` | any | any | false | `Unavailable` |
| Scan successful, but a store's history starts after `requestedFromUtc` | `complete` | false | `partial` | false | `Partial` |
| Scan successful, but a store's oldest record could not be determined | `complete` | false | `unknown` | false | `Partial` |

`Complete` therefore never hides a retention gap: an empty or short result for a request longer than the retained
history is `Partial`, and `coverage` says why and from when evidence exists. Conversely, `coverage.state: complete`
never makes a truncated or source-incomplete result `Complete`. `status` keeps its schema-1 meaning (the state of the
sources), so the two dimensions stay distinguishable to the model and to audit.

#### 7. Bounds that do not change

`system.events`: scan ceiling 10,000 records per source, a 20-second bound per call (now also covering the coverage
probes), messages of 2,000 characters in raw rows, `maxOutputBytes` 4,096–65,536 (default 32,768), `limit` 1–500
(default 50). `system.crashes`: the ceilings of §4. No new privilege, no PowerShell, no `wevtutil`, no shell; the Windows
XPath is still built only from validated values and package constants, and `journalctl` and `coredumpctl` are still
started directly with fixed switches and every value as a separate argument.

#### 8. Audit

The `system.events` audit summary keeps its fields and adds `mode`, `observedGroups` and `returnedGroups` (aggregate
mode), `coverage.state`, and each store's `name`, `state` and `oldestAvailableUtc`; `bySeverity` counts records (in
aggregate mode, the sum of the returned groups' `count` per `severity`). `system.crashes` gains an
`IToolAuditSummaryProvider` with `mode`, `status`, `complete`, `truncated`, `observedItems`, counts by `kind`, each
source's `name` and `status`, and the same coverage fields. Neither summary ever carries a message, an application or
module name, a report id, a bucket or a path.

#### 9. Schema versions and compatibility

| Tool | Before | After | Nature of the change |
|---|---|---|---|
| `system.events` | `schemaVersion: 1` | `2` | Existing default behaviour preserved: `mode` defaults to `raw`, whose rows are the schema-1 rows. Additive fields (`mode`, `coverage`, `sources[].examinedFromUtc`); an opt-in aggregate mode with its own shape (`groups` instead of `events`) and the aggregate-only `windowDays`; `complete` now also requires complete coverage. |
| `system.crashes` | `schemaVersion: 1` | `2` | Intentional default behavioural change: `mode` defaults to `aggregate`, whose shape is structurally different (`groups` instead of `items`). Additive row fields and `mode`, `window`, `coverage`; vocabulary extension of `kind`; `summary` and `dumpPath` population corrected; a fixed 65,536-byte budget where none existed; `complete` now also requires complete coverage. The other maintenance tools stay at schema 1. |
| `system.stability` | — | `1` | New tool ([ADR-0041](0041-typed-cross-platform-stability-evidence.md)). |

Compatibility story:

- **SDK/API abstractions.** Additive, no break: no change to `bOps.Abstractions`; the new arguments use the HARDEN-6
  `ToolParameter` constraint properties, `ToolParameterType.Enum` and the existing `ToolCallResult.Completeness`. Every
  contract type added is package-local in `bOps.Packages.System.Core`.
- **Arguments.** Every schema-1 argument keeps its name, type, range and default, and every schema-1 call stays valid.
- **Tool-result schema.** Both tools bump `schemaVersion` to 2; readers must check it.
- **`system.events` behaviour.** Preserved: a call without `mode` returns raw rows with every schema-1 field and the
  same meaning, except `complete` (stricter), plus additive fields a reader may ignore. Aggregation and long horizons
  exist only behind an explicit `mode: aggregate`.
- **`system.crashes` behaviour.** Intentionally changed: a call without `mode` returns groups. A reader that needs the
  schema-1 row shape passes `mode: raw` and gets every schema-1 field with the same meaning, except `complete`
  (stricter), `summary` (no longer raw `P1`) and `kind` (more values), plus additive fields it may ignore.
- **Consumers.** No consumer outside the System packages and their tests parses either output (checked at `fd3b61c`);
  the conformance suite and the documentation move to schema 2 with the implementation.

### Alternatives considered

**Raise `windowMinutes`/`sinceMinutes` to 259,200 and reject raw requests above 10,080 in the package.** Rejected: the
provider schema would advertise values the tool then refuses in raw mode, the "raw stays at 7 days" rule would leave
the manifest and live only in package code, and a model would have to express six months in minutes. Keeping the minute
bound and adding a day argument keeps every numeric bound a manifest constraint.

**Generic conditional or dependent-schema constraints in `bOps.Abstractions`.** Rejected: a public SDK change and a
provider-schema projection problem (conditional JSON Schema support differs between providers) for two rules in one
package family. If a second package needs cross-field rules, that is a new ADR.

**A separate aggregate tool (`system.events.summary`, `system.crashes.summary`).** Rejected for the reason ADR-0032
rejected `service.logs`: two vocabularies for one concept, and a model choosing between near-identical tools.

**Keep `raw` as the default of `system.crashes`.** Rejected: the default is what the model reads first, and the
incident shows that per-record crash output spends the context on duplicates and hides the signal (plan §2.3; packet
scope 5).

**Make `aggregate` the default of `system.events` too.** Rejected by the independent review: the packet asks for an
aggregate mode and long horizons for events, not for a new default; `system.events` answers "what did this log record
around this time", where messages and chronology are the point (the reason ADR-0032 rejected a `warning` severity
default); the self-noise hypothesis that would motivate it (H-6) is unconfirmed and belongs to HARDEN-9; and keeping
`raw` preserves every existing invocation. Aggregation stays one explicit `mode` value away.

**A boolean `aggregate` argument (or a second aggregation switch) for `system.events`.** Rejected: one canonical `mode`
argument for both tools; two switches for one choice would be two vocabularies for one concept. HARDEN-9 uses this
`mode`.

**Report retention only through `Completeness`.** Rejected: `Partial` says that something is missing, not what or since
when. **Report retention only through `coverage` and keep `Complete`.** Rejected: typed consumers (runtime, audit,
HARDEN-9's evidence rule) read `Completeness` without parsing package JSON, and `Complete` for a six-month request over
two months of log is exactly the false negative this train exists to remove.

**Infer a directory's coverage from its oldest file.** Rejected: WER purges reports by count and age, and an empty
minidump directory may be a clean machine or a deleted folder. `unknown` is the only honest value.

**Time buckets in `system.events`/`system.crashes` aggregates.** Rejected: they multiply output for a question those
tools do not need to answer; the machine-level timeline belongs to `system.stability` (ADR-0041 §8).

**Groups that mix `occurred` and `reported` times.** Rejected: `firstSeenUtc` would be a minimum over incomparable
clocks. `timestampKind` is part of the key.

### Consequences

- The default crash answer is compact and comparable: counts per signature with first and last time, and an explicit
  `mode: raw` for per-crash detail. `system.events` keeps its per-record default and gains the same compact view through
  an explicit `mode: aggregate`. A months-long question can be asked (up to 180 days, aggregate mode only) and is
  answered within the same scan ceilings, with both the reach of the scan and the reach of the log visible.
- `complete: true` now means "every source read, nothing cut, and the history reaches the start of the request". More
  results are `Partial` than before, by design; `coverage` explains which and why.
- Schema 2 deliberately changes the default output shape of `system.crashes` only; its schema-1 readers pass
  `mode: raw`. `system.events` keeps its default output shape. The conformance suite, the System.Core contract tests,
  the audit-summary tests, `docs/system-events.md` and `docs/system-maintenance.md` change with the implementation.
- Coverage costs one extra bounded read per store (one Event Log record, or one `journalctl` line).
- No change to `bOps.Abstractions`, policy, runtime, persistence or the audit schema.

### Independent review corrections (2026-10-02)

The independent architecture review of commit `017477e` (CHANGES REQUIRED) changed this amendment before any
implementation: `system.events` keeps `raw` as its default and aggregation is an explicit `mode: aggregate` (R5, §1,
§9); the order of the cross-field rules is fixed (R16, §2); the acceptance is recorded as an operator decision through
the architecture gate (R15). The remaining blocking corrections (R1–R4) are in
[ADR-0041](0041-typed-cross-platform-stability-evidence.md#independent-review-corrections-2026-10-02).
