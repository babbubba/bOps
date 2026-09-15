# Handoff — V0.1 scaffolding complete

Written at the end of the session that unblocked the build and finished V0.1 per the previous
handoff's plan. Everything below is exact, not a summary — follow it literally to resume.

## State right now

**`bOps.sln` exists at the repo root and `dotnet build bOps.sln` builds clean end to end — 0
warnings, 0 errors** (verified with a full clean of every `bin`/`obj` and a from-scratch
rebuild, not just an incremental one). All 15 projects are in the solution: every `src/`
project from the previous session, plus four new ones under `tests/`.

`dotnet test bOps.sln` results:

- **`bOps.Runtime.Tests`** — 44 tests, all passing. Covers `ToolRegistry.Register` (rule B3:
  accept/reject on verification), `AgentRunner` (unknown tool, throwing tool, timing-out tool,
  the fail-closed policy-absence guard, repeated-policy-denial → `PolicyBlocked`, MaxSteps,
  budget exceeded, multi-tool-call-per-turn, model-call failure of both a known and an unknown
  exception type), and a JSON round-trip test for every contract record in `Tools.cs`,
  `Audit.cs`, `Model.cs`, `Policy.cs`, `TaskState.cs`, `Identity.cs`, `Providers.cs`.
- **`bOps.Packages.System.Windows.Tests`** — 7 tests, all passing, run against the real Windows
  host (this machine) via the shared conformance suite. Never mocked, per
  `agentic/04-testing-rules.md`.
- **`bOps.Packages.System.Linux.Tests`** — 7 tests; 1 passes (a manifest-shape check that
  touches no `/proc`), 6 **skip visibly** with `[LinuxOnlyFact]` (a custom `FactAttribute` that
  sets `Skip` when not on Linux). There is no Linux host in this dev environment. This project
  compiles clean and is ready to run for real the moment one exists (a container, CI's
  `ubuntu-latest` matrix from V0.5, or the Aspire AppHost). **Do not delete or "fix" the skips —
  they are the correct, visible behavior the testing rules require, not a gap.**
- **`bOps.Packages.System.Conformance`** — not itself a test project; a shared assertion
  library (`SystemToolConformance`) referenced by both `.Tests` projects above, asserting
  structure and invariants (percentages in 0–100, memory self-consistency, manifest shape) —
  never exact values, per `agentic/04-testing-rules.md`.

CLI smoke test (`cd src/core/bOps.Cli && dotnet run -- "how is this machine doing?"`): DI wiring,
config binding, tool registry (5 `system.*`/`process.list` tools for the current OS), and
provider registry all succeed; the task fails cleanly at the HTTP call with `ApiKey` empty (rule
S6 — no real key is or should ever be in `appsettings.json`), and — after this session's audit
fix below — that failure now produces a `modelCall` audit event with `Outcome: Failure`, not
silence.

## What this session did, in order

### 1. Fixed the NU1902 blocker

Bumped `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` from
`1.13.1` to `1.18.0` in `src/core/bOps.Cli/bOps.Cli.csproj` — a patched version existed, so no
suppression was needed (`docs/architecture/suppressions.md` was not touched). The version bump
surfaced three unrelated, pre-existing compile errors that restore had been masking:

- `Program.cs` was missing `using OpenTelemetry.Trace;`, `using OpenTelemetry.Metrics;`, and
  `using Microsoft.Extensions.Configuration;` (`AddOtlpExporter`, `IConfigurationSection.Get<T>`).
- The OS-provider selection (`CurrentPlatform.Id == "windows" ? new WindowsSystemToolProvider() :
  ...`) tripped `CA1416` (platform-compat analyzer): the analyzer only recognizes
  `OperatingSystem.IsWindows()`/`IsLinux()` as a guard for a `[SupportedOSPlatform("windows")]`
  type, not an arbitrary string comparison. Rewrote the selection in `Program.cs` to guard with
  `OperatingSystem.IsWindows()` / `IsLinux()` directly.

### 2. Found and fixed two real rule-C1/S9 violations while smoke-testing

The task instructions said a bug reaching all the way to the HTTP call was expected and fine to
leave (no `ApiKey`); what was **not** fine, and what the smoke test caught, is what happened
*after* that HTTP call failed:

- **An `HttpRequestException`/non-success HTTP status from `OpenAiCompatibleChatModel.SendAsync`
  crashed the whole CLI process with an unhandled exception.** Rule C1 ("nothing thrown escapes
  an iteration") is explicit that a provider failure must become an observation, not a crash.
  Fixed in `src/packages/bOps.Packages.Providers.OpenAiCompatible/OpenAiCompatibleChatModel.cs`:
  `SendAsync` now wraps both "could not reach the provider" and "provider replied with a
  non-success status" in `ModelProtocolException`, exactly like it already did for a malformed
  response body.
- **Even after that fix, `AgentRunner.RunAsync` only caught `ModelProtocolException`
  specifically around the model call** — any other exception from a provider package (a bug in
  a *different* `IChatModel` implementation, not necessarily `OpenAiCompatibleChatModel`) would
  still have escaped and crashed the loop. Broadened the catch to `catch (Exception ex) when (ex
  is not OperationCanceledException)` in both `RunAsync` and `CallModelAsync` — defense in depth
  for rule C1, not a replacement for provider packages doing their own wrapping.
- **A failed model call produced *zero* audit events.** `CallModelAsync` only wrote a
  `ModelCallAuditEvent` *after* a successful `model.CompleteAsync()`; a task that fails at step 0
  left no trace at all in the audit log, violating rule S9 ("every model call" is audited,
  whatever the outcome). Fixing this required changing the shape of `ModelCallAuditEvent` in
  `bOps.Abstractions` (added `ModelCallOutcome { Success, Failure }` and an `ErrorMessage`
  field), which `agentic/05-workflow.md` requires an ADR for — see
  `docs/architecture/adr/0013-model-call-audit-outcome.md`. Both the failure and success paths
  now audit unconditionally.
- **`ToolArguments` had no `JsonConverter`.** It has no public settable state by design (it's
  backed by a private `JsonObject`), so any record carrying it — `ModelToolCall`,
  `ToolCallRequest`, `PolicyContext` — silently serialized its `Arguments` as `{}` under plain
  `System.Text.Json` reflection-based serialization, discarding every argument. This is exactly
  the class of defect architecture rule A2's round-trip-test requirement exists to catch, and the
  new `JsonRoundTripTests.ModelToolCall_RoundTrips` / `ToolCallRequest_RoundTrips` caught it on
  the first run. Fixed with a `ToolArgumentsJsonConverter` (mirroring the existing
  `NodeIdJsonConverter`/`PackageIdJsonConverter` pattern) and `[JsonConverter(...)]` on
  `ToolArguments` itself, in `bOps.Abstractions/ToolArguments.cs`.

None of this was worked around or deferred — each is a real fix, tested, in the diff.

### 3. Implemented rule C4 (repeated policy denial → `PolicyBlocked`), which did not exist yet

`agentic/01-architecture-rules.md` §C4 and `agentic/04-testing-rules.md`'s agent-loop minimum
test list both require it ("N consecutive denials of the same tool (default 2) end the task as
`PolicyBlocked`"), but `AgentRunner` had no such tracking — a model that kept proposing the same
forbidden tool would have retried it until `MaxSteps`. Added `AgentRunnerOptions.
MaxConsecutivePolicyDenials` (default 2) and consecutive-denial tracking in `RunAsync`, keyed on
tool name and `AuthorizationKind.PolicyDenied` specifically (not `UnknownTool`, which is a
different failure mode with its own audit path). Also discovered while implementing this: rule
S3's "a Forbidden decision is always audited as a `PolicyDecisionAuditEvent`" was not happening —
`RejectAsync` only wrote a `ToolCallAuditEvent`. Fixed: it now writes both, for the
`PolicyDenied` case.

### 4. Created the four missing test projects and wrote all tests from HANDOFF.md section 4

In priority order, as specified. See "State right now" above for counts. Test doubles
(`FakeChatModel`, `ThrowingChatModel`, `HangingChatModel`, `RecordingAuditSink`,
`AlwaysAvailableCapabilityProbe`, `FakeReadTool`/`ThrowingTool`/`HangingTool`/
`FakeHighRiskTool`/`UnverifiedHighRiskTool`/`DeclaredButNotVerifiableTool`) live in
`tests/bOps.Runtime.Tests/` — `FakeChatModel` is the "replays a recorded sequence of
`ModelResponse` values" double `agentic/04-testing-rules.md` calls for; it did not exist before
this session either.

`bOps.Packages.System.Windows.Tests` targets `net10.0-windows` (not plain `net10.0`) — unlike
`bOps.Packages.System.Windows` itself, this test project only ever calls the Windows package and
only ever runs on a Windows host, so it can declare the platform directly and let `CA1416`
verify every call site instead of suppressing it.

### 5. Folded HANDOFF.md's "design decisions" items 1, 4, 5 into `agentic/01-architecture-rules.md`

- Item 1 (`bOps.Packages.Sys.*` namespace vs. `bOps.Packages.System.*` project name) — added to
  §A8.
- Item 2 (Windows package plain `net10.0` TFM) — mentioned briefly in the same place in §A8, per
  the task's "if there's a natural place" instruction.
- Item 4 (`AuthorizationKind.UnknownTool`, `PackageId.Unknown`) — added to §B8, with the enum
  now spelled out (it was previously just a code comment: `// automatic | user-approved | ...`).
- Item 5 (`ModelProtocolException`) — added to §C1, along with this session's broadened
  exception handling (ADR-0013).
- Also fixed in passing: §C referred to the loop living in `AgentPlanner.cs`; the actual file is
  `AgentRunner.cs` (there is no separate planner in V0.1 — planning and execution are not yet
  split). Corrected the filename reference.
- Item 3 (`RefreshCapabilitiesAsync`) was already documented — untouched.

## New deviation discovered this session, not yet elsewhere

**No ADRs exist for ADR-0001 through ADR-0012**, despite `agentic/05-workflow.md` listing them
under "The first ADRs to exist, per the plan and the decisions taken" as if they should already
be written (0001, 0002, 0005, 0006, 0011, 0012 are named explicitly; 0003/0004/0007–0010 are
implied by the numbering gap). Only `docs/architecture/adr/0013-model-call-audit-outcome.md`
exists, written this session for the specific audit-schema change described above. Backfilling
the other six is a real gap but is a documentation-only exercise with no code impact and no
urgency (the decisions themselves are already fully recorded, with rationale, in
`agentic/06-decisions.md` and `agentic/07-plan-corrections.md`) — it was judged out of scope for
this session, which was about finishing V0.1's code and tests, not writing retroactive ADRs. Flag
it to the user; do not silently start writing six ADRs as a side effect of an unrelated task.

## What V0.1 deliberately does NOT have yet (unchanged from the previous handoff, still correct)

- No `bOps.Policy` project. `AgentRunner` refuses (fails closed) any tool whose risk is above
  `Read`, with an audited `PolicyDenied` reason, and now also terminates the task as
  `PolicyBlocked` after repeated denials of the same tool (rule C4, added this session).
- No `bOps.Memory` project / SQLite. `TaskState` and `PlanStep` exist as in-memory-only shapes
  inside one `AgentRunner.RunAsync` call. Arrives at V0.7.
- No dynamic plugin loading. Every package is a direct `ProjectReference`. Arrives at V0.10.
- No `fs.*` (Filesystem) package. Deferred to V0.5.

## Next steps

V0.1 is genuinely solid: clean full-solution build, real test coverage at the discipline
`agentic/04-testing-rules.md` requires for each layer, and every rule violation the smoke test
and the round-trip tests turned up was fixed rather than deferred. This session's budget went
entirely into getting V0.1 right rather than starting V0.2 ("Explicit agent loop with
replanning" per `agentic/00-project-spec.md`'s roadmap table) — per the scope-discipline rule in
`agentic/05-workflow.md`, starting a real architectural change (splitting planning from
execution) with whatever budget happened to be left over would have meant leaving it
half-done, which the task instructions explicitly said to avoid. **V0.2 has not been started.**
The next session should begin there, reading `agentic/00-project-spec.md`'s roadmap table and
`agentic/07-plan-corrections.md` for what "replanning" is meant to fix relative to the original
plan, before writing any code.
