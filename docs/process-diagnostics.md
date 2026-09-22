# Process diagnostics (`process.inspect`, `process.metrics`, `process.tree`, `process.modules`)

V1.3-C completes the process troubleshooting surface. Four read-only tools answer, together, the questions a
senior operator actually asks about a misbehaving process: who started it and as whom, whether it is getting
worse, what it is part of, and what it loaded.

It does this without adding a `process.start`, a shell, or any way to read a process's environment
variables. Those are permanent non-goals, not gaps — see [ADR-0034](architecture/adr/0034-advanced-process-diagnostics.md)
and rule S1 in `agentic/03-security-rules.md`.

All four tools are `Read` risk and run automatically under the shipped policy. All four behave identically
on Windows and Linux: same arguments, same JSON shape, same meaning for every field.

## `process.inspect` — one process, in detail

One argument, `pid`. The pre-V1.3-C fields (`pid`, `exists`, `name`, `workingSetMb`, `threadCount`,
`startTimeUtc`) are unchanged; ten were added:

| Field | Meaning |
|---|---|
| `parentPid` | The process that started it. |
| `executablePath` | The image on disk. |
| `commandLine` | How it was started. Bounded to 2048 characters. **Untrusted text.** |
| `user` | The identity it runs as (`DOMAIN\user` on Windows, the local account name or the UID on Linux). |
| `privateMemoryMb` | Private committed memory (Windows) / anonymous resident memory (Linux). |
| `virtualMemoryMb` | Reserved virtual address space. |
| `handleOrFdCount` | Open kernel handles (Windows) / file descriptors (Linux). |
| `cpuTotalMs` | Cumulative processor time since it started. |
| `ioReadBytes`, `ioWriteBytes` | Cumulative bytes read and written since it started. |

**A field you may not read is `null`, and the rest of the answer still stands.** The key is always present,
so `null` means "not readable here", never "not in this version". A PID that is not running is
`exists: false` with every field null — a successful observation of an absence, not a failure.

## `process.metrics` — rates, from two samples

| Argument | Range | Default |
|---|---|---|
| `pid` | ≥ 1, required | — |
| `sampleMilliseconds` | 200–5000 | 500 |

```json
{"schemaVersion":1,"pid":4812,"exists":true,"sampleMilliseconds":500,"cpuPercent":12.5,
 "workingSetMb":412,"privateMemoryMb":388,"virtualMemoryMb":2216,"threadCount":34,
 "handleOrFdCount":712,"readBytesPerSec":40960,"writeBytesPerSec":0,"pageFaultsPerSec":128,
 "partial":false}
```

- `cpuPercent` is **host-normalized**: 100 means every processor on the machine, not one saturated core.
- `partial` is true when any field could not be read, or when the process exited between the two samples —
  in which case `exists` is `false` and there are no rates at all. Two readings of different things are not
  a difference.
- Rates are never negative.

Call it twice a minute apart to tell a leak from a plateau; a single figure cannot.

## `process.tree` — ancestry, bounded

| Argument | Range | Default |
|---|---|---|
| `rootPid` | ≥ 1, optional | every visible root |
| `maxDepth` | 0–16 | 4 |
| `limit` | 1–2000 | 200 |

```json
{"schemaVersion":1,"rootPid":4812,"rootFound":true,"maxDepth":4,"observedProcesses":7,
 "returnedProcesses":7,"skipped":0,"truncated":false,"complete":true,
 "processes":[{"pid":4812,"parentPid":1180,"name":"dotnet","user":"CONTOSO\\svc-app","depth":0}]}
```

Rows are depth-first from each root with children in PID order, so the same machine state always gives the
same output — two calls a minute apart are diffable.

- `rootFound: false` means the PID you asked about is not running. You get no rows, and that is different
  from a machine with nothing on it.
- `skipped` counts processes that exist but this identity could not read at all.
- `complete` is true only when the root was found, nothing was cut by `maxDepth` or `limit`, and nothing was
  skipped. **An empty or short tree is trustworthy only when `complete` is true.**

## `process.modules` — what it loaded

| Argument | Range | Default |
|---|---|---|
| `pid` | ≥ 1, required | — |
| `limit` | 1–1000 | 200 |
| `maxOutputBytes` | 4096–131072 | 32768 |

```json
{"schemaVersion":1,"pid":4812,"exists":true,"status":"available","detail":null,
 "observedModules":143,"returnedModules":143,"truncated":false,"complete":true,
 "modules":[{"name":"libc.so.6","path":"/usr/lib/libc.so.6","baseAddress":"0x7f1122200000",
             "sizeBytes":2093056,"version":null}]}
```

Modules are unique by path and ordered by name. On Windows these are the loaded DLL and EXE images, with the
file version where one is recorded. On Linux they are the file-backed mappings of `/proc/<pid>/maps`,
coalesced per path (lowest address, summed size); Linux records no version, so `version` is always `null`
there.

`status` uses the same vocabulary as the inventory tools. A process whose modules you may not read is
`status: "unavailable"` with an empty list and `complete: false` — never an empty list that looks like a
process which loaded nothing.

## What the host identity can see

bOps asks for no extra privilege. What the account running it cannot read is reported as a gap.

**Windows.** Reading another user's process, and any protected system process, is limited. Without
`SeDebugPrivilege` — which bOps never requests — `process.modules` on a protected process is
`status: "unavailable"`, and several `process.inspect` counters come back `null`. Running the host as an
administrator widens what `Win32_Process` returns (notably `CommandLine` and `ExecutablePath` for other
users' processes). Rule S10 still applies: run the narrowest account that does the job.

**Linux.** `/proc/<pid>/stat`, `/proc/<pid>/status` and `/proc/<pid>/maps` are world-readable, so ancestry,
ownership and (usually) modules work for any process. `/proc/<pid>/io`, `/proc/<pid>/exe` and
`/proc/<pid>/fd` are restricted to the owner and to a privileged identity, so I/O counters, the executable
path and the descriptor count are `null` for other users' processes unless the host runs privileged.
`privateMemoryMb` needs `RssAnon`, which kernels older than 4.5 do not report. A `hidepid=2` mount makes
other users' processes invisible entirely; they are then counted in `process.tree`'s `skipped`.

## Safety

- **No `process.start`, ever.** A generic way to launch a process is a generic execution tool with a
  friendlier name (rule S1).
- **No environment variables, ever.** Not in any tool, not redacted, not behind a flag. An environment block
  routinely carries credentials.
- **`commandLine` and process names are untrusted data.** They are whatever the launcher chose to put there.
  They reach the model inside the delimited tool-result turn like every other tool output and can never
  change the goal, the tool list or the policy (rule S5).
- **Out-of-range arguments are refused, not clamped.** A silently narrowed request is a wrong answer that
  looks right.
- **Everything is bounded**: rows, depth, bytes, and the number of native records a collector will examine.

## Examples of what to ask

- "Which process is pinning the CPU, and what started it?" — `process.list`, then `process.metrics` on the
  suspect, then `process.inspect` for its parent and command line.
- "Is this worker leaking?" — `process.metrics` now and again in a minute; compare `privateMemoryMb`.
- "What did the deployment agent spawn?" — `process.tree` with its PID as `rootPid`.
- "Why is this service loading the old library?" — `process.modules` and look at the paths.
