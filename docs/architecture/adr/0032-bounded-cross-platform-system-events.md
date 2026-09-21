# ADR-0032 — Bounded cross-platform system events (`system.events`)

Status: Accepted
Date: 2026-09-21

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
