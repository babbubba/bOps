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

## Platform evidence matrix (local K8 gate)

| Backend | Evidence status |
|---|---|
| Windows WUA pending updates | REAL-PLATFORM-EXERCISED |
| Windows WUA update history | REAL-PLATFORM-EXERCISED |
| Windows WER/Event Log crash metadata | REAL-PLATFORM-EXERCISED |
| Windows `EnumDeviceDrivers` | REAL-PLATFORM-EXERCISED |
| Linux apt, dnf, zypper, package history, coredumpctl, core_pattern, `/proc/modules`, `/sys/module` | FIXTURE-TESTED |
| Linux host smoke | NOT RUN locally (gate host is Windows); REAL LINUX = PENDING CI |

Fixture coverage proves parsing and incomplete/truncation semantics. Linux fixture passes do not count
as real Linux platform validation.
