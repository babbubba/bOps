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
| `limit` | Events (raw) or groups (aggregate) to return, 1 to 500 (default 50). |
| `maxOutputBytes` | Size of the result, 4096 to 65536 (default 32768). |

Names (`source`, `eventId`, `channel`) accept letters, digits and ``space _ . @ : / ( ) -`` only, so a value can
never carry query syntax.

Two rules involve more than one argument. They are checked first, in this order, and only the first one that fails
is reported:

1. `windowMinutes` and `windowDays` together: "Use either windowMinutes or windowDays, not both."
2. `windowDays` in raw mode — an explicit `mode: raw`, or no `mode` at all: "windowDays applies only to mode
   aggregate; raw mode is limited to windowMinutes up to 10080 (7 days)."

A call without `mode` behaves exactly as before HARDEN-7: raw rows over at most seven days.

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
makes `complete` stricter (below).

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

## Examples of what to ask

- "Why did nginx stop?" `source: nginx.service`, `minSeverity: error`, `windowMinutes: 30`.
- "Was a process killed for memory?" `text: out of memory`, `channel: kernel` (Linux).
- "Any disk errors?" `source: disk`, `eventId: 7` (Windows), or `text: I/O error` (Linux).
- "What went wrong in the last 20 minutes?" `minSeverity: error`, `windowMinutes: 20`.
- "Which errors have repeated over the last three months?" `mode: aggregate`, `windowDays: 90`, `minSeverity: error`.
