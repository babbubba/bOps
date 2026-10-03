# System maintenance evidence

`system.updates`, `system.update_history`, `system.crashes` and `system.drivers` return bounded,
read-only evidence with source status, completeness and warnings. A missing, stale, inaccessible or
truncated source stays incomplete; it is never presented as a clean machine or complete empty history.

## Sources and limits

- **Windows:** Windows Update Agent (WUA) supplies pending updates and update history; Windows Error
  Reporting (WER) and Application Error/WER Event Log records supply crash metadata; `EnumDeviceDrivers`
  supplies loaded driver inventory. Driver names/addresses may be privilege-limited and therefore partial.
- **Linux:** fixed apt, dnf and zypper queries report pending package updates; local package-manager logs
  supply history; `coredumpctl` supplies bounded crash metadata, with known `core_pattern` metadata as a
  limited fallback; `/proc/modules` and bounded `/sys/module` metadata supply loaded modules.

No update is installed, downloaded or removed. No driver or module is loaded, unloaded, installed or
removed. Crash dump paths are metadata only: dump contents are never read or extracted. Linux commands
are fixed and direct; caller-controlled executables, arguments, shells and raw expressions are not used.

## `system.crashes` (schema 2)

Design: [ADR-0032 HARDEN-7 amendment](architecture/adr/0032-bounded-cross-platform-system-events.md) §4 and
[ADR-0041](architecture/adr/0041-typed-cross-platform-stability-evidence.md) §6–§7.

| Argument | Meaning |
|---|---|
| `mode` | `aggregate` (the default): crash groups. `raw`: one row per crash. |
| `sinceMinutes` | Window in minutes, 1 to 10080 (default 1440), in either mode. |
| `sinceDays` | Window in days, 1 to 180, **aggregate mode only**, never together with `sinceMinutes`. |
| `limit` | Groups or rows, 1 to 1000 (default 100). |

The two cross-field rules are checked first, in this order: both window arguments together; then `sinceDays` with
`mode: raw`. `sinceDays` needs no explicit `mode`, because aggregate is the default.

**One crash is one record.** On Windows, Application Error 1000 (`IntegratorReportId`), WER 1001 (`ReportId`) and
Report.wer (`ReportIdentifier`, `IntegratorReportIdentifier`) records whose report GUIDs intersect — transitively — are
merged into one crash; a record without a usable GUID is never merged, and nothing is ever matched by time, name,
module or code. A WER re-processing burst of one report therefore counts once. `observedItems` counts merged crashes.

**Typed meaning.** WER 1001 is read by field name (`EventName`, `P1`…`P10`, `Bucket`, `ReportId`, `AttachedFiles`),
with the documented positional layout as a fallback for records without names: `BlueScreen` → `kernel-bugcheck`
(`P1` as `bugcheckCode`), `LiveKernelEvent` → `kernel-live-dump` (`P1` as `liveDumpCode`), `APPCRASH`, `BEX`,
`CLR20R3` → `application-crash` (application `P1`, module `P4`, exception `P7`/`P8`), `AppHangB1`, `APPHANG` →
`application-hang`, anything else → `wer` with the event name kept. Application Error 1000 gives application,
module, exception code, the hexadecimal process id and the report GUID (positional fallback 0, 3, 6, 8, 12). Codes are
canonical lower-case hexadecimal: `0x50`, `0x193`, `0x1a1`, `0xc0000005`; a Linux core dump's code is its signal number.
The same normalizer feeds `system.stability`, so the two tools cannot classify a report differently.

**Time.** `timestampKind` is `occurred` for the Report.wer `EventTime` and the Application Error 1000 record time, and
`reported` for the WER 1001 processing time and a Report.wer file time. A `BlueScreen` Report.wer `EventTime` is `reported`:
Windows writes it after the reboot (on the operator workstation, after the next boot's Kernel-Power 41), so it is not the
crash time, and no other time is promoted in its place; a merged crash takes its occurrence time when
one of its sources proves it, and then carries the WER processing time in `reportedUtc`. A crash belongs to the window
by that primary time. The tool description the model reads states the `BlueScreen` exception in the same words, so that
`EventTime` is never read as the crash time (HARDEN-9, residual N-2).

**Aggregate groups** key on kind, event name (only for kind `wer`), code, application, module and `timestampKind`, with
`count`, `firstSeenUtc`, `lastSeenUtc`, `dumpReferenceCount`, `evidenceSources` and `uncorrelatedCount` — the members
with no report GUID, which another source may also have recorded. `uncorrelatedCount: 0` does not mean every crash was
confirmed by several sources: a crash with a GUID may have been seen by one source only, and `evidenceSources` says
which.

**Raw rows** keep every schema-1 field in the schema-1 order (`timestampUtc`, `process`, `pid`, `kind`, `dumpPath`,
`eventIdOrCrashId`, `summary`, `source`) and add `timestampKind`, `reportedUtc`, `reportId`, `eventName`, `code`,
`bugcheckCode`, `liveDumpCode`, `exceptionCode`, `faultModule`, `bucket` and `evidenceSources`. `summary` is the event
name, code and faulting module joined with `"; "`. `dumpPath` is the first `.dmp` reference among WER `AttachedFiles`,
with the `\\?\` prefix removed; only a drive-absolute path without control characters and at most 1,024 characters is
kept, anything else is dropped rather than repaired. It is a path string only — the file is never opened — it may
contain a user-profile directory, it appears only in raw mode, and it is never audited.

**Sources and bounds.** `windows-event-application-error`, `windows-event-wer` (512 records each), and the WER report
directories of both roots with distinct names: `windows.wer.programdata.reportarchive`,
`windows.wer.programdata.reportqueue`, `windows.wer.localappdata.reportarchive`, `windows.wer.localappdata.reportqueue`
(512 reports each). Report.wer is read by a streaming key scan of at most 256 KiB per file — it stops as soon as the
report header ends — and 32 MiB per call; the old 32 KiB whole-file limit is gone. A report the identity may not read is
counted and makes its source `partial` (non-elevated, most Report.wer files under `ProgramData` and every kernel report
in `ReportQueue` are unreadable); it is never treated as absent. The whole call runs within 20 seconds, each Event Log
read within at most 10 of them, each source getting its share of what is left. The result is at most 65,536 bytes in
both modes.

**Coverage and completeness.** `coverage` lists `windows.channel.Application` (or `linux.journald`, probed in the system
journal where core dumps are recorded) with its oldest record and size, and each WER directory with `state: unknown`.
`complete` is true only when every source was read, nothing was cut and the log reaches back to the start of the
request.

## Platform evidence matrix

| Backend | Evidence status |
|---|---|
| Windows WUA pending updates | REAL-PLATFORM-EXERCISED |
| Windows WUA update history | REAL-PLATFORM-EXERCISED |
| Windows WER/Event Log crash metadata (schema 2, HARDEN-7) | REAL-PLATFORM-EXERCISED (non-elevated) |
| Windows `EnumDeviceDrivers` | REAL-PLATFORM-EXERCISED |
| Linux coredumpctl (schema 2, HARDEN-7) | FIXTURE-TESTED; real WSL run without `coredumpctl` reports `unavailable` |
| Linux apt, dnf, zypper, package history, core_pattern, `/proc/modules`, `/sys/module` | FIXTURE-TESTED |

Fixture coverage proves parsing and incomplete/truncation semantics. Linux fixture passes do not count
as real Linux platform validation.
