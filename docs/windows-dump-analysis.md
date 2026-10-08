# Windows kernel dump analysis (`system.dump_analyze`)

Design: [ADR-0048](architecture/adr/0048-bounded-windows-kernel-dump-analysis.md) · [D-045](../agentic/06-decisions.md).

`system.dump_analyze` analyzes **one** Windows kernel crash dump with Microsoft's `kd.exe` and returns bounded JSON
(`schemaVersion` 1). The JSON describes the bugcheck, the debugger's automated attribution and failure bucket, the
analysis stack, the modules involved, the symbol state and the kernel PnP and boot/shutdown black boxes. The tool is
read-only and Windows-only. It runs one fixed analysis: the model can choose only which approved dump to analyze. It never
supplies a debugger command, symbol server, script or extension, and the tool never emits raw memory.

## Scope

Supported in V1, when KD can open the file:

- small kernel dumps (minidumps) in `%SystemRoot%\Minidump`;
- kernel, automatic, active or complete memory dumps at `%SystemRoot%\MEMORY.DMP`;
- LiveKernelReports kernel dumps under `%SystemRoot%\LiveKernelReports`, for example `WATCHDOG\*.dmp`.

Out of scope: user-mode and application dumps (APPCRASH or BEX dumps, such as a `testhost.exe` crash), CDB, ProcDump,
creating dumps, changing crash-control or page-file settings, and any automatic driver remediation.

## Accepted paths

`path` must be an absolute local path ending in `.dmp` or `.mdmp` that is one of:

- a file anywhere under `%SystemRoot%\Minidump\`;
- a file anywhere under `%SystemRoot%\LiveKernelReports\`;
- exactly `%SystemRoot%\MEMORY.DMP`.

The following are rejected as a validation failure, and nothing is launched:

- relative, UNC, network, `\\?\` and `\\.\` device paths;
- forward slashes;
- alternate data streams;
- control characters;
- characters other than letters, digits, space, `.`, `_`, `-` and parentheses, including 8.3 short names with `~`;
- `..` escapes;
- anything outside those locations.

A junction, symbolic link or other reparse point anywhere on the path, including an approved directory that is itself a
link, is rejected. Configuring additional dump folders is not supported in V1.

## Prerequisite: Debugging Tools for Windows

bOps never installs the debugger. Install **Debugging Tools for Windows** yourself, from the Windows SDK installer
(feature "Debugging Tools for Windows") or the Windows Driver Kit. The tool appears in the planner's catalog only when an
acceptable `kd.exe` is found, in this order:

1. each fully qualified directory on `PATH`;
2. `%ProgramFiles(x86)%\Windows Kits\10\Debuggers\<arch>\kd.exe`;
3. `%ProgramFiles%\Windows Kits\10\Debuggers\<arch>\kd.exe`.

`<arch>` is `x64`, `arm64` or `x86`. `kd.exe` is a **required** prerequisite. `dumpchk.exe` is an **optional** prerequisite: when it is absent, the tool remains available but is reported as degraded and analysis proceeds without the integrity preflight.

Check an installation with:

```powershell
where.exe kd
Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\kd.exe"
Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\dumpchk.exe"
```

The modern WinDbg app (Microsoft Store / `winget` package) is a GUI debugger. It does not necessarily put a usable
`kd.exe` on `PATH` or in the Windows Kits directory. If neither check finds `kd.exe`, install the SDK feature.

ADR-0049 prerequisite readiness checks both dependencies at host start and periodically (30 seconds by default). If KD is missing,
`system.dump_analyze` stays registered but unavailable and **System messages** records an actionable Warning naming Microsoft
Debugging Tools for Windows and `kd.exe`. If only DumpChk is missing, the tool stays available/degraded and an Information message
explains that the optional preflight is unavailable. After installing the tools, the next prerequisite refresh records recovery and
makes the tool available without requiring a bOps restart.

## Privileges

A non-elevated identity normally cannot read `%SystemRoot%\Minidump` or `MEMORY.DMP`. The result is then
`status: "unavailable"`, `failure: "access-denied"`, with warning `access-denied-elevation-may-be-required`. That is a
limit on the evidence: it never means that no dump exists. The tool never tries to elevate itself. Run bOps under an
identity allowed to read the dump. Never use LocalSystem; see rule S10.

## Symbols

Symbols decide most of the quality of the analysis. bOps uses one fixed symbol path:
`srv*%LOCALAPPDATA%\bOps\Symbols*https://msdl.microsoft.com/download/symbols`, the Microsoft public symbol server through
a local cache owned by bOps. A machine-wide `_NT_SYMBOL_PATH` is ignored: the debugger runs with `-sins` and a cleaned
environment, so nothing can redirect it to another server. V1 has no setting for an operator-chosen symbol path.

`symbols.status` takes one of these values:

| `symbols.status` | Meaning |
|---|---|
| `loaded` | Kernel and every implicated module have PDB symbols. |
| `partial` | Kernel symbols loaded, but an implicated or stack module has only export symbols or none. This is normal for third-party drivers: Microsoft's public symbols do not include them. |
| `unavailable` | Kernel symbols could not be loaded (for example offline). |
| `error` | The module list was not produced, so the symbol state is unknown. |

A symbol problem never makes a dump `invalid`. An unresolved third-party driver is unknown. It is never evidence that the
driver is healthy.

### Time

All tools share the runner's `Agent:DefaultToolTimeout`, which defaults to 30 seconds. Analysis with cached symbols
usually fits. The **first** analysis may need to download kernel symbols and take minutes. If dump analysis times out,
raise the setting, for example `"Agent": { "DefaultToolTimeout": "00:05:00" }`, the same way as for Docker builds. A
retry also benefits from any symbols already cached. The tool's own hard limit is 10 minutes, and the debugger's process
tree is always killed on a timeout or cancellation.

## How it runs

1. The path is authorized. This is repeated immediately before each debugger launch.
2. If present, `dumpchk.exe <path>` runs as an integrity preflight (at most 60 s). Only its exit status is used. A failure
   is reported as `dumpchk-reported-problem` but does not stop KD, because DumpChk's exit-code semantics have not yet been
   verified on a real installation.
3. `kd.exe -noshell -sins -y <symbol path> -z <path> -c <fixed script>` runs once. The fixed script contains:
   - `.bugcheck`
   - `!analyze -v`
   - `lm`
   - `!blackboxpnp`
   - `!blackboxbsd`
   - `q`

   Each command is wrapped in markers carrying a random per-run nonce, and only text between exact marker lines is
   parsed. No memory display, search, extension loading, script or shell command exists.
4. The per-run working directory is deleted afterwards.

## Reading the result

```json
{
  "schemaVersion": 1,
  "status": "partial",
  "complete": false,
  "truncated": false,
  "failure": null,
  "dump": { "path": "C:\\WINDOWS\\Minidump\\…", "fileName": "…", "sizeBytes": 11014055, "lastWriteUtc": "…", "kind": "kernel-small" },
  "debugger": { "engine": "kd", "version": "10.0.…", "dumpCheckUsed": true, "dumpCheckPassed": true },
  "symbols": { "status": "partial" },
  "bugcheck": { "code": "0x1e", "name": "KMODE_EXCEPTION_NOT_HANDLED", "parameters": ["0xffffffffc0000005", "…", "0x0", "…"] },
  "analysis": { "qualifier": "debugger-attribution", "processName": "System", "moduleName": "…", "imageName": "….sys",
                "symbolName": "…", "exceptionCode": "c0000005", "failureBucketId": "…", "failureIdHash": "{…}", "bucketId": null },
  "stack": [ { "index": 0, "module": "nt", "symbol": "KeBugCheckEx", "offset": null, "address": "0xfffff807…" } ],
  "modules": [ { "name": "…", "start": "0x…", "end": "0x…", "symbolState": "no symbols", "implicated": true } ],
  "blackbox": { "pnp": { "available": false, "deviceIds": [], "lines": ["…"] }, "bsd": { "available": false, "deviceIds": [], "lines": ["…"] } },
  "warnings": ["symbols-partial", "blackbox-pnp-unavailable", "blackbox-bsd-unavailable", "small-dump-limited-structures"],
  "rawAnalysisExcerpt": null,
  "interpretation": "Debugger evidence, not a diagnosis. …"
}
```

Field notes:

- `stack[].address` is the frame's return address.
- `modules` lists the implicated module first, then the modules the stack references.
- `rawAnalysisExcerpt` appears only when no bugcheck could be parsed. It is at most 4 KiB, taken from the fixed `!analyze`
  section only.

### Status

| `status` | When |
|---|---|
| `complete` | Every section was produced, kernel symbols are loaded, there is a bugcheck code and a resolved module, and there was no timeout, cut or preflight problem. |
| `partial` | Evidence exists but something limits it; the warnings say what. |
| `unavailable` | No evidence. `failure` is `not-found`, `access-denied`, `debugger-unavailable`, `timeout` or `debugger-failure`. |
| `invalid` | DumpChk rejected the dump and KD produced no evidence. |

A black box with `available: false` means that no black-box data was printed. Small minidumps often lack these
structures, which adds `small-dump-limited-structures`. Absent data is never evidence that a component was healthy.

### Debugger attribution is not a root cause

`analysis` is the debugger's own heuristic. `MODULE_NAME`, `IMAGE_NAME` and `FAILURE_BUCKET_ID` say where the debugger
found the failure or was executing; they do not prove that the module *caused* the crash. Before stating a cause,
correlate the attribution with:

- the bugcheck and its parameters;
- the stack;
- WHEA hardware errors (`system.stability` `hardwareError`);
- PnP, USB/xHCI, display and PCIe evidence;
- driver versions (`system.drivers`);
- timing (`system.crashes`, `system.events`).

## Relation to other tools

| Tool | Role |
|---|---|
| `system.stability` | Machine-level stability evidence and the minidump inventory (names, sizes, times). |
| `system.crashes` | WER and crash metadata, including dump paths. |
| `system.dump_analyze` | Opens **one** explicitly named kernel dump and analyzes its contents. |

Neither of the other two tools calls `system.dump_analyze`. The planner chooses it as the next step when a kernel dump
path is known and the tool is available.

## Bounds

| Item | Limit |
|---|---|
| Stack frames | 64 |
| Modules | 32 |
| Warnings | 32 |
| Text fields | 2 KiB |
| Black-box excerpt lines | 64 × 256 characters |
| Device identifiers | 32 |
| Raw excerpt | 4 KiB |
| Debugger stdout read | 4 Mi characters, the rest drained |
| Whole result | 32 KiB |

When the result would exceed 32 KiB, data is dropped in this order: the raw excerpt, then black-box lines, then supporting
modules, then the tail of the stack. The result is then marked `truncated` and `partial`.

## Audit

The audit summary has aggregate fields only:

- status, failure and truncation;
- dump kind;
- bugcheck code;
- symbol status;
- DumpChk use;
- black-box availability;
- the number of stack frames.

It never contains the path, frames, modules, device identifiers or debugger text.

## Validation status

The parser was written from Microsoft's documented `!analyze -v` fields and tested against **synthetic** fixtures
(`tests/bOps.Packages.System.Windows.Tests/Fixtures/KernelDump/README.md`). It has **not** yet been validated against
output from a real Debugging Tools release or a real kernel dump. Black-box output layouts are undocumented, so only
availability, excerpts and device identifiers are reported. A follow-up will add sanitized real captures and decide
whether to parse `!analyze -xml` instead.
