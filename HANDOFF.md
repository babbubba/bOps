# Handoff — V0.11 complete (both tranches), uncommitted; hand off toward V1.0

V0.11 (`piano-bops-v0.9.1-v2.0.md` §7) is fully implemented: every read-only capability from
tranche 1 (committed and pushed in the previous session, CI-confirmed green) and every
side-effecting capability from tranche 2 (`fs.move`, `process.stop`/`kill`,
`service.start`/`stop`/`restart`). **Nothing from this session is committed yet.**

## What tranche 2 delivers

- **`fs.move`** (`bOps.Packages.Filesystem/FsMoveTool.cs`) — `RiskLevel.High`. Never overwrites an
  existing destination. **Important design note**: the plan's own requirement that this tool
  "verifies content identity" is satisfied *inside* `ExecuteAsync` — the source's SHA-256 is
  hashed before the move and the destination's immediately after, and a mismatch is reported as
  `ToolOutcome.Failure` — **not** by the separate, deferred `VerificationSpec` step. This is a
  genuine architectural finding, not a shortcut: `IVerifiableTool.EvaluateVerificationAsync`
  receives only the *original call's arguments* and the *separately executed verification tool's*
  result (`bOps.Runtime.AgentRunner.EvaluateVerificationAsync`) — never the tool's own prior
  `ToolCallResult`. By the time a deferred verification call could run, the source is already
  gone, so a historical pre-move hash cannot be threaded into it without changing
  `IVerifiableTool`'s shape, which would need its own ADR (`agentic/05-workflow.md`'s trigger
  list: "alters a type in `bOps.Abstractions`"). Verifying inside the move is strictly *stronger*
  than a deferred check could be (no time-of-check/time-of-use gap), so nothing real is lost —
  see the type's own doc comment for the full reasoning. The declared `VerificationSpec` still
  exists and still confirms the externally-observable half: a file now exists at the destination
  (via `fs.stat`, exactly like `fs.write`'s own verification).
- **`process.stop` / `process.kill`** (new tools in `bOps.Packages.System.{Core,Windows,Linux}`,
  per the plan's own note that Process stays inside the System family rather than becoming its
  own package). `process.stop` is `RiskLevel.Medium` (a graceful request); `process.kill` is
  `RiskLevel.High` (forced). Both verify via `process.inspect` — the shared
  `ProcessStopToolBase.EvaluateProcessAbsence` interprets `exists: false` as `Confirmed`, reusing
  the JSON shape tranche 1 deliberately designed for exactly this.
  - **Windows** `process.stop`: `Process.CloseMainWindow()` — the only generic Win32
    "please close" mechanism, and it only works for a process with a message loop and a main
    window. A console or service process has neither, and this is reported as an honest
    `ToolOutcome.Failure`, not silently escalated to a forced kill: **Windows has no generic
    SIGTERM equivalent for an arbitrary process.** `process.kill` uses `Process.Kill()`
    (`TerminateProcess`), which works universally.
  - **Linux** `process.stop`: a direct `kill(pid, SIGTERM)` libc call via `LibraryImport` — a
    single P/Invoke, not a subprocess, so (unlike `bOps.Packages.Service.Linux`'s `systemctl`
    shell-out) there is no process-spawning surface to reason about at all. `process.kill` uses
    `Process.Kill()`, which sends `SIGKILL` on Unix — the same portable BCL call as Windows, no
    OS-specific implementation needed for `process.kill` itself.
- **`service.start` / `service.stop` / `service.restart`** (`bOps.Packages.Service.{Core,Windows,Linux}`)
  — all `RiskLevel.Medium`, matching `docker.start`/`stop`/`restart`'s precedent and the plan's
  own explicit "`service.restart` as the first `MEDIUM` tool" note. All verify via
  `service.status`, checking for `"running"` (start/restart) or `"stopped"` (stop) — the shared
  `ServiceStartToolBase.EvaluateExpectedStatus` interprets the result.
  - **Windows**: `ServiceController.Start()`/`Stop()`. `Restart` has no native SCM equivalent
    (unlike `systemctl restart`), so it stops (tolerating "already stopped" or "cannot stop"),
    waits up to 15s for `Stopped` via `WaitForStatus`, then starts regardless of whether the wait
    timed out — a service genuinely stuck mid-stop is exactly what the separate, deferred
    `service.status` verification exists to catch.
  - **Linux**: fixed `systemctl start`/`stop`/`restart <name>` invocations (ADR-0021's pattern,
    unchanged) — `systemctl restart` sequences stop-then-start inside systemd itself, so no manual
    choreography is needed there. A shared `ServiceUnitName.IsPlausible` guard (extracted from
    tranche 1's inline regex, now used by five tools) rejects an implausible name before ever
    spawning `systemctl`.

## Verified for real, this session

- `dotnet build bOps.slnx --configuration Release`: **0 warnings, 0 errors**, full solution
  including the new `bOps.TestFixtures.WindowsService` project (below).
- `dotnet test bOps.slnx --configuration Release --filter "Category!=LiveModel"`: **all 15 test
  assemblies passed, 0 failures**, including:
  - `fs.move` against a real temp directory: successful move + hash verification, refusal to
    overwrite an existing destination, refusal when either side's write policy denies it,
    `Confirmed`/`Refuted` verification against real `fs.stat` calls.
  - `process.stop`/`process.kill` against **real, test-owned child processes this process itself
    spawns and always cleans up** — a windowless `cmd.exe` (Windows' honest "no main window"
    failure path) and a real windowed `mshta.exe about:blank` process (`CloseMainWindow()`
    succeeding for real) on Windows; `sleep 300` on Linux (written for CI — no Linux host here).
  - `service.start`/`stop`/`restart` against a **real, freshly-installed, uniquely-named Windows
    Service** (`bOpsTestService.exe`, below) on Windows — see the elevation note below for the
    one thing not verified in *this* dev session.
  - `bOps.Architecture.Tests`: still 4/4 — rule A1 holds; nothing in this tranche touches
    `bOps.Runtime`/`Policy`/`Memory`/`Audit`.
- **Manually ran the real `bops.exe`** end to end with a real goal string (no API key configured,
  so it fails at the expected 401) specifically to confirm the CLI's composition root still wires
  up cleanly with every new tool registered — it does; the failure trace shows the run reaching
  `AgentRunner.CreatePlanAsync` normally, same as tranche 1's equivalent check.

## New: `bOps.TestFixtures.WindowsService`

A new, minimal project (`tests/bOps.TestFixtures.WindowsService/`) — a do-nothing Windows Service
(`Microsoft.Extensions.Hosting.WindowsServices` + a no-op `BackgroundService`) built only so
`bOps.Packages.Service.Windows.Tests` has a **real, throwaway** service to install
(`sc.exe create`), start/stop/restart via the actual tools under test, and delete
(`sc.exe delete`) — every test creates and deletes its *own* uniquely-named instance
(`ThrowawayWindowsService.cs`), never a shared or real system service. This is the same "own
throwaway resource" discipline `FsToolsTests` already uses for its temp directory, applied to a
resource type (a Windows Service) that has no simpler equivalent.

**This needed a real design decision, and it is the one thing this session could not verify
directly**: creating/deleting a Windows Service requires an elevated (Administrator) process.
This dev environment's own session confirmed it is **not** elevated
(`WindowsIdentity`/`WindowsPrincipal.IsInRole(Administrator)` returns `false`, and the
`Administrators` group even shows as "deny-only" in this token — self-elevation is not possible
here at all). A new `RequiresElevationFactAttribute` (mirrors `WindowsOnlyFactAttribute`'s
"skip visibly, never silently" pattern) skips these five tests here and lets them run for real
only where the process actually is elevated — which GitHub Actions' `windows-latest` runner's job
process is, by default, per well-established public precedent (this is exactly why V0.10 and
tranche 1's own manual CLI smoke tests, and the Linux-only tests throughout this project, all
follow the same trust model: written and reasoned through carefully, verified for real on CI where
this dev environment cannot verify them itself).

## Scope boundaries — deliberate, not gaps to silently fill later

- **`service.start`/`stop`/`restart`'s Linux implementation has no real lifecycle test.**
  `.github/workflows/ci.yml`'s `Test` step runs as the default unprivileged `runner` user, not
  root — creating a system-scope systemd unit and actually starting/stopping it needs root or a
  polkit rule this project does not control, and this dev environment has no Linux host to verify
  a workaround against either. `LinuxServiceActionToolsTests` therefore covers manifest/wiring
  correctness (risk levels, verification declarations, the implausible-name guard) for real, but
  not a genuine create→start→stop→delete cycle — unlike the Windows equivalent, which does get
  that full real cycle via CI's elevated runner. The three tools share `SystemctlInvoker`, already
  proven for real by `service.list`/`service.status`'s own CI-verified tests, so the gap is
  specifically "the full lifecycle, elevated," not "systemctl invocation at all."
- **`fs.move`'s "content identity" verification lives in `ExecuteAsync`, not in
  `EvaluateVerificationAsync`.** Explained in detail above and in the tool's own doc comment —
  this is the direct, load-bearing consequence of a real constraint in
  `IVerifiableTool`'s current shape, surfaced and reasoned through rather than worked around
  silently, per `agentic/05-workflow.md`'s "if a rule blocks you" guidance.
- **`process.stop` on Windows cannot reach a process without a main window.** This is a genuine
  Windows platform limitation (no generic SIGTERM equivalent), not a bug — `process.kill` is the
  documented escalation path, and the tool says so in its own failure message.
- **No change to `bOps.Abstractions`, `bOps.Runtime`, `bOps.Policy`, `bOps.Memory` or
  `bOps.Audit`** — every new capability is a package, exactly as A1 requires; `bOps.Architecture.Tests`
  confirms it mechanically.

## Exact next steps, in order

1. Commit tranche 2 in well-scoped commits (Filesystem's `fs.move`; System's `process.stop`/`kill`;
   Service's `start`/`stop`/`restart` + the new Windows test-service fixture; README/SBOM) —
   mirroring tranche 1's and V0.10's granularity.
2. Ask the user before pushing (standing rule, `agentic/05-workflow.md`). No `Co-Authored-By:
   Claude` trailer.
3. After pushing, confirm CI is green on GitHub's own runners — this is specifically where
   `process.stop`/`kill`'s Linux half and `service.start`/`stop`/`restart`'s **full elevated
   Windows lifecycle** get their first real execution. A failure in the five
   `RequiresElevationFact`-gated Windows tests would mean the "windows-latest runs elevated"
   assumption above was wrong for this specific job — treat that as new information to act on,
   not a surprise to explain away.

## Next: V1.0 — security hardening and a stable public contract

V0.11 was the last version before V1.0 in `piano-bops-v0.9.1-v2.0.md`'s roadmap. V1.0's own goal
(per the plan) is making the runtime and SDK "publishable as a reliable base for commercial
extensions" — freezing `bOps.Abstractions`'s 1.0 surface (rule A12/D-012), `docs/security/threat-
model.md` (referenced but not yet written — ADR-0020 and ADR-0021 both point at it), and whatever
hardening the plan's own V1.0 section specifies. Per the project's own scope-discipline rule
(`agentic/05-workflow.md`), starting V1.0 work is a new decision for the user to make explicitly,
not something to begin automatically just because V0.11 closed out clean.
