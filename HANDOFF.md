# Handoff — V0.3 (policy engine, approval flow, audit hash-chaining) complete

Written at the end of the session that implemented V0.3 on top of the completed V0.2 explicit
agent loop. Everything below is exact, not a summary — follow it literally to resume.

## State right now

**`dotnet build bOps.sln` builds clean end to end — 0 warnings, 0 errors** (verified with a full
clean of every `bin`/`obj` and a from-scratch rebuild), now under `AnalysisLevel=latest-all`
(escalated this session, per D-011). `dotnet test bOps.sln`: **97 passing, 6 skipped** (the
Linux package tests — correctly and visibly skipped, no Linux host in this dev environment).
Nothing failing. 18 projects in `bOps.sln`, up from 15: `bOps.Policy` (new core project),
`bOps.Policy.Tests` and `bOps.Audit.Tests` (both new — `bOps.Audit` had zero tests before this
session, despite `agentic/04-testing-rules.md` requiring test-first coverage for it since V0.1).

CLI smoke test (`cd src/core/bOps.Cli && dotnet run -- "how is this machine doing?"`): logs "No
policy file at 'policy.yaml'; using the built-in default", then DI wiring, config binding, tool
registry, provider registry and the policy engine all succeed; the task fails cleanly at the
planning call's HTTP request with `ApiKey` empty (rule S6 — never a real key in
`appsettings.json`). The audit chain was verified by hand on a fresh `audit.jsonl`: `Seq: 0`,
`PrevHash` equal to `JsonLinesAuditSink.GenesisHash`, a real SHA-256 `Hash`.

## What this session did

Implemented V0.3 per `agentic/00-project-spec.md`'s roadmap: **"Policy engine and approval flow;
analyzers escalate to `all`."** `agentic/06-decisions.md` D-008 also ties audit hash-chaining to
this version ("deferred to V0.3, alongside the policy engine"), so that shipped too. Full design
rationale, alternatives rejected, and what's deliberately not done is in
[`docs/architecture/adr/0015-policy-engine-approval-flow-and-audit-hash-chaining.md`](docs/architecture/adr/0015-policy-engine-approval-flow-and-audit-hash-chaining.md)
— read that before touching any of this area again. Summary:

### 1. `AnalysisLevel` escalated to `latest-all` (D-011) — done first, in isolation

Surfaced real, fixable issues across the *existing* codebase, all fixed rather than suppressed:
missing `ArgumentNullException.ThrowIfNull` guards on ~15 public entry points across
`bOps.Abstractions`, `bOps.Runtime`, `bOps.Packages.System.Core` and
`bOps.Packages.Providers.OpenAiCompatible`; four exception types (`ModelProtocolException`,
`ToolRegistrationException`, `ProviderNotSupportedException`, `ToolArgumentException`) missing
standard constructors (CA1032); a P/Invoke missing `[DefaultDllImportSearchPaths]`; several
test-only types changed from `public` to `internal`; one genuinely dead test double
(`HangingChatModel`, never referenced anywhere — deleted, not suppressed). One new suppression:
`CA2007` (`ConfigureAwait(false)`), solution-wide, documented in
`docs/architecture/suppressions.md` — this solution has no `SynchronizationContext` anywhere
(console host, libraries consumed only by that host and its own tests), so the diagnostic cannot
catch a real bug here. `agentic/02-coding-standards.md`'s escalation table is updated to match.

### 2. `bOps.Policy` (new core project)

- `PolicyEngine : IPolicyEngine` evaluates a `PolicyConfig` against a `PolicyContext`. Checks, in
  order: package known (else Forbidden) → `RiskLevel.Critical` (always Forbidden, unconditional,
  before any config lookup) → per-package risk ceiling (can only force Forbidden, never grant
  more) → per-tool override → per-risk-level default → Forbidden (no entry covers it).
- `PolicyConfigLoader.Load(string yaml)` parses `policy.yaml` via `YamlDotNet` (new dependency on
  `bOps.Policy`, not `bOps.Abstractions`), and throws `PolicyConfigurationException` — loudly,
  not a silent coercion — if `defaults.critical` is set to anything but `forbidden`.
- `PolicyConfig.SafeDefault` (Read/Low automatic, Medium/High approval, Critical forbidden) is
  used when no `policy.yaml` file exists. `PolicyConfig.AllForbidden` is used when a file exists
  but fails to load — deliberately more conservative than the safe default, because an operator
  who wrote a broken policy intended something other than the built-in behavior. See the ADR for
  why these are two different fallbacks, not one.
- `bOps.Cli`'s `Program.cs` decides which of the three (loaded / safe-default / all-forbidden)
  applies, via the new `LoadPolicyEngineAsync` local function, reading `Policy:FilePath` from
  config (default `"policy.yaml"`, not present in the repo — nothing ships one; an operator
  writes their own to opt into anything beyond the safe default).

### 2b. Approval flow

- `ConsoleApprovalProvider` (`bOps.Cli`, `internal`): prints the tool, risk, reason, arguments
  and verification description, blocks on a `y/N` console prompt plus an optional note.
- `bOps.Abstractions/Audit.cs` gains `ApprovalAuditEvent` (`Package`, `Tool`, `Approved`,
  `Approver`, `Note`) — distinct from `PolicyDecisionAuditEvent` (what policy decided vs. what
  the human decided). `AuthorizationKind.UserApproved`/`UserRejected` — both existed since V0.1,
  neither had ever been set until this session — are now set on the resulting
  `ToolCallAuditEvent`.
- A rejected approval now counts as a "deviation" that triggers a replan (rule C8), alongside the
  V0.2 set (`PolicyDenied`, `UnknownTool`, `Timeout`) — an operator's "no" is exactly the kind of
  "this did not go as the plan assumed" signal replanning exists for.
- `AgentRunner.ExecuteStepAsync`'s V0.1/V0.2 hardcoded "no policy engine yet, refuse everything
  above Read" block is gone, replaced by a real `policyEngine.Evaluate(...)` call.
  `PackageTrustLevel` is hardcoded to `Official` for every call — every package loaded today is
  first-party; real per-package trust has no mechanism to hang off until dynamic loading (V0.10).

### 3. Audit hash-chaining (D-008)

- `JsonLinesAuditSink` now writes `{Seq, PrevHash, Hash, EventJson}` per line, `Hash =
  SHA256(PrevHash + EventJson)`. `EventJson` is a JSON **string** (escaped), not a nested object
  — a real bug was caught here during testing: re-serializing a reparsed `JsonObject` is not
  guaranteed to reproduce the exact text that was hashed, which made the first implementation of
  the verifier report false-positive tampering. Storing the exact string avoids the whole
  problem, since JSON string decoding is lossless by construction.
- The sink reads the last line of an existing file at construction to continue the chain across
  process restarts, and **throws `InvalidOperationException`** if that line is not a well-formed
  envelope. This was also caught by testing, against a real stale local `audit.jsonl` left over
  from V0.1/V0.2 smoke tests (pre-chain format, raw events, no envelope) — the first
  implementation silently "continued" the chain from it with `PrevHash: null`, defeating the
  entire point. Confirmed fixed against that exact file before deleting it (it was a `.gitignore`d
  dev artifact, never committed, safe to delete — if you find another local `audit.jsonl`
  predating this session, delete it too; nothing needs to migrate it).
- `AuditChainVerifier.Verify(IEnumerable<string>)` / `.VerifyFile(path)` independently re-derive
  every hash and prev-hash link, returning which `Seq` broke and why. Nothing calls this
  automatically yet — no CLI subcommand exists to run it on demand (the CLI has no subcommand
  parsing at all currently, just `bops "<goal>"`) — flagged as a reasonable, small follow-up in
  the ADR, not done here.
- This is tamper-**evident**, not tamper-**proof** — `agentic/03-security-rules.md` rule S9 and
  `agentic/01-architecture-rules.md` §B8 both say so explicitly now, replacing the old "append-
  only by convention" language that was accurate for V0.1–V0.2 and is not anymore.

### 4. Tests

44 new tests across three areas, closing gaps `agentic/04-testing-rules.md` had specified since
V0.1 but that were never actually covered (no `bOps.Policy` or `bOps.Audit` test project existed
before this session):

- `bOps.Policy.Tests` (19 tests, new project): every minimum case the testing rules list for
  "Policy engine" — each risk level, tool overrides matching/not matching, Critical rejected at
  load (both `automatic` and `approval` attempts), an unknown package, a ceiling lowering a
  decision, **a ceiling that tries to raise one (must not — this one did not exist until this
  session's gap-audit against the testing-rules checklist)**, malformed YAML, an unrecognized
  risk level, an unrecognized policy mode.
- `bOps.Audit.Tests` (8 tests, new project): the chain is valid across consecutive writes and
  across a simulated restart (new sink, same file), tampering is detected (content altered
  without recomputing the hash), a removed line is detected, an empty/missing file verifies as
  valid, a pre-chain-format tail is rejected at construction, **and concurrent writes do not
  interleave or corrupt the chain (50 parallel `WriteAsync` calls, also not covered before this
  session)**.
- `bOps.Runtime.Tests` (+5, 51→56): approval granted (tool executes, `UserApproved`,
  `ApprovalAuditEvent`), approval rejected (denied, replans, `UserRejected`), a Forbidden
  decision never calls `IApprovalProvider` at all (`NeverCalledApprovalProvider` throws if it
  is), **and `Sensitive` arguments are redacted before reaching the audit log — required by
  `04-testing-rules.md` since V0.1, never actually tested until this session's gap audit**.
- New test doubles: `DefaultTestPolicyEngine` (Read automatic, else forbidden — the V0.1/V0.2
  hardcoded shape, now the *test* default so most existing tests needed no changes),
  `StubPolicyEngine`, `StubApprovalProvider`, `NeverCalledApprovalProvider`.
- `bOps.Abstractions/AssemblyInfo.cs` gains `InternalsVisibleTo("bOps.Policy.Tests")` — needed to
  construct a `ToolManifest` fixture with a specific `Package` directly in policy-engine tests,
  same reason `bOps.Runtime.Tests` already had it. Comment updated to distinguish "production
  assemblies that stamp `Package`" (still only `bOps.Runtime`) from "test assemblies that need to
  construct fixtures with it already set."

None of this was worked around or deferred — every fix and every new behavior is real, tested,
in the diff.

## Design choices worth knowing before extending this further

- **A plain tool `Failure` still does not trigger a replan** (unchanged from V0.2/ADR-0014); a
  rejected approval now does. See ADR-0015's "Alternatives considered" if this distinction stops
  making sense once more tool types exist.
- **No real per-package trust assignment exists.** Every `PolicyContext.Trust` is hardcoded
  `PackageTrustLevel.Official` in `AgentRunner`. This is honest about the current state (nothing
  loaded today isn't first-party) rather than inventing a mechanism with no real input until
  V0.10's dynamic loading exists.
- **`bOps.Policy` takes a dependency on `YamlDotNet`.** This is on `bOps.Policy`, never on
  `bOps.Abstractions`, which stays dependency-free per `agentic/05-workflow.md`. `bOps.Runtime`
  depends only on `IPolicyEngine`/`IApprovalProvider` (both in `bOps.Abstractions`) and never
  references `bOps.Policy` — the concrete engine stays swappable at the host's composition root.
- **`ModelCallAuditEvent` still does not say *why* a model was called** (plan vs. replan vs.
  step) — a pre-existing gap from ADR-0014, not addressed here, still just `StepIndex: -1` /
  triggering-step-index as the only hint.

## What V0.3 deliberately does NOT have yet (unchanged from V0.2 unless noted)

- No `bOps.Memory` project / SQLite — V0.7. Still in-memory only.
- No dynamic plugin loading — V0.10. Still direct `ProjectReference`s, so `PackageTrustLevel` has
  nothing real to compute from yet (see above).
- No `fs.*` (Filesystem) package — V0.5. **This means no shipped tool is anything but `Read`
  yet** — the policy engine's `Approval`/`Forbidden` paths are real and fully tested, but not yet
  exercised by any actual operator-facing tool call. That starts mattering at V0.5.
- No post-action verification service — V0.4. `IVerifiableTool` exists and is enforced at
  registration; nothing calls `EvaluateVerificationAsync` yet.
- No CLI command to run `AuditChainVerifier` on demand (see "Audit hash-chaining" above).

## Next steps

V0.3 is genuinely done: a real policy engine and approval flow gate every non-`Read` tool call,
the audit log is tamper-evident and independently verifiable, and the whole codebase compiles
clean under the stricter analyzer level the roadmap called for. **V0.4 — "Post-action
verification" — has not been started.** Per the scope-discipline rule, the next session should
begin by reading `agentic/00-project-spec.md`'s roadmap entry for V0.4 and
`agentic/03-security-rules.md` rule S4 (verification fails closed — `Inconclusive`/`NotApplicable`
are never success) before writing any code. The seam is `AgentRunner.RecordAsync`'s
`verification: null` parameter, always `null` today — V0.4 is where something real gets passed
there, calling `IVerifiableTool.EvaluateVerificationAsync` after a non-`Read` tool executes, using
its declared `VerificationSpec` to resolve and call the verification tool.

Two smaller, non-urgent items carried forward again from the last two handoffs, still real, still
judged out of scope for a session implementing code rather than backfilling documentation:

1. The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
   0002, 0005, 0006, 0011, 0012) are still unwritten. ADR-0002 ("Five risk levels, and Forbidden
   as an unbypassable invariant") is now directly relevant to the code in this session and would
   be a natural one to write first if a future session has spare budget.
2. No CLI subcommand runs `AuditChainVerifier`. Small, real, not done — see ADR-0015.
