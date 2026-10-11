# AF-05 benchmark results

| | |
|---|---|
| Packet | [AF-05](2026-10-10-af-05-maf-regression-tuning-cleanup.md) |
| Governing ADR | [ADR-0052](../../docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md), D-048 |
| Branch | `feat/af-05-maf-regression-tuning-cleanup` |
| Harness | `tests/bOps.AgentFramework.Tests/Regression/` |
| Regenerate | `BOPS_BENCHMARK_OUT=<file> dotnet test tests/bOps.AgentFramework.Tests --configuration Release --filter "FullyQualifiedName~Regression"` |

## 1. Evidence classes — read this first

Every row in this document belongs to exactly one class. Nothing here is a statement about a live provider.

| Class | Meaning | Used in this document |
|---|---|---|
| **DETERMINISTIC FIXTURE** | The real `AgentRunner`, `ModelInvocationBroker`, `MafContextProjector`, MAF 1.24.0 compaction and the real package fact builders (`StabilityFacts`, `SystemCrashFacts`, `SystemEventFacts`) run against a scripted model and fixed tool results. | **All measurements below.** |
| **LIVE LOCAL** | A real local OpenAI-compatible / Qwen-like server. | **NOT RUN** — no server listening on 8080/11434/1234, no `llama-server`/`ollama`/LM Studio installed or running. |
| **LIVE EXTERNAL** | A real hosted provider (OpenRouter, OpenAI, DeepSeek). | **NOT RUN — unavailable credentials/environment**: `BOPS_MODELPROVIDER_API_KEY` is unset in this environment; the vault was deliberately not opened. |
| **ANTHROPIC NATIVE** | The native Anthropic adapter against the real API. | **NOT RUN — unavailable credentials/environment** (`ANTHROPIC_API_KEY` unset). |

Provider-wire correctness stays covered by the existing recorded-wire suites (`bOps.Packages.Providers.OpenAiCompatible.Tests`, `bOps.Packages.Providers.Anthropic.Tests`, `ProviderWireEndToEndTests`), which are unchanged and green. They prove translation, not behaviour of a real model on these workloads.

### What is real and what is synthetic in the fixture

| Real | Synthetic |
|---|---|
| runner, plan/replan, step routing, policy, EvidenceRead budget and soft exhaustion, grounding, limitations digest, evidence index, the MAF projector and its pipeline, tool-result compaction, raw persistence | the **model** (a scripted function of the request it receives), **token counts** (UTF-8 bytes of everything offered ÷ 3.5 — a proxy, never a provider tokenizer), **latency** (not simulated) |
| EvidenceFacts of stability, crashes and events (built by the real package classes from 180-day / 126-crash / 2,869-event fixtures) | tool output JSON bodies (shaped like, not byte-equal to, the historical task) |

Consequences that must not be forgotten:

- Token columns are comparable **between rows**, not to any provider invoice.
- "Provider-reported prompt tokens vs estimate" cannot be measured here (there is no provider). The estimate-delta telemetry was fixed (it used to include the safety reserve) and is covered by a test, but **no calibration value is claimed**: *unavailable*.
- Wall-clock of a model call is *unavailable*. The only wall-clock column is the real CPU time spent in projection.
- A scripted summarizer cannot show summary **quality**; summarization rows show cost and what information the summarizer was offered.
- **Facts retained** means: the expected fact is visible somewhere in the text the final call received (grounding block, limitations digest, history, evidence-read replies). It is a *direct visibility* measure. It does not count drill-down (`runtime.evidence_read`), which remains available at the final call.

## 2. Scenarios

| Scenario | Shape |
|---|---|
| `c508-equivalent` | Plan of 7: `system.info`, `system.stability` (Partial, unexpectedShutdown 48, kernelCrash 6, hardwareError 365, displayFault 26, 5 minidumps), `system.crashes` (Partial, 126 observed crashes, groups, dump references), `system.events` (Partial, 2,869 events in 56 pairs, 50 returned), then `system.cpu`, `system.memory`, `storage.health`. Before `system.cpu` the scripted model asks for **six** evidence reads (the historical pathology) while a logical call may be served **four**. The numbers are regression evidence, not product constants. |
| `c508-extended-10` | The same plus ten further large, fact-less diagnostics before `system.cpu`, so the **final call** is made under real compaction pressure (17 operational steps). |
| `short-diagnostic` | `system.info`, `system.cpu`, `system.memory`, final answer. |

Quality gate for the c508 shape (asserted by `MafRegressionTests`): kernel crash, hardware error, display fault, unexpected shutdown, crash count and group count, minidump/dump references, the newest kernel dump path, event-group counts, and the three limitations (events / crashes / stability partial) must be visible to the final call, and the final answer must carry an `Evidence limitations` section.

## 3. Findings (DETERMINISTIC FIXTURE)

**F1 — Context profile activation.** Before AF-05 the MAF path ran only for an exact `Agent:Context:Profiles` entry; the shipped configuration has none, so the default product (`OpenRouter` / `openrouter/free`) ran the legacy HARDEN-8 request. After AF-05 there is one path: MAF with an exact profile, or MAF with a **structural** bound (no capacity invented) when the pair is unlisted.

**F2 — Parity of the default path.** Legacy HARDEN-8 vs the structural MAF path, both with three verbatim recent groups: c508-equivalent 68,511 → 69,894 all-in tokens (+2.0 %), c508-extended-10 138,788 → 144,391 (+4.0 %), short diagnostic 5,824 → 5,820; facts retained 1/9 → 1/9, 1/9 → 1/9, 3/3 → 3/3. The default configuration is not better or worse; the improvement is available by configuring a profile (F3).

**F3 — A known profile keeps the evidence.** c508-equivalent with an exact profile of 10,240–32,768 tokens: **9/9 facts, 3/3 limitations**, Completed, 70,239 (10,240) to 81,379 (32,768) all-in tokens. Under real compaction pressure (c508-extended-10) a 16,384 profile still compacts old results and retains 1/9 directly visible facts (F5), exactly like the legacy request.

**F4 — EvidenceRead exhaustion is soft in every configuration.** All 4 allowed reads served, the model's 2 further requests were not offered/served, the task **Completed**, and `system.cpu`, `system.memory`, `storage.health` each executed exactly once (unknown profile, 12,288, 16,384 and 32,768 windows asserted; `MafRegressionTests`).

**F5 — Grounding saturation (measured, not fixed).** `EvidenceGrounding/v1` shows at most **12** facts, newest steps first (ADR-0042 PRE-5 amendment). In the c508 shape `system.events` is the newest fact-bearing step and emits 12 facts alone, so the block shows 12 and says "18 additional observed fact(s) are not shown": none of the stability or crashes facts reaches the final prompt through grounding. When compaction removes the raw results, those facts are reachable **only** through drill-down. An **uncommitted experiment** that selects facts round-robin across steps (referenced first, then position, then newest) kept the same 12-fact bound and the same tokens (±0.3 %) and raised facts retained from **1/9 to 7/9** (c508-extended-10, unknown profile and 16,384 / 12,288 windows) and from 1/9 to 7/9 (c508-equivalent, unknown profile). This is a change to an Accepted ADR-0042 rule, so it was **not** applied; see the recommendation in the AF-05 packet.

**F6 — High-density Facts do not reduce reads in this workload.** The reads in the fixture are scripted, so "reads avoided" is not measurable; what is measurable is that, because of F5, the AF-04 facts of `system.stability` and `system.crashes` are never present in the final prompt when `system.events` is newer. Their value is therefore limited to cases where the step is within the newest 12 facts.

**F7 — Summarization never paid for itself.** Every paired comparison on the fixtures that triggered at least one summary has summarization-on all-in tokens **above** summarization-off (+2.4 % to +6.4 % at the new default ratio), never fewer facts retained than off at the new default, and fewer (5/9 against 9/9) at the old default ratio. It stays opt-in. See §5.

**F8 — Small windows fail safely, classified.** With four protected evidence-read replies in one call, a profile of 9,216 or less leaves no history allowance in the fixture: the task ends `Failed` / `ModelFailure` / `ContextOverflow` with the explicit message, no provider call is made with a silently truncated authority. The practical minimum depends on the system prompt and the tool schemas; the documentation says so.

**F9 — Pipeline order measured, order unchanged.** See §6.

## 4. Tuning decisions

| Setting | Old | New | Scenario | Before → after | Rationale |
|---|---|---|---|---|---|
| `Agent:Context:MinimumPreservedToolGroups` | 2 | **3** | short-diagnostic, unprofiled model | 2/3 → 3/3 facts, same tokens | For an unprofiled model it is the only structural bound; 2 compacts the oldest of a three-step diagnostic that nothing required compacting, and 3 is the HARDEN-8 verbatim default so unprofiled behaviour keeps parity with the code it replaces. With a known profile the sweeps are flat (identical rows for 0–6). c508-equivalent unprofiled: 61,742 → 69,894 tokens (K=2 → 3) for the same 1/9. |
| `Agent:Summarization:TriggerRatio` | 0.75 | **0.9** | c508-equivalent @ 10,240, task model | 77,666 → 73,802 all-in (−5.0 %), 5/9 → 9/9 facts | 0.75 summarized history that still fit. c508-extended-10 @ 16,384: 235,694 → 232,221 (−1.5 %). Still more all-in than summarization off (70,239 and 218,289), so the default stays **disabled**. |
| `Agent:Context:SafetyReserveTokens` | 1,024 | 1,024 (unchanged) | c508-equivalent @ 10,240 | 0: 73,904 (+5.2 %), 512: 73,783 (+5.0 %); 2,048 and 4,096: **Failed** (no history allowance) | The only reserve that is cheaper than 0/512 and does not fail the 10k window. c508-extended-10 @ 16,384: 4,096 saves 17 % (181,882 vs 218,289) but the facts are already 1/9 at 1,024. |
| `Agent:Context:MinimumPreservedTurns` | 1 | 1 (unchanged) | c508-extended-10 @ 16,384 | flat (identical rows for 0, 1, 2, 4) | No evidence for a change. |
| `Agent:Summarization:RecentGroupsToPreserve` | 4 | 4 (unchanged) | c508-extended-10 @ 16,384, ratio 0.75 | 2: 236,189; 4: 235,694; 8: 231,618 | Within the noise of the fixture; 8 mostly stops summarization from triggering. |
| `EvidenceRead.MaxAttempts` / `MaxControlRejections` | 4 / 4 | 4 / 4 (unchanged) | c508 shapes | 4 served, 0 control rejections, task completes | Soft exhaustion already prevents the historical failure; no data supports raising them, and raising them grows the protected context (F8). |
| History budget formula, tool-result trigger, sliding window, truncation | — | unchanged | — | — | Derived from the profile; no independent setting exists to tune. |

## 5. Summarization: disabled vs enabled, task model vs dedicated

c508-equivalent (all-in tokens; facts retained): window 10,240 — off 70,239 (9/9); on, task model 73,802 (9/9, +5.1 %, 5 summary calls); on, dedicated `Sum/small` 73,802 (9/9). Window 12,288 — off 77,447; on 79,307 (+2.4 %). Windows ≥ 16,384 — no summary call at all. c508-extended-10 @ 16,384 — off 218,289 (1/9); on 232,221 (1/9, +6.4 %, 13 summary calls); @ 12,288 — off 164,239; on 169,458 (+3.2 %).

The task-model and the dedicated summarizer cost the same here only because the fake summary has the same size on both; a real dedicated model changes cost, latency and **data egress**, none of which this fixture can show. Nothing was selected automatically. A dedicated summarizer is never a default.

Short diagnostic: no summary call at 16,384 or 32,768 with summarization enabled; identical tokens (5,820) and facts (3/3) with it on and off.

## 6. Pipeline order (ADR-0052 §11) — measured, not changed

What the summarizer is offered from the c508-equivalent final history (1 summary call each):

| Order | Summarizer input | Raw fact markers visible to the summarizer (of 5) |
|---|---:|---:|
| ToolResult → Summarization (**normative**) | 1,320 characters | 0 |
| Summarization → ToolResult (reverse) | 13,765 characters | 5 |

The normative order gives the summarizer a tenth of the input and no raw tool-result data (so no injected instruction in it). The reverse gives it the data, at ten times the input. The authoritative facts are not at stake either way: they live in grounding and the limitations digest, never in a summary (`SummaryAuthorityTests` pass unchanged). The measured loss under the normative order is narrative detail of old results, which remains drillable. **No material loss that would require an ADR-0052 amendment was demonstrated; the order is unchanged.**

## 7. Generated tables

Columns: *ER* = EvidenceRead; *Facts/Limitations retained* = expected items visible to the final call (direct visibility); *Projection ms* = real CPU time in projection, summed over the run; *Hist. groups before→after* is the worst single call; token columns are the synthetic proxy; summary columns include the summary calls made through the broker. "Profile" shows the configured window/reserved output and the telemetry `profile_source`.

### 7.1 BEFORE — the HARDEN-8 legacy request as the unprofiled default (commit `7db054d`, generated by the same harness before the AF-05 changes)

Only the configuration matrices are reproduced; the sweeps of that commit are superseded by §7.2. The row "unknown profile (legacy request, product default today)" is the baseline the AF-05 default path is compared with in F2.

#### short-diagnostic — configuration matrix

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| short-diagnostic | unknown profile (legacy request, product default today) | none (BoundedHistory legacy request) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 3 max verbatim | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.0 |
| short-diagnostic | MAF 32768, summarization off | 32768/1024 (Agent.Context.Profiles) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 0->0 | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 26.4 |
| short-diagnostic | MAF 32768, summarization on (task model) | 32768/1024 (Agent.Context.Profiles) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 0->0 | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.9 |
| short-diagnostic | MAF 16384, summarization off | 16384/1024 (Agent.Context.Profiles) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 0->0 | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.2 |
| short-diagnostic | MAF 16384, summarization on (task model) | 16384/1024 (Agent.Context.Profiles) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 0->0 | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.3 |

#### c508-equivalent — configuration matrix

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-equivalent | unknown profile (legacy request, product default today) | none (BoundedHistory legacy request) | 14 | 69756 | 294 | 0 | 0 | 0 | **70050** | 7 max verbatim | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 1/9 | 3/3 | 0.0 |
| c508-equivalent | MAF 32768, summarization off | 32768/1024 (Agent.Context.Profiles) | 14 | 80761 | 340 | 0 | 0 | 0 | **81101** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.6 |
| c508-equivalent | MAF 32768, summarization on (task model) | 32768/1024 (Agent.Context.Profiles) | 14 | 80761 | 340 | 0 | 0 | 0 | **81101** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.7 |
| c508-equivalent | MAF 32768, summarization on (dedicated Sum/small) | 32768/1024 (Agent.Context.Profiles) | 14 | 80761 | 340 | 0 | 0 | 0 | **81101** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 4.7 |
| c508-equivalent | MAF 16384, summarization off | 16384/1024 (Agent.Context.Profiles) | 14 | 80761 | 340 | 0 | 0 | 0 | **81101** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.4 |
| c508-equivalent | MAF 16384, summarization on (task model) | 16384/1024 (Agent.Context.Profiles) | 14 | 80761 | 340 | 1 | 278 | 342 | **81721** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 4.0 |
| c508-equivalent | MAF 12288, summarization off | 12288/1024 (Agent.Context.Profiles) | 14 | 76883 | 340 | 0 | 0 | 0 | **77223** | 4->1 | 3 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 5.1 |
| c508-equivalent | MAF 12288, summarization on (task model) | 12288/1024 (Agent.Context.Profiles) | 14 | 76883 | 340 | 4 | 1112 | 1368 | **79703** | 4->1 | 3 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.9 |
| c508-equivalent | MAF 12288, summarization on (dedicated Sum/small) | 12288/1024 (Agent.Context.Profiles) | 14 | 76883 | 340 | 4 | 1112 | 1368 | **79703** | 4->1 | 3 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 4.2 |
| c508-equivalent | MAF 10240, summarization off | 10240/1024 (Agent.Context.Profiles) | 14 | 69676 | 340 | 0 | 0 | 0 | **70016** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.3 |
| c508-equivalent | MAF 10240, summarization on (task model) | 10240/1024 (Agent.Context.Profiles) | 14 | 67353 | 318 | 8 | 6418 | 2736 | **76825** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 5/9 | 3/3 | 4.5 |
| c508-equivalent | MAF 10240, summarization on (dedicated Sum/small) | 10240/1024 (Agent.Context.Profiles) | 14 | 67353 | 318 | 8 | 6418 | 2736 | **76825** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 5/9 | 3/3 | 5.1 |
| c508-equivalent | MAF 9216, summarization off | 9216/1024 (Agent.Context.Profiles) | 9 | 37137 | 217 | 0 | 0 | 0 | **37354** | 4->0 | 4 | 4 | 0 | 0 | 4 | 0 | Failed | n/a | 0/3 | 1.8 |
| c508-equivalent | MAF 9216, summarization on (task model) | 9216/1024 (Agent.Context.Profiles) | 9 | 37137 | 217 | 4 | 1112 | 1368 | **39834** | 4->0 | 4 | 4 | 0 | 0 | 4 | 0 | Failed | n/a | 0/3 | 2.3 |
| c508-equivalent | MAF 8192, summarization off | 8192/1024 (Agent.Context.Profiles) | 8 | 27338 | 205 | 0 | 0 | 0 | **27543** | 4->0 | 4 | 3 | 0 | 0 | 4 | 0 | Failed | n/a | 0/3 | 1.6 |

#### c508-extended-10 (compaction pressure at the final call) — configuration matrix

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-extended-10 | unknown profile (legacy request, product default today) | none (BoundedHistory legacy request) | 24 | 145318 | 556 | 0 | 0 | 0 | **145874** | 7 max verbatim | 0 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 0.0 |
| c508-extended-10 | MAF 32768, summarization off | 32768/1024 (Agent.Context.Profiles) | 24 | 310535 | 602 | 0 | 0 | 0 | **311137** | 0->0 | 0 | 4 | 0 | 2 | 17 | 0 | Completed | 9/9 | 3/3 | 24.6 |
| c508-extended-10 | MAF 32768, summarization on (task model) | 32768/1024 (Agent.Context.Profiles) | 24 | 301501 | 602 | 5 | 12854 | 1710 | **316667** | 14->11 | 4 | 4 | 0 | 2 | 17 | 0 | Completed | 9/9 | 3/3 | 28.7 |
| c508-extended-10 | MAF 16384, summarization off | 16384/1024 (Agent.Context.Profiles) | 24 | 217177 | 556 | 0 | 0 | 0 | **217733** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 29.6 |
| c508-extended-10 | MAF 16384, summarization on (task model) | 16384/1024 (Agent.Context.Profiles) | 24 | 192318 | 556 | 16 | 37242 | 5472 | **235588** | 14->5 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 39.5 |
| c508-extended-10 | MAF 16384, summarization on (dedicated Sum/small) | 16384/1024 (Agent.Context.Profiles) | 24 | 192318 | 556 | 16 | 37237 | 5472 | **235583** | 14->5 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 37.0 |
| c508-extended-10 | MAF 12288, summarization off | 12288/1024 (Agent.Context.Profiles) | 24 | 163445 | 556 | 0 | 0 | 0 | **164001** | 14->1 | 13 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 28.3 |
| c508-extended-10 | MAF 12288, summarization on (task model) | 12288/1024 (Agent.Context.Profiles) | 24 | 162384 | 556 | 18 | 14005 | 6156 | **183101** | 14->1 | 13 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 36.7 |

### 7.2 AFTER — final AF-05 configuration (defaults: `MinimumPreservedToolGroups`=3, `TriggerRatio`=0.9)


#### pipeline order — what the summarizer is offered (c508-equivalent final history; DETERMINISTIC FIXTURE)

| Order | Summarizer calls | Summarizer input (chars) | Raw fact markers in the summarizer input (of 5) |
|---|---:|---:|---:|
| ToolResult -> Summarization (normative) | 1 | 1320 | 0 |
| Summarization -> ToolResult (reverse) | 1 | 13765 | 5 |

#### short-diagnostic — configuration matrix

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| short-diagnostic | no projector (unprojected full history) | none (unprojected) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 3 max verbatim | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.0 |
| short-diagnostic | unknown profile (structural MAF, the shipped default for an unprofiled model) | unknown (unknown-structural) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 0->0 | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.2 |
| short-diagnostic | MAF 32768, summarization off | 32768/1024 (Agent.Context.Profiles) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 0->0 | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.2 |
| short-diagnostic | MAF 32768, summarization on (task model) | 32768/1024 (Agent.Context.Profiles) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 0->0 | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.2 |
| short-diagnostic | MAF 16384, summarization off | 16384/1024 (Agent.Context.Profiles) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 0->0 | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.2 |
| short-diagnostic | MAF 16384, summarization on (task model) | 16384/1024 (Agent.Context.Profiles) | 5 | 5705 | 115 | 0 | 0 | 0 | **5820** | 0->0 | 0 | 0 | 0 | 0 | 3 | 0 | Completed | 3/3 | n/a | 0.2 |

#### sweep SafetyReserveTokens — c508-equivalent @ MAF 10240, summarization off

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-equivalent | SafetyReserveTokens=0 | 10240/1024 (Agent.Context.Profiles) | 14 | 73564 | 340 | 0 | 0 | 0 | **73904** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 4.3 |
| c508-equivalent | SafetyReserveTokens=512 | 10240/1024 (Agent.Context.Profiles) | 14 | 73443 | 340 | 0 | 0 | 0 | **73783** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 5.4 |
| c508-equivalent | SafetyReserveTokens=1024 | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 0 | 0 | 0 | **70239** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 4.5 |
| c508-equivalent | SafetyReserveTokens=2048 | 10240/1024 (Agent.Context.Profiles) | 9 | 37249 | 217 | 0 | 0 | 0 | **37466** | 4->0 | 4 | 4 | 0 | 0 | 4 | 0 | Failed | n/a | 0/3 | 2.8 |
| c508-equivalent | SafetyReserveTokens=4096 | 10240/1024 (Agent.Context.Profiles) | 7 | 18689 | 193 | 0 | 0 | 0 | **18882** | 4->0 | 4 | 2 | 0 | 0 | 4 | 0 | Failed | n/a | 0/3 | 1.5 |

#### sweep SafetyReserveTokens — c508-extended-10 @ MAF 16384, summarization off

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-extended-10 | SafetyReserveTokens=0 | 16384/1024 (Agent.Context.Profiles) | 24 | 222314 | 556 | 0 | 0 | 0 | **222870** | 14->14 | 10 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 30.8 |
| c508-extended-10 | SafetyReserveTokens=512 | 16384/1024 (Agent.Context.Profiles) | 24 | 220023 | 556 | 0 | 0 | 0 | **220579** | 14->14 | 10 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 31.2 |
| c508-extended-10 | SafetyReserveTokens=1024 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 29.3 |
| c508-extended-10 | SafetyReserveTokens=2048 | 16384/1024 (Agent.Context.Profiles) | 24 | 202725 | 556 | 0 | 0 | 0 | **203281** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 30.7 |
| c508-extended-10 | SafetyReserveTokens=4096 | 16384/1024 (Agent.Context.Profiles) | 24 | 181326 | 556 | 0 | 0 | 0 | **181882** | 14->1 | 13 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 28.5 |

#### sweep MinimumPreservedToolGroups — c508-extended-10 @ MAF 16384, summarization off

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-extended-10 | MinimumPreservedToolGroups=0 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 27.4 |
| c508-extended-10 | MinimumPreservedToolGroups=1 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 25.7 |
| c508-extended-10 | MinimumPreservedToolGroups=2 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 26.6 |
| c508-extended-10 | MinimumPreservedToolGroups=3 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 25.2 |
| c508-extended-10 | MinimumPreservedToolGroups=4 | 16384/1024 (Agent.Context.Profiles) | 24 | 217440 | 556 | 0 | 0 | 0 | **217996** | 14->3 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 27.3 |
| c508-extended-10 | MinimumPreservedToolGroups=6 | 16384/1024 (Agent.Context.Profiles) | 24 | 216934 | 556 | 0 | 0 | 0 | **217490** | 14->3 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 27.1 |

#### sweep MinimumPreservedToolGroups — c508-equivalent @ MAF 10240, summarization off

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-equivalent | MinimumPreservedToolGroups=0 | 10240/1024 (Agent.Context.Profiles) | 14 | 70248 | 340 | 0 | 0 | 0 | **70588** | 4->4 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 4.4 |
| c508-equivalent | MinimumPreservedToolGroups=1 | 10240/1024 (Agent.Context.Profiles) | 14 | 70034 | 340 | 0 | 0 | 0 | **70374** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.3 |
| c508-equivalent | MinimumPreservedToolGroups=2 | 10240/1024 (Agent.Context.Profiles) | 14 | 69953 | 340 | 0 | 0 | 0 | **70293** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.5 |
| c508-equivalent | MinimumPreservedToolGroups=3 | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 0 | 0 | 0 | **70239** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 4.5 |
| c508-equivalent | MinimumPreservedToolGroups=4 | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 0 | 0 | 0 | **70239** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.6 |
| c508-equivalent | MinimumPreservedToolGroups=6 | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 0 | 0 | 0 | **70239** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 4.7 |

#### sweep MinimumPreservedTurns — c508-extended-10 @ MAF 16384, summarization off

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-extended-10 | MinimumPreservedTurns=0 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 24.7 |
| c508-extended-10 | MinimumPreservedTurns=1 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 26.2 |
| c508-extended-10 | MinimumPreservedTurns=2 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 28.9 |
| c508-extended-10 | MinimumPreservedTurns=4 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 26.0 |

#### sweep summarization TriggerRatio x RecentGroupsToPreserve — c508-extended-10 @ MAF 16384 (task model)

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-extended-10 | TriggerRatio=0.5, RecentGroupsToPreserve=2 | 16384/1024 (Agent.Context.Profiles) | 24 | 154783 | 556 | 18 | 77253 | 6156 | **238748** | 14->3 | 12 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 36.6 |
| c508-extended-10 | TriggerRatio=0.5, RecentGroupsToPreserve=4 | 16384/1024 (Agent.Context.Profiles) | 24 | 173109 | 556 | 18 | 58262 | 6156 | **238083** | 15->5 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 49.0 |
| c508-extended-10 | TriggerRatio=0.5, RecentGroupsToPreserve=8 | 16384/1024 (Agent.Context.Profiles) | 24 | 214756 | 556 | 15 | 11176 | 5130 | **231618** | 17->9 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 34.1 |
| c508-extended-10 | TriggerRatio=0.75, RecentGroupsToPreserve=2 | 16384/1024 (Agent.Context.Profiles) | 24 | 189333 | 556 | 16 | 40828 | 5472 | **236189** | 14->3 | 12 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 35.9 |
| c508-extended-10 | TriggerRatio=0.75, RecentGroupsToPreserve=4 | 16384/1024 (Agent.Context.Profiles) | 24 | 192424 | 556 | 16 | 37242 | 5472 | **235694** | 14->4 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 35.5 |
| c508-extended-10 | TriggerRatio=0.75, RecentGroupsToPreserve=8 | 16384/1024 (Agent.Context.Profiles) | 24 | 214756 | 556 | 15 | 11176 | 5130 | **231618** | 17->9 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 40.8 |
| c508-extended-10 | TriggerRatio=0.9, RecentGroupsToPreserve=2 | 16384/1024 (Agent.Context.Profiles) | 24 | 206210 | 556 | 13 | 21473 | 4446 | **232685** | 14->4 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 53.0 |
| c508-extended-10 | TriggerRatio=0.9, RecentGroupsToPreserve=4 | 16384/1024 (Agent.Context.Profiles) | 24 | 206931 | 556 | 13 | 20288 | 4446 | **232221** | 14->4 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 54.1 |
| c508-extended-10 | TriggerRatio=0.9, RecentGroupsToPreserve=8 | 16384/1024 (Agent.Context.Profiles) | 24 | 216572 | 556 | 13 | 7821 | 4446 | **229395** | 14->8 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 52.4 |
| c508-extended-10 | TriggerRatio=1, RecentGroupsToPreserve=2 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 43.0 |
| c508-extended-10 | TriggerRatio=1, RecentGroupsToPreserve=4 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 41.0 |
| c508-extended-10 | TriggerRatio=1, RecentGroupsToPreserve=8 | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 42.3 |

#### sweep summarization TriggerRatio x RecentGroupsToPreserve — c508-equivalent @ MAF 10240 (task model)

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-equivalent | TriggerRatio=0.5, RecentGroupsToPreserve=2 | 10240/1024 (Agent.Context.Profiles) | 14 | 61574 | 294 | 10 | 19268 | 3420 | **84556** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 1/9 | 3/3 | 6.9 |
| c508-equivalent | TriggerRatio=0.5, RecentGroupsToPreserve=4 | 10240/1024 (Agent.Context.Profiles) | 14 | 65205 | 294 | 9 | 9066 | 3078 | **77643** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 1/9 | 3/3 | 7.5 |
| c508-equivalent | TriggerRatio=0.5, RecentGroupsToPreserve=8 | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 0 | 0 | 0 | **70239** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 7.2 |
| c508-equivalent | TriggerRatio=0.75, RecentGroupsToPreserve=2 | 10240/1024 (Agent.Context.Profiles) | 14 | 65983 | 318 | 9 | 14091 | 3078 | **83470** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 5/9 | 3/3 | 7.2 |
| c508-equivalent | TriggerRatio=0.75, RecentGroupsToPreserve=4 | 10240/1024 (Agent.Context.Profiles) | 14 | 67574 | 318 | 9 | 6696 | 3078 | **77666** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 5/9 | 3/3 | 7.3 |
| c508-equivalent | TriggerRatio=0.75, RecentGroupsToPreserve=8 | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 0 | 0 | 0 | **70239** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 7.0 |
| c508-equivalent | TriggerRatio=0.9, RecentGroupsToPreserve=2 | 10240/1024 (Agent.Context.Profiles) | 14 | 70654 | 340 | 5 | 6413 | 1710 | **79117** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.8 |
| c508-equivalent | TriggerRatio=0.9, RecentGroupsToPreserve=4 | 10240/1024 (Agent.Context.Profiles) | 14 | 70203 | 340 | 5 | 1549 | 1710 | **73802** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 8.0 |
| c508-equivalent | TriggerRatio=0.9, RecentGroupsToPreserve=8 | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 0 | 0 | 0 | **70239** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 7.0 |
| c508-equivalent | TriggerRatio=1, RecentGroupsToPreserve=2 | 10240/1024 (Agent.Context.Profiles) | 14 | 70350 | 340 | 4 | 5976 | 1368 | **78034** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.6 |
| c508-equivalent | TriggerRatio=1, RecentGroupsToPreserve=4 | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 4 | 1112 | 1368 | **72719** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.6 |
| c508-equivalent | TriggerRatio=1, RecentGroupsToPreserve=8 | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 0 | 0 | 0 | **70239** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.7 |

#### sweep MinimumPreservedToolGroups — c508-equivalent, UNKNOWN profile (the only structural bound)

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-equivalent | unknown profile, MinimumPreservedToolGroups=0 | unknown (unknown-structural) | 14 | 45071 | 294 | 0 | 0 | 0 | **45365** | 7->7 | 7 | 4 | 0 | 2 | 7 | 0 | Completed | 1/9 | 3/3 | 2.1 |
| c508-equivalent | unknown profile, MinimumPreservedToolGroups=2 | unknown (unknown-structural) | 14 | 61448 | 294 | 0 | 0 | 0 | **61742** | 7->7 | 5 | 4 | 0 | 2 | 7 | 0 | Completed | 1/9 | 3/3 | 3.0 |
| c508-equivalent | unknown profile, MinimumPreservedToolGroups=3 | unknown (unknown-structural) | 14 | 69600 | 294 | 0 | 0 | 0 | **69894** | 7->7 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 1/9 | 3/3 | 3.0 |
| c508-equivalent | unknown profile, MinimumPreservedToolGroups=4 | unknown (unknown-structural) | 14 | 74784 | 294 | 0 | 0 | 0 | **75078** | 7->7 | 3 | 4 | 0 | 2 | 7 | 0 | Completed | 1/9 | 3/3 | 2.9 |
| c508-equivalent | unknown profile, MinimumPreservedToolGroups=6 | unknown (unknown-structural) | 14 | 80798 | 340 | 0 | 0 | 0 | **81138** | 7->7 | 1 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 4.0 |
| c508-equivalent | unknown profile, MinimumPreservedToolGroups=8 | unknown (unknown-structural) | 14 | 81039 | 340 | 0 | 0 | 0 | **81379** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.3 |
| c508-equivalent | unknown profile, MinimumPreservedToolGroups=12 | unknown (unknown-structural) | 14 | 81039 | 340 | 0 | 0 | 0 | **81379** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 3.2 |

#### sweep MinimumPreservedToolGroups — c508-extended-10, UNKNOWN profile (the only structural bound)

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-extended-10 | unknown profile, MinimumPreservedToolGroups=0 | unknown (unknown-structural) | 24 | 84959 | 556 | 0 | 0 | 0 | **85515** | 17->17 | 17 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 9.3 |
| c508-extended-10 | unknown profile, MinimumPreservedToolGroups=2 | unknown (unknown-structural) | 24 | 124233 | 556 | 0 | 0 | 0 | **124789** | 17->17 | 15 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 12.4 |
| c508-extended-10 | unknown profile, MinimumPreservedToolGroups=3 | unknown (unknown-structural) | 24 | 143835 | 556 | 0 | 0 | 0 | **144391** | 17->17 | 14 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 12.3 |
| c508-extended-10 | unknown profile, MinimumPreservedToolGroups=4 | unknown (unknown-structural) | 24 | 164572 | 556 | 0 | 0 | 0 | **165128** | 17->17 | 13 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 13.3 |
| c508-extended-10 | unknown profile, MinimumPreservedToolGroups=6 | unknown (unknown-structural) | 24 | 202604 | 556 | 0 | 0 | 0 | **203160** | 17->17 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 13.7 |
| c508-extended-10 | unknown profile, MinimumPreservedToolGroups=8 | unknown (unknown-structural) | 24 | 236055 | 556 | 0 | 0 | 0 | **236611** | 17->17 | 9 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 15.3 |
| c508-extended-10 | unknown profile, MinimumPreservedToolGroups=12 | unknown (unknown-structural) | 24 | 289221 | 556 | 0 | 0 | 0 | **289777** | 17->17 | 5 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 18.0 |

#### c508-equivalent — configuration matrix

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-equivalent | no projector (unprojected full history) | none (unprojected) | 14 | 81039 | 340 | 0 | 0 | 0 | **81379** | 8 max verbatim | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 0.0 |
| c508-equivalent | unknown profile (structural MAF, the shipped default for an unprofiled model) | unknown (unknown-structural) | 14 | 69600 | 294 | 0 | 0 | 0 | **69894** | 7->7 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 1/9 | 3/3 | 2.8 |
| c508-equivalent | MAF 32768, summarization off | 32768/1024 (Agent.Context.Profiles) | 14 | 81039 | 340 | 0 | 0 | 0 | **81379** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 5.4 |
| c508-equivalent | MAF 32768, summarization on (task model) | 32768/1024 (Agent.Context.Profiles) | 14 | 81039 | 340 | 0 | 0 | 0 | **81379** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 5.8 |
| c508-equivalent | MAF 32768, summarization on (dedicated Sum/small) | 32768/1024 (Agent.Context.Profiles) | 14 | 81039 | 340 | 0 | 0 | 0 | **81379** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.1 |
| c508-equivalent | MAF 16384, summarization off | 16384/1024 (Agent.Context.Profiles) | 14 | 81039 | 340 | 0 | 0 | 0 | **81379** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 5.3 |
| c508-equivalent | MAF 16384, summarization on (task model) | 16384/1024 (Agent.Context.Profiles) | 14 | 81039 | 340 | 0 | 0 | 0 | **81379** | 0->0 | 0 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.4 |
| c508-equivalent | MAF 12288, summarization off | 12288/1024 (Agent.Context.Profiles) | 14 | 77107 | 340 | 0 | 0 | 0 | **77447** | 4->1 | 3 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.0 |
| c508-equivalent | MAF 12288, summarization on (task model) | 12288/1024 (Agent.Context.Profiles) | 14 | 77107 | 340 | 3 | 834 | 1026 | **79307** | 4->1 | 3 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.6 |
| c508-equivalent | MAF 12288, summarization on (dedicated Sum/small) | 12288/1024 (Agent.Context.Profiles) | 14 | 77107 | 340 | 3 | 834 | 1026 | **79307** | 4->1 | 3 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.7 |
| c508-equivalent | MAF 10240, summarization off | 10240/1024 (Agent.Context.Profiles) | 14 | 69899 | 340 | 0 | 0 | 0 | **70239** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 5.8 |
| c508-equivalent | MAF 10240, summarization on (task model) | 10240/1024 (Agent.Context.Profiles) | 14 | 70203 | 340 | 5 | 1549 | 1710 | **73802** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 7.8 |
| c508-equivalent | MAF 10240, summarization on (dedicated Sum/small) | 10240/1024 (Agent.Context.Profiles) | 14 | 70203 | 340 | 5 | 1549 | 1710 | **73802** | 4->0 | 4 | 4 | 0 | 2 | 7 | 0 | Completed | 9/9 | 3/3 | 6.8 |
| c508-equivalent | MAF 9216, summarization off | 9216/1024 (Agent.Context.Profiles) | 9 | 37249 | 217 | 0 | 0 | 0 | **37466** | 4->0 | 4 | 4 | 0 | 0 | 4 | 0 | Failed | n/a | 0/3 | 3.1 |
| c508-equivalent | MAF 9216, summarization on (task model) | 9216/1024 (Agent.Context.Profiles) | 9 | 37249 | 217 | 4 | 1112 | 1368 | **39946** | 4->0 | 4 | 4 | 0 | 0 | 4 | 0 | Failed | n/a | 0/3 | 3.9 |
| c508-equivalent | MAF 8192, summarization off | 8192/1024 (Agent.Context.Profiles) | 8 | 27422 | 205 | 0 | 0 | 0 | **27627** | 4->0 | 4 | 3 | 0 | 0 | 4 | 0 | Failed | n/a | 0/3 | 2.5 |

#### c508-extended-10 (compaction pressure at the final call) — configuration matrix

| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Projection ms (real) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---:|
| c508-extended-10 | no projector (unprojected full history) | none (unprojected) | 24 | 311090 | 602 | 0 | 0 | 0 | **311692** | 18 max verbatim | 0 | 4 | 0 | 2 | 17 | 0 | Completed | 9/9 | 3/3 | 0.0 |
| c508-extended-10 | unknown profile (structural MAF, the shipped default for an unprofiled model) | unknown (unknown-structural) | 24 | 143835 | 556 | 0 | 0 | 0 | **144391** | 17->17 | 14 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 13.1 |
| c508-extended-10 | MAF 32768, summarization off | 32768/1024 (Agent.Context.Profiles) | 24 | 311090 | 602 | 0 | 0 | 0 | **311692** | 0->0 | 0 | 4 | 0 | 2 | 17 | 0 | Completed | 9/9 | 3/3 | 27.9 |
| c508-extended-10 | MAF 32768, summarization on (task model) | 32768/1024 (Agent.Context.Profiles) | 24 | 310221 | 602 | 1 | 1623 | 342 | **312788** | 14->13 | 2 | 4 | 0 | 2 | 17 | 0 | Completed | 9/9 | 3/3 | 33.6 |
| c508-extended-10 | MAF 16384, summarization off | 16384/1024 (Agent.Context.Profiles) | 24 | 217733 | 556 | 0 | 0 | 0 | **218289** | 14->14 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 39.6 |
| c508-extended-10 | MAF 16384, summarization on (task model) | 16384/1024 (Agent.Context.Profiles) | 24 | 206931 | 556 | 13 | 20288 | 4446 | **232221** | 14->4 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 52.4 |
| c508-extended-10 | MAF 16384, summarization on (dedicated Sum/small) | 16384/1024 (Agent.Context.Profiles) | 24 | 206931 | 556 | 13 | 20288 | 4446 | **232221** | 14->4 | 11 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 54.4 |
| c508-extended-10 | MAF 12288, summarization off | 12288/1024 (Agent.Context.Profiles) | 24 | 163683 | 556 | 0 | 0 | 0 | **164239** | 14->1 | 13 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 42.4 |
| c508-extended-10 | MAF 12288, summarization on (task model) | 12288/1024 (Agent.Context.Profiles) | 24 | 163867 | 556 | 6 | 2983 | 2052 | **169458** | 14->1 | 13 | 4 | 0 | 2 | 17 | 0 | Completed | 1/9 | 3/3 | 46.6 |
