# ADR-0016 — Post-action verification

Status: Accepted

## Context

`agentic/00-project-spec.md`'s roadmap names V0.4 "Post-action verification." The contract has
existed since V0.1 (`agentic/01-architecture-rules.md`, rule B3, D-006): a non-`Read` tool must
declare a `VerificationSpec` and implement `IVerifiableTool`, or `ToolRegistry.Register` refuses
it. What V0.1–V0.3 never did is call it — `AgentRunner.RecordAsync`'s `verification` parameter was
always `null`, and `ToolCallAuditEvent.Verification` was documented but never populated for a real
execution. Principle 3 ("every side-effecting action is verified after execution") was structural
at the registration boundary but not yet real at the point that matters.

## Decision

### When verification runs

After every **executed** non-`Read` tool call — `ToolOutcome.Success`, `Failure`, or `Timeout` —
`AgentRunner.ExecuteStepAsync` calls the tool's declared `VerificationSpec`. It does **not** run
for a call that was never executed at all: policy denial, an operator's rejection, or an argument
validation failure (rule S2) leave nothing for verification to check, and `Verification` on the
resulting `ToolCallAuditEvent` stays `null`, same as it already does for a `Read` tool.

Verification runs on `Failure` and `Timeout`, not only `Success`, because `IVerifiableTool.
EvaluateVerificationAsync(originalArguments, verificationToolResult, ct)` — the interface shape
fixed since V0.1 — never receives the original call's own `ToolCallResult`. It only ever compares
the *current, observed* state (from calling `VerifyToolName`) against what the *original
arguments* intended. That check is meaningful regardless of how the original call reported itself:
rule S7 already says a timed-out side-effecting action "may have partially happened and therefore
still requires verification," and the same reasoning extends to a call that failed partway through
(a tool that throws mid-execution, caught by `ExecuteWithTimeoutAsync` and converted to `Failure`,
per the error model in `agentic/02-coding-standards.md`).

### How verification runs

`AgentRunner.EvaluateVerificationAsync` resolves `VerificationSpec.VerifyToolName` in the same
`IToolRegistry`, builds a `ToolArguments` carrying over only the named `ArgumentsFrom` from the
original call, validates it against the verification tool's own manifest (rule S2, applied
uniformly), and executes it through the same `ExecuteWithTimeoutAsync` path the original call
used — same per-tool timeout, same "a tool that throws becomes `Failure`, never a crash" handling
(rule C1). Whatever `ToolCallResult` comes back — including one the runtime itself manufactured
because the verification tool did not resolve, or failed manifest validation — is handed to
`IVerifiableTool.EvaluateVerificationAsync` exactly as if it were a normal execution result. This
is deliberately the *only* place that turns "could not verify" into `VerificationStatus.
Inconclusive`: the runtime never invents its own fallback interpretation, closing the exact
shortcut rule S4 names as "the single most dangerous shortcut available here" — treating a missing
or failed verification as `Confirmed`.

`EvaluateVerificationAsync` itself is wrapped in a catch for any non-cancellation exception (rule
C1 extended to verification: a package's verification logic is third-party code too and must not
be able to crash the loop), producing `VerificationStatus.Inconclusive` with the exception message
as detail — never `Confirmed` by default (rule S4).

Verification call is **not** routed through `IPolicyEngine` or `IApprovalProvider`. It is
runtime-mandated infrastructure that follows automatically from a call the operator already
approved (or that policy already placed in `Automatic`) — not a new action the model is proposing.
The model never sees or requests the verification call; principle 1 ("the LLM never touches the
machine") is unaffected because the LLM was never involved in this call at all.

### What happens with the result

- `ToolCallAuditEvent.Verification` carries the `VerificationStatus` for every executed non-`Read`
  call (rule S9, principle 4 — this is now a fact worth recording, not a placeholder).
- The observation fed back to the model gets an explicit `Verification: {status}` line (with
  `Detail` when present), appended after the tool's own output/error text — rule S4: "Refuted
  triggers replanning with an explicit observation," which requires the model to actually see it,
  not just the audit log.
- `VerificationStatus.Refuted` is added to the rule C8 "deviation" set (alongside `PolicyDenied`,
  `UnknownTool`, `UserRejected`, and `Timeout`) and triggers a replan: the plan assumed an effect
  that verification just contradicted, which is exactly the kind of "this did not go as the plan
  assumed" signal replanning exists for.
- `Inconclusive` (and `NotApplicable`, though nothing in this codebase can currently produce it —
  see "Alternatives considered") does **not** trigger a replan. Rule S4 says it is never treated as
  *success* — the runtime never lets it substitute for `Confirmed` anywhere — but it is not
  evidence of *failure* either. It is real information handed to the model, which is free to act on
  it (retry, ask a clarifying question, proceed anyway) exactly like it already does with a plain
  `Failure` observation, without the runtime forcing a whole new plan on every uncertain check.

### Telemetry

A `bops.verification` child activity per rule D ("child spans for model call, policy evaluation,
execution and verification"), tagged with the original tool, the verification tool, the
verification tool's own outcome, and the final `VerificationStatus`. No new metric instrument was
added — `agentic/01-architecture-rules.md`'s metrics list (step duration, tool duration by tool,
token usage, approvals requested vs. granted) does not name one, and inventing one beyond what was
asked is exactly what scope discipline (`agentic/05-workflow.md`) warns against.

## Alternatives considered

- **Verify only on `Success`.** Rejected: rule S7 explicitly requires verifying a `Timeout` because
  the action may have partially happened, and the same argument applies to a `Failure` caused by a
  tool throwing mid-execution. Since `EvaluateVerificationAsync` never receives the original
  outcome anyway — only the verification tool's own result — there is no extra cost to always
  calling it once the tool actually ran, and skipping it on `Failure` would silently narrow rule
  S4's "every side-effecting action is verified" to "every side-effecting action that reported
  success," which is not what principle 3 says.
- **Treating a missing/unresolvable verification tool as `NotApplicable`, decided by the runtime
  itself.** Rejected: `NotApplicable` per rule B3/S4 exists for a manifest that "legitimately has
  none," which cannot happen for a non-`Read` tool (registration enforces a `VerificationSpec` on
  every one). A verification tool that fails to resolve at runtime is not "no verification
  applies" — it is "verification was attempted and could not run," which is exactly what
  `Inconclusive` means. The runtime deliberately never assigns `NotApplicable` on its own; only a
  package's own `EvaluateVerificationAsync` could, in a case this codebase does not currently
  exercise (no shipped tool is non-`Read` yet — see "Consequences").
- **Replanning on `Inconclusive` too, alongside `Refuted`.** Considered, since S4 treats
  `Inconclusive` as "not success." Rejected: S4 also never calls it evidence of failure, and an
  agent that replans every time a check merely could not confirm anything (a disabled verification
  package, a slow read tool that timed out) would be replan-happy for no benefit — the same
  reasoning ADR-0014 already used to exclude a plain `Failure` from the deviation set.
- **A new `VerificationOutcomeAuditEvent`, separate from `ToolCallAuditEvent`.** Rejected: rule B8
  already shapes `Verification` as a field on `ToolCallAuditEvent`, not a sibling event type — a
  verification result is a property of the call it verifies, not an independent thing that
  happened, and the field already exists in the contract from V0.1. No `bOps.Abstractions` change
  was needed for this ADR at all.
- **Recording the verification tool call itself as its own `ToolCallAuditEvent`, the way a
  model-requested call is.** Rejected: it was never proposed by the model, was not subject to
  policy or approval, and recording it as a peer event would make it indistinguishable from a real
  agent action in the log. Its outcome is captured where it belongs — as `Verification` on the
  original call's event — and as a telemetry span for operational visibility.

## Consequences

- Every non-`Read` tool call now genuinely gets verified — though, as of V0.4, no tool shipped in
  this repository is anything but `Read` (the Filesystem/Network packages arrive at V0.5), so this
  path is exercised by `bOps.Runtime.Tests` rather than by any real tool yet, exactly as ADR-0015
  said about policy and approval at V0.3. It starts mattering for real starting with V0.5.
- A package author's `IVerifiableTool.EvaluateVerificationAsync` must handle being called with a
  `ToolCallResult` it did not necessarily expect (a `Failure` from a verification tool that does
  not exist, or one whose declared `ArgumentsFrom` does not resolve) — this was already implied by
  the interface shape and by the testing rule 04 requirement to cover "a verification tool that
  itself fails," but this ADR is where the runtime actually started exercising it.
- No `bOps.Abstractions` change. `VerificationSpec`, `VerificationStatus`, `VerificationOutcome`,
  `IVerifiableTool`, and `ToolCallAuditEvent.Verification` all already existed; V0.4 is the runtime
  finally calling and populating what V0.1–V0.3 only declared and enforced at registration.

## Not done (flagged, not silently skipped)

- **`ModelCallAuditEvent` still does not say *why* a model was called** — the same pre-existing gap
  ADR-0014 and ADR-0015 both carried forward, unrelated to this ADR's scope.
- **No CLI command runs `AuditChainVerifier`.** Same flagged follow-up as ADR-0015.
- **The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
  0002, 0005, 0006, 0011, 0012) are still unwritten**, flagged again from every prior handoff.
