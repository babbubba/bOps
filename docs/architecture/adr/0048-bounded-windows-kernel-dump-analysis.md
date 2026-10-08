# ADR-0048 — Bounded Windows kernel dump analysis through Microsoft Debugging Tools

Status: **Accepted and implemented — 2026-10-08**
Date: 2026-10-08
Decision register: [D-045](../../../agentic/06-decisions.md)

## Context

bOps can find kernel crash dumps but cannot look inside them. `system.stability` lists the files in
`%SystemRoot%\Minidump` (names, sizes, times; never contents), and `system.crashes` records the `dumpPath` of a WER report
as metadata only. An operator machine produced `C:\WINDOWS\Minidump\100626-20984-01.dmp` after bugcheck `0x1E`. bOps
could report that the file existed, but it could not say which module the debugger attributed the failure to, what the
stack looked like, or what the kernel black boxes recorded. That is the evidence a crash diagnosis needs.

A kernel dump can contain sensitive kernel memory. Analysing one must not give the model a general-purpose local-file
reader, a debugger console or a shell (rules S1, S11). Package code may depend only on `bOps.Abstractions` and its family
shared library. The core must not name a package, tool or debugger (rule A1).

## Decision

### 1. One new read-only tool, kernel dumps only

The Windows system package adds `system.dump_analyze` (`RiskLevel.Read`, platform `windows`). V1 covers Windows small
kernel dumps (minidumps), kernel and complete memory dumps that KD can open, and LiveKernelReports kernel dumps.
User-mode and application dumps, CDB, `process.dump_analyze`, dump generation and crash-control settings are out of scope.
`system.crashes` and `system.stability` do not call it. They only mention it, so the planner can choose it as the next
tool.

### 2. Contract

The tool takes one required argument, `path` (`ToolParameterType.Path`, 7–260 characters). It has no parameter for a
debugger command, symbol path, script, extension, timeout or output format. The shared manifest, argument reader, DTOs,
bounded formatter and audit summary live in `bOps.Packages.System.Core`. The Windows package passes in the name of the
capability that gates the tool, so the shared code never names a debugger.

### 3. Capability gating

The package owns the capability `windows.debugger.kd` (`WindowsDebuggerCapabilities.KernelDumpAnalysis`). The API and CLI
composition roots register the check with `CachingCapabilityProbe.RegisterCheck`, before the first
`RefreshCapabilitiesAsync`, and only on Windows. The check only looks at files: it launches, installs and downloads
nothing. When no acceptable `kd.exe` is found, the tool does not appear in the planner's catalog.

### 4. Backend and discovery

The backend is Microsoft's command-line Debugging Tools for Windows. `kd.exe` is the analysis engine, and `dumpchk.exe` is
an optional preflight. DbgEng COM interop, WinDbg GUI automation, PowerShell, `cmd.exe` and a home-grown dump parser are
all rejected. Discovery is deterministic and bounded:

1. the repository has no existing debugger-location setting, so there is nothing to check first;
2. the fully qualified `PATH` entries, in order, at most 64;
3. `Windows Kits\10\Debuggers\<arch>` under Program Files (x86), then under Program Files.

A candidate must be a regular file, not a reparse point, whose version resource names Microsoft. `dumpchk.exe` is used only
if it sits in the same directory as the chosen `kd.exe`. Nothing is installed automatically.

### 5. Path authorization

The policy denies by default. An accepted path is an absolute local drive path ending in `.dmp` or `.mdmp`, made only of
letters, digits, space, `.`, `_`, `-` and parentheses. It must not contain `/`, a second `:` (an alternate data stream),
control characters, or a UNC, `\\?\`, `\\.\` or `\??\` prefix. After `Path.GetFullPath`, it must lie strictly under
`%SystemRoot%\Minidump` or `%SystemRoot%\LiveKernelReports`, or be exactly `%SystemRoot%\MEMORY.DMP`.

The policy then checks every existing component from the drive root to the file with `File.GetAttributes`, and any reparse
point (junction, symbolic link, mount point) means rejection. `FileInfo.Exists` is not used, because it answers false when
a parent directory cannot be listed. A missing file is `not-found`. An unreadable one is `access-denied`, which is never
reported as absence. The decision is made again immediately before DumpChk and again before KD (rule S11). A rejected path
is a `ToolFailureKind.Validation` failure, and nothing is launched.

### 6. Invocation: one fixed, delimited command sequence

Each run is `kd.exe -noshell -sins -y <symbol path> -z <dump path> -c <script>`. Every argument goes through
`ProcessStartInfo.ArgumentList` with `UseShellExecute = false` and no window. Standard input is closed immediately.
Standard output (at most 4 Mi characters) and standard error (at most 64 Ki) are drained concurrently. The run has a hard
limit of 10 minutes and also honours the caller's cancellation, and on either one the whole process tree is killed.

The process environment has every `_NT_*` and `DBGHELP_*` variable and `INIT` removed. Each run gets its own empty
working directory owned by bOps, so no `ntsd.ini` or `tools.ini` can be picked up, and the directory is deleted in
`finally`.

The script is source-controlled:

```
.echo <<<BOPS_<nonce>_BUGCHECK_BEGIN>>>;.bugcheck;.echo <<<BOPS_<nonce>_BUGCHECK_END>>>;
.echo <<<BOPS_<nonce>_ANALYZE_BEGIN>>>;!analyze -v;.echo <<<BOPS_<nonce>_ANALYZE_END>>>;
.echo <<<BOPS_<nonce>_MODULES_BEGIN>>>;lm;.echo <<<BOPS_<nonce>_MODULES_END>>>;
.echo <<<BOPS_<nonce>_PNP_BEGIN>>>;!blackboxpnp;.echo <<<BOPS_<nonce>_PNP_END>>>;
.echo <<<BOPS_<nonce>_BSD_BEGIN>>>;!blackboxbsd;.echo <<<BOPS_<nonce>_BSD_END>>>;q
```

The nonce is 16 random hexadecimal characters generated for each run, so data inside a dump cannot forge a section
boundary. The dump path is never part of the `-c` string. The script contains no memory display (`db`, `dd`, `dq`, `dw`,
`du`, …), no search, no `.load`, no script and no `.shell`.

DumpChk runs first when it is present, with the path as its only argument and a 60-second limit. Only its exit status is
used. Its exit-code semantics have not been verified against a real installation, so a failed DumpChk does not stop KD.
The dump is reported `invalid` only when DumpChk failed **and** KD produced no evidence. When DumpChk failed but KD still
produced analysis, the result is `partial` with the warning `dumpchk-reported-problem`. When DumpChk is absent, the tool
stays visible and reports the warning `dumpchk-unavailable`.

### 7. Text-first parsing (operator decision) and bounded output

The structure of `!analyze -xml`/`-xmf` output is not documented, and no real debugger output could be captured when this
ADR was written. The operator therefore chose text-first parsing for V1. The parser reads only text between exact nonce
marker lines. From `!analyze -v` it takes only an allowlist of fields that Microsoft documents: `BUGCHECK_CODE`,
`BUGCHECK_P1`–`P4`, `PROCESS_NAME`, `MODULE_NAME`, `IMAGE_NAME`, `SYMBOL_NAME`, `EXCEPTION_CODE_STR`,
`FAILURE_BUCKET_ID`, `FAILURE_ID_HASH` and `BUCKET_ID`. It also reads the `NAME (code)` header, `Arg1`–`Arg4` and the
`STACK_TEXT` block. `.bugcheck` is the fallback source for the code and arguments, and the `lm` list supplies module
ranges and symbol state.

The black boxes are reported as `available` plus bounded excerpt lines and device instance identifiers. Their layout is
undocumented and is not otherwise interpreted. The XML parser is deferred until real output has been captured and
reviewed.

The result is versioned JSON (`schemaVersion` 1), and every list and string in it has a limit:

| Item | Limit |
|---|---|
| Stack frames | 64 |
| Modules (implicated first, then those the stack references) | 32 |
| Warnings | 32 |
| Text fields | 2 KiB |
| Black-box lines | 64 × 256 characters |
| Device identifiers | 32 |
| `rawAnalysisExcerpt` | 4 KiB |
| Whole result | 32 KiB UTF-8 |

When the result is still too large, data is dropped in a fixed order: the raw excerpt, then black-box lines, then
non-implicated modules, then the tail of the stack. `rawAnalysisExcerpt` appears only when no bugcheck code could be
parsed, and it is taken only from the fixed `!analyze` section. Raw memory is never emitted. A field the debugger did not
print is `null`, which is distinct from an empty list.

### 8. Completeness and outcome

`status` takes one of these values:

- **`complete`:** every section is closed, kernel symbols are PDB-backed, there is a bugcheck code and a resolved module,
  and there was no timeout, output cut or DumpChk failure.
- **`partial`:** some evidence exists, but at least one limitation applies.
- **`unavailable`:** `failure` is `not-found`, `access-denied`, `debugger-unavailable`, `timeout` or `debugger-failure`.
- **`invalid`:** DumpChk failed and KD found no evidence.

A missing black box or a missing preflight is reported as a warning. It does not make the debugger's own analysis
partial, and black-box `available: false` is absence of data, never evidence of health. A symbol problem is
`symbols.status` `partial` or `unavailable`, never `invalid`. A timeout with nothing parsed returns
`ToolOutcome.Timeout` (`ToolFailureKind.Timeout`). A timeout after some evidence returns a successful `partial` result
with `failure: "timeout"`.

The `analysis` object carries `qualifier: "debugger-attribution"`, and the manifest and a fixed `interpretation` string
state that the debugger's attribution is not a proven root cause. `IToolAuditSummaryProvider` records aggregate fields
only: status, failure, truncation, dump kind, bugcheck code, symbol status, DumpChk use, black-box availability and stack
frame count. It never records the path, frames, modules, device identifiers or debugger text.

### 9. Symbols

The symbol path is fixed: `srv*%LOCALAPPDATA%\bOps\Symbols*https://msdl.microsoft.com/download/symbols`. No argument can
change it, `-sins` and the environment scrub stop machine-wide `_NT_SYMBOL_PATH` values from injecting another server,
and there are no credentials. The repository has no existing safe setting for an operator symbol path, so V1 adds none.
Microsoft public symbols do not cover third-party drivers, which therefore normally resolve as `partial`. Downloading
symbols is part of how the analysis works. The tool's risk is still `Read`.

### 10. Timeout conflict (recorded, not resolved here)

Every tool runs under the runner's single `Agent:DefaultToolTimeout`, which defaults to 30 s, and no manifest can declare
its own timeout. Analysis with symbols already cached usually fits in that time. The first download of kernel symbols
often does not. This ADR does not raise the global default and does not add a per-manifest timeout, which would be an
Abstractions change and an ADR of its own. When the runner's token fires, the tool kills KD's process tree and the runner
reports `Timeout`.

Following the precedent of Docker builds (`docs/docker-management.md`), operators who use dump analysis should raise
`Agent:DefaultToolTimeout`, for example to `00:05:00`. A retry also benefits from symbols that were already cached. A
per-tool timeout declaration is a candidate follow-up.

## Alternatives considered

- **DbgEng COM / native API.** This gives the most structure, but it brings native interop and lifetime handling into a
  plain `net10.0` package and puts the debugger in-process. Rejected for V1.
- **WinDbg GUI automation, or PowerShell driving WinDbg.** These are a shell or GUI command surface. Rejected (rule S1).
- **XML-first `!analyze -xml -xmi -xcs -xmf`.** Its output structure is undocumented and no real output was available.
  Building a parser for an unverified shape would invent data. Deferred to a follow-up once captured.
- **A model-supplied debugger command or symbol server.** This would be a generic execution surface. Rejected.
- **Configurable additional dump roots.** Deferred. V1 accepts only the three system locations.
- **Treating a failed DumpChk as decisive.** Its exit codes are unverified, and trusting them could hide analyzable dumps.
  Rejected for V1, as recorded in §6.

## Consequences

- An LLM can obtain bounded, delimited kernel-dump evidence for one approved dump, without reading raw memory and without a
  command surface.
- A host without `kd.exe` is unchanged: the tool is invisible there.
- Parser behaviour against real debugger releases is **not yet validated**. The fixtures are synthetic and labelled. The
  first real validation is the operator's dump `C:\WINDOWS\Minidump\100626-20984-01.dmp`, analysed after deployment.
  Sanitized real captures should replace the synthetic fixtures, and the XML follow-up should be decided then.
- With the default 30-second runner timeout, a first analysis that needs a cold symbol download may time out until the
  operator raises `Agent:DefaultToolTimeout` (§10).
- Two cross-platform catalog tests became platform-aware, because `system.dump_analyze` is the first first-party tool that
  exists on one operating system only.
