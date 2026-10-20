# ARCH-02A — MAF execution parity spike (E1–E3): evidence report

Base `e7ffab6` (ARCH-01) · branch `spike/arch-02a-maf-parity` · `Microsoft.Agents.AI` `[1.24.0]` (Microsoft.Extensions.AI 10.10.x) ·
spike code `spikes/arch-02a-maf-parity/` (unreferenced, outside `bOps.slnx`, **throwaway — never merges**) · 12 offline test cases, all green, scripted `IChatClient`, real `ChatClientAgent` + `FunctionInvokingChatClient`.

**Method limits.** The MAF side is measured with the real framework. The bOps side is the code at `AgentRunner.cs:1181-1300` (first call wins, `ExpectedTool`, `SemanticExpectedArguments`, `UnexecutedToolCalls`, one ADR-0047 correction) and `EarlyFinalRecovery.cs`; it was **not re-run** here (no runtime suite, per budget). `BopsAuthority` in the spike is a ~15-line stand-in for that gate, not a rebuild of the runner. Hence nothing below is a claim of full end-to-end parity (I-10).

## Matrix

| Case | bOps (code) | MAF 1.24.0 measured | Class |
|---|---|---|---|
| E1a authorised tool, default `AIFunction` | bOps validates, runs, audits, persists, then asks the model | MAF runs the body **inside `RunAsync`** before any bOps check, then makes a 2nd model call itself | **NO-GO** (I-1, I-4, I-7) in auto mode |
| E1b tool + wrong `ExpectedArguments`, declaration-only tools | `SemanticMismatch`, not executed | MAF returns the call untouched, 1 model call per `RunAsync`; the verdict comes entirely from bOps code | PARITY (MAF contributes nothing) |
| E1c tool **not offered** | rejected, replan, no extra call | MAF injects a synthetic "not found" result and makes a **hidden 2nd model call** | **DIVERGENCE → NO-GO** with defaults (I-4, I-7) |
| E1d two calls, one turn | first runs, rest kept as `UnexecutedToolCalls` | both returned, none executed (declaration-only) | PARITY |
| E1e provider-escaped name | adapter/registry resolves wire name | exact-name match only; escaped name = "unknown" → same hidden call as E1c | DIVERGENCE (mapping must stay in bOps) |
| E1f `TerminateOnUnknownCalls=true` | — | removes the hidden call, but needs a hand-built `FunctionInvokingChatClient` + `UseProvidedChatClientAsIs` | PARITY, only via manual pipeline |
| E2a validation error, auto mode | exactly 1 correction, then replan | MAF absorbed 2 failures and self-corrected: 4 model calls, 3 body runs | **NO-GO** (I-7, I-4) |
| E2b validation error, declaration mode | 1 correction, then replan | 1 model call per run, no MAF retry — but the budget lives entirely in the bOps loop | PARITY (MAF adds nothing) |
| E3 empty / `length` / early-final / valid final | classified by `EarlyFinalRecovery`, bounded | text, `finish_reason` (`stop`/`length`), input/output tokens all preserved in `AgentResponse`/`RawRepresentation`; 1 call, no hidden retry | PARITY on propagation |
| E3 early-final with pending steps | not `Completed`, replan | MAF has no plan/step notion; gate must be bOps code | NOT APPLICABLE to MAF (no benefit) |

Not determined: persisted-event/audit-row/terminal-state parity (needs the runner), tool-surface widening via MAF skills/hosted tools (not exercised; ADR-0052 §15 still forbids).

## Usable MAF features (concrete)

- `IChatClient` + `ChatResponse`/`UsageDetails`/`ChatFinishReason` as a faithful carrier (already used by AF-01).
- Declaration-only tools (`AIFunctionFactory.CreateDeclaration`) so tool calls surface unexecuted; `TerminateOnUnknownCalls=true` on a hand-built pipeline.
- Nothing in E1–E3 *decides* anything: every authority check, budget and classification still runs in bOps code.

## Estimated removable AgentRunner code

≈ **0 lines** from E1–E3. Step authority, first-call-wins, semantic validation, correction budget, early-final gating and fail-closed bounds are all absent from MAF; with MAF's auto loop they would be *bypassed*, not replaced. Using MAF as a function wrapper around the existing runner is not a gain. (The runner's other 5k lines were not examined — E4–E7 out of scope.)

## Risks / incompatibilities

1. Default `ChatClientAgent` wraps the client in an auto-invoking loop: hidden model calls, in-MAF tool execution, autonomous retries (consecutive-error/iteration limits are MAF's, not bOps budgets).
2. Safe use requires declaration-only tools **and** a non-default pipeline, i.e. bOps must drive one `RunAsync` per model turn — then MAF is a thin shell over what `GovernedChatClient` already does.
3. Name resolution, offered-tool view and error semantics differ (exact-match; "not found" is fed back to the model).
4. Preview API surface (`MAAI001`) and pinned-version churn add maintenance cost with no removed code.

## Decision for ARCH-02B

**NO-GO** for tool calling, correction and finalization orchestration (E1a/E1c/E2a violate I-1/I-4/I-7 under defaults; the safe configuration yields no code reduction). Capabilities stay in bOps. MAF remains limited to the AF-01 chat-client/compaction boundary. ARCH-02B is not justified by E1–E3; E4–E7 not started.
