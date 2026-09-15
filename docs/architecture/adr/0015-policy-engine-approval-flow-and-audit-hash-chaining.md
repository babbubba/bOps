# ADR-0015 — Policy engine, approval flow, audit hash-chaining, and `AnalysisLevel=latest-all`

Status: Accepted

## Context

`agentic/00-project-spec.md`'s roadmap names V0.3 "Policy engine and approval flow; analyzers
escalate to `all`." `agentic/06-decisions.md` D-011 already scheduled the analyzer escalation for
this version; D-008 already scheduled audit hash-chaining "alongside the policy engine." V0.1 and
V0.2 shipped with `AgentRunner.ExecuteStepAsync` hardcoding "no policy engine exists yet, refuse
everything above `Read`" — a deliberate, documented placeholder for exactly this version (rule
S3), and `JsonLinesAuditSink` writing raw, unchained JSON lines, documented as "append-only by
convention, not tamper-evident" (rule S9).

## Decision

### Policy engine (`bOps.Policy`, new project)

- `PolicyEngine : IPolicyEngine` evaluates a `PolicyContext` against a `PolicyConfig` (defaults
  per `RiskLevel`, per-tool overrides, per-package risk ceilings). It never names a tool or
  package in code (rule A1) — every identifier it acts on comes from `PolicyConfig`, loaded from
  data. `RiskLevel.Critical` is checked first, unconditionally, before any config lookup: no
  tool override, default, or ceiling can ever produce anything but `Forbidden` for it (rule S3).
- `PolicyConfigLoader.Load(string yaml)` parses `policy.yaml` (via `YamlDotNet` — a new
  dependency on `bOps.Policy`, not `bOps.Abstractions`, which stays dependency-free per
  `agentic/05-workflow.md`) into a `PolicyConfig`, and **rejects** — throws
  `PolicyConfigurationException`, not a silent coercion — a document that assigns
  `defaults.critical` anything but `forbidden` (rule S3: "the policy loader rejects a
  configuration that assigns any other mode to Critical, with a clear error").
- Two built-in configs, distinguished by *why* there is no operator-authored policy:
  `PolicyConfig.SafeDefault` (Read/Low automatic, Medium/High approval, Critical forbidden) when
  no `policy.yaml` file exists at all — a normal, unconfigured starting point — and
  `PolicyConfig.AllForbidden` (everything forbidden) when a file exists but failed to load. An
  operator who wrote a broken policy clearly intended something other than the built-in default;
  falling back to that default could silently be *more* permissive than what they thought they
  had configured, so the file's presence and validity are treated as two different failure modes
  with two different safe answers (rule S3: "a malformed policy.yaml... resolves to Forbidden.
  Never to Automatic").
- `AgentRunner.ExecuteStepAsync` now calls `policyEngine.Evaluate(...)` where V0.1/V0.2 hardcoded
  refusal. `PackageTrustLevel` is hardcoded to `Official` for every call — every package loaded
  today is first-party, shipped in this repository; real per-package trust assignment has no
  mechanism to hang off until dynamic loading arrives at V0.10 (D-003). This is a known, accepted
  simplification, not an oversight.

### Approval flow

- `ConsoleApprovalProvider` (`bOps.Cli`) implements `IApprovalProvider`: prints the tool, its
  risk, why it needs approval, its arguments, and what will be verified afterwards, then blocks
  on a `y/N` prompt plus an optional note. The CLI is the primary interface (principle 6), so
  this is where approval lives first; a Phase 2 API/UI host implements its own
  `IApprovalProvider` (a queue, not a blocking prompt) without `AgentRunner` or `PolicyEngine`
  changing at all — that is the entire reason `IApprovalProvider` is an interface `AgentRunner`
  depends on, not a concrete console type.
- `AuditEvent` gains `ApprovalAuditEvent` (`Package`, `Tool`, `Approved`, `Approver`, `Note`) —
  distinct from `PolicyDecisionAuditEvent`, which records what policy decided (approval is
  required, and why); this records what the human decided, and by whom, which policy cannot know
  in advance (rule B7: "an audit log that cannot say who approved is not an audit log"). A
  rejected approval terminates that step exactly like a policy denial — `ToolCallAuditEvent`
  gets `Authorization = AuthorizationKind.UserRejected` (that enum value existed since V0.1 but
  nothing had ever set it) — and, like `PolicyDenied`/`UnknownTool`/`Timeout`, now also counts as
  a "deviation" that triggers a replan (rule C8): an operator saying no to a proposed action is
  exactly the kind of "this did not go as the plan assumed" signal replanning exists for.

### Audit hash-chaining (`bOps.Audit`)

- `JsonLinesAuditSink` now writes each event inside an `AuditChainEnvelope { Seq, PrevHash, Hash,
  EventJson }`, where `Hash = SHA256(PrevHash + EventJson)`. `EventJson` is stored as a JSON
  **string** (the event's exact serialized text, escaped), not a nested JSON object: a string
  round-trips through encoding byte-for-byte, while re-serializing a reparsed `JsonObject` is not
  guaranteed to reproduce the identical text that was actually hashed — which would make
  `AuditChainVerifier` report tampering that never happened. This was caught by a test, not
  reasoned out in advance; see "Alternatives considered."
- The sink reads the last line of an existing file at construction to continue its chain across
  process restarts, and throws `InvalidOperationException` if that line is not a well-formed
  envelope (missing `Hash`/`PrevHash`) — which is what a pre-V0.3 file (raw events, no envelope)
  or a truncated write looks like. This was also caught by testing against a real (if stale)
  local `audit.jsonl` left over from V0.1/V0.2 smoke tests: the first implementation silently
  deserialized such a line into an envelope with `Hash`/`PrevHash` both `null` and continued the
  chain from there, which defeats the entire point. Refusing to continue an untrustworthy chain
  is the correct failure mode; there is no requirement to migrate a pre-chain file.
- `AuditChainVerifier.Verify` / `VerifyFile` independently re-derive every hash and confirm
  `prevHash` continuity, returning which `Seq` broke and why. Nothing calls it automatically yet
  — it exists so the chain is checkable, not merely present. A CLI command to run it on demand is
  a reasonable, small follow-up, not done here (see "Not done" below).
- This is tamper-**evident**, not tamper-**proof**: nothing stops someone with write access to
  the file from rewriting it from a point and recomputing every hash after it. The chain proves
  content was not altered without also being recomputed — it does not prove the file's custodian
  is honest. `agentic/03-security-rules.md` must keep saying so.

### `AnalysisLevel` escalation (D-011)

`Directory.Build.props` moves from `latest-recommended` to `latest-all`. This surfaced real,
fixable issues across the existing codebase — missing `ArgumentNullException.ThrowIfNull` guards
on public entry points (`bOps.Abstractions`, `bOps.Runtime`, `System.Core`,
`Providers.OpenAiCompatible`), four exception types missing standard constructors (CA1032), a
P/Invoke missing `[DefaultDllImportSearchPaths]`, several test-only types that could be
`internal`, and one genuinely dead test double (`HangingChatModel`, never referenced) — all
fixed, not suppressed. One new suppression was added, `CA2007` (`ConfigureAwait(false)`),
solution-wide: it protects against deadlocking a capturing `SynchronizationContext`, and nothing
in this solution — a console host plus libraries consumed only by that host and its own tests —
ever runs under one. `agentic/02-coding-standards.md`'s escalation-schedule table predicted this
column would be "reviewed and shortened"; in practice it grew by one deliberate, documented
exception instead, which the table now says explicitly.

## Alternatives considered

- **A per-package trust-level table, populated now.** Rejected: nothing shipped today has a real
  installation story to hang trust assignment off (D-003: dynamic loading is V0.10). Hardcoding
  `Official` for every call is honest about the current state rather than inventing a mechanism
  with no real input to drive it.
- **Embedding the audited event as a nested JSON object in `AuditChainEnvelope`**, which reads
  more naturally in the raw file. Rejected after a test caught the problem directly: reparsing
  and re-serializing a `JsonNode` tree is not guaranteed to reproduce byte-identical text to what
  was originally hashed (property order and number formatting are not contractually stable
  across a parse/write round trip), which would make the verifier produce false positives. A
  JSON string value is guaranteed lossless by the JSON spec itself.
- **Silently adopting whatever the last line of an existing audit file says**, to make the sink
  maximally permissive about pre-existing files. Rejected — see "Audit hash-chaining" above: this
  was the first implementation, caught by testing against a real stale file, and reverted in
  favor of failing loudly.
- **Replanning on every `AuthorizationKind` other than `Automatic`, including a plain tool
  `Failure`.** Considered for `UserRejected` alongside the existing `PolicyDenied`/`UnknownTool`/
  `Timeout` set, and included — an operator's "no" is a real deviation from the plan's
  assumptions. `Failure` remains excluded, per ADR-0014's original reasoning: the model already
  self-corrects from a failure observation on its own next turn.
- **A CLI subcommand to run `AuditChainVerifier` on demand** (`bops audit verify`). Reasonable
  and small, but the CLI currently has no subcommand parsing at all (`bops "<goal>"` is the only
  form) — adding one is its own small piece of scope, not needed for the verifier to exist and be
  tested. Left as a flagged follow-up, not done as a side effect of this change.

## Consequences

- Every non-`Read` tool call now genuinely goes through policy, for the first time — though no
  tool shipped as of V0.3 is anything but `Read`, so the `Approval`/`Forbidden` paths are
  exercised by tests (`bOps.Policy.Tests`, `bOps.Runtime.Tests`) rather than by any real tool
  yet. They will matter starting with the Filesystem/Network packages at V0.5.
- `AuditEvent` (`ApprovalAuditEvent`) and `TaskState`/`AgentTaskStatus` (none changed this time —
  only the audit hierarchy did) are breaking shape changes, acceptable under D-012:
  `bOps.Abstractions` stays on `0.x` until V1.0 specifically so changes like this do not require
  a migration story.
- A pre-V0.3 `audit.jsonl` cannot be continued by the new sink — it must be moved aside. This
  never affected any real deployment (none exists yet); it only ever applied to local dev/smoke
  artifacts, which are `.gitignore`d and were never committed.
- `bOps.Policy` is the fifth core project. It depends only on `bOps.Abstractions` (rule A7) and
  is referenced by `bOps.Cli` and its own tests — `bOps.Runtime` depends on `IPolicyEngine`/
  `IApprovalProvider` (both in `bOps.Abstractions`) and never references `bOps.Policy` directly,
  keeping the concrete policy engine swappable at the host's composition root.

## Not done (flagged, not silently skipped)

- **`ModelCallAuditEvent` still does not say *why* a model was called** (plan vs. replan vs.
  step) — a pre-existing gap from ADR-0014, unrelated to this ADR's scope, not addressed here.
- **No CLI command runs `AuditChainVerifier`.** See "Alternatives considered."
- **The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
  0002, 0005, 0006, 0011, 0012) are still unwritten**, flagged again from the V0.1 and V0.2
  handoffs. ADR-0002 ("Five risk levels, and Forbidden as an unbypassable invariant") is now
  directly relevant to the code this ADR describes and would be a natural one to backfill first.
