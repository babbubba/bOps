# System events (`system.events`)

`system.events` reads recent operating-system events for the agent, so a diagnosis can say why a service failed
and not only that it did. It is one read-only tool with the same arguments and the same result on both systems
(design: [ADR-0032](architecture/adr/0032-bounded-cross-platform-system-events.md)):

- **Windows:** the Event Log, through `EventLogReader` (channels `System` and `Application` by default).
- **Linux:** journald, through `journalctl` run directly (no shell).

It is `Read` risk: it needs no approval, changes nothing and asks for no extra privilege.

## Arguments

Every argument is optional. A value out of range is rejected with the reason, never adjusted.

| Argument | Meaning |
|---|---|
| `windowMinutes` | How far back to look, 1 to 10080 (default 60). |
| `minSeverity` | `critical`, `error`, `warning`, `information` or `verbose`: events at least this severe. Omitted: every severity. |
| `source` | Exact, case-insensitive. Windows: the provider name (`Service Control Manager`). Linux: the syslog identifier or the systemd unit (`nginx` or `nginx.service`). |
| `eventId` | Windows: the event id (`7036`). Linux: the 32-digit hexadecimal `MESSAGE_ID`. |
| `channel` | Windows: a channel such as `Microsoft-Windows-Kernel-Power/Thermal-Operational` (default: `System` and `Application`). Linux: a journal transport: `kernel`, `journal`, `syslog`, `stdout`, `driver` or `audit` (default: all). |
| `text` | Case-insensitive text the message must contain, up to 256 characters. |
| `limit` | Events to return, 1 to 500 (default 50). |
| `maxOutputBytes` | Size of the result, 4096 to 65536 (default 32768). |

Names (`source`, `eventId`, `channel`) accept letters, digits and ``space _ . @ : / ( ) -`` only, so a value can
never carry query syntax.

## Result

JSON, newest event first, ties in a fixed order:

```json
{
  "schemaVersion": 1,
  "status": "complete",
  "complete": true,
  "window": { "fromUtc": "2026-09-21T11:00:00.0000000Z", "toUtc": "2026-09-21T12:00:00.0000000Z" },
  "observedEvents": 2,
  "returnedEvents": 2,
  "truncated": false,
  "sources": [ { "name": "linux.journald", "status": "available", "detail": null } ],
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

**Read `complete` before trusting an empty list.** It is true only when every source was read fully and nothing
was cut. Otherwise:

- `status` is `partial` or `unavailable`, and each entry of `sources` says which source and why (permission
  denied, channel missing, `journalctl` not installed, a read that timed out, records that could not be read).
  A source that could not be read is never reported as an empty log.
- `truncated` is true when the `limit`, the byte budget or the scan ceiling (10,000 records per source) cut the
  result. Narrow the window or add a filter.
- A native level bOps cannot place is `unknown`, and such an event never passes a `minSeverity` filter.

Fields that a platform does not record are `null`: `unit` is Linux only, and Windows records no process name.
A Windows event whose provider registers no message text is returned with its data values as the message.

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
  status, completeness, observed and returned counts, counts by severity, and the status of each source.
- No shell, no `PowerShell`, no `wevtutil`; on Linux every `journalctl` switch is fixed in code and every value
  is a separate argument.

## Examples of what to ask

- "Why did nginx stop?" `source: nginx.service`, `minSeverity: error`, `windowMinutes: 30`.
- "Was a process killed for memory?" `text: out of memory`, `channel: kernel` (Linux).
- "Any disk errors?" `source: disk`, `eventId: 7` (Windows), or `text: I/O error` (Linux).
- "What went wrong in the last 20 minutes?" `minSeverity: error`, `windowMinutes: 20`.
