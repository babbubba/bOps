# ADR-0034 — Advanced process diagnostics (`process.inspect` extended, `process.metrics`, `process.tree`, `process.modules`)

Status: Accepted
Date: 2026-09-22

## Context

The existing process surface answers "what is running" (`process.list`), "does this PID exist and how big is
it" (`process.inspect`) and "make it stop" (`process.stop`, `process.kill`). That is enough to notice a
problem and not enough to explain one. A senior operator diagnosing a misbehaving process asks four further
questions, and today bOps can answer none of them:

- **Who started it, and as whom?** The parent PID, the image on disk, the command line and the owning
  identity are what turn "some `dotnet` is eating the CPU" into "the deployment agent restarted the worker
  with the wrong arguments".
- **Is it getting worse?** A single working-set figure cannot distinguish a process that is idle at 2 GB from
  one climbing through it. Rates need two samples.
- **What is it part of?** A process is rarely alone; the tree above and below it is usually the actual
  subject of the diagnosis.
- **What did it load?** A wrong or unexpected DLL or shared object is a real and common root cause.

The pull towards answering all of this with one generic tool is exactly the pull rule S1 exists to resist.
`process.start` is not in scope at any version, and neither is an environment dump: an environment block
routinely carries connection strings, tokens and passwords, and `agentic/00-project-spec.md` lists both as
permanently out of scope. This ADR adds capability without adding either.

## Decision

### One extension and three new `Read` tools

`process.inspect` keeps its name, its single `pid` parameter and every field it already reported, and gains
ten nullable fields. Three tools are added: `process.metrics`, `process.tree` and `process.modules`. All four
are contributed by `bOps.Packages.System.Windows` and `bOps.Packages.System.Linux`, each declaring its own
platform (rule A8). The manifests, argument validation, the tree walk, the rate arithmetic and the JSON
output live in `bOps.Packages.System.Core`; each OS package implements only collection. Nothing is added to
`bOps.Abstractions`.

### They are plain `ITool`, with no `VerificationSpec`

Every one of these tools is `RiskLevel.Read` and declares no verification, which is the convention every
read-only `system.*`/`process.*` tool in this codebase already follows and which
`SystemToolConformance.AssertManifestIsWellFormed` already asserts (`Assert.Null(manifest.Verification)`).
Rule B3 requires a `VerificationSpec` and `IVerifiableTool` for a tool whose risk is *not* `Read`; only
`process.stop` and `process.kill` in this family qualify, and both already have one. Verification exists to
confirm that an intended effect happened. An observation has no effect to confirm, and a read tool that
declared one would be verifying that reading twice returns the same answer — which on a live machine is not
even true, and would turn a normal race into a `Refuted` step. This is recorded explicitly because the
question is reasonable and the answer should not have to be re-derived.

### `process.inspect` — additive, field by field

Added, all nullable: `parentPid`, `executablePath`, `commandLine`, `user`, `privateMemoryMb`,
`virtualMemoryMb`, `handleOrFdCount`, `cpuTotalMs`, `ioReadBytes`, `ioWriteBytes`.

Every field is read independently. A field this identity may not read is `null` and the rest of the
observation still stands — one refused counter must never cost the whole answer, which is the same rule the
pre-existing fields already followed. The key is always present, so a reader can tell "not reported" from
"not in this version". `commandLine` is attacker-influenceable text like any other tool output (rule S5) and
is bounded to 2048 characters.

The environment of a process is not in this shape and never will be.

### `process.metrics` — two samples, one difference

| Argument | Type | Meaning |
|---|---|---|
| `pid` | Integer, required, ≥ 1 | The process to sample. |
| `sampleMilliseconds` | Integer, 200–5000, default 500 | Interval between the two samples. |

Result: `pid`, `exists`, `sampleMilliseconds`, `cpuPercent`, `workingSetMb`, `privateMemoryMb`,
`virtualMemoryMb`, `threadCount`, `handleOrFdCount`, `readBytesPerSec`, `writeBytesPerSec`,
`pageFaultsPerSec`, `partial`.

`cpuPercent` is host-normalized: `Δ processor time ÷ wall interval ÷ Environment.ProcessorCount × 100`,
clamped to 0–100, so 100 means the whole machine and not one saturated core. A process that exits between the
samples is `exists: false, partial: true` and carries no rates: two readings of different things are not a
difference. `partial` is also true when any reported field could not be read. A rate is never negative; two
reads of a per-thread counter can arrive out of order, and that is a reading artefact, not a fact about the
process.

### `process.tree` — a bounded, deterministic walk

| Argument | Type | Meaning |
|---|---|---|
| `rootPid` | Integer, optional, ≥ 1 | Walk down from this process. Omit for every visible root. |
| `maxDepth` | Integer, 0–16, default 4 | Generations below the root. |
| `limit` | Integer, 1–2000, default 200 | Rows returned. |

Rows carry `pid`, `parentPid`, `name`, `user` and `depth`, depth-first from each root with children ordered
by PID, so the same machine state always produces the same rows in the same order. The envelope adds
`rootFound`, `observedProcesses`, `returnedProcesses`, `skipped`, `truncated` and `complete`. A `rootPid`
that is not among the observed processes is `rootFound: false` with no rows, never an empty tree that reads
like a quiet machine. `skipped` counts processes that exist but could not be read at all, and `complete` is
true only when the root was found, nothing was cut and nothing was skipped.

The owning identity is resolved only for the rows that are actually returned. On Windows that is a separate
WMI call per process, and making 285 of them to report 200 rows would defeat the bound.

A parent chain can close on itself after PID reuse; the walk keeps a visited set, so that is a bounded walk
rather than a stack overflow.

### `process.modules` — unique by path, bounded in rows and bytes

| Argument | Type | Meaning |
|---|---|---|
| `pid` | Integer, required, ≥ 1 | The process whose modules to list. |
| `limit` | Integer, 1–1000, default 200 | Modules returned. |
| `maxOutputBytes` | Integer, 4096–131072, default 32768 | UTF-8 size of the result. |

Rows carry `name`, `path`, `baseAddress` (lower-case hexadecimal, `0x`-prefixed), `sizeBytes` and `version`,
unique by normalized path and ordered by name then path. The envelope carries the inventory vocabulary
(`available`, `partial`, `unavailable`, `unsupported`, `notApplicable`) plus `detail`, `observedModules`,
`returnedModules`, `truncated` and `complete`. A process whose module list this identity may not read is
`status: unavailable` with an empty list and `complete: false` — the gap is the answer, not a process that
happens to have loaded nothing.

### Arguments are rejected, never clamped

Out-of-range values are refused with a message the model can act on, following D-028. A silently widened or
narrowed request is a wrong answer that looks right. `pid` and `rootPid` are rejected below 1: PID 0 is not
an ordinary process on either system.

### Windows collection

- `System.Diagnostics.Process` for memory, threads, `TotalProcessorTime` and `Modules`.
- `System.Management` (added to `bOps.Packages.System.Windows` only) for `Win32_Process`: `ParentProcessId`,
  `ExecutablePath`, `CommandLine`, and `GetOwner` for the identity. This is the managed CIM client — there is
  no PowerShell, no `wmic.exe`, no `SeDebugPrivilege` and no arbitrary `Process.Start`.
- kernel32 via `LibraryImport`: `GetProcessIoCounters`, `GetProcessHandleCount` and
  `K32GetProcessMemoryInfo` (kernel32's forwarder of psapi's `GetProcessMemoryInfo`) for page faults.

The only value ever interpolated into a WQL query is a PID already validated as an integer, so no
model-produced text reaches the query language. A projected WMI result is asked only for the properties its
own `SELECT` named, because asking for one it left out is a WMI error rather than a null. `GetOwner` is
invoked on an object bound by path, because a projected query result carries no path to invoke against.

`Win32_Process` reports `explorer.exe` where `Process.ProcessName` — and therefore `process.list` and
`process.inspect` — reports `explorer`; the tree uses the spelling the other process tools already use, so
the agent can correlate them by name.

### Linux collection

- `/proc/<pid>/stat` for the parent PID, CPU ticks, page faults, threads and virtual size. The executable
  name is not escaped by the kernel and may contain spaces and parentheses, so fields are read after the
  *last* `)`.
- `/proc/<pid>/status` for the real UID, `VmSize`, `VmRSS`, `RssAnon` and `Threads`.
- `/proc/<pid>/cmdline` (NUL-separated), `/proc/<pid>/exe` (a symbolic link), `/proc/<pid>/io` and a count of
  `/proc/<pid>/fd`.
- `/proc/<pid>/maps` for modules, coalescing the several mappings of one path into one module: the lowest
  mapped address and the sum of the mapped sizes. Anonymous mappings and the kernel's pseudo-regions
  (`[heap]`, `[stack]`, `[vdso]`) are not modules.
- UIDs are resolved from the local `/etc/passwd` only. A UID with no local entry stays a number.

`ENOENT` is the exit race and `EACCES` is partial visibility; both answer `null` for the field concerned, and
the caller distinguishes them by whether the process directory still exists. Clock ticks per second is taken
as 100, the value `sysconf(_SC_CLK_TCK)` and the kernel's own `/proc` documentation give on every
architecture this project targets.

`rchar`/`wchar` are used rather than `read_bytes`/`write_bytes`, because they count every byte the process
moved through read and write, cached or not, which is what Windows's `ReadTransferCount`/`WriteTransferCount`
counts. Two platforms reporting the same field name must mean the same thing.

Linux records no version metadata for a mapped image, so `version` is `null` there; guessing one from the
file name of a symlinked `.so` would be a fact the kernel never stated.

### No new privilege

bOps asks for no extra privilege to see more. What the host identity cannot read is reported as a gap. The
documentation says which rights widen visibility and leaves that choice to the operator (rule S10).

## Alternatives considered

- **A generic `process.start`, or a shell to run `ps`/`tasklist`/`lsof`.** Rejected permanently: rule S1 and
  the explicit non-goals in `agentic/00-project-spec.md`. This batch exists precisely to make that
  unnecessary.
- **Returning the process environment, redacted.** Rejected. A redaction list is a blacklist, and the one
  variable that is not on it is the one that matters. There is no version of this that is safe enough to be
  worth it.
- **A toolhelp snapshot (`CreateToolhelp32Snapshot`) instead of WMI for the parent PID.** Rejected for this
  batch: it would avoid the `System.Management` dependency, but it gives neither the command line nor the
  owner, so WMI would still be needed for those and the package would carry two mechanisms. Revisit if WMI's
  per-call cost becomes the bottleneck for `process.tree`.
- **`NtQueryInformationProcess` to read the PEB for the command line.** Rejected: an undocumented interface,
  a bitness-sensitive struct walk, and it sits one field away from the environment block this ADR refuses to
  return.
- **A separate `process.parent` or `process.children` tool.** Rejected: one tree with a depth is the same
  question asked once, and two more names for it would make the model choose between them.
- **Folding metrics into `process.inspect` by giving it a sampling argument.** Rejected: `process.inspect` is
  a single instantaneous observation used as the verifier of `process.stop` and `process.kill`; making it
  sometimes take half a second and sometimes not would change the cost of every verification.
- **Clamping out-of-range arguments.** Rejected, following D-028.
- **A `maxOutputBytes` on `process.tree`.** Not added: the task's contract names three parameters for that
  tool and the row limit plus bounded per-field strings already cap the result; the runtime's own output
  truncation is the backstop. Adding a fourth optional parameter would widen a contract that was specified
  deliberately.

## Consequences

- The agent can identify ancestry, ownership, launch command, executable, resource rates and loaded modules
  through typed, bounded, `Read` tools, and can correlate them with `system.events` and the service tools.
- `bOps.Packages.System.Windows` gains one NuGet dependency, `System.Management`. It is Windows-only, which
  the package already declares with `[assembly: SupportedOSPlatform("windows")]`.
- WMI is a per-call cost. `process.tree` on a busy machine makes one query for the forest plus one owner
  lookup per returned row; the default limit of 200 is what keeps that inside the default tool timeout.
- On Linux, `/proc/<pid>/io` and `/proc/<pid>/exe` are readable only by the owner and by a privileged
  identity, so an unprivileged host reports `null` I/O and executable path for other users' processes. That
  is a visible gap, not a silent zero.
- `privateMemoryMb` is `null` on kernels older than 4.5, which do not report `RssAnon`.
- No change to `bOps.Abstractions`, policy, runtime, persistence or audit.
