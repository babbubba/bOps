# Machine stability evidence (`system.stability`)

`system.stability` answers "is this machine stable, and what kind of instability happened when?" with typed, bounded,
read-only evidence over up to 180 days (design: [ADR-0041](architecture/adr/0041-typed-cross-platform-stability-evidence.md)).
It is `Read` risk, needs no approval, changes nothing and asks for no extra privilege. There is no query language: no
argument names a provider, event id, channel, journal field, message pattern or path.

## Arguments

| Argument | Meaning |
|---|---|
| `windowDays` | How far back to look, 1 to 180 (default 30). |
| `limit` | Signature groups returned, 1 to 200 (default 50). Category totals are never cut. |

There is no `mode`: the tool is aggregate-only.

## Categories

Every result lists all eight categories, in this order, each `applicable`, `notApplicable` (the platform has no typed
evidence that means it) or `notCollected` (related evidence exists that this version does not read; `detail` says where
to look instead).

| Category | Windows | Linux (kernel journal) |
|---|---|---|
| `unexpectedShutdown` | Kernel-Power 41, EventLog 6008 | `notApplicable` |
| `kernelCrash` | the System-log bugcheck record (WER-SystemErrorReporting 1001) | `Kernel panic - not syncing` |
| `kernelFault` | `notCollected` (non-display live dumps such as `0x1a1` are in `system.crashes`) | `Oops:`, `BUG:`, general protection fault |
| `hardwareError` | WHEA-Logger | MCE, EDAC |
| `displayFault` | Display 4101; LiveKernelEvent `0x117`, `0x141`, `0x193` | `notCollected` |
| `storageError` | disk 7, 11, 51, 153; stornvme 129; storahci 129 | `notCollected` |
| `memoryExhaustion` | `notCollected` | OOM kill, cgroup OOM kill |
| `minidump` | `%SystemRoot%\Minidump` inventory | `notCollected` |

`displayFault` is graphics-stack fault evidence — a timeout detection and recovery or a display-kernel live dump — not
proof that the adapter was reset. LiveKernelEvent `0x1a1` (a win32k watchdog, a hang signature) is never display
evidence. Boot context (Windows 6005 boots, 6006 clean shutdowns) is reported in `context` and never counted as
instability.

`hardwareError` carries a `severityClass`: `corrected` only when the platform says so (a warning-level WHEA-Logger record;
the Linux "Machine check events logged" line or an EDAC `CE`), `uncorrected` only when it says that (an error- or
critical-level WHEA record; an EDAC `UE`), and `unknown` otherwise — including informational WHEA records, which are
counted as `unknown`, never promoted.

## Result

`schemaVersion` 1: `status`, `complete`, `truncated`, `window`, `bucket`, `coverage`, `sources`, `categories`, `context`,
`timeline`, `observedGroups`, `returnedGroups`, `groups` and `minidumps`. No message text appears anywhere, and there
is no hint or pre-built call for another tool.

### Reading the counts

| Category `status` | `count` |
|---|---|
| `available` | The exact number of evidence records in the window. |
| `partial` | The records actually observed — a **lower bound** (a source was unreadable, stopped by a ceiling or the time bound, or skipped records). |
| `unavailable` | `null` — unknown. Never `0`. |
| `notApplicable` | `0` — every source of the category is absent on this machine (for example no minidump directory). |

A category whose **applicability** is `notApplicable` or `notCollected` has `status` and `count` `null`: that is not the
same as an applicable category whose status is `notApplicable` (count `0`).

`groups` and `timeline` rows of a `partial` category are observed, lower-bound evidence. A category with no group or
timeline row is evidence of zero events only when its `count` is `0` and `complete` is true; an `unavailable` category
has no rows and a `null` count. The category `count` is the authoritative value.

Counts are **evidence records, not incidents**: one display timeout can appear as a Display 4101 and a LiveKernelEvent
`0x141`; one bugcheck as a Kernel-Power 41 and a System-log 1001 (and as a `kernel-bugcheck` in `system.crashes`). Never
sum categories into an incident count, and never add them to `system.crashes` counts.

### Time

Every group and timeline row carries `timestampKind`: `occurred` when the source documents the time as when it happened
(WHEA corrected errors, Display 4101, storage errors, boot context, Linux kernel records), `reported` when it is when
the record was written (Kernel-Power 41, 6008 and the bugcheck record are logged at the next boot; WER live dumps when
WER processed them; fatal WHEA errors after the restart; minidump file times). A `reported` time is never an occurrence
time.

The timeline buckets are derived from `windowDays`: hours for 1–2 days, days for 3–31, weeks aligned to Monday 00:00 UTC
for 32–180, one dense row per category and timestamp kind.

### Minidumps

The inventory lists file names, sizes and last-write times only — files are never opened — with at most the 16 most recent
by last-write time and the totals in `observed` and `totalBytes`. `fileNameLocalDate` is the date a `MMDDYY-n-n.dmp` name carries,
in the machine's local calendar: descriptive only, never used as the crash time, for the window or for buckets. The
inventory describes the files present now and carries **no retention guarantee**: 0 files does not mean no past dumps.
A non-elevated Windows identity normally cannot read `C:\Windows\Minidump`; the category is then `unavailable` with
`null` counters and the result `Partial` — bOps never elevates. To analyze the contents of one listed minidump, use the
separate `system.dump_analyze` when it is available ([windows-dump-analysis.md](windows-dump-analysis.md)).

### Completeness and coverage

`complete` is true only when every source was read, nothing was cut and every log reaches back to the start of the
request. `coverage` lists `windows.channel.System`, `windows.channel.Application` (the display live dumps come from
the Application log, which is often the shorter one) or `linux.journald` with `oldestAvailableUtc` and `state`; the
minidump directory is listed with `state: unknown`, since a directory has no retention guarantee. On Linux the coverage
probe reads the oldest kernel-transport entry, the same scope as the evidence.

### Bounds

5,000 records per Windows Event Log source, 256 minidump entries, 10,000 Linux kernel records; one 20-second time bound
for the whole call, shared fairly between the sources so one flooded source cannot starve the rest; a fixed 32 KiB
result — groups are cut first, then minidump file names, and the category totals stay exact.

## Safety and audit

No PowerShell, `wevtutil`, WMI or shell; Windows uses `EventLogReader` with XPath built only from package constants and
the window; Linux starts `journalctl` directly with fixed switches. The audit summary holds status, completeness,
`windowDays`, per-category applicability, status and count, the boot-context counts, each source's status and the
coverage — never a component, a file name, a directory or any native field.

## Source tables, rules and aggregation (technical specification)

*Consolidated from ADR-0041 (§3, §8) during the architecture consolidation (DOC-02). The binding rules are in
`docs/architecture/current/04-evidence-and-diagnosis.md` (C04-R10); adding a category, provider tuple, Linux rule or bucket width needs an
amendment of that contract and a `schemaVersion` bump when the shape changes.*

### Windows sources

All Event Log sources are read through `EventLogReader` with an XPath built only from package constants and the window; event data is
read by field name from the record's own XML inside the package and no XML leaves the package.

| Category | Source name | Channel | Provider | Event ids | `code` | `component` | `severityClass` | `timestampKind` |
|---|---|---|---|---|---|---|---|---|
| `unexpectedShutdown` | `windows.stability.unexpectedShutdown` | System | `Microsoft-Windows-Kernel-Power` | 41 | `BugcheckCode` (a decimal field) as canonical hex (`0x0` when zero) | `powerButton` when `PowerButtonTimestamp` is non-zero, else `null` | `null` | `reported` |
| | (same source) | System | `EventLog` | 6008 | `null` | `null` | `null` | `reported` |
| `kernelCrash` | `windows.stability.kernelCrash` | System | `Microsoft-Windows-WER-SystemErrorReporting` | 1001 | leading `0x…` token of `param1`, canonical hex | `null` | `null` | `reported` |
| `kernelFault` | — (`notCollected`) | | | | | | | |
| `hardwareError` | `windows.stability.hardwareError` | System | `Microsoft-Windows-WHEA-Logger` | all | `null` | `null` | `corrected` (level 3, warning), `uncorrected` (level 1 or 2), else `unknown` | `occurred` when `corrected`, else `reported` |
| `displayFault` | `windows.stability.displayFault.system` | System | `Display` | 4101 | `null` | first data value when it matches `^[A-Za-z0-9_.-]{1,64}$` (the driver), else `null` | `null` | `occurred` |
| | `windows.stability.displayFault.wer` | Application | `Windows Error Reporting` | 1001 with `EventName` = `LiveKernelEvent` and `P1` (hex) in {`117`, `141`, `193`} | canonical hex of `P1` (`0x117`, `0x141`, `0x193`) | `null` | `null` | `reported` |
| `storageError` | `windows.stability.storageError` | System | `disk` (7, 11, 51, 153); `stornvme` (129); `storahci` (129) | as listed | `null` | first data value when it matches `^[A-Za-z0-9_.\\-]{1,128}$` (the device or port), else `null` | `null` | `occurred` |
| `memoryExhaustion` | — (`notCollected`) | | | | | | | |
| `minidump` | `windows.minidump` | — | — | — | `null` | `null` | `null` | `reported` |

- The XPath of each source filters natively on channel, provider, event id and the window. For `windows.stability.displayFault.wer` the
  `EventName` and `P1` conditions are tested after reading, so its 5,000-record ceiling counts every WER 1001 record of the window.
- `displayFault` means graphics-stack fault evidence, not proof of a reset: Display 4101 and LiveKernelEvent `0x117`
  (VIDEO_TDR_TIMEOUT_DETECTED) and `0x141` (VIDEO_ENGINE_TIMEOUT_DETECTED) record a timeout detection and recovery (TDR); `0x193`
  (VIDEO_DXGKRNL_LIVEDUMP) records a live dump taken by the display kernel (`dxgkrnl`), which shows a graphics-stack problem but does not by
  itself prove an adapter reset. LiveKernelEvent `0x1a1` (WIN32K_CALLOUT_WATCHDOG_LIVEDUMP, a win32k callout watchdog hang signature) is
  not display evidence: like every other non-display LiveKernelEvent it is listed by `system.crashes` as `kind: kernel-live-dump` and
  `system.stability` does not count it.
- `kernelFault` is `notCollected` on Windows: "Non-display LiveKernelEvent reports (for example 0x1a1) are listed by system.crashes as
  kernel-live-dump." `memoryExhaustion` is `notCollected` on Windows: "Windows low-memory diagnostics are not collected by this version; use
  system.events."
- Boot context (not a category): source `windows.stability.bootContext`, System channel, provider `EventLog`, event 6005 (event log service
  started — a boot) and 6006 (event log service stopped — a clean shutdown), `occurred`; reported only in `context`, never counted as
  instability.
- Minidump inventory: the directory `%SystemRoot%\Minidump` (resolved from the Windows folder, never from an argument or environment
  variable), top level only, entries whose name matches `^[A-Za-z0-9_.-]{1,128}$` and ends in `.dmp` (case-insensitive); reparse points are
  not followed and other entries are counted as skipped (source `partial`). Only the name, the size in bytes and the last-write time (UTC) are
  read; the file is never opened. A file belongs to the window by its last-write time. `fileNameLocalDate` is the date encoded in a name of the
  form `MMDDYY-<digits>-<digits>.dmp`, as `20YY-MM-DD`, when that is a valid date, else `null`; it is descriptive file metadata, never an
  occurrence time. A missing directory is `notApplicable` ("The minidump directory does not exist."); an unreadable one is `unavailable`
  ("The minidump directory is not readable by this identity; it usually requires elevation."), which makes the result `partial`.

### Linux source

One source, `linux.journald.kernel`, collected by one `journalctl` invocation started directly (`UseShellExecute = false`, fixed environment
`LC_ALL=C`, `TZ=UTC`, no pager, no colours, every value its own `ArgumentList` item): `--no-pager --output=json --reverse --utc --since=<from>
--until=<to> --lines=10000 --output-fields=MESSAGE,_TRANSPORT _TRANSPORT=kernel`. `_TRANSPORT=kernel` is used rather than `--dmesg`, which
would restrict the read to the current boot. Each record's `MESSAGE` is matched against the fixed rules below in this order, first match wins;
records matching no rule are ignored. "Starts with" is an ordinal, case-sensitive prefix test on the message as journald returns it.

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

Every Linux kernel-transport record is `occurred`. The other categories on Linux: `unexpectedShutdown` is `notApplicable` ("journald has no
typed record of an unclean shutdown; bOps does not infer one from missing shutdown messages."); `displayFault` is `notCollected` ("GPU fault and
reset messages are driver-specific and are not collected by this version."); `storageError` is `notCollected` ("Kernel block-layer and
controller errors are not collected by this version; use system.events with channel kernel."); `minidump` is `notCollected` ("Kernel crash dumps
(kdump) are not inventoried by this version."); `kernelCrash` carries the detail "A panic is visible only if it reached the persistent journal
before the machine stopped." and boot `context` is `notCollected`. An identity outside the `adm` and `systemd-journal` groups cannot see kernel
records: the source is then `partial` with that reason, so every Linux count of 0 is untrustworthy and the result is not `complete`.

### Result byte budget and envelope

The whole result is at most 32,768 UTF-8 bytes. When it would exceed the budget, groups are removed from the end and then minidump files; the
envelope always fits (a formatter that cannot fit it is an internal error caught by tests, never a silently shortened envelope).

### Aggregation and time buckets

- Group key: (`category`, `provider`, `eventId`, `code`, `component`, `severityClass`, `timestampKind`), compared ordinally (`null` is a value
  of its own). One group per distinct key with `count`, `firstSeenUtc` and `lastSeenUtc` (oldest and newest record time of the group, all of the
  group's kind). The `minidump` category forms one group (`provider`, `eventId`, `code`, `component`, `severityClass` all `null`,
  `timestampKind: reported`).
- Group order: category in the fixed order above, then `count` descending, then `lastSeenUtc` descending, then `provider`, `eventId`, `code`,
  `component`, `severityClass`, `timestampKind` (case-insensitive ordinal, then ordinal, `null` first). `observedGroups` is the number of
  distinct keys; `limit` and the byte budget cut from the end.
- Bucket width is derived deterministically from `windowDays`, never chosen by the implementation or the caller:

  | `windowDays` | `bucket.width` | Alignment of bucket starts | Maximum `bucket.count` |
  |---|---|---|---|
  | 1–2 | `hour` | the start of a UTC hour | 49 |
  | 3–31 | `day` | 00:00 UTC | 32 |
  | 32–180 | `week` | Monday 00:00 UTC | 27 |

  `bucket.firstStartUtc` is `window.fromUtc` rounded down to the alignment; bucket *i* is `[firstStartUtc + i × width, firstStartUtc + (i + 1) ×
  width)`; `bucket.count` is the number of buckets up to and including the one containing `window.toUtc`. The first and last buckets may extend
  outside the window; they count only records inside it.
- Timeline: one row per (`category`, `timestampKind`) with at least one record, ordered by category order and then `occurred` before
  `reported`; `counts` has exactly `bucket.count` integers, entry *i* being the number of the category's records of that kind whose time falls in
  bucket *i*. Boot `context` is not in the timeline. With at most 8 categories and 2 kinds the timeline has at most 16 rows.

The result is therefore deterministic for a given set of native records and a given `now`, comparable between calls with the same `windowDays`,
and bounded independently of how many records the logs hold.
