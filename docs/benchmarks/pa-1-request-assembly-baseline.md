# PA-1 — Request-assembly golden baseline

> **Update 2026-10-19:** PROMPT-ROUTING-FINALIZATION corrected PRA-R2/R3/R4/P1 and re-captured 10 of the 12 goldens; `PromptBaselineDefectTests` was replaced by `PromptRoutingFinalizationTests`. This report keeps describing the baseline at `7053ca0`; the result is in `prompt-routing-finalization.md`.

Scope: PA-1 only. This report freezes **what the product sends to the provider today** so that `PROMPT-ROUTING-FINALIZATION`
(PRA-R2, PRA-R3, PRA-R4, PRA-P1) can be measured against it. Nothing here corrects a defect, changes a prompt, a manifest, a policy or
a model setting, and no commit was made. Verdict: **10 of 10 scenarios implemented, every new test passes; 1 scenario is PARTIAL
(PA1-02A covers one replan trigger), the rest are PASS** (matrix in section C).

Sources: the `bOps_Continuita_Master_2026-10-18.md`, `PROMPT-RUNTIME-AUDIT-01-report.md` and `RELEASE-VALIDATION-01-report.md` files are
not in this repository (searched by name); `HANDOFF.md` and ADR-0060/0061 were not re-read. The current code at the baseline commit was
the authority, as instructed. Names from PROMPT-RUNTIME-AUDIT-01 were verified against the code (section B).

---

## A. Git baseline

| | |
|---|---|
| Worktree | `C:/source/repos/babbubba/bOps-knowbench01a` |
| Branch | `codex/know-bench-01a` |
| HEAD | `7053ca0c8380f258442903360c44ec0fe1789bae` — `feat(system): disclose source filter verification in system.events` |
| Previous | `06cd4aa fix(runtime): reject content-free final answers (LIVE-F1-FIX-01)`, `291e82f docs(adr): mark ADR-0060 LIVE-01B uncommitted note as historical` |
| Initial `git status --short` | `?? docs/benchmarks/bench00-maf-compatibility-report.md` (nothing else) — matches the expected baseline |
| Final `git status --short` | ` M tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj`, `?? docs/benchmarks/bench00-maf-compatibility-report.md`, `?? docs/benchmarks/pa-1-request-assembly-baseline.md`, `?? tests/bOps.AgentFramework.Tests/Golden/pa1/`, `?? tests/bOps.AgentFramework.Tests/PromptAssembly/` |
| bench00 | still untracked; SHA-256 `1D1B3AF03239A9CA3D8EFD5BF6C338E4F1D2BD1CAFBDCF082D3B00F8E70F1960` before and after |
| Commits / push / merge / rebase / reset / clean | none |

The session's own working directory (`C:\source\repos\babbubba\bOps`, branch `codex/host-canary-01b`) is a different worktree and was not
touched. Scratch captures went to the session scratchpad, outside the repository.

---

## B. Changes

No production file, manifest, prompt, ADR or runtime configuration was changed. `git diff --stat`: one tracked file, **+2 lines**.

| Kind | Path |
|---|---|
| Modified (test project) | `tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj` — copies `Golden/pa1/*.md` to the output directory (the JSON was already copied by `Golden\**\*.json`) |
| New — harness | `tests/bOps.AgentFramework.Tests/PromptAssembly/PaWire.cs` (offline HTTP upstream, reply builders, tap model, synthetic tool, wire-body view) |
| | `…/PaScenarios.cs` (`PaCompose` real-pipeline composer, `PaFixtures`, the scripted scenarios) |
| | `…/PaGolden.cs` (golden document, canonical comparison, Markdown rendering, capture mode) |
| | `…/PaInvariants.cs` (level-B helpers) · `…/PaCases.cs` (case table) |
| New — tests | `…/RequestAssemblyGoldenTests.cs` (level A, 24 cases) · `…/RequestAssemblyInvariantTests.cs` (level B, 35 cases) · `…/PromptBaselineDefectTests.cs` (level A defect pins, 12 cases) |
| New — fixtures | `tests/bOps.AgentFramework.Tests/Golden/pa1/` — 12 × `.json` + 12 × `.md` + `README.md` (≈ 340 KB) |
| New — report | `docs/benchmarks/pa-1-request-assembly-baseline.md` (this file) |

Reused (not modified): `KnowledgeRunnerWorld`, `TestPack`/`TestPublisher` (linked from `bOps.Knowledge.Tests`), `RecordingAuditSink`,
`DefaultTestPolicyEngine`, `InMemoryTaskStore`, `AlwaysAvailableCapabilityProbe` (linked from `bOps.Runtime.Tests`), the d3da032 golden
(`Golden/d3da032/planning-replan-bound.json`, used as a cross-check). `LiveRegressionHarness` was **not** reused for the requests: its
`LiveScriptedModel` is an `IChatModel` that replaces the provider, so it never reaches the adapter and cannot show the wire body.

Names from PROMPT-RUNTIME-AUDIT-01, verified at the baseline: `AgentRunner.BuildStepSystemPrompt` (`AgentRunner.cs:5150`),
`BuildStepRequest` (local function, `:1060`), `RebuildHistoricalTurns` (`:2960`), `PlanCompleteInstructions` (`:183`),
`KnowledgeControlInstruction` (`:232`), `EarlyFinalRecovery.ContinuationTurn` (`EarlyFinalRecovery.cs:51`), `EvidenceGroundingLedger`
(`EvidenceGrounding.cs`; check at `AgentRunner.cs:3692`), `OpenAiCompatibleChatModel.SendAsync` (`:180`), where `requestJson` is produced
(`:182`) and becomes `ModelCallDetails.RequestJson`.

### How a request is produced (the path that is exercised)

```
scripted task state ─► AgentRunner (BuildPlanningRequest / BuildReplanRequest / BuildStepRequest, recovery turns)
   ─► ModelInvocationBroker + MafContextProjector            ← the composition of bOps.Api, bOps.Cli and ExecutionRunnerFactory
   ─► PaTapModel (records the ModelRequest the adapter receives: history with provenance)
   ─► OpenAiCompatibleChatModel (real; native tool calling) ─► JsonSerializer ─► HttpRequestMessage
   ─► PaUpstream (offline HttpMessageHandler: keeps the body bytes, answers from a script)
```

Only the model's answers and the tools' output are scripted. The upstream answers in OpenAI Chat Completions schema, so every answer
also goes through the adapter's real parser (finish reasons, tool-call aliases, `length` handling).

---

## C. Matrix PA-1

Common to all scenarios: synthetic goal and read-only tools (`test.read`, `test.other`, `test.events`), fixed task id
`7a1c0de1-0000-4a11-8000-000000000001`, `new AgentRunnerOptions()` (the prompt-relevant keys equal `appsettings.json`), provider body
properties limited to `model`, `messages`, `tools`, `tool_choice`, `max_tokens`. Test commands are in section E; the golden for each
row is `Golden/pa1/<file>.json|.md`; level-B tests are in `RequestAssemblyInvariantTests`, level-A defect pins in `PromptBaselineDefectTests`.

Provider-request numbers are positions in the run (0 = planning).

| ID | Input / plan state | Assembly path exercised | Frozen requests and what they show | Golden file | Result | Why |
|---|---|---|---|---|---|---|
| **PA1-01** | goal, 2 tools, no history | `CreatePlanAsync` → broker → adapter | #0: no `tools`, no `tool_choice`; messages `[system, user(goal)]`; catalogue sorted ordinally, parameters, `PlanningGuidance`; standing S5/evidence rules first | `pa1-01-planning` | PASS | Cross-checked against the d3da032 planning baseline (same prompt up to the catalogue) |
| **PA1-02A** | plan `[read, other]` done; a third call to a tool that is not offered at plan end | `ReplanWithinBudgetAsync(PlanDeviation)` → `ReplanAsync` | #4: no tools; previous plan as assistant text, completed checks, the refused call and its result, last protected turn `Every step of plan revision 0 was complete …` | `pa1-02a-replan-ordinary` | **PARTIAL** | Only one `PlanDeviation` trigger is reproduced (the proposal at plan end). Policy denial, user rejection, timeout and refuted verification use the same builder with another trigger observation; not exercised |
| **PA1-02B** | `read` done, model answers, `other` unresolved | `PrematureFinal` bookkeeping step → `ReplanAsync(EarlyFinalWithUnresolvedPlan)` | #2 the step that drew the premature answer; #3 replan: previous plan, completed checks, last turn `Final answer not accepted: … 2 [test.other]; …` + `ReplanDirective`; premature text never repeated | `pa1-02b-replan-early-final` | PASS | Cross-checked against the d3da032 replan baseline (same prompt up to the completed checks and the same last turn) |
| **PA1-03** | plan `[read, other]`, step 0 pending | `BuildStepRequest` | #1: one function `test_read` (alias of `test.read`), schema, no control function, plan listed (2 steps), `[system, user]`; `test_other` absent. Level B also checks #2 offers only `test_other` (D-043) | `pa1-03-step-pending-tool` | PASS | The routing text that can invite early finalization is pinned separately (D, PRA-R3/P1) |
| **PA1-04** | large result persisted (7284 chars, observation shortened to 4000) | `ResolveStepEvidenceReadsAsync` | #2: `[test_other, runtime_evidence_read]`, evidence index with `ev1:<task>:0`, shortened observation; #3: continuation with the model's read call and the `EvidenceRead/v1` answer as the last two messages | `pa1-04-evidence-read` | PASS | Native control call only; the text-directive form (`EvidenceRead/v1` JSON as assistant text) is not exercised |
| **PA1-05** | synthetic signed Knowledge pack, bound and granted; one `read_resource` | `RefreshKnowledgeIndexAsync`, `KnowledgeIndexPublication`, `KnowledgeReadBridge` | #1: `[test_read, runtime_knowledge_read]`, `[system, user(index data turn), user(goal)]`, control block at the tail of the system prompt, no pack text in it; #2: read result as a Knowledge-provenance tool message after the goal, pending tool still offered | `pa1-05-knowledge` | PASS | Production composition with the real host over a synthetic pack; identical bytes through the MAF-turn broker |
| **PA1-06** | every planned step complete | `BuildStepSystemPrompt(planExhausted)` | #3: no operational tool; only `runtime_evidence_read`; `PlanCompleteInstructions`; both observations in order | `pa1-06-plan-exhausted` | PASS | — |
| **PA1-07** | model returns an empty reply while `other` is pending | `IsUnusableFinal` → `UnusableFinalRetryInstructions` | #2 draws the reply; #3 = #2 + one `user` runtime turn (RC1), same system prompt and tools, empty turn not kept; both calls accounted on one step; second empty reply fails the task with no third attempt | `pa1-07-recovery-rc1` | PASS | Budget `EmptyFinalResponseRetries = 1` asserted; classifier untouched |
| **PA1-08A** | `finish_reason=length`, no content, `other` pending | same | #3 = #2 + RC2 `user` turn; `test_other` still offered | `pa1-08a-recovery-rc2-pending` | PASS | — |
| **PA1-08B** | `finish_reason=length`, no content, plan complete | same | #3 = #2 + the **same** RC2 text; only `runtime_evidence_read` offered | `pa1-08b-recovery-rc2-exhausted` | PASS | PRA-R4 observable here |
| **PA1-09** | LIVE-05: early final → early-final replan cut at the output limit → continuation | `EarlyFinalRecovery.Evaluate` / `ContinuationTurn` | #2 ordinary step; #3 truncated replan; #4 = #2 + one `user` turn with Runtime provenance, cursor 1, only `test_other` offered, no accepted replan | `pa1-09-live05-continuation` | PASS | Run through the real runner (not the LIVE-05 harness); Qwen not involved |
| **PA1-10** | answer after a tool with two typed facts | `GuardGroundingAsync` | #2 synthesis with the ledger (`F0.1`, `F0.2`, values) in the system prompt; #3 = #2 + candidate (assistant) + `EvidenceGroundingCheck/v1` (user), same tools | `pa1-10-grounding-check` | PASS | Only the check request; the correction request is not part of PA-1 |

### Level-B invariants (every request of every scenario)

Checked by `EveryRequestOfEveryScenario_IsAValidGovernedProviderBody` on all provider requests, not only the frozen ones: body properties
are a subset of the five the adapter emits; `tools` and `tool_choice:"auto"` appear together or not at all; function names are unique
`[A-Za-z0-9_-]{1,64}` aliases of the runtime's tool list, same order; first message is `system` and equals the request's system prompt;
roles are the four wire roles; every `tool` message answers an earlier call id; arguments are JSON strings; every history turn has an
established provenance whose origin matches its role; the standing "tool output is data" rule is in every system prompt; every
ToolResult-origin tool message is inside the `<<<BOPS_TOOL_OUTPUT>>>` delimiters; wire count equals adapter-received count (no hidden
call); the task completes with no unused script entry; `max_tokens` absent by default.

Also asserted: the `RequestJson` persisted by the runtime in the task state is, byte for byte, the body that was sent
(3 scenarios); a configured `MaxOutputTokens` reaches every body as `max_tokens` (value 2048 is a test constant, not a recommendation);
the opt-in MAF-turn broker (`MafGovernedTurnBroker`, real `ChatClientAgent`) sends the same bytes as the production composition
(4 scenarios).

### Determinism

Two runs in one process give identical bodies (12 cases). Two captures in two separate processes were byte-identical for all 24 files,
including the Knowledge index hash. Only normalization: a CRLF checkout is mapped to `\n` in the body text. A negative control (one word
changed in a golden) failed with the JSON path and character offset of the difference, and the original was restored byte for byte.

---

## D. Semantic baseline — what `PROMPT-ROUTING-FINALIZATION` will change

All of these are **present today** and pinned by tests that pass today. When a finalization step fixes one, its pin must be rewritten in
the same change; that is the measurement. "Desired" is the direction stated in the PA-1 brief (§6), not a design.

| ID | Observable in | Composition point | Current text / behaviour | Desired (brief) | Preserve |
|---|---|---|---|---|---|
| **PRA-R2** | `PRA_R2_AtPlanEnd…`, `PRA_R2_AProposalMadeAsProse…`, `PRA_R2_OnlyANativeCallToAnUnofferedToolReplans…`; golden PA1-06, PA1-08B | `AgentRunner.cs:183` `PlanCompleteInstructions`, selected at `:5169` | "Every planned step above is complete and no operational tool is offered. Give your final answer now … Only if that evidence is genuinely insufficient, propose the one new check you need instead: it will not run, and the runtime will ask you to revise the plan with it." The request offers no operational tool. Prose proposal ⇒ accepted as the **final answer** (1 plan, task completes). Only a native call to a tool that is not offered ⇒ `UnknownTool` ⇒ replan | A proposal has a guaranteed channel that actually triggers the replan | Plan-complete requests offer no operational tool (D-043); the replan budget; the explanation turn `Every step of plan revision … was complete …` |
| **PRA-R3** | `PRA_R3_TheStepPrompt…`, `PRA_R3_TheReplanLead…`, `PRA_R3_TheContinuationTurn…`; golden PA1-03, PA1-05, PA1-02B, PA1-09 | `BuildStepSystemPrompt` `:5191` (tail sentence); `KnowledgeControlInstruction` `:232`; `ReplanningLead` `:111`; `PrematureFinal.ReplanDirective` | Every step prompt ends "Propose the concrete tool call for the next unfinished step above; while planned steps remain, a final answer makes the runtime re-evaluate the plan." With Knowledge the prompt adds "make the operational call (or give your final answer) in your next reply" while two steps are pending. The replan lead is the same generic sentence for every trigger ("Your previous plan no longer matches what you have learned. Revise it.") and the early-final replan then offers an empty plan as a way out. The LIVE-05 continuation says "Propose the call … before giving any final answer" while the system prompt of the same request still says a final answer re-plans | One coherent rule: no invitation to finalize while steps are pending, consistent across Knowledge, early-final replan and recovery | Early-final bookkeeping (no premature text repeated, replan not skipped); LIVE-05 continuation turn: Runtime provenance, single, cursor-exact; Knowledge never required and never authority |
| **PRA-R4** | `PRA_R4_TheRc2Text…`, `PRA_R4_TheRc1Text…`, `PRA_R4_ARecoveryInstruction…`; golden PA1-07, PA1-08A/B | `AgentRunner.cs:153` / `:203` constants, chosen by `UnusableFinalRetryInstructions` `:3767`, appended to `logicalCall.ContinuationTurns` `:1166` | RC2 is one fixed string whether or not an operational tool exists: with the plan complete it still says "call the offered tool now" while only `runtime_evidence_read` is offered. RC1 mentions neither plan nor step. The instruction stays in every later request of the same logical call (it is in the evidence-read continuation) and is gone in the next step | Recovery text depends on the state (tool pending vs plan complete); temporary instructions do not outlive their purpose | One retry (`EmptyFinalResponseRetries`), the empty turn is not kept, tool surface unchanged by a retry, both calls accounted on one step |
| **PRA-P1** | `PRA_P1_TheOneToolRouting…`, `PRA_P1_ThePromptNamesTools…`; golden PA1-03, PA1-04, PA1-05 | `BuildStepSystemPrompt` `:5160-5173` | "The single offered native tool is the current plan step's tool. Invoke it at most once …" — stated, never explained, while the plan section lists two steps. None of the plausible explanatory phrases occurs. The prompt names tools canonically (`test.other`, `runtime.evidence_read`, `runtime.knowledge_read`) while the provider is offered aliases (`test_other`, `runtime_evidence_read`, `runtime_knowledge_read`) | The model is told why only one operational tool is offered and how names map | D-043 (one operational tool per pending step, never the next one early); alias map closed per request; control functions beside, never instead of, the step tool |
| **PRA-V1** | `PRA_V1_AnAnswerThatContradictsAFact…`; golden PA1-10 | `GuardGroundingAsync` `AgentRunner.cs:3692`; `EvidenceGroundingLedger.CheckInstruction` | The ledger is typed (`F0.1 … = Integer 3`, `F0.2 … = Boolean false`) but whether an answer contradicts it is the model's own verdict: an answer saying "no event … observed" is kept when the model replies `{"contradictions":[]}`. The check request is the synthesis request + candidate + fixed turn; it also still offers `runtime_evidence_read` although it says not to call tools | Deterministic contradiction detection (GROUNDING-FACTS) | Check/correction are one bounded exchange, no step, no tool executed, no new facts |
| **PRA-Q1** | — | not an assembly property | Qwen reasoning cost. The only assembly facts: the body carries no reasoning or sampling property (only the five), and `max_tokens` is absent by default, so a `length` stop is the server's limit, not one bOps requested | out of scope | — |

### Further observations (not in the brief; recorded, not fixed)

1. **Two descriptions of one channel.** Every step prompt carries the text-directive form of Evidence Read (`EvidenceRead/v1` JSON as
   assistant text) even when no evidence exists and no control function is offered (PA1-03, PA1-05 #1); when the function is offered
   (PA1-04) the prompt describes both the directive and the native function.
2. **Adjacent assistant messages.** In the ordinary replan with a trigger tool call (PA1-02A #4) the previous-plan text (assistant) is
   immediately followed by the assistant tool-call message. Whether llama.cpp's template accepts this for Qwen is not verified.
3. **No output limit requested.** `Agent:MaxOutputTokens` is unset in `appsettings.json`; `max_tokens` is absent from every body.
4. **`required: []`** is serialized for tools whose parameters are all optional.

---

## E. Tests

Platform: Windows 11, .NET SDK from `global.json`, Debug. No Qwen, Docker, WSL or network service was used.

| Command (filter shortened) | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `dotnet test tests/bOps.AgentFramework.Tests --filter "FullyQualifiedName~PromptAssembly"` — **new PA-1 tests** | **71** | 0 | 0 |
| `dotnet test tests/bOps.AgentFramework.Tests --filter` `KnowledgeGoldenRequestTests`, `ContextGateTests`, `ToolRoutingRegressionTests`, `KnowledgeStepPromptTests`, `PlanningContractVisibilityTests`, `MafContext*`, `MafTurn*`, `MafBoundaryGuardTests`, `ContextProvenanceTests`, `GovernedChatClientTests`, `KnowledgeIndexRunnerTests`, `KnowledgeRefusalRecoveryTests`, `KnowledgeContextContinuityTests`, `ReplayContractAlignmentTests` | 146 | 0 | 0 |
| `dotnet test tests/bOps.Runtime.Tests --filter` `EarlyFinalReplanTests`, `EarlyFinalValidationRecoveryTests`, `ToolRoutingTests`, `EvidenceIndex*`, `EvidenceGrounding*`, `PlanningCorrectionTests`, `FinalAnswerUsabilityTests`, `EvidenceControlPlaneTests`, `EvidenceAddressabilityTests`, `ModelOutputObservationTests` | 304 | 0 | 0 |
| `dotnet test tests/bOps.Packages.Providers.OpenAiCompatible.Tests` (whole project; serialization/wire) | 127 | 0 | 0 |
| `dotnet test tests/bOps.Runtime.Tests --filter "ToolCallProtocolTests\|ModelFailureEndToEndTests"` — run on purpose to confirm the baseline | 13 | **2** | 0 |

New PA-1 tests by kind (71 in total): 24 golden (12 compare + 12 reproducibility); 35 level B (12 valid-body theory cases, 14 per-scenario
facts, 1 d3da032 cross-check, 1 `max_tokens` pass-through, 3 persisted-`RequestJson` cases, 4 MAF-turn equivalence cases); 12 level-A
defect pins.

**Known baseline failures, reproduced unchanged, not fixed, not in any other suite run here:**

1. `ToolCallProtocolTests.TheToolCatalog_KeepsTheNameTheRiskAndTheFirstSentenceOfEachTool_InOrdinalOrder_AndIsBounded`
2. `ModelFailureEndToEndTests.ATransientProviderFailure_RecoversWithinBounds_AndEveryAttemptIsAudited`

No new regression was observed in any executed suite. The whole solution was not run (instructed). `bOps.Runtime.Tests` was not run in full,
so "Runtime.Tests passes" is **not** claimed.

### Coverage limits (what these tests do not show)

- **llama.cpp's chat template is not verified.** The body is what leaves bOps; the server then applies its own template to `messages`
  and `tools` and tokenizes. Nothing here shows the text Qwen actually receives, and no remote tool was used to find out.
- **Offline upstream.** A scripted OpenAI-schema handler, not a llama.cpp server; provider id `LlamaCpp` and native tool calling, the
  same adapter `LlamaCppProviderPackage` builds. The **fallback** mode (tools embedded in the system prompt, `SupportsNativeToolCalling=false`)
  is NOT_COVERED.
- **Composition.** The golden uses the production composition (broker + `MafContextProjector`). `MafGovernedTurnBroker` is composed by no
  production host; equivalence of bytes was shown for 4 scenarios (planning-to-end, LIVE-05, evidence read, knowledge), not all 10.
- **Options.** `new AgentRunnerOptions()` is used, not `appsettings.json`; they agree on the keys that shape prompts
  (`MaxObservationCharacters 4000`, `EvidenceDisclosureEnabled true`, replan budgets), not proven for deployments that override them.
- **Windows only.** No Linux/WSL run (instructed). Nothing on the wire depends on the platform (tool `Platforms` is never serialized),
  but cross-platform byte identity is not demonstrated.
- **Not exercised** (candidates for AE-1 or later): `Completeness != Complete` limitation digest, conditional steps (`activation`),
  semantic/argument/tool-name correction turns (ADR-0047/0050, AF-TOOLROUTE-01), the aggressive projection after `ContextOverflow`, MAF
  summarization/compaction, delegated roles, the content-free RC text, plan-correction re-asks, the `EvidenceRead/v1` text directive,
  the grounding correction request, other `PlanDeviation` triggers.

---

## F. Preparation for AE-1

Reusable as they are (all in `tests/bOps.AgentFramework.Tests/PromptAssembly/`, scenario-agnostic):

| Helper | Use |
|---|---|
| `PaReply` (`Text`, `Call`, `Empty`, `OutputLimited`, `Truncated`, `Plan`, `Knowledge`, `EvidenceRead`) + `PaUpstream` | script a run at the **provider** level: finish reasons, empty and truncated replies, native calls of any tool, control calls |
| `PaSetup` / `PaCompose.RunAsync` | one call builds the real runner + broker + projector + adapter; options, tools, knowledge host, store, actor, MAF-turn broker are knobs |
| `PaTool`, `PaFixtures` (`Read`, `Other`, `Facts`, `LongListing`) | side-effect-free synthetic Read tools; typed facts; a result large enough to be shortened |
| `PaRun`, `PaWireBody` | state, bodies and adapter-received requests together; accessors for roles, aliases, schemas, control functions |
| `PaGolden` | `Document` / `Verify` / `Render` and capture mode (`BOPS_PA1_CAPTURE_DIR`) for new request baselines |
| `PaInvariants` | `AssertEveryRequestIsAValidGovernedBody`, `AssertExtendsBy` (retry/continuation = previous request + exact tail), `Block`, `PlanTools` |
| `PaScenarios.*` setups | `PlanExhaustedThenOrdinaryReplan`, `EarlyFinalReplan`, `EvidenceRead`, `Knowledge`, `Grounding`, `EmptyReply(Twice)`, `OutputLimitedWhilePending`, `OutputLimitedWhenComplete`, `EarlyFinalContinuation` |

The audit's T05 case (RC2 asking for a tool at plan end and a redundant Evidence read) is PA1-08B. I do not have the definitions of
T02/T06/T08/T10 in this repository, so I do not map them; the helpers above take any script, and the closest existing scenarios are
PA1-02B/PA1-09 (early final and recovery), PA1-05 (Knowledge) and PA1-10 (grounding). Not implemented, as instructed.

**Fitness of the baseline for AE-1:** adequate for comparing request assembly before and after a prompt change on these ten shapes
(structural diff and a readable `.md` diff). It is not evidence of how Qwen behaves, and the 'not exercised' list above should be read
before treating it as complete.
