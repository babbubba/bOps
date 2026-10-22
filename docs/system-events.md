# System events (`system.events`)

`system.events` reads operating-system events for the agent, so a diagnosis can say why a service failed
and not only that it did. It is one read-only tool with the same arguments and the same result on both systems
(design: [ADR-0032](architecture/adr/0032-bounded-cross-platform-system-events.md) and its HARDEN-7 amendment):

- **Windows:** the Event Log, through `EventLogReader` (channels `System` and `Application` by default).
- **Linux:** journald, through `journalctl` run directly (no shell).

It is `Read` risk: it needs no approval, changes nothing and asks for no extra privilege. For typed machine
stability evidence (unexpected shutdowns, bugchecks, hardware, display and storage faults) use
[`system.stability`](system-stability.md); for crashes and hangs use `system.crashes`
([maintenance evidence](system-maintenance.md)).

## Arguments

Every argument is optional. A value out of range is rejected with the reason, never adjusted.

| Argument | Meaning |
|---|---|
| `mode` | `raw` (the default): one row per event. `aggregate`: one group per channel, source, unit, event id and severity, with a count and first and last times. |
| `windowMinutes` | How far back to look, 1 to 10080 (default 60), in either mode. |
| `windowDays` | How far back to look, 1 to 180. **Only with `mode: aggregate`**, and never together with `windowMinutes`. |
| `minSeverity` | `critical`, `error`, `warning`, `information` or `verbose`: events at least this severe. Omitted: every severity. |
| `source` | Exact, case-insensitive. Windows: the provider name (`Service Control Manager`). Linux: the syslog identifier or the systemd unit (`nginx` or `nginx.service`). |
| `eventId` | Windows: the event id (`7036`). Linux: the 32-digit hexadecimal `MESSAGE_ID`. |
| `channel` | Windows: a channel such as `Microsoft-Windows-Kernel-Power/Thermal-Operational` (default: `System` and `Application`). Linux: a journal transport: `kernel`, `journal`, `syslog`, `stdout`, `driver` or `audit` (default: all). |
| `text` | Case-insensitive text the message must contain, up to 256 characters. It is matched against the first 2000 characters of the message, the same part the result can show. |
| `excludeSources` | Comma-separated sources to leave out (below): at most 8 names of at most 128 characters, 1024 characters in all. Echoed in the result. |
| `limit` | Events (raw) or groups (aggregate) to return, 1 to 500 (default 50). |
| `maxOutputBytes` | Size of the result in UTF-8 bytes of the serialized JSON, 4096 to 65536 (default 32768). |

Names (`source`, `eventId`, `channel`, and each entry of `excludeSources`) accept ASCII letters, digits and
``space _ . @ : / ( ) -`` only, so a value can never carry query syntax.

The byte budget counts the serialized result, in which a character outside ASCII (and a quote) is written as a six-byte escape.
The envelope (everything but `events`/`groups`) is never cut by it: with the largest echoes it measures about 3,050 bytes
(raw) and 3,500 (aggregate), so even `4096` leaves room for one or two small rows, and only rows or groups are removed, with
`truncated: true`. A source's `detail` is host text (a localized error message, for instance), so it is cut at 256 characters
**and** 256 serialized bytes, keeping its beginning (where an error states its reason) and ending with `...` when cut;
ordinary messages within both bounds are unaffected. Ask for `8192` or more when aggregates need long
sample messages.

Two rules involve more than one argument. They are checked first, in this order, and only the first one that fails
is reported:

1. `windowMinutes` and `windowDays` together: "Use either windowMinutes or windowDays, not both."
2. `windowDays` in raw mode — an explicit `mode: raw`, or no `mode` at all: "windowDays applies only to mode
   aggregate; raw mode is limited to windowMinutes up to 10080 (7 days)."

A call without `mode` behaves exactly as before HARDEN-7: raw rows over at most seven days.

### Leaving out noisy sources (`excludeSources`)

A host often logs one very frequent source — typically the process that runs bOps itself — and its records can fill the
`limit`, the byte budget and, on Windows, the scan ceiling, pushing out other records. `excludeSources` removes named
sources before grouping, the `limit` and the byte budget, in both modes, without hiding that it did:

- **Grammar.** A comma-separated string. Each entry is trimmed and must be non-empty, at most 128 characters, and use the
  `source` character set; at most 8 entries and 1024 characters in all (the total prevails: eight 128-character entries do
  not fit). Two entries equal ignoring case, an empty entry (`a,,b`, a trailing comma) and an entry equal to `source` are
  rejected with the reason; nothing is corrected. A syntactically valid name that matches nothing is accepted and echoed —
  whether a source logged in the window is not knowable beforehand. `null` means the argument is absent.
- **What is matched.** The `source` value the result shows, exactly and ignoring case: the Windows provider name, or on
  Linux the syslog identifier, else the unit, else the command name, else `unknown`. A Linux record whose identifier is `x`
  and whose unit is `x.service` is excluded by `x`, not by `x.service`, unlike the `source` filter, which also matches the unit.
- **Windows** excludes natively, in the Event Log query itself (`Provider[@Name!='…' and …]`, one term per name, using the
  spelling the provider registered on the machine), so excluded records are neither read nor counted towards the scan
  ceiling; the shared filter then removes any record the native comparison missed. **Linux** applies the shared filter
  after reading, because journald matches cannot be negated: excluded records are scanned and count towards the scan
  ceiling, and no journal expression is ever built from an entry.
- **Echo.** Every result has a top-level `excludeSources` array right after `window` — `[]` when nothing was excluded,
  otherwise the accepted names in the caller's spelling, sorted. It is part of the envelope and is never cut by the byte
  budget. No count of excluded records is reported (Windows cannot count what it did not read).
- **An excluded source was not looked at.** `complete: true` then means complete for the filtered question, and the
  absence of a source is never evidence that it logged nothing.

### Checking a source name (`sourceFilter`)

A `source` that names nothing is read in full and returns zero events with `complete: true`, which says nothing about the
subject the name was meant for ([ADR-0061](architecture/adr/0061-system-events-source-filter-disclosure.md)). Every result
therefore carries `sourceFilter` right after `excludeSources` — `null` without a `source`, otherwise:

```json
"sourceFilter": {
  "requested": "WHEA",
  "matching": "exactCaseInsensitive",
  "matchedFields": ["source"],
  "registrationStatus": "notRegistered",
  "observedMatch": false,
  "caution": "noMatchSourceNotRegistered",
  "semantics": "Only records matching requested in matchedFields were kept. Zero matches does not show that nothing of that kind was logged: no source here may use that name, and other sources and logs were not queried."
}
```

- **`registrationStatus`** is what the host's registry says about the *name*, never about events: `registered` (Windows: the
  provider catalog lists it, ignoring case), `notRegistered` (the catalog was read and does not list it), `unknown` (always on
  Linux, which has no registry of identifiers or units; on Windows when the catalog could not be read). A registered provider
  may publish to a log the call did not query, and a name that is not registered may still have historical records.
- **`observedMatch`** is whether any record the collector read satisfied the whole query, source together with the window and
  every other filter. It is computed before grouping, `limit` and the byte budget, so it equals `observedEvents > 0` and never
  depends on what was cut from the output. A `truncated` or partially covered result says nothing about unread records.
- **`caution`** is `null` when a record confirmed the name or the name is registered; `noMatchSourceNotRegistered` when nothing
  matched and the name is not registered; `noMatchSourceUnverified` when nothing matched and the name cannot be verified.
- **`matchedFields`**: Windows `["source"]` (the provider name); Linux `["source","unit"]`.
- Completeness is untouched: none of these fields changes `status`, `complete`, `truncated` or the typed completeness, and no
  similar name is ever suggested. Read a zero result together with `complete`, `truncated`, `coverage` and `sourceFilter`: it
  answers the filtered question only, never "nothing of that kind happened". Use the full, exact name.

## Result

JSON, `schemaVersion` 2. A raw result (the default), newest event first, ties in a fixed order:

```json
{
  "schemaVersion": 2,
  "mode": "raw",
  "status": "complete",
  "complete": true,
  "truncated": false,
  "window": { "fromUtc": "2026-09-21T11:00:00.0000000Z", "toUtc": "2026-09-21T12:00:00.0000000Z" },
  "excludeSources": [],
  "sourceFilter": null,
  "coverage": {
    "requestedFromUtc": "2026-09-21T11:00:00.0000000Z",
    "requestedToUtc": "2026-09-21T12:00:00.0000000Z",
    "state": "complete",
    "stores": [ { "name": "linux.journald", "basis": "journal", "oldestAvailableUtc": "2026-08-13T05:39:54.5818890Z", "logMaximumBytes": null, "state": "complete" } ]
  },
  "observedEvents": 2,
  "sources": [ { "name": "linux.journald", "status": "available", "detail": null, "examinedFromUtc": "2026-09-21T11:00:00.0000000Z" } ],
  "returnedEvents": 2,
  "events": [
    {
      "timestampUtc": "2026-09-21T11:57:00.0000000Z",
      "severity": "error",
      "source": "systemd",
      "unit": "nginx.service",
      "eventId": null,
      "channel": "journal",
      "message": "nginx.service: Failed with result 'exit-code'.",
      "messageTruncated": false,
      "processId": null,
      "processName": null
    }
  ]
}
```

The rows are the schema-1 rows, unchanged. Schema 2 adds `mode`, `coverage` and each source's `examinedFromUtc`, and
makes `complete` stricter (below). The always-present `excludeSources` echo and `sourceFilter` object (above) are additive and
stay within schema 2: an empty array and `null` are exactly the behaviour before they existed.

An aggregate result (`mode: aggregate`) has the same envelope with `observedGroups`, `returnedGroups` and `groups`
in place of `returnedEvents` and `events`:

```json
{
  "channel": "System", "source": "Microsoft-Windows-Kernel-Power", "unit": null, "eventId": "41", "severity": "critical",
  "count": 13, "firstSeenUtc": "2026-08-02T06:24:31.3696618Z", "lastSeenUtc": "2026-09-30T18:42:00.2763151Z",
  "sampleMessage": "The system has rebooted without cleanly shutting down first. …", "sampleMessageTruncated": false
}
```

Groups are ordered by `count`, then the newest `lastSeenUtc`, then the key. `observedEvents` counts the matched
records in both modes; the sample is the newest record's message, cut to 512 characters. Every filter applies
before grouping.

An aggregate result also carries a top-level `sampleMessageSemantics` (a fixed text, absent in raw mode; additive,
`schemaVersion` stays 2) stating that `sampleMessage` belongs only to the group's newest event, that `count` covers every
event sharing the group key, and that attributes seen in the sample are not necessarily common to the whole group.

**Message distribution of a group.** An aggregate also says how the *original* messages of each group (compared ordinally, in
full — not the 512-character sample) are distributed. It is additive (`schemaVersion` stays 2), absent in raw mode, and
purely quantitative: different messages are not necessarily different causes, so nothing here is a subtype or a diagnosis.

```json
{
  "channel": "System", "source": "Microsoft-Windows-WHEA-Logger", "unit": null, "eventId": "19", "severity": "warning",
  "count": 41, "firstSeenUtc": "…", "lastSeenUtc": "…", "sampleMessage": "… Internal parity error … APIC … 1 …", "sampleMessageTruncated": false,
  "distinctMessages": 5,
  "messageVariants": {
    "distribution": "partial", "unlistedEvents": 9,
    "variants": [
      { "count": 18, "message": "… Internal parity error … APIC … 1 …", "messageTruncated": false },
      { "count": 9, "message": "… Translation Lookaside Buffer Error … APIC … 1 …", "messageTruncated": false },
      { "count": 5, "message": "… Internal parity error … APIC … 9 …", "messageTruncated": false }
    ]
  }
}
```

- `distinctMessages` is exact for every described group. When it is above 1, `messageVariants` follows:
  `distribution` is `complete` (the listed variants account for every event of the group), `partial` (only the 3 most frequent
  variants are listed; `unlistedEvents` counts the events of the others) or `unlisted` (the messages are so dispersed — nearly
  unique, e.g. they embed a time or an id — that a list covering less than half the events would say little; no list is given).
  Variants are ordered by `count`, then newest, then ordinally. A variant's `message` is cut to 200 characters and `messageTruncated`
  says so, so two variants can look identical when they differ only after the cut (or after the 512-character sample).
- The top-level `messageDistribution` states which groups are described: `describedGroups` leading groups (rank order), with
  `availability` `all`, `partial` or `none`. Groups after them carry neither field: their distribution is **unknown**, not uniform.
  The details are bounded to `SystemEventsLimits.MessageDistributionBytes` (2,048 UTF-8 bytes) in total and to the output budget, and are
  added only to groups the result already keeps — **no group is dropped to make room for them**; with no room left, `availability` is
  `none`. The groups, `count`, `sampleMessage`, `observedGroups`, `returnedGroups`, `complete` and `truncated` do not depend on them.

**Times are record times.** For some events (Kernel-Power 41, the bugcheck record, Windows Error Reporting reports)
the record is written later than the incident it describes — often at the next boot or in a later WER processing
burst. `system.events` does not interpret this; `system.crashes` and `system.stability` label it with
`timestampKind`.

### Completeness and coverage

**Read `complete` before trusting an empty list.** It is true only when every source was read fully, nothing was cut,
**and** every log reaches back to the start of the request. Otherwise:

- `status` is `partial` or `unavailable`, and each entry of `sources` says which source and why (permission
  denied, channel missing, `journalctl` not installed, a read that timed out, records that could not be read).
  A source that could not be read is never reported as an empty log.
- `truncated` is true when the `limit`, the byte budget, the scan ceiling (10,000 records per source) or the time
  bound cut the result. Narrow the window or add a filter. `sources[].examinedFromUtc` says how far back the scan of
  each source actually got.
- `coverage.state` is `partial` when a log does not reach back to `requestedFromUtc` — a 180-day request against a
  System log that holds two months reads `partial`, with that log's `oldestAvailableUtc` — and `unknown` when its
  oldest record could not be determined. `logMaximumBytes` is the channel's configured size on Windows (a capacity,
  not a retention promise) and always `null` for the journal.
- The typed `ToolCallResult.Completeness` follows: `Complete` only with `complete: true`, `Unavailable` when no
  source could be read, `Partial` otherwise.

On Linux the coverage probe reads the oldest entry in the same scope as the read itself: the requested transport, or
the whole journal the identity can see. A broader stream never makes a narrower read look complete.

A native level bOps cannot place is `unknown`, and such an event never passes a `minSeverity` filter. Fields that a
platform does not record are `null`: `unit` is Linux only, and Windows records no process name. A Windows event whose
provider registers no message text is returned with its data values as the message.

### Bounds

One call, coverage probes included, runs within 20 seconds (below the runner's 30-second tool timeout). Each source
gets a share of the time that is left, so one flooded log cannot starve the next; a source stopped by its share is
`partial` and the result `truncated`. The scan ceiling (10,000 records per source) and the byte budget are the same in
both modes; a long aggregate horizon that hits a ceiling says so instead of silently raising it.

## What the host identity can see

bOps asks for no privilege to read more, so the result is what the identity that runs it can read.

- **Windows:** the `Security` channel and some others need rights the identity may not have; they come back
  as `unavailable` with the reason. Adding the identity to the `Event Log Readers` group widens what it sees.
- **Linux:** an identity that is not in the `adm` or `systemd-journal` group sees only its own messages.
  journald says so, and the source is reported `partial` with that reason. Adding the identity to one of those
  groups gives it the whole journal. `journalctl` must be installed.

Both are the operator's choice; nothing in bOps changes group membership.

## Safety

- **Event messages are untrusted data.** Anyone who can log can write one, including text that looks like an
  instruction. It reaches the model inside the delimited tool-result turn like all tool output and never changes
  what the runtime does.
- **Audit and telemetry never carry a message.** The audit event holds the arguments and an aggregate summary:
  mode, status, completeness, observed and returned counts (or groups), counts by severity, the status of each
  source, `coverage.state` and each store's name, state and oldest available time.
- No shell, no `PowerShell`, no `wevtutil`; on Linux every `journalctl` switch is fixed in code and every value
  is a separate argument.

## Technical specification: schema versions, envelope budget (consolidated)

*Consolidated from ADR-0032 (HARDEN-7 §3, §9; HARDEN-9 §6) and ADR-0061 §2.8 during the architecture consolidation (DOC-02); binding rules:
`docs/architecture/current/04-evidence-and-diagnosis.md` (C04-R06 to C04-R08).*

- Schema versions: `system.events` is `schemaVersion: 2` (before: 1). Defaults are preserved: `mode` defaults to `raw`, whose rows are the schema-1 rows; additive fields
  are `mode`, `coverage` and `sources[].examinedFromUtc`; aggregate mode has its own shape (`groups` instead of `events`) and the aggregate-only `windowDays` (1–180);
  `complete` now also requires complete coverage. Schema 2 added `excludeSources` and `sourceFilter` without a version change (both additive and always present; the empty
  value of `excludeSources` and a `null` `sourceFilter` mean exactly the previous behaviour).
- Time bounds: one call shares a single 20-second budget divided into bounded per-operation slices; each Windows Event Log read is capped at 10 seconds within its slice
  (`EventLogReadCap`), and the coverage probe is one more read that consumes the same shared budget, not an independent one.
- Manifests: Windows and Linux manifests stay identical apart from `Platforms`; the constrained `excludeSources` parameter is projected by both provider adapters as
  `minLength`/`maxLength`. Audit: the redacted arguments already carry the values (not `Sensitive`); the audit summary never carries a message and, when `sourceFilter`
  is not `null`, adds `sourceFilter {registrationStatus, observedMatch, caution}` without the requested name.
- Envelope budget of `sourceFilter` (measured at the 4,096-byte minimum `maxOutputBytes` with the largest legal values: eight `excludeSources` entries of 1,024 characters in
  total, a 128-character `source`, two default Windows channels with 256-character details, two coverage stores with the largest `logMaximumBytes`, no rows,
  `registrationStatus: notRegistered`, the longest caution code):

  | Mode | Envelope before | `"sourceFilter": null` | Worst-case `sourceFilter` object | Worst-case envelope | Left for rows or groups |
  |---|---|---|---|---|---|
  | raw | 2,514 B | 2,534 B (+20) | 528 B | 3,058 B | 1,038 B |
  | aggregate | 2,966 B | 2,986 B (+20) | 528 B | 3,510 B | 586 B |

  A typical `sourceFilter` is about 390 bytes. If the worst-case envelope exceeds 3,584 bytes, or any combination reaches 4,096, the implementation stops and returns to the
  architecture owner rather than shortening the field. At the 4,096 floor with every maximum present at once, 586 bytes remain in aggregate mode, so a group carrying a
  512-character sample message may not fit; the budget then removes it, sets `truncated: true`, and `observedEvents`/`observedMatch` still report that records were found.

## Examples of what to ask

- "Why did nginx stop?" `source: nginx.service`, `minSeverity: error`, `windowMinutes: 30`.
- "Was a process killed for memory?" `text: out of memory`, `channel: kernel` (Linux).
- "Any disk errors?" `source: disk`, `eventId: 7` (Windows), or `text: I/O error` (Linux).
- "What went wrong in the last 20 minutes?" `minSeverity: error`, `windowMinutes: 20`.
- "Which errors have repeated over the last three months?" `mode: aggregate`, `windowDays: 90`, `minSeverity: error`.
- "Did this provider log anything?" `source` with the exact, full name; if the result says `observedMatch: false` with a
  `caution`, the name is unconfirmed — say so instead of concluding that nothing happened.
- "…apart from this host's own noise?" the same, plus `excludeSources: .NET Runtime` — the result echoes the exclusion, and
  that source was not looked at.
