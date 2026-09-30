# ADR-0039 — Model-call failure containment: timeout, classification, bounded retry, sanitized reason

Status: Accepted (2026-09-30, operator decision, with the amendments recorded below)

Amends ADR-0013 (model-call audit outcome); ADR-0013's text is not edited and carries an `Amended by: ADR-0039`
pointer. Carries the ADR-0022-style note for additive `bOps.Abstractions` members. Governs HARDEN-2 of the
V1.3.x reliability train (`agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md`, findings F-02, F-12,
defects C-03, C-15, hypothesis H-1, and the HARDEN-1 review finding deferred to HARDEN-2: duplicate keys in plan
JSON).

**Operator amendments at acceptance.** D-3: 402 is `QuotaExceeded`, not `Authentication`, and 413 is
`ContextOverflow` only on explicit context evidence. D-5: the overall budget always wins; a `Retry-After` that does
not fit both the maximum delay and the remaining budget ends the call. D-6: the sanitizer is **not** public API.
D-7: a launcher backstop transition **is** audited, by a narrow dedicated event. D-8: the attempt field is named
`ModelAttempt`, not `Attempt`. D-9: a malformed plan/replan output is recorded and audited as `MalformedResponse`.
D-1, D-2, D-4 and D-10 were accepted as proposed.

## Context

ADR-0013 made every model call produce exactly one `ModelCallAuditEvent` and made the loop survive any
exception from `IChatModel.CompleteAsync` — *except* `OperationCanceledException`, which it deliberately let
through so an operator's cancellation keeps its meaning. Reading the code at `c3ccba0` shows that exception is
also what a provider **timeout** looks like, and that nothing downstream can tell the two apart:

1. **A timeout escapes every handler (C-03).** Provider packages build their `HttpClient` with
   `IHttpClientFactory.CreateClient(name)` and never set a timeout, so the default 100 s applies. When it fires,
   `HttpClient` throws `TaskCanceledException`. `OpenAiCompatibleChatModel.SendAsync` and
   `AnthropicChatModel` catch only `HttpRequestException`; `AgentRunner.CallModelAsync`, the step-loop, plan and
   replan catches all use `when (ex is not OperationCanceledException)`; `AgentTaskLauncher.TryRunDetached`
   catches `OperationCanceledException` only `when (cancellation.IsCancellationRequested)` and everything else
   `when (ex is not OperationCanceledException)`. A timeout therefore matches **no** clause: no
   `ModelCallAuditEvent`, no `ModelCallRecord`, no terminal status, the task stays persisted `Running` with no
   executor. This is the most likely explanation of the 30-minute unaudited gap in task `88f97dda` (H-1).
   Tool timeouts, by contrast, are correct: `ExecuteWithTimeoutAsync` uses
   `catch (OperationCanceledException) when (!ct.IsCancellationRequested)`.
2. **Failures are unclassified and retried too briefly (C-15).** Each adapter retries up to 3 times inside
   `CompleteAsync`, with 100/200 ms delays, on 408/429/502/503/504 only, ignoring `Retry-After`. 401, 500 and
   "context length exceeded" are indistinguishable from any other failure. Retries inside the adapter are
   invisible to the runtime, so they are neither audited nor recorded.
3. **The reason is not actionable.** The adapter message is always
   `Provider 'X' returned HTTP 400 (BadRequest) for the chat completion request.` The useful part — in the
   incident, *"Function at index 0 has an invalid name: "fs.size"…"* from upstream `Poolside`, after 7 upstream
   429s — is only in `ModelCallRecord.ResponseJson`, which the API view strips, next to a `user_id`.
4. **The launcher has no terminal backstop.** Any exception that does escape the runner is only logged; the
   persisted status is left as it was, normally `Running`.
5. **Duplicate keys in plan JSON escape as `ArgumentException`** (deferred from the HARDEN-1 review).
   `AgentRunner.TryParsePlan` uses `JsonNode.Parse` with default options, which accepts `{"steps":[…],"steps":[…]}`
   and throws `ArgumentException` only on the later `root["steps"]` access. `TryParsePlan` catches
   `JsonException`, `InvalidOperationException` and `FormatException` only, so the exception escapes
   `CreatePlanAsync`/`ReplanAsync` and fails the task as if the model call itself had failed, instead of taking
   the malformed-plan path. HARDEN-1 fixed the same defect on the provider side with `StrictJson` (`716aa6c`).

`AgentRunner.CallModelAsync` is the **only** caller of `IChatModel.CompleteAsync` in the repository.

## Decision

### 1. Timeout versus cancellation — the runtime owns the per-attempt timeout

- `CallModelAsync` runs each attempt under its own timeout `CancellationTokenSource` (driven by the runner's
  `TimeProvider`), linked with the caller's token, cancelled after
  `min(Agent:ModelCallAttemptTimeout, remaining call budget)` (§4). It awaits
  `model.CompleteAsync(request, attemptToken).WaitAsync(attemptToken)`, so an adapter that ignores its token
  still cannot hold the loop past the timeout. An abandoned attempt has its eventual exception observed and
  discarded.
- `OperationCanceledException` while the **caller's** token is cancelled is a genuine cancellation and keeps
  propagating unchanged, exactly as ADR-0013 intended. Cancellation is never converted into a failure.
- Any other `OperationCanceledException` from an attempt (the attempt timeout fired, or a transport timeout the
  adapter did not wrap) is a model-call failure of kind `Timeout`: recorded, audited (§5) and subject to §4.
- Adapters map their own transport timeout (an `OperationCanceledException` thrown while the token they were
  given is *not* cancelled — `HttpClient.Timeout`) to `ModelProtocolException` with kind `Timeout`, so the
  classification is the same whichever timer fires first.
- The `try`/`catch` that classifies a failure surrounds **only** the adapter invocation (a synchronous throw included). The
  runtime's own bookkeeping after a successful call (token metering, the success record, the success audit write, the
  required-shape check of a plan reply) is outside it: a failure there is never classified as a model failure, never records
  a second attempt for the same call and never triggers a retry, so it cannot cause a second billable call. It propagates to
  the existing step containment or the host backstop (§9).

### 2. Provider-neutral failure classification (additive)

New enum in `bOps.Abstractions`:

| `ModelFailureKind` | Meaning |
|---|---|
| `Unknown` | Could not be safely classified: any exception other than `ModelProtocolException`, an adapter that did not say, or a status with no mapping. |
| `Transient` | Provider-side temporary failure: 500, 502, 503, 529 (overloaded). |
| `RateLimited` | 429 without quota evidence. |
| `Timeout` | Runtime attempt timeout, adapter transport timeout, 408, 504. |
| `Unreachable` | No provider response was obtained: DNS, socket, connection refused/reset, TLS. |
| `Authentication` | 401; 403 unless the provider's structured error safely says something else (e.g. a quota code). |
| `QuotaExceeded` | 402; or any 4xx whose structured error code identifies exhausted quota/credit (e.g. `insufficient_quota`). |
| `InvalidRequest` | 400, 404, 405, 409, 413, 422 and other 4xx without context evidence. |
| `ContextOverflow` | A 4xx whose structured error code or sanitized message **explicitly** identifies a context/token limit (e.g. `context_length_exceeded`, "maximum context length", "prompt is too long"). A 413 without such evidence is `InvalidRequest`. |
| `MalformedResponse` | A response that is not a usable model reply: schema mismatch, no choices, the JSON-schema fallback reply refused twice, or (§8) a plan/replan reply that does not parse. |

- **Classification happens only in provider packages** (status code, headers and body inspection). The
  runtime never parses a provider body and never names a provider; it reads the kind.
- Additive members (init-only properties; no constructor change, so binary-compatible):
  - `ModelProtocolException`: `FailureKind` (default `Unknown`), `RetryAfter` (`TimeSpan?`, parsed by the
    adapter from `Retry-After` as delta-seconds or HTTP-date relative to the response `Date`, never negative),
    `ProviderStatusCode` (`int?`, the status when the transport had one; `null` for non-HTTP failures). The
    exception `Message` is the adapter's **safe** reason (§6).
  - `ModelCallRecord` and `ModelCallAuditEvent`: `FailureKind` (`ModelFailureKind?`), `ModelAttempt` (`int?`),
    `RetryDecision` (`ModelRetryDecision?`), `RetryDelayMs` (`long?`), `ProviderStatusCode` (`int?`). On the
    audit event every new property is `[JsonIgnore(Condition = WhenWritingNull)]`; old events deserialize with
    `null`, and the hash chain format is unchanged (additive properties only). `ModelAttempt` is deliberately
    not called `Attempt`: HARDEN-3 introduces a *task execution* attempt.
- New enum `ModelRetryDecision` on a failed attempt: `Retry` (another attempt follows after `RetryDelayMs`),
  `NotRetryable`, `AttemptsExhausted`, `BudgetExhausted`, `RetryAfterExceedsLimit`. Every value except `Retry`
  marks the **terminal** attempt of the logical call.
- `ChatModelOptions.RequestTimeout` (`TimeSpan?`) and `ChatModelOptions.DefaultRequestTimeout` (150 s), §10.
**HTTP 200 with an embedded error.** Some OpenRouter-compatible providers answer `200` with a root-level structured
`error` object instead of a `choices` array. The adapter detects that object before normal success parsing and classifies
it from the embedded provider error code, exactly as it would the same status. Only a root-level structured `error` counts:
assistant text that merely mentions "error" does not. A response is never both success and failure, and only sanitized,
bounded fields leave the adapter. In this case `ProviderStatusCode` carries the embedded provider error code, not the HTTP
transport status (`200`).

### 3. Adapters make one attempt per `CompleteAsync`

The transport retry loops in `OpenAiCompatibleChatModel.SendAsync` and `AnthropicChatModel` (3 attempts,
100/200 ms) are **removed**: each `CompleteAsync` sends one HTTP request and throws a classified
`ModelProtocolException` on failure. Retrying moves to the runtime so that every attempt is recorded and
audited, one policy governs every provider, and cancellation/timeout semantics live in one place. The one
documented, inseparable exchange that stays inside an adapter is the JSON-schema fallback strategy's corrective
re-ask (plan §3.1.1): a content-level exchange, not a transport retry; a transport failure during either leg
still throws immediately. The `IChatModel` documentation says adapters must not retry transport failures.

### 4. Bounded retry in the runtime

`CallModelAsync` is a bounded loop over the attempts of one **logical call**:

| Kind | Generic retry |
|---|---|
| `Transient`, `RateLimited`, `Timeout`, `Unreachable` | Yes, within attempts and budget |
| `Authentication`, `QuotaExceeded`, `InvalidRequest`, `ContextOverflow`, `MalformedResponse`, `Unknown` | **Never** (`NotRetryable`) |

- **Attempts:** at most `Agent:ModelCallMaxAttempts` (default **3**).
- **Budget — always wins:** the whole logical call, attempts and waits, ends within `Agent:ModelCallBudget`
  (default **300 s**) from the start of the first attempt. Each attempt's timeout is
  `min(Agent:ModelCallAttemptTimeout (default 120 s), remaining budget)`. Not every theoretical attempt has to
  fit: the budget is the explicit upper bound.
- **Backoff:** before retrying after failed attempt *n*, `min(Agent:ModelRetryMaxDelay (default 30 s),
  Agent:ModelRetryBaseDelay (default 1 s) × 2^(n−1)) × (0.5 + 0.5 × jitter)`, jitter in [0, 1).
- **`Retry-After`:** the adapter parses it; the runtime decides. A negative value counts as zero. A value above
  `Agent:ModelRetryMaxDelay` ends the call (`RetryAfterExceedsLimit`) instead of sleeping. Otherwise the delay is
  `max(RetryAfter, backoff)` — never earlier than the provider asked.
- **Never sleep past the budget:** a delay that would not leave at least one second of budget for the next
  attempt ends the call (`BudgetExhausted`) with the classified failure.
- The wait uses the caller's token: cancelling during a wait is a cancellation, not a failure.
- Production jitter is random; the jitter source and the wait are internal seams of `AgentRunner` so tests are
  deterministic without real 120-second waits.
- Empty-final-response re-asks (`EmptyFinalResponseRetries`) and the plan/replan corrective re-ask (§8) are
  **separate logical calls**, each with its own attempts and budget, as today. The two mechanisms never nest
  into each other's counting.
- A delegated role's step/deadline budget is still checked per step; one logical call can overrun a role
  deadline by at most `ModelCallBudget`. Failed attempts consume no tokens.

### 5. Every attempt is audited and recorded — one event per attempt

- Every attempt, successful or failed, writes its own `ModelCallAuditEvent` and appends its own
  `ModelCallRecord` to the step's (or plan's) `ModelCalls`. A later attempt never replaces an earlier one's
  evidence. Each carries `ModelAttempt` (1-based within the logical call) and the attempt's own `DurationMs`; a
  failed one also carries `FailureKind`, `ProviderStatusCode` where there was one, the sanitized `ErrorMessage`
  (§6), `RetryDecision`, and `RetryDelayMs` when `Retry`.
- ADR-0013's invariant becomes: **every model call attempt produces exactly one `ModelCallAuditEvent`.**
- The terminal model failure of a task is the last attempt's event (terminal `RetryDecision`); the task's
  persisted `Failed` state (§7) is the other half of the record.
- A failure audit is written with the caller's token. If the task is cancelled at that moment the audit write
  is itself cancelled; that is cancellation, handled by the cancellation path, unchanged from today.

### 6. Sanitized, bounded, actionable reason — no public sanitizer

- **Provider packages** own provider-specific parsing. They extract only bounded safe fields and build the
  `ModelProtocolException` message from them:
  - OpenAI-compatible: `error.message`; `error.code`/`error.type`; `error.metadata.provider_name`;
    `error.metadata.raw` — if it is JSON, its `error.message`/`message`, otherwise the text; a summary of
    `error.metadata.previous_errors` as counts per code (e.g. `429×7`).
  - Anthropic: `error.type`, `error.message`.
  - A non-JSON body (e.g. an HTML error page) contributes nothing but the status code.
  - Never extracted: `user_id` or any other identifier field, `is_byok`, request content, headers.
  The shared provider-side implementation (classification table, `Retry-After` parsing, redaction, bounding) is
  one **internal** source file owned by the OpenAI-compatible package and linked into the Anthropic package —
  the pattern ADR-0038 already uses for `ToolWireNames` and `StrictJson`.
- **The runtime** never parses a provider body. It applies its own **internal** defence-in-depth redaction and
  bounding to every text it records, audits or shows (a third-party adapter may not sanitize).
- Redaction replaces with `[redacted]`: `Bearer …`; `sk-…`/`sk_…`-shaped keys; `api_key|apikey|token|secret|
  password|authorization|user_id` followed by `:`/`=` and a value; opaque tokens of ≥ 32 characters mixing letters
  and digits; e-mail addresses. A safe reason is bounded to **500 characters** (ellipsis marked).
- The sanitizer algorithm is not public API: neither `bOps.Abstractions` nor `bOps.Runtime` exposes it.
- `ModelCallRecord.ResponseJson`/`RequestJson` are unchanged: stored as today (bounded by
  `MaxModelPayloadCharacters`) and still stripped from the default API view (`TaskStateView`).

### 7. Terminal reason on the task

A failed logical call ends the task through the existing `BuildFailed` path (synthetic step described
`Model protocol failure`, status `Failed`, every attempt's `ModelCalls` attached). Its observation is a
runtime-built, provider-neutral operator reason chosen **by kind**, the attempt count, then the sanitized safe
reason:

| Kind | Operator reason |
|---|---|
| `Authentication` | Provider authentication failed. Check the configured provider credential. |
| `QuotaExceeded` | Provider account quota or credit is unavailable. |
| `RateLimited` | Provider rate limit exceeded. |
| `Timeout` | Provider/model call timed out. |
| `Unreachable` | Provider could not be reached. Check the provider URL and the network. |
| `Transient` | Provider is temporarily unavailable. |
| `InvalidRequest` | Provider rejected the model request. |
| `ContextOverflow` | Model context limit exceeded. |
| `MalformedResponse` | Provider/model returned a malformed response. |
| `Unknown` | Model call failed unexpectedly. |

No new `TaskState` field is added (`TerminalReason` belongs to HARDEN-3). The synthetic step keeps its
description so existing readers keep working.

### 8. Plan and replan output: duplicate keys are malformed output, recorded as such

- `TryParsePlan` parses with `JsonDocumentOptions { AllowDuplicateProperties = false }` (the rule `StrictJson`
  applies on the provider side; the runtime cannot reference that internal file) and also treats
  `ArgumentException` as a parse failure. Duplicate keys at any depth, like any unparseable plan, never throw out
  of the runner.
- A plan or replan reply that does not parse is recorded and audited as a failed attempt of kind
  `MalformedResponse` with `RetryDecision = NotRetryable` (the transport call succeeded: its usage and bodies are
  kept on the record). No generic transport retry is layered on top.
- The existing, explicitly bounded plan/replan corrective re-ask is unchanged: one re-ask (a separate logical
  call); if that reply is malformed too, the existing behaviour applies — the task continues with an explicit
  empty plan ("proceeding step by step without an explicit plan") and reaches a terminal state through the
  normal loop.

### 9. Launcher backstop — never `Running` without an executor

`AgentTaskLauncher.TryRunDetached` is the final containment boundary for the API host:

- `OperationCanceledException` while the launcher's own token is cancelled → `Cancelled` (unchanged).
- **Any other exception, including an `OperationCanceledException` the launcher did not request** → logged at
  `Error`, then `AgentRunner.ContainEscapedFailureAsync` re-reads the latest persisted state (the last known state
  only when nothing is stored) and **only if it is still `Running`** persists it `Failed` with a synthetic step
  `Unexpected runtime failure` whose observation is `unexpected runtime failure: <exception type>: <message>`,
  redacted and bounded (§6). A terminal state (`Completed`, `Failed`, `Cancelled`, `BudgetExceeded`,
  `MaxStepsReached`, `PolicyBlocked`, `ReplanLimitReached`, …) is never replaced; if the state cannot be read, it
  is not written. The backstop never throws out of the detached task; its own failures are logged.
- **The transition is audited** by a new, deliberately narrow `TaskExecutionFaultAuditEvent`
  (discriminator `taskExecutionFault`): *an unexpected exception escaped normal runner containment and the
  launcher performed the fail-safe `Running` → `Failed` transition.* It carries the base fields (timestamp, node,
  task id, step index of the synthetic step, actor), `ExceptionType` and the bounded sanitized `Reason` — never a
  stack trace, request/response bodies, credentials, headers, prompt content or tool secrets. It is written only
  when the transition happened.
- This event does **not** pre-empt HARDEN-3: the general task lifecycle audit (started, resumed, terminal) is
  HARDEN-3's `TaskLifecycleAuditEvent`. No compare-and-set store capability is added here (HARDEN-3); the
  re-read-then-write is the minimal guard.
- The runner's normal containment is not duplicated: with §1–§8 in place the backstop fires only on a runtime
  defect.

### 10. Configuration (additive)

| Key | Default | Meaning |
|---|---|---|
| `Agent:ModelCallMaxAttempts` | 3 | Attempts per logical model call (1 disables retry). |
| `Agent:ModelCallAttemptTimeout` | `00:02:00` | Runtime timeout of one attempt. |
| `Agent:ModelCallBudget` | `00:05:00` | Wall-clock cap for one logical call, attempts and waits included. |
| `Agent:ModelRetryBaseDelay` | `00:00:01` | Backoff base. |
| `Agent:ModelRetryMaxDelay` | `00:00:30` | Backoff cap and the largest `Retry-After` honoured. |
| `ModelProvider:RequestTimeout` (`ChatModelOptions.RequestTimeout`) | `00:02:30` | Explicit outer transport guard the adapter sets on its `HttpClient`, replacing the implicit 100 s. |

`AgentRunnerOptions.Validate` rejects incoherent values (attempts < 1; non-positive attempt timeout or budget;
negative base delay; maximum delay below base delay) when the runner is built, and the hosts additionally require
`ModelCallAttemptTimeout < RequestTimeout`, failing fast with a message naming the keys, so the runtime attempt
timeout normally fires first. The CLI validates while it starts. The API resolves `IChatModel` lazily, so it validates on the
first model resolution (the first task or delegation request), before any task is persisted. In both hosts an incoherent
configuration fails closed; the API does not validate at process start.

## Alternatives considered

- **Keep retries inside the adapters and report an attempt count on one audit event.** Rejected: the runtime
  could not audit the individual attempts' kind, status and duration, each adapter would need its own copy of the
  policy, and the timeout/cancellation distinction would be re-implemented per adapter.
- **Runtime classifies from HTTP status codes.** Rejected: HTTP is a provider-transport detail and bodies are
  provider-specific; the runtime reading them breaks rule 1 of `CLAUDE.md`.
- **402 as `Authentication`; every 413 as `ContextOverflow`** (the proposal). Rejected at acceptance: an
  exhausted balance is not a credential problem, and 413 can be a generic payload limit.
- **A public sanitizer in `bOps.Abstractions`** (the proposal). Rejected at acceptance: a text-sanitization
  algorithm is not a domain contract; sharing it does not justify enlarging the public API.
- **No audit event for a backstop transition** (the proposal's option a). Rejected at acceptance: a
  `Running` → `Failed` transition made outside the runner must leave durable audit evidence.
- **Retry `ContextOverflow`, `MalformedResponse`, `QuotaExceeded` or `Unknown`.** Rejected: resending the same
  request cannot fix them. `ContextOverflow` recovery by compaction is HARDEN-8's.
- **Honour any `Retry-After`, however long.** Rejected: an unbounded wait is an unbounded retry by another name.
- **Converting a genuine cancellation into `Failed`.** Rejected: an operator's cancellation stays `Cancelled`.

## Consequences

- A model timeout is audited, recorded and ends the task `Failed` with an actionable reason; it can no longer
  leave a task `Running` (C-03, H-1). An unexpected exception in the API launcher leaves `Failed`, audited, never
  `Running`, and never overwrites a terminal state.
- Up to `ModelCallMaxAttempts` audit events per logical call instead of one; a 429-then-200 step shows two model
  calls in its `ModelCalls`. A malformed plan reply is now a `Failure` audit event of kind `MalformedResponse`
  instead of a `Success` one.
- Worst-case latency of one failing logical call rises from about 100 s + 300 ms to `ModelCallBudget` (default
  5 min), in exchange for surviving real rate limits.
- Adapters become simpler (no transport loop) and must classify every failure they throw.
- Public surface grows (additive): `ModelFailureKind`, `ModelRetryDecision`, `TaskExecutionFaultAuditEvent`, new
  init-only members on `ModelProtocolException`, `ModelCallRecord`, `ModelCallAuditEvent`, `ChatModelOptions`, new
  `AgentRunnerOptions` properties and `Validate`, and `AgentRunner.ContainEscapedFailureAsync`. No member is removed
  or changed.
- Out of scope, unchanged: resume semantics, including a failed resume of an already-terminal task (left as it is
  and logged — HARDEN-3), orphaned `Running` display (HARDEN-4), cross-provider fallback (HARDEN-13), context
  compaction (HARDEN-8). The CLI runs the runner in-process: §1–§8 contain model failures there too; a CLI process
  that crashes on a runtime defect is visible to its operator, and its orphaned state is HARDEN-3/4's subject.

## Decisions (as accepted)

| # | Decision | Accepted |
|---|---|---|
| D-1 | Retry location | Runtime (`CallModelAsync`); adapters make one attempt per `CompleteAsync` (§3). As proposed. |
| D-2 | Audit granularity | One `ModelCallAuditEvent` and one `ModelCallRecord` per attempt (§5). As proposed. |
| D-3 | Kinds and mapping | Ten kinds of §2, including `QuotaExceeded`; 402 → `QuotaExceeded`; 413 → `ContextOverflow` only on explicit evidence. **Amended.** |
| D-4 | Retryable kinds | `Transient`, `RateLimited`, `Timeout`, `Unreachable` only. As proposed, with `QuotaExceeded` non-retryable. |
| D-5 | Defaults | 3 attempts; 120 s per attempt; 300 s budget that always wins; backoff 1 s → 30 s with jitter; `Retry-After` must fit both the maximum delay and the remaining budget; transport guard 150 s. **Clarified.** |
| D-6 | Sanitizer | Internal only: provider-side shared linked file; runtime-side internal defence in depth; no public API. **Amended.** |
| D-7 | Backstop audit | Dedicated narrow `TaskExecutionFaultAuditEvent`; general lifecycle audit stays HARDEN-3's. **Amended.** |
| D-8 | Field names | `FailureKind`, `ModelAttempt`, `RetryDecision`, `RetryDelayMs`, `ProviderStatusCode`. **Amended** (`ModelAttempt`). |
| D-9 | Plan/replan malformed output | Strict parse; malformed reply recorded and audited as `MalformedResponse`; existing single corrective re-ask; no transport retry on top. **Amended.** |
| D-10 | Backstop overwrite rule | Re-read; only a still-persisted `Running` becomes `Failed`; never overwrite a terminal state. As proposed. |
