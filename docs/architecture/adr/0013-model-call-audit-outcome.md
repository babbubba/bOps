# ADR-0013 — `ModelCallAuditEvent` gains an `Outcome`, and a failed model call is caught in the loop

Status: Accepted

## Context

Rule S9 (agentic/03-security-rules.md) requires that every model call is audited, whatever the
outcome — "the denied calls are the ones an investigator actually wants." Rule C1
(agentic/01-architecture-rules.md) requires that nothing thrown escapes an agent-loop iteration,
citing "a provider that returns malformed JSON" as the canonical example.

While fixing the V0.1 build blocker (an unhandled `HttpRequestException` from an OpenRouter 401
response crashing the CLI process outright), two related defects surfaced:

1. `AgentRunner.RunAsync` only caught `ModelProtocolException` around the model call. Any other
   exception from `IChatModel.CompleteAsync` — for example a bug in a third-party provider
   package that throws something else — would still escape the loop and crash the process,
   which is exactly what rule C1 forbids.
2. `AgentRunner.CallModelAsync` wrote its `ModelCallAuditEvent` *after* a successful
   `model.CompleteAsync()` call. When that call throws — for any reason — no audit event is
   written at all. A task that fails at step 0 because the model is unreachable currently leaves
   zero trace in the audit log, which is a direct violation of rule S9.

`ModelCallAuditEvent` as originally shaped (`Provider`, `Model`, `Usage`) has no way to record
that a call failed, so fixing (2) requires changing the shape of a type in `bOps.Abstractions` —
which agentic/05-workflow.md requires an ADR for.

## Decision

- Add `ModelCallOutcome { Success, Failure }` to `bOps.Abstractions` and a required
  `Outcome` property plus an optional `ErrorMessage` on `ModelCallAuditEvent`.
- `AgentRunner.CallModelAsync` now wraps the call to `IChatModel.CompleteAsync` in a
  `try/catch (Exception ex) when (ex is not OperationCanceledException)`, writes a
  `ModelCallAuditEvent` with `Outcome = Failure` and the exception's message, then rethrows.
- `AgentRunner.RunAsync` broadens its catch around `CallModelAsync` from
  `ModelProtocolException` specifically to the same `Exception` pattern, ending the task as
  `AgentTaskStatus.Failed`. This makes the loop robust to *any* provider defect, not only the
  ones a provider package remembers to wrap in `ModelProtocolException` — defense in depth for
  rule C1, not a replacement for provider packages doing that wrapping (they still should, so the
  failure message is meaningful).

## Alternatives considered

- **A `bool Succeeded` field instead of an enum.** Rejected for consistency: every other outcome
  in the audit schema (`ToolOutcome`, `VerificationStatus`, `PolicyMode`) is an explicit enum, not
  a boolean, precisely because a boolean cannot grow a third state later (agentic/07-plan-corrections.md
  calls out `TaskState.Status` as a bare string being a defect for the same reason).
- **Reusing `ToolOutcome` for the model-call outcome.** Rejected: `ToolOutcome` carries
  `Denied` and `Timeout`, neither of which currently applies to a model call in V0.1 (there is no
  policy engine gating model calls, and no separate per-model-call timeout budget yet — rule S7
  scopes the timeout requirement to tool `ExecuteAsync`). A dedicated, smaller enum says exactly
  what applies today and can grow independently.
- **Leaving the narrow `catch (ModelProtocolException)` in place** and only fixing the specific
  `HttpRequestException` case at its source (`OpenAiCompatibleChatModel`). Rejected: it fixes the
  one path found today but not rule C1 in general — the loop would still crash on any exception a
  future or third-party provider package fails to wrap.

## Consequences

- Every model call — successful or not — now produces exactly one `ModelCallAuditEvent`.
- The agent loop cannot be crashed by a misbehaving `IChatModel` implementation, first-party or
  third-party.
- `ModelCallAuditEvent` is a breaking shape change, acceptable under D-012: `bOps.Abstractions`
  stays on `0.x` until V1.0 specifically so changes like this do not require a migration story.
