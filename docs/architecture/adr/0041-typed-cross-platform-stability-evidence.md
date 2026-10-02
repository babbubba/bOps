# ADR-0041 — Typed cross-platform stability evidence (`system.stability`)

Status: Accepted (2026-10-02, operator decision through the HARDEN-7 architecture gate, with the independent-review
corrections recorded below)
Date: 2026-10-02

Governs the `system.stability` part of HARDEN-7 of the V1.3.x reliability train
([packet](../../../agentic/_tasks/2026-09-25-v1.3x-harden-07-windows-stability-evidence.md); plan
[`2026-09-25-v1.3x-reliability-hardening.md`](../../../agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md) §2.3, §2.4,
finding F-10, hypothesis H-7). Accepted together with the HARDEN-7 amendment of
[ADR-0032](0032-bounded-cross-platform-system-events.md), whose §1 (modes), §5 (temporal coverage), §6 (completeness)
and §9 (schema versions) this ADR uses without restating. §6 (evidence time) and §7 (identity and correlation) of this
ADR are normative for `system.crashes` as well. Completeness is the `ToolResultCompleteness` of the ADR-0022 HARDEN-6
amendment.

## Context

On the operator workstation the evidence that answers "why has this PC been freezing for months?" existed in the
System log — 16 Kernel-Power 41 unexpected shutdowns, at least 200 WHEA-Logger hardware errors, 5 bugcheck records
since August — and no typed tool surfaced it (plan §2.4). `system.events` can find each of them only if the model
already knows the provider name and event id, and then returns one row per record within 7 days. `system.crashes` sees
WER reports of kernel events but not shutdowns, hardware errors, display driver faults or storage resets, which are the
signals that distinguish failing hardware, a failing driver and a failing application.

The capability must stay inside the safety model: read-only, typed, bounded, no query language, no caller-chosen
provider or event id, no dump content, no privilege it does not already have, and honest about what it could not see
and about which of its timestamps are occurrence times.

## Decision

### 1. Tool identity and placement

`system.stability` is a `RiskLevel.Read` tool with no `VerificationSpec`, contributed by `bOps.Packages.System.Windows`
(`WindowsStabilityTool`) and `bOps.Packages.System.Linux` (`LinuxStabilityTool`), each declaring its own platform
(architecture rule A8). The manifest, argument reading, category table, aggregation, bucketing, coverage evaluation,
ordering, bounds, JSON output and audit summary live in `bOps.Packages.System.Core`
(`SystemToolManifests.Stability(platform)` and a `SystemStabilityToolBase` implementing `IToolAuditSummaryProvider`);
each OS package implements only collection. The two manifests are identical apart from `Platforms`. Every provider
name, event id, journal match and message pattern below is a constant of the OS package; none is named by the core and
none comes from the caller.

### 2. Arguments

Every argument is optional; out-of-range values are rejected by the runtime from the manifest constraints (HARDEN-6),
never clamped; unknown arguments are rejected.

| Name | Type | Manifest constraint | Default | Meaning |
|---|---|---|---|---|
| `windowDays` | Integer | `Minimum = 1`, `Maximum = 180` | 30 | How far back to look: the window is `[now − windowDays × 1440 minutes, now]`, UTC. |
| `limit` | Integer | `Minimum = 1`, `Maximum = 200` | 50 | Maximum number of `groups` returned. |

There is no `mode` (the tool is aggregate-only), no category filter (the eight categories are fixed and their totals
are always returned), and no argument that names a provider, event id, channel, journal field, message text, path or
query. The output budget is fixed (§5); it is not an argument. There are no cross-field rules.

The manifest description states: read-only machine stability evidence over up to 180 days; the eight categories;
aggregated counts with typed `occurred`/`reported` times and a time-bucket timeline; source status and temporal
coverage; that a category count is exact only when the category `status` is `available`, a lower bound when it is
`partial` and `null` (unknown) when it is `unavailable`; that a category count of 0 is trustworthy only when `complete` is
true; that category counts are evidence records and must not be summed into an incident count; and that per-crash detail
is `system.crashes` and raw records are `system.events`.

### 3. Categories

Eight categories, in this fixed order: `unexpectedShutdown`, `kernelCrash`, `kernelFault`, `hardwareError`,
`displayFault`, `storageError`, `memoryExhaustion`, `minidump`. Every result lists all eight, on both platforms, each
with an `applicability`:

- `applicable` — this platform has typed evidence for the category and this tool collects it.
- `notApplicable` — this platform has no typed evidence that means this category; bOps does not infer one.
- `notCollected` — this platform has related evidence that this version does not read; absence of findings means
  nothing, and `detail` says where to look instead.

`detail` is a fixed package text (at most 256 characters) for `notApplicable` and `notCollected`, and for applicable
categories with a known visibility limitation; otherwise `null`.

#### 3.1 Windows

All Event Log sources are read through `EventLogReader` with an XPath built only from package constants and the
window. Event data is read by field name from the record's own XML inside the package; no XML leaves the package.

| Category | Source name | Channel | Provider | Event ids | `code` | `component` | `severityClass` | `timestampKind` |
|---|---|---|---|---|---|---|---|---|
| `unexpectedShutdown` | `windows.stability.unexpectedShutdown` | System | `Microsoft-Windows-Kernel-Power` | 41 | `BugcheckCode` (a decimal field) as canonical hex (`0x0` when zero) | `powerButton` when `PowerButtonTimestamp` is non-zero, else `null` | `null` | `reported` |
| | (same source) | System | `EventLog` | 6008 | `null` | `null` | `null` | `reported` |
| `kernelCrash` | `windows.stability.kernelCrash` | System | `Microsoft-Windows-WER-SystemErrorReporting` | 1001 | leading `0x…` token of `param1`, canonical hex | `null` | `null` | `reported` |
| `kernelFault` | — | — | — | — | — | — | — | — |
| `hardwareError` | `windows.stability.hardwareError` | System | `Microsoft-Windows-WHEA-Logger` | all | `null` | `null` | `corrected` (level 3, warning), `uncorrected` (level 1 or 2), else `unknown` | `occurred` when `corrected`, else `reported` |
| `displayFault` | `windows.stability.displayFault.system` | System | `Display` | 4101 | `null` | first data value when it matches `^[A-Za-z0-9_.-]{1,64}$` (the driver), else `null` | `null` | `occurred` |
| | `windows.stability.displayFault.wer` | Application | `Windows Error Reporting` | 1001 with `EventName` = `LiveKernelEvent` and `P1` (hex) ∈ {`117`, `141`, `193`} | canonical hex of `P1` (`0x117`, `0x141`, `0x193`) | `null` | `null` | `reported` |
| `storageError` | `windows.stability.storageError` | System | `disk` (7, 11, 51, 153); `stornvme` (129); `storahci` (129) | as listed | `null` | first data value when it matches `^[A-Za-z0-9_.\\-]{1,128}$` (the device or port), else `null` | `null` | `occurred` |
| `memoryExhaustion` | — | — | — | — | — | — | — | — |
| `minidump` | `windows.minidump` | — | — | — | `null` | `null` | `null` | `reported` |

- The XPath of each source filters natively on channel, provider, event id and the window. For
  `windows.stability.displayFault.wer` the `EventName` and `P1` conditions are tested after reading, so its 5,000-record
  ceiling (§4) counts every WER 1001 record of the window.
- **`displayFault` means graphics-stack fault evidence, not proof of a reset.** Display 4101 and LiveKernelEvent
  `0x117` (VIDEO_TDR_TIMEOUT_DETECTED) and `0x141` (VIDEO_ENGINE_TIMEOUT_DETECTED) record a timeout detection and
  recovery (TDR); LiveKernelEvent `0x193` (VIDEO_DXGKRNL_LIVEDUMP) records a live dump taken by the display kernel
  (`dxgkrnl`), which shows a graphics-stack problem but does not by itself prove that the adapter was reset. The `code`
  of each group keeps the distinction. LiveKernelEvent `0x1a1` (WIN32K_CALLOUT_WATCHDOG_LIVEDUMP, a win32k callout
  watchdog — a hang signature, plan §2.3) is **not** display evidence and is not in this category; like every other
  non-display LiveKernelEvent it is listed by `system.crashes` as `kind: kernel-live-dump`, and `system.stability` does
  not count it (see `kernelFault` below). No separate category is introduced for it.
- `kernelFault` is `notCollected` on Windows: "Non-display LiveKernelEvent reports (for example 0x1a1) are listed by
  system.crashes as kernel-live-dump."
- `memoryExhaustion` is `notCollected` on Windows: "Windows low-memory diagnostics are not collected by this version;
  use system.events."
- **Boot context** (not a category): source `windows.stability.bootContext`, System channel, provider `EventLog`, event
  6005 (event log service started — a boot) and 6006 (event log service stopped — a clean shutdown), `occurred`. They
  are reported only in `context` (§5) and never counted as instability.
- **Minidump inventory.** The directory `%SystemRoot%\Minidump` (resolved from the Windows folder, never from an
  argument or environment variable), top level only, entries whose name matches `^[A-Za-z0-9_.-]{1,128}$` and ends in
  `.dmp` (case-insensitive); reparse points are not followed and other entries are counted as skipped (source
  `partial`). For each file only the name, the size in bytes and the last-write time (UTC) are read; the file is never
  opened. A file belongs to the window by its last-write time. `fileNameLocalDate` is the date encoded in a name of the
  form `MMDDYY-<digits>-<digits>.dmp`, as `20YY-MM-DD`, when that is a valid date, else `null`; it is descriptive file
  metadata, not an occurrence time (§6). A missing directory is
  `notApplicable` ("The minidump directory does not exist."); an unreadable one is `unavailable` ("The minidump
  directory is not readable by this identity; it usually requires elevation."), which makes the result `partial`.

#### 3.2 Linux

One source, `linux.journald.kernel`, collected by one `journalctl` invocation in the ADR-0032 model: started directly,
`UseShellExecute = false`, fixed environment (`LC_ALL=C`, `TZ=UTC`, no pager, no colours), every value its own
`ArgumentList` item: `--no-pager --output=json --reverse --utc --since=<from> --until=<to> --lines=10000
--output-fields=MESSAGE,_TRANSPORT _TRANSPORT=kernel`. `_TRANSPORT=kernel` is used rather than `--dmesg`, which would
restrict the read to the current boot. Each record's `MESSAGE` is matched against the fixed rules below, in this order,
first match wins; records matching no rule are ignored. "Starts with" is an ordinal, case-sensitive prefix test on the
message as journald returns it.

| Category | Rule | `code` | `component` | `severityClass` |
|---|---|---|---|---|
| `kernelCrash` | starts with `Kernel panic - not syncing` | `panic` | `null` | `null` |
| `kernelFault` | starts with `Oops:` | `oops` | `null` | `null` |
| | starts with `BUG: unable to handle` or `kernel BUG at` | `bug` | `null` | `null` |
| | starts with `general protection fault` | `general-protection-fault` | `null` | `null` |
| `hardwareError` | starts with `mce: [Hardware Error]: Machine check events logged` | `mce` | `null` | `corrected` |
| | starts with `mce: [Hardware Error]:` | `mce` | `null` | `unknown` |
| | starts with `EDAC ` and contains ` CE ` | `edac` | `null` | `corrected` |
| | starts with `EDAC ` and contains ` UE ` | `edac` | `null` | `uncorrected` |
| `memoryExhaustion` | starts with `Out of memory: Killed process` | `oom-kill` | the process name in `Killed process <pid> (<name>)`, 1–64 characters without control characters, else `null` | `null` |
| | starts with `Memory cgroup out of memory: Killed process` | `cgroup-oom-kill` | as above | `null` |

Every Linux kernel-transport record is `occurred` (§6). The other categories:

- `unexpectedShutdown`: `notApplicable` — "journald has no typed record of an unclean shutdown; bOps does not infer one
  from missing shutdown messages."
- `displayFault`: `notCollected` — "GPU fault and reset messages are driver-specific and are not collected by this
  version."
- `storageError`: `notCollected` — "Kernel block-layer and controller errors are not collected by this version; use
  system.events with channel kernel."
- `minidump`: `notCollected` — "Kernel crash dumps (kdump) are not inventoried by this version."
- `kernelCrash` (applicable) carries the `detail` "A panic is visible only if it reached the persistent journal before
  the machine stopped." Boot `context` is `notCollected` on Linux.

An identity outside the `adm` and `systemd-journal` groups cannot see kernel records; as in ADR-0032 the source is then
`partial` with that reason, so every Linux count of 0 is untrustworthy and the result is not `complete`.

### 4. Collection bounds and coverage stores

- **Windows:** each Event Log source is read in reverse order with a ceiling of 5,000 records per source (so a flood
  in one category cannot hide another); the minidump enumeration stops after 256 entries. **Linux:** 10,000 kernel
  records. Hitting a ceiling makes the source `partial`, the result `truncated`, and sets the source's
  `examinedFromUtc` (ADR-0032 amendment §5).
- **Time:** every read of one call, including the coverage probes, shares one 20-second bound (shorter than the 30-second
  default tool timeout). A read stopped by the bound makes its source `partial`; cancellation stops native reads
  promptly (`CancelReading`, killing the `journalctl` child).
- **Records** that cannot be read or whose required data is malformed are skipped and make their source `partial`;
  values that fail their pattern become `null`, never a guess.
- **Coverage stores** (ADR-0032 amendment §5): Windows — `windows.channel.System` (`eventLog`) backs every
  `windows.stability.*` source except `windows.stability.displayFault.wer`, which is backed by
  `windows.channel.Application` (`eventLog`); `windows.minidump` is a `directory` store. Linux — `linux.journald`
  (`journal`) backs `linux.journald.kernel`.

### 5. Result contract (`schemaVersion` 1)

```json
{
  "schemaVersion": 1,
  "status": "partial",
  "complete": false,
  "truncated": false,
  "window": { "fromUtc": "2026-04-05T10:00:00.0000000Z", "toUtc": "2026-10-02T10:00:00.0000000Z" },
  "bucket": { "width": "week", "firstStartUtc": "2026-03-30T00:00:00.0000000Z", "count": 27 },
  "coverage": {
    "requestedFromUtc": "2026-04-05T10:00:00.0000000Z",
    "requestedToUtc": "2026-10-02T10:00:00.0000000Z",
    "state": "partial",
    "stores": [
      { "name": "windows.channel.Application", "basis": "eventLog", "oldestAvailableUtc": "2026-09-02T04:11:31.0000000Z", "logMaximumBytes": 20971520, "state": "partial" },
      { "name": "windows.channel.System", "basis": "eventLog", "oldestAvailableUtc": "2026-07-31T06:12:09.0000000Z", "logMaximumBytes": 20971520, "state": "partial" },
      { "name": "windows.minidump", "basis": "directory", "oldestAvailableUtc": null, "logMaximumBytes": null, "state": "unknown" }
    ]
  },
  "sources": [
    { "name": "windows.minidump", "status": "unavailable", "detail": "The minidump directory is not readable by this identity; it usually requires elevation.", "examinedFromUtc": null },
    { "name": "windows.stability.unexpectedShutdown", "status": "available", "detail": null, "examinedFromUtc": "2026-04-05T10:00:00.0000000Z" }
  ],
  "categories": [
    { "category": "unexpectedShutdown", "applicability": "applicable", "status": "available", "detail": null, "count": 16 },
    { "category": "kernelFault", "applicability": "notCollected", "status": null, "detail": "Non-display LiveKernelEvent reports (for example 0x1a1) are listed by system.crashes as kernel-live-dump.", "count": null },
    { "category": "minidump", "applicability": "applicable", "status": "unavailable", "detail": null, "count": null }
  ],
  "context": { "applicability": "applicable", "status": "available", "detail": null, "boots": 61, "cleanShutdowns": 44 },
  "timeline": [
    { "category": "unexpectedShutdown", "timestampKind": "reported", "counts": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 0, 3, 1, 2, 4, 1, 2, 0] }
  ],
  "observedGroups": 12,
  "returnedGroups": 12,
  "groups": [
    {
      "category": "unexpectedShutdown",
      "provider": "Microsoft-Windows-Kernel-Power", "eventId": "41",
      "code": "0x0", "component": null, "severityClass": null, "timestampKind": "reported",
      "count": 14,
      "firstSeenUtc": "2026-08-01T07:12:44.0000000Z", "lastSeenUtc": "2026-09-25T06:01:02.0000000Z"
    }
  ],
  "minidumps": {
    "applicability": "applicable", "status": "unavailable",
    "directory": "C:\\Windows\\Minidump",
    "observed": null, "returned": null, "totalBytes": null,
    "files": []
  }
}
```

(The example abbreviates `sources`, `categories` and `timeline`; a real result lists every source and all eight
categories.)

Field rules:

- **`status`, `truncated`, `complete`** — `status` is the existing rule over `sources` (`complete`, `partial`,
  `unavailable`; `notApplicable` sources excluded). `truncated` is true when a scan ceiling or the time bound stopped a
  source, when `limit` cut `groups`, or when the byte budget removed anything. `complete` and the typed
  `ToolResultCompleteness` follow the ADR-0032 amendment §6 exactly. `notApplicable` and `notCollected` categories are
  not sources and never affect `status`, `complete` or `Completeness`.
- **`sources`** — every source of the platform, ordered by `name` (case-insensitive ordinal, then ordinal), with
  `status` (`available`, `partial`, `unavailable`, `unsupported`, `notApplicable`), a bounded `detail` (≤ 256
  characters) and `examinedFromUtc`.
- **`categories`** — all eight, in the fixed order. For an applicable category: `status` is derived from its sources
  (all `available` → `available`; all `notApplicable` → `notApplicable`; otherwise `partial` when at least one source is
  `available` or `partial`, else `unavailable`). `count` is the number of its evidence records in the window after
  deduplication (§7), computed before `limit` and the byte budget so it is never reduced by them, and its meaning
  depends on the category `status`:

  | Category `status` | `count` |
  |---|---|
  | `available` | The exact number of evidence records in the window. |
  | `partial` | The number of records actually observed — a **lower bound**: a source of the category was unreadable, stopped by a ceiling or the time bound, or skipped records. |
  | `unavailable` | `null` — unknown. No source of the category could be read; this is never reported as `0`. |
  | `notApplicable` | `0` — every source of the category is absent on this machine (for example no minidump directory), so there is nothing that could hold evidence; `detail` says why. |

  For the `notApplicable` and `notCollected` **applicabilities**: `status` and `count` are `null`. Unknown evidence is
  never represented as absence of evidence. Categories carry no times; times are in `groups` and `timeline`.
- **`context`** — Windows: `boots` (6005) and `cleanShutdowns` (6006) in the window, with the status of
  `windows.stability.bootContext`; both counts are `null` when that source is `unavailable` and lower bounds when it is
  `partial`. Linux: `applicability: notCollected`, `status`, `boots` and `cleanShutdowns` `null`.
- **`bucket` and `timeline`** — §8.
- **`groups`** — §8. `provider` and `eventId` are the native tuple of the group's records (Windows), or `"kernel"` and
  `null` (Linux), or `null` and `null` (`minidump`). A group carries no hint, query or pre-built argument set for
  another tool: the model chooses any follow-up call (for example `system.events` with its own window and mode) itself.
- **`minidumps`** — Windows: `directory` (the resolved path), `observed` (files in the window), `totalBytes` (their total
  size), `returned` and `files` — at most the 16 newest by last-write time, each `{ "name", "sizeBytes",
  "fileTimeUtc", "timestampKind": "reported", "fileNameLocalDate" }`. The 16-file listing is a bounded reference list
  by design; the category count and the `minidump` group carry the totals, so a longer directory does not set
  `truncated` (only the enumeration ceiling or the byte budget does). The counters follow the category rule: with
  `status` `available`, `observed` and `totalBytes` are exact; with `partial` (enumeration ceiling reached, entries
  skipped or unreadable) they are the values actually observed — **lower bounds**; with `unavailable` (the directory
  could not be read at all) `observed`, `returned` and `totalBytes` are `null`, `files` is empty and `status` says why —
  never `0`. `returned` is otherwise the number of entries in `files`. A missing directory (`notApplicable`) has
  `observed`, `returned` and `totalBytes` `0`. The inventory describes the files present now; a directory carries no
  retention guarantee (ADR-0032 amendment §5), so it says nothing about dumps that were deleted. Linux:
  `applicability: notCollected`; `status`, `directory`, `observed`, `returned` and `totalBytes` `null`; `files` empty.
- **Strings** — `code` ≤ 32 characters, `component` ≤ 128, `detail` ≤ 256, file names ≤ 128; every value either
  matched its pattern or is `null`. No message text appears anywhere in the result.
- **Budget** — the UTF-8 result is at most 32,768 bytes. When it would be larger, groups are removed from the end of
  their order, then minidump `files` from the end; `returnedGroups`/`returned` are updated and `truncated` is set. The
  envelope (every field except `groups` and `minidumps.files`) is bounded by construction — 8 categories, at most 8
  sources, 3 stores and 16 timeline rows of at most 49 integers — and always fits; a formatter that cannot fit it is an
  internal error caught by tests, never a silently shortened envelope.

### 6. Evidence time (normative for `system.stability` and `system.crashes`)

`timestampKind` has two values:

- `occurred` — by the documented semantics of the source, the native time is when the described thing happened.
- `reported` — the time the system recorded, processed or wrote evidence about it, which may be later (a boot after the
  crash, a WER processing burst weeks later, a dump file written at the next start).

**When bOps cannot establish that a time is an occurrence time, it is `reported`.** A `reported` time is never
relabelled, displayed or aggregated as an occurrence time.

| Evidence | Time used | `timestampKind` |
|---|---|---|
| Application Error 1000 (Application) | record time | `occurred` |
| WER 1001 (Application, `Windows Error Reporting`) | record time — WER processing | `reported` |
| Report.wer with `EventTime`, every kind except `BlueScreen` | `EventTime` (FILETIME) | `occurred` |
| Report.wer with `EventTime`, `BlueScreen` (`kernel-bugcheck`) | `EventTime` (FILETIME) — written after the reboot (correction of 2026-10-02 below) | `reported` |
| Report.wer without `EventTime` | file last-write time | `reported` |
| `Microsoft-Windows-WER-SystemErrorReporting` 1001 (System) | record time — logged at the next boot | `reported` |
| Kernel-Power 41 | record time — logged at the next boot | `reported` |
| EventLog 6008 | record time — logged at the next boot (the shutdown time is only in localized message text and is not parsed) | `reported` |
| EventLog 6005, 6006 | record time | `occurred` |
| WHEA-Logger, `corrected` | record time | `occurred` |
| WHEA-Logger, `uncorrected` or `unknown` | record time — a fatal error is logged after the restart | `reported` |
| Display 4101; `disk`, `stornvme`, `storahci` | record time | `occurred` |
| Minidump file | last-write time | `reported` |
| Linux `coredumpctl` | the core dump's timestamp | `occurred` |
| Linux kernel-transport journal record | realtime timestamp | `occurred` |

- **Minidump file names.** A minidump's only time is its file last-write time, which is `reported`: Windows writes the
  file when it extracts the dump, normally at the next start. The `MMDDYY` encoded in a name of the form
  `MMDDYY-<digits>-<digits>.dmp` is returned as `fileNameLocalDate`: the date the name carries, at day precision, in the
  machine's local calendar, never converted to UTC. bOps does **not** claim that it is the day of the bugcheck — nothing
  available to this version proves whether Windows encodes the crash day or the extraction day — so it is descriptive
  metadata only: it is never an occurrence time, never a `timestampKind`, and never used for window membership,
  bucketing, ordering or `firstSeenUtc`/`lastSeenUtc`. A minidump time becomes an occurrence time only through an
  occurrence source of the same crash (for `system.crashes`, a Report.wer `EventTime` merged by report identity, §7).
  Whether the file-name date reliably equals the crash day may be re-evaluated separately on real-host evidence.
- **BlueScreen reports (evidence-driven correction, 2026-10-02).** A `BlueScreen` Report.wer `EventTime` is written when WER
  creates the report after the reboot, so it is `reported`, not `occurred`; see the correction at the end of this ADR. The
  precedence below is unchanged: the `EventTime` keeps its position as the primary time and only its kind changes. Nothing is
  promoted in its place — not the Kernel-Power 41 time, not a minidump file time, not a file-name date — so a bugcheck with no
  source that proves its occurrence stays `reported`.
- **Merged crash records** (`system.crashes`, §7): the primary `timestampUtc` is, in order of precedence, the Report.wer
  `EventTime` (`occurred`), the Application Error 1000 record time (`occurred`), then the earliest WER 1001 record time
  (`reported`). When the primary time is `occurred` and a WER 1001 is a member, `reportedUtc` is the earliest WER 1001
  record time; otherwise `null`. `source` names the evidence that supplied the primary time.
- **Aggregation** never mixes kinds: `timestampKind` is part of every `system.crashes` and `system.stability`
  aggregation key (ADR-0032 amendment §4; §8 here), and every `timeline` row is per category **and** kind.
  `system.events` groups carry record times and no `timestampKind` (ADR-0032 amendment §3).

### 7. Identity, deduplication and correlation (normative for `system.stability` and `system.crashes`)

- **Exact native identity.** Two Windows Event Log records with the same channel and record id are one record. Within
  a call this removes duplicates from overlapping reads; it is not used across calls.
- **WER report identity.** A crash's identity is the set of report GUIDs its records carry: Application Error 1000
  `IntegratorReportId`; WER 1001 `ReportId`; Report.wer `ReportIdentifier` and `IntegratorReportIdentifier`. A value is
  used only if it parses as a GUID and is not the all-zero GUID; it is normalized to lower-case `D` format. Records
  whose identity sets intersect, transitively, are **one crash**: one `system.crashes` record, with fields taken by the
  precedence of §6 for time and, for the other fields, the first non-null in the order Report.wer, Application Error
  1000, WER 1001 (Application Error 1000 first for `process`, `pid`, `faultModule` and `exceptionCode`). `reportId` is
  the WER 1001 `ReportId` or Report.wer `ReportIdentifier` when present, else the first other GUID in ordinal order.
  Repeated WER 1001 records of one report (a re-processing burst) therefore count once.
- **No stable identity, no merge.** A record without a usable GUID is never merged with another record. bOps never
  correlates by time proximity, process name, code, bucket, message text or any other similarity; a possible duplicate
  is reported as such (`uncorrelatedCount` in `system.crashes` groups) and is not removed.
- **`system.stability` counts evidence records, not incidents.** Within a category, records are deduplicated only by
  the exact identities above (WER 1001 `LiveKernelEvent` records in `displayFault` by `ReportId`). Records that probably
  describe one incident but share no identity stay separate: a Display 4101 and a LiveKernelEvent 141 of one display
  timeout (TDR) are two records in two groups; a Kernel-Power 41 with a non-zero `BugcheckCode` and the System-log 1001 of the
  same bugcheck are two records in two categories. The manifest description says that category counts are evidence
  records and must not be summed into an incident count.

### 8. Aggregation and time buckets

- **Group key:** (`category`, `provider`, `eventId`, `code`, `component`, `severityClass`, `timestampKind`), compared
  ordinally (`null` is a value of its own). One group per distinct key with `count`, `firstSeenUtc` and `lastSeenUtc`
  (oldest and newest record time of the group, all of the group's kind). The `minidump` category forms one group
  (`provider`, `eventId`, `code`, `component`, `severityClass` all `null`, `timestampKind: reported`).
- **Group order:** category in the fixed order of §3, then `count` descending, then `lastSeenUtc` descending, then
  `provider`, `eventId`, `code`, `component`, `severityClass`, `timestampKind` (case-insensitive ordinal, then ordinal,
  `null` first). `observedGroups` is the number of distinct keys; `limit` and the byte budget cut from the end.
- **Bucket width** is derived deterministically from `windowDays`, never chosen by the implementation or the caller:

  | `windowDays` | `bucket.width` | Alignment of bucket starts | Maximum `bucket.count` |
  |---|---|---|---|
  | 1–2 | `hour` | the start of a UTC hour | 49 |
  | 3–31 | `day` | 00:00 UTC | 32 |
  | 32–180 | `week` | Monday 00:00 UTC | 27 |

  `bucket.firstStartUtc` is `window.fromUtc` rounded down to the alignment; bucket *i* is
  `[firstStartUtc + i × width, firstStartUtc + (i + 1) × width)`; `bucket.count` is the number of buckets up to and
  including the one containing `window.toUtc`. The first and last buckets may extend outside the window; they count only
  records inside it.
- **Timeline:** one row per (`category`, `timestampKind`) with at least one record, ordered by category order and then
  `occurred` before `reported`; `counts` has exactly `bucket.count` integers, entry *i* being the number of the
  category's records of that kind whose time falls in bucket *i*. Boot `context` is not in the timeline. With at most 8
  categories and 2 kinds the timeline has at most 16 rows.

The result is therefore deterministic for a given set of native records and a given `now`, comparable between calls
with the same `windowDays`, and bounded independently of how many records the logs hold.

### 9. Coverage and completeness

`coverage` is the ADR-0032 amendment §5 object over the stores of §4 here; `complete` and `ToolResultCompleteness`
follow that amendment's §6 table. Consequences specific to this tool:

- A non-elevated Windows identity normally cannot read the minidump directory: that source is `unavailable`, `status`
  is `partial`, and the result is `Partial`, never `Complete` — elevation is not requested.
- Because `displayFault` reads the Application log, a request longer than the Application log's history makes
  `coverage.state` `partial` even when the System log reaches back further; the store list shows which log is short.
- The `windows.minidump` store is a `directory` store and never changes `coverage.state` (ADR-0032 amendment §5).
- Category counts of 0 are trustworthy only when `complete` is true; the manifest description says so. A category
  whose sources could not be read has `count: null`, never `0` (§5), whatever `complete` says.

### 10. Boundary with `system.crashes` and `system.events`

| Tool | Question it answers | Sources | Returns | Never returns |
|---|---|---|---|---|
| `system.events` | "What did this log record?" — any native record, found by caller filters | Event Log channels, journald | record-time groups or rows with messages | typed categories or `timestampKind` |
| `system.crashes` | "What crashed or hung, how often, which code, application and module, when?" — one record per crash | Application Error 1000, WER 1001 (Application), Report.wer, `coredumpctl` | crash groups or rows with report identity, typed codes, typed time, dump references (raw mode) | shutdowns, hardware, display, storage or memory-pressure evidence |
| `system.stability` | "Is this machine stable, and what kind of instability happened when?" — machine-level signals | System-log tuples of §3.1, WER 1001 LiveKernelEvent display codes only, minidump directory, Linux kernel journal | category totals, signature groups, a bucketed timeline, minidump inventory | messages, application or module names, process ids, report ids, WER buckets, `AttachedFiles` dump references, user-mode application crashes |

Two overlaps are deliberate and stated in both manifest descriptions:

- **Bugchecks.** `system.stability` counts each bugcheck in `kernelCrash` from the System-log
  `Microsoft-Windows-WER-SystemErrorReporting` 1001; `system.crashes` lists the WER report of it (Application WER
  1001 `BlueScreen`, kind `kernel-bugcheck`) with its dump reference and, when Report.wer provides it, the report's
  `EventTime` — a `reported` time written after the reboot, not the crash time (evidence-driven correction of 2026-10-02).
- **Display live dumps.** `system.stability` counts LiveKernelEvent `0x117`, `0x141` and `0x193` in `displayFault`;
  `system.crashes` lists every LiveKernelEvent, including `0x1a1`, as `kernel-live-dump`. Both read the WER 1001 data
  through one package-internal normalizer in `bOps.Packages.System.Windows`, so the two tools cannot classify the same
  record differently.

The model must not add counts across the two tools. Neither tool returns the other's dataset: `system.stability` has no
per-crash rows, `system.crashes` has no machine-level categories or timeline.

### 11. Security, privacy and audit

- Read-only; no state is changed. No elevation is requested and the tool never changes identity; what the identity
  cannot read is `unavailable` or `partial`, never empty and never escalated.
- No PowerShell, no `wevtutil`, no shell, no WMI. Windows uses `EventLogReader` with XPath built from package constants
  and the window only; Linux starts `journalctl` directly with the fixed switches of §3.2. No argument can carry a
  provider, event id, channel, journal field, message pattern, path or query: there is no event query language.
- No dump content: dump files are enumerated for name, size and time only and are never opened, read, parsed, hashed
  or uploaded. No raw Windows event XML and no journald field bag leave the package; the result contains no message
  text.
- Bounded records (§4 ceilings), bounded bytes (§5 budget), bounded strings (§5), bounded duration (§4, 20 s) and
  bounded output size, independent of log size.
- Native text that reaches the result (`component`, file names) is untrusted data matched against a fixed pattern or
  dropped; like every tool result it enters the context as delimited data and never alters runtime state.
- The result carries no hint, pre-built argument set or suggested call for another tool; it cannot steer a follow-up
  query, and its schema does not depend on another tool's arguments.
- **Audit:** the tool is an `IToolAuditSummaryProvider`. The summary holds `status`, `complete`, `truncated`,
  `windowDays`, per category `applicability`, `status` and `count`, `context` counts, each source's `name` and
  `status`, `coverage.state` and each store's `name`, `state` and `oldestAvailableUtc`. It never holds a group
  `component`, a minidump file name or directory, a message, or any native field. Telemetry carries no result content.

### 12. Public contract and compatibility

- New tool `system.stability`, `schemaVersion` 1; additive to both System packages and the conformance suite
  (`bOps.Packages.System.Conformance` gains stability manifest, envelope and bounds assertions run on real Windows and
  real Linux).
- **No change to `bOps.Abstractions`**: the manifest uses existing `ToolParameter` constraints, the result uses the
  existing `ToolCallResult.Completeness`, and the audit summary the existing `IToolAuditSummaryProvider`. All new
  contract types (limits, category table, formatter, tool base) are package-local in `bOps.Packages.System.Core`.
- No change to policy, runtime, persistence, the audit schema or the provider adapters. The core names no provider,
  event id, journal match or path.

## Alternatives considered

**Document the provider/event-id tuples and let the model use `system.events`.** Rejected: it is what failed (F-10).
The model has to know Windows internals, gets one row per record within 7 days, and has no typed time or category.

**Let the caller choose categories, providers or event ids.** Rejected: a caller-chosen provider/event-id list is a
query language in all but name, and it is the drift the packet names as a stop condition. The eight categories are
fixed, cheap and always returned.

**Fold the categories into `system.crashes`.** Rejected: shutdowns, WHEA errors and storage resets are not crashes, and
a single tool answering both questions would return two datasets in one shape or bloat both.

**A fixed bucket width.** Rejected: hourly buckets over 180 days (4,320 values per row) are unbounded in practice, weekly
buckets over one day carry no information. Three widths derived from `windowDays` keep every timeline at most 49 values
per row and deterministic.

**Buckets in the group key.** Rejected: groups would multiply by the number of buckets; a dense timeline per category and
kind carries the distribution at a fraction of the size.

**Read Report.wer and correlate in `system.stability` to upgrade LiveKernelEvent times to `occurred`.** Rejected for
this version: it duplicates `system.crashes`'s collection; `system.crashes` already carries occurrence times when they
exist, and `system.stability` labels its LiveKernelEvent times honestly as `reported`.

**Infer unexpected shutdowns on Linux from the boot list** (a boot whose journal lacks shutdown messages). Rejected: a
heuristic that turns a missing log line into a fact; `notApplicable` is honest. **Pattern-match GPU and block-layer
messages on Linux now.** Rejected for this version: driver-specific wording without a tested mapping would invent
Windows parity; `notCollected` says so and points to `system.events`.

**Parse minidumps for the bugcheck code.** Rejected: opening dump files is out of scope and a privacy boundary; the
System-log bugcheck record already carries the code.

**PowerShell `Get-WinEvent`, `wevtutil` or WMI event queries.** Rejected as in ADR-0032: a command surface for what the
.NET API does natively.

**Correlate display faults, shutdowns and bugchecks into incidents by time proximity.** Rejected: fuzzy matching turns a
hypothesis into a count. The tool reports evidence records with typed times; correlation is the model's reasoning
(HARDEN-9), stated as such.

**A `displayReset` category including LiveKernelEvent `0x1a1`** (the classification of the original packet). Rejected
by the independent review: `0x193` is a display-kernel live dump that does not prove a reset, and `0x1a1` is a win32k
callout watchdog (a hang signature), not display evidence. `displayFault` with `0x117`, `0x141` and `0x193` is what the
evidence supports; a separate category for `0x1a1` alone is not introduced.

**A per-group `drillDown` hint with pre-built `system.events` arguments.** Rejected by the independent review: it is not
needed for this capability, duplicates `provider`/`eventId` already in the group, ties this schema to another tool's
arguments, cannot carry a correct window (a hint without one would run with `system.events`' 60-minute default and miss
the group's records), and spends the output budget without adding evidence. The model chooses its own follow-up call.

**Treat the minidump file-name date as the occurrence day.** Rejected: no evidence available to this version proves
that Windows encodes the crash day rather than the extraction day. The date is returned as descriptive
`fileNameLocalDate` and the file time stays `reported`.

## Consequences

- "Why has this PC been freezing for months?" has a single bounded, typed first call: category totals, signatures and a
  weekly timeline over up to 180 days, with each log's reach and each source's gaps explicit.
- Windows and Linux share one contract without false parity: categories that a platform cannot evidence say
  `notApplicable` or `notCollected` instead of a reassuring zero, and a category or minidump counter whose sources could
  not be read is `null`, not `0`.
- On a non-elevated Windows host the result is `Partial` because the minidump directory is unreadable; that is the
  honest state, and the rest of the evidence is still returned.
- Some incidents appear as several evidence records (a display timeout in two groups, a bugcheck in two categories, a
  bugcheck in both `system.stability` and `system.crashes`); the descriptions and this ADR say so, and HARDEN-9's
  evidence-reasoning rule must treat them as related evidence, not as independent occurrences.
- Adding a category, a provider tuple, a Linux message rule or a bucket width changes the public result and needs an
  amendment to this ADR and a `schemaVersion` bump when the shape changes.
- No change to `bOps.Abstractions`, policy, runtime, persistence or the audit schema.

## Evidence-driven correction discovered during real Windows validation (2026-10-02)

Operator-decided, evidence-driven correction made during the HARDEN-7 implementation, after the real-Windows elevated
validation run on the operator workstation; it narrows one row of §6 and reopens nothing else.

- **Observation.** For `BlueScreen` (`kernel-bugcheck`) reports the Report.wer `EventTime` is later than the Kernel-Power 41
  record that Windows logs at the next boot, so it cannot be the crash time: `0x50` Report.wer `EventTime` 08:45:27Z vs
  Kernel-Power 41 08:44:37Z (2026-09-04; minidump written 08:44:47Z); `0x1e` 14:26:56 vs 14:26:35 (2026-08-06); `0x3b` 20:59:23
  vs 20:59:04 (2026-08-03) — always after the boot. For `LiveKernelEvent`
  reports the `EventTime` matches the live dump (the `0x193` dump `WATCHDOG-20260730-1010.dmp` has `EventTime` 2026-07-30
  08:10Z), and for application reports it follows the Application Error 1000 record by a fraction of a second (`APPCRASH`
  0.14 s).
- **Decision.** By the rule of §6 that a time not proven to be an occurrence time is `reported`, the Report.wer `EventTime`
  of a `BlueScreen` report is `reported`. `LiveKernelEvent` and application crash and hang reports keep `occurred`. The
  primary-time precedence of merged crashes is unchanged, and no other time (Kernel-Power 41, minidump file time,
  `fileNameLocalDate`) is promoted to an occurrence time in its place.
- **Effect.** `system.crashes` `kernel-bugcheck` groups and rows built from a Report.wer `EventTime` carry
  `timestampKind: reported` (and `reportedUtc: null`). No schema, argument or field changes; `system.stability` is unaffected
  (its bugcheck evidence is the System-log record, already `reported`).

## Independent review corrections (2026-10-02)

The independent architecture review of commit `017477e` returned CHANGES REQUIRED. Its blocking findings are resolved in
this text before any implementation:

- **R1** — the category is `displayFault` (Display 4101, LiveKernelEvent `0x117`, `0x141`, `0x193`); `0x1a1` is not
  display evidence and is left to `system.crashes` (`kernel-live-dump`), §3.1, §10.
- **R2** — a category or minidump counter whose sources could not be read is `null`, a `partial` one is a lower bound,
  §5.
- **R3** — `drillDown` is removed from schema 1; no other hint replaces it, §5, §11.
- **R4** — `occurredLocalDate` is replaced by the descriptive `fileNameLocalDate`; minidump times stay `reported`, §3.1,
  §6.
- **R5** — the plan, the HARDEN-7 and HARDEN-9 packets and D-037 are reconciled with this ADR and the ADR-0032
  amendment.

Non-blocking findings R6–R12 are implementation obligations recorded in the HARDEN-7 packet.
