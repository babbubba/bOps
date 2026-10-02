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

The inventory lists file names, sizes and last-write times only — files are never opened — with at most the 16 newest
by name and the totals in `observed` and `totalBytes`. `fileNameLocalDate` is the date a `MMDDYY-n-n.dmp` name carries,
in the machine's local calendar: descriptive only, never used as the crash time, for the window or for buckets. The
inventory describes the files present now and carries **no retention guarantee**: 0 files does not mean no past dumps.
A non-elevated Windows identity normally cannot read `C:\Windows\Minidump`; the category is then `unavailable` with
`null` counters and the result `Partial` — bOps never elevates.

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
