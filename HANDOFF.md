# Handoff — V0.1 scaffolding in progress

Written because the previous session ran out of budget mid-task. Everything below is exact,
not a summary — follow it literally to resume without guessing.

## State right now

Committed at `bd56ef5` (on top of `0c90459` which added the README/agentic/LICENSE). Nothing
uncommitted. Last command run, and its exact output, is under "The one blocker" below.

**Builds clean**, individually verified with `dotnet build <path>.csproj`:
- `src/core/bOps.Abstractions`
- `src/core/bOps.Audit`
- `src/core/bOps.Runtime`
- `src/packages/bOps.Packages.System.Core`
- `src/packages/bOps.Packages.System.Windows`
- `src/packages/bOps.Packages.System.Linux`
- `src/packages/bOps.Packages.Providers.OpenAiCompatible`
- `src/packages/bOps.Packages.Providers.OpenRouter`
- `src/packages/bOps.Packages.Providers.Ollama`
- `src/packages/bOps.Packages.Providers.LlamaCpp`

**Does not build yet**: `src/core/bOps.Cli` — see below.

**No `.sln` file exists yet.** Every build so far has been per-project
(`dotnet build src/core/bOps.Abstractions/bOps.Abstractions.csproj`). Creating
`bOps.sln` and adding every project to it is still to do — do it once bOps.Cli builds, with:

```bash
dotnet new sln -n bOps
dotnet sln bOps.sln add (find src tests -name "*.csproj")
```

(adjust the `find` for the shell in use — Bash tool is Git Bash/POSIX on this machine).

## The one blocker

`dotnet build src/core/bOps.Cli/bOps.Cli.csproj` fails restore with:

```
error NU1902: Il pacchetto 'OpenTelemetry.Api' 1.13.1 presenta una vulnerabilità nota di gravità
moderata, https://github.com/advisories/GHSA-g94r-2vxg-569j
error NU1902: ... 'OpenTelemetry.Exporter.OpenTelemetryProtocol' 1.13.1 ... GHSA-4625-4j76-fww9
error NU1902: ... 'OpenTelemetry.Exporter.OpenTelemetryProtocol' 1.13.1 ... GHSA-mr8r-92fq-pj8p
error NU1902: ... 'OpenTelemetry.Exporter.OpenTelemetryProtocol' 1.13.1 ... GHSA-q834-8qmm-v933
```

This is NuGet's built-in audit (`NuGetAudit`), which the SDK treats as an error by default when
warnings are errors (`TreatWarningsAsErrors=true` in `Directory.Build.props`). The packages
referenced in `src/core/bOps.Cli/bOps.Cli.csproj` are:

```xml
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.13.1" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.13.1" />
```

### Fix it (pick one, in order of preference)

1. **Bump to a patched version.** Check
   `https://www.nuget.org/packages/OpenTelemetry.Exporter.OpenTelemetryProtocol` and
   `https://www.nuget.org/packages/OpenTelemetry.Extensions.Hosting` for a version past the
   advisories above (likely a 1.13.x point release or 1.14+). Update both `Version=` attributes
   in `bOps.Cli.csproj` (keep them on the same version — they're meant to move together) and
   rebuild. **This is the correct fix if a patched version exists** — do this first, don't skip
   to option 2.
2. **If no patched version exists yet**, suppress specifically and record it:
   - Add `<NoWarn>$(NoWarn);NU1902</NoWarn>` to `src/core/bOps.Cli/bOps.Cli.csproj`'s own
     `PropertyGroup` (not the shared root list in `Directory.Build.props` —
     `agentic/02-coding-standards.md` forbids adding to the shared list to unblock a task).
   - Add a row to `docs/architecture/suppressions.md` for `NU1902`, scope
     `bOps.Cli.csproj`, reason (which advisories, why they don't apply — e.g. "OTLP exporter
     used only for local dev telemetry, not exposed to untrusted input"), and "remove when a
     patched version ships."

Do not touch `Directory.Build.props`'s shared `NoWarn` list for this — it's project-specific.

## What to do right after the blocker is fixed

1. `dotnet build src/core/bOps.Cli/bOps.Cli.csproj` — confirm clean.
2. Smoke-test the CLI once it builds:
   ```bash
   cd src/core/bOps.Cli
   dotnet run -- "how is this machine doing?"
   ```
   Expect it to fail at the HTTP call (no real `ApiKey` is configured in
   `appsettings.json` — it's intentionally empty per agentic rule S6) but everything up to
   that point — config binding, DI wiring, tool registry populated with 5 `system.*`/
   `process.list` tools for the current OS, `IChatModelRegistry` resolving `"OpenRouter"` — must
   succeed. If DI resolution throws before the HTTP call, that's a real bug to fix, not
   something to work around.
3. Create `bOps.sln` (see command above) and verify `dotnet build bOps.sln` builds everything
   including the CLI.
4. **Write tests.** Nothing under `tests/` exists yet, despite the directories being created
   (`tests/bOps.Runtime.Tests`, `tests/bOps.Packages.System.Conformance`,
   `tests/bOps.Packages.System.Windows.Tests`, `tests/bOps.Packages.System.Linux.Tests` are
   empty folders with no `.csproj`). Per `agentic/04-testing-rules.md`, this is a real gap, not
   a nice-to-have:
   - `bOps.Runtime.Tests` — test-first is the rule for the core (rule D-010), and this project
     has zero coverage right now. Priority order for tests to write, matching the minimum case
     list in `agentic/04-testing-rules.md`: `ToolRegistry.Register` rejecting a non-Read tool
     without `VerificationSpec`/`IVerifiableTool`; `AgentRunner` handling an unknown tool name,
     a tool that throws, a tool that times out, `MaxSteps` reached, and — importantly — the
     already-implemented "no policy engine yet → non-Read tools are refused" guard in
     `AgentRunner.ExecuteStepAsync`.
   - `bOps.Packages.System.Conformance` — described in `04-testing-rules.md` but not yet
     created as a real shared assertion library. Needs to exist before the Windows/Linux test
     projects can reference it meaningfully.
   - `bOps.Packages.System.Windows.Tests` / `.Linux.Tests` — integration tests against the
     real OS (never mocked, per the same doc). The Linux one only runs in CI/WSL/a Linux box;
     the Windows one runs here.
   - Every new contract type in `bOps.Abstractions` needs a JSON round-trip test (rule A2) —
     none exist yet either. This is straightforward and mechanical: one test per record type in
     `Tools.cs`, `Audit.cs`, `Model.cs`, `Policy.cs`, `TaskState.cs`, serialize then deserialize,
     assert equality.
5. Only after the above: continue down the V0.1 roadmap item, or move to V0.2 per
   `agentic/00-project-spec.md`'s roadmap table, if the user directs that.

## Design decisions made this session that aren't yet reflected in `agentic/`

These were necessary, small, technical corrections discovered while actually writing the code.
They are consistent with the spirit of the already-written rules but are not yet written into
`agentic/01-architecture-rules.md` itself. If picking this up cold, know these are deliberate,
not accidents — but consider folding them into the docs as a small follow-up:

1. **C# namespace is `bOps.Packages.Sys.*`, not `bOps.Packages.System.*`.** A namespace segment
   literally named `System` breaks every unqualified `System.*` reference inside it (the
   compiler resolves `System.Console` etc. against the enclosing namespace first). Project/
   assembly names keep the `bOps.Packages.System.*` spelling (matches `agentic/` and the
   README); only the `RootNamespace` MSBuild property and the `namespace` declarations in code
   differ. See the comment in `src/packages/bOps.Packages.System.Core/bOps.Packages.System.Core.csproj`.
2. **`bOps.Packages.System.Windows` targets plain `net10.0`, not `net10.0-windows`.**
   `bOps.Cli` (plain `net10.0`, cross-platform) references both the Windows and Linux System
   packages and picks one at runtime by OS — a project on a plain TFM cannot reference one on a
   platform-specific TFM, so `net10.0-windows` would have made that reference graph illegal.
   Platform intent is instead declared with `[assembly: SupportedOSPlatform("windows")]` in
   `src/packages/bOps.Packages.System.Windows/AssemblyInfo.cs`.
3. **`IToolRegistry` gained `Task RefreshCapabilitiesAsync(CancellationToken ct = default)`**,
   not present in the first draft of `agentic/01-architecture-rules.md` §B4. `GetAvailableManifests()`
   must stay synchronous (the planner calls it every step) but capability probing is
   inherently async, so there has to be an explicit, host-driven refresh point. **This one is
   already written into `agentic/01-architecture-rules.md`** (search for
   "RefreshCapabilitiesAsync") — the other two above are not yet.
4. **`AuthorizationKind` gained a fifth value, `UnknownTool`**, and `PackageId` gained a
   static `PackageId.Unknown`, for auditing a tool-call attempt that never resolved to any
   registered tool (a model hallucinating a tool name). Not yet mentioned in
   `agentic/01-architecture-rules.md`'s `AuditEvent` section (§B8) — it lists only
   `Automatic | UserApproved | UserRejected | PolicyDenied`.
5. **`ModelProtocolException`** (in `bOps.Abstractions/Model.cs`) is a new exception type not
   in the original architecture doc, thrown by `OpenAiCompatibleChatModel` when the
   JSON-schema-fallback strategy (plan §3.1.1) fails to parse valid output after one retry, and
   caught by `AgentRunner.RunAsync` to end the task as `AgentTaskStatus.Failed` rather than
   crashing — satisfying rule C1 ("nothing thrown escapes an iteration") for this specific,
   real failure mode that the original architecture doc didn't anticipate.

None of these need a decision from the user — they're implementation necessities consistent
with rules already agreed (D-006 through D-012). Fold them into `agentic/01-architecture-rules.md`
as a quick edit when convenient, so the doc matches the code exactly.

## What V0.1 deliberately does NOT have yet (by design, not oversight)

- No `bOps.Policy` project. `AgentRunner` refuses (fails closed) any tool whose risk is above
  `Read`, with an audited `PolicyDenied` reason — see `agentic/01-architecture-rules.md`
  rule S3 and the comment in `AgentRunner.ExecuteStepAsync`. This is correct for V0.1: every
  tool shipped so far (`system.info/cpu/memory/disk`, `process.list`) is `Read`.
- No `bOps.Memory` project / SQLite. `TaskState` and `PlanStep` exist as in-memory-only shapes
  inside one `AgentRunner.RunAsync` call; nothing persists across process runs. Arrives at V0.7.
- No dynamic plugin loading. Every package above is a direct `ProjectReference` from
  `bOps.Cli.csproj`. Arrives at V0.10 (`agentic/06-decisions.md`, D-003).
- `fs.*` (Filesystem package) does not exist — deferred to V0.5 per the roadmap table in
  `agentic/00-project-spec.md`. Don't add filesystem tools while still on V0.1.

## Reference: files this session created or touched

Everything under `src/`, plus `Directory.Build.props`, `.editorconfig`, `global.json`,
`docs/architecture/suppressions.md`, and two small edits to
`agentic/01-architecture-rules.md` and `agentic/02-coding-standards.md` (the
`RefreshCapabilitiesAsync` addition and the `AnalysisLevel` value fix — `recommended` isn't a
valid MSBuild value on this SDK; it must be `latest-recommended` / `latest-all`). All of it is
in commit `bd56ef5`. Nothing else in the repo was touched.
