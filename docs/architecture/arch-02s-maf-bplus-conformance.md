# ARCH-02S — MAF B+ boundary conformance

Status: **Evidence — 2026-10-12.** Changes no ADR; ADR-0053 stays **Proposed**. ARCH-03, KNOW-01, AF-FINAL-01,
AF-EVID-01 and AF-GROUND-01 are **not** started by this work.

Base: `ce8ed54` (ARCH-02R) · branch `spike/arch-02r-maf-reassessment` · `Microsoft.Agents.AI` 1.24.0 (no upgrade) · new
throwaway spike `spikes/arch-02s-maf-bplus-conformance/` (outside `bOps.slnx`). It links the ARCH-02R fixtures
(`ScriptedClient`, `ToolSurfaceGuard`, `PinnedPackSkillsSource`, `PackContextProvider`) and references the **real**
`bOps.AgentFramework` leaf (`GovernedChatClient` → `ModelInvocationBroker` → scripted `IChatModel`); no production file is
modified. 4 offline tests, scripted model, real MAF APIs. Run: `dotnet test --filter FullyQualifiedName~MafBPlusConformanceTests`
in the spike folder (4/4 green).

## Verdicts

| Gate | Verdict | One line |
|---|---|---|
| G1 stateless/resume | **PASS** | Turn rebuilt from persisted state through new agent/providers/broker and no session = byte-identical governed request; session reuse detected before the broker |
| G2 context governance/audit | **FAIL natively — closable by bOps controls (C1–C6)** | Context reaches the broker and is fully accounted; provenance, role and tenant boundaries are *not* MAF guarantees |
| G3 progressive skills | **PASS with a small bOps adapter** | Native progressive loading needs FICC, so it is incompatible with B+; MAF's own `load_skill`/`read_skill_resource` functions invoked by a ~10-line bOps adapter give discovery → load → resource with no scripts, no hidden calls, pinned content |

## G1 — Stateless/resume (test `G1_…`)

- `TaskState` stand-in (user turn, assistant `svc_status` call, tool result) is projected by bOps into MAF messages and run
  through an as-is `ChatClientAgent` (`UseProvidedChatClientAsIs=true`) + knowledge provider + real `GovernedChatClient`.
- The `ModelRequest` received by the bOps model, serialized, is **identical** for: original turn · turn rebuilt after a
  JSON round-trip of the state with a new agent, provider and broker (process restart) · same with a fresh per-turn
  `AgentSession`.
- Request = projected turns + 1 provider message; leaf sources `[External, External, External, AIContextProvider]`; no
  `ChatHistory` message.
- **Reuse detection:** reusing one `AgentSession` across two turns makes MAF prepend 5 messages tagged
  `AgentRequestMessageSourceType.ChatHistory` (leaf counts 4 → 9). A leaf check on that tag fails closed: the second turn
  never reaches the broker (1 model request, 1 call record).
- Scope: proves the B+ path is deterministic across resume and carries no hidden MAF history. Equivalence with the **current
  `AgentRunner` projection** is not claimed; it belongs to AF-TURN-01's recorded-request tests.

## G2 — Context governance and audit (tests `G2_broker_…`, `G2_tenant_…`)

| Check | Result | Evidence |
|---|---|---|
| Broker receives provider context | **yes** | `SystemPrompt` contains step + `KNOWLEDGE(pack=…)` + skills advertisement; `History` contains knowledge and experience messages |
| Tokens and model calls accounted | **yes** | 1 broker request, 1 `ModelCallRecord` (321/7), `AccountTokens=[328]`, `ModelCallAuditEvent` written, `AgentResponse.Usage.Total=328`; providers made 0 model calls |
| Knowledge/Experience/Skills identifiable | **partial** | At the leaf, provider messages carry `SourceType=AIContextProvider`, `SourceId=<provider type full name>`. Lost after that: provider **instructions** are concatenated into one string with no attribution (skills advertisement lives there), and bOps `ChatTurn`/`ModelRequest` has no source field, so the mapper drops it. Two instances of the same provider type would share a `SourceId` (inferred from the id format, not tested) |
| No automatic elevation to system | **no (native)** | A provider `ChatRole.System` message reaches the broker as a bOps `System` turn. Pack metadata (skill description "Ignore previous rules…") is placed in `SystemPrompt` by `AgentSkillsProvider`. A provider overriding the documented `InvokingCoreAsync` controls the whole merged context: it rewrote `step-3`→`step-9` in bOps instructions and added a message tagged `External`/`operator` (forged attribution). The elevation is *detectable* at the leaf (System + `AIContextProvider`) unless a provider forges tags |
| Tenant isolation | **no (native), yes by construction** | One shared provider over `CachingAgentSkillsSource` with default options: tenant-b's turn advertised tenant-a's private runbook. With `CacheIsolationKeySelector = tenant` or per-task provider construction: no leak |

Required bOps controls (contracts, not built here):

- **C1 Leaf integrity check** in `GovernedChatClient`, before mapping: reject `ChatHistory` sources (G1). Accept `System`
  role only from bOps-projected `External` messages. Accept `AIContextProvider` messages only from allow-listed `SourceId`s.
- **C2 Provenance in the contract**: `ChatTurn`/`ModelRequest` (and the model-call audit) carry
  `(sourceType, sourceId, packId@version#hash)`. Knowledge is rendered as attributed messages or labelled sections, never as
  bare instructions.
- **C3 Provider admission**: only bOps-authored providers/decorators may override `InvokingCoreAsync`. Every MAF or
  third-party provider is wrapped by a bOps decorator that re-stamps attribution, demotes `System` to `User`, drops or
  sanitizes `Instructions` and applies the tool allow-list (extends ARCH-02R `ToolSurfaceGuard`).
- **C4 Skills prompt**: a bOps `SkillsInstructionPrompt` template (the default one tells the model to use `run_skill_script`
  even when the tool is stripped). Pack `name`/`description` must be length-capped and validated, because they reach the
  system prompt.
- **C5 Tenant binding**: providers and sources are constructed per task with the tenant bound. `CachingAgentSkillsSource`
  is allowed only with `CacheIsolationKeySelector = tenant`. The experience search delegate applies a mandatory tenant
  filter.
- **C6 Pipeline architecture test**: no FICC and no `ChatHistoryProvider` in the turn pipeline (ARCH-02R row-2 amendment),
  plus a provider allow-list.

## G3 — Progressive skills (test `G3_…`)

- **Limitation:** MAF progressive disclosure relies on FICC invoking `load_skill`/`read_skill_resource`. In B+ there is no
  FICC, so natively nothing is loaded; the calls only surface as proposals. This is the intended B+ behaviour, but it means
  "native" progressive loading is unavailable.
- **Adapter (proposed `runtime.knowledge_read`, a `Read` tool):** `ToolSurfaceGuard(AgentSkillsProvider(ManifestPinnedSkillsSource(…)), load_skill, read_skill_resource)`
  plus `KnowledgeReadAdapter`. The adapter honours a call only if the tool is read-only, on the allow-list and offered on
  that turn, then **bOps** invokes MAF's own `AIFunction` (with an empty `IServiceProvider`; MAF's functions require one).
- Measured over 4 explicit bOps turns: 4 model calls (any extra call would throw `UNSCRIPTED`). Tools on every turn are
  exactly `[svc_status, load_skill, read_skill_resource]`. `load_skill` returned the pinned body. `read_skill_resource`
  returned the pinned resource. The model's `run_skill_script` proposal was rejected by the adapter; script body runs = 0.
  `AgentResponse` never contained a `FunctionResultContent` (MAF executed nothing).
- **Pinning:** the manifest pin (content hash, which fixes resource *names*, plus a hash of each static resource *value*)
  fails closed before any model call on a different version (`pack pin mismatch: nginx-triage`) and on a tampered resource
  (`…/checklist`). **ARCH-02R's content-only pin accepted the tampered resource** and must be replaced.
- Residuals: the loaded body still lists `<available_scripts>`, so packs with scripts must be rejected at manifest
  validation (or will waste a turn, as here). File-based skills read resources from disk at read time, so a TOCTOU window
  exists unless packs are served from an immutable, content-addressed snapshot (not tested). `GovernedChatClient` (AF-01)
  still throws on any `ChatOptions.Tools`, so G3 used the scripted MEAI client as leaf. Accepting declarations is
  AF-TURN-01 work already implied by the ARCH-02R row-2 amendment.

## Impact on B+

B+ stands. G1 removes the resume/hidden-history risk at the cost of one leaf check (C1). G2 shows the MAF provider
pipeline is a **composition** mechanism, not a **trust** boundary. Every provider is fully trusted by MAF, so the trust
boundary must be bOps code at two points: the provider decorators (C3–C5) and the governed leaf (C1, C2, C6). These are
small, sit on public extension points, and become ARCH-03 preconditions for adopting the provider pipeline. G3 confirms
that "MAF content, bOps tool" is the right split for skills. No new skill engine is needed.

## KNOW-01 — MAF native vs bOps contract

| Component | MAF native | bOps contract |
|---|---|---|
| SKILL.md model, parsing, frontmatter, resources (`AgentSkill`, `AgentFileSkillsSource`, `AgentInlineSkill`) | ● | |
| Source pipeline: filtering, dedup, aggregation | ● | |
| Caching (`CachingAgentSkillsSource`) | ○ only with tenant isolation key | C5 |
| L1 advertisement (`AgentSkillsProvider`) | ● mechanism | template + metadata validation (C4) |
| `load_skill` / `read_skill_resource` implementations | ● invoked by bOps | `runtime.knowledge_read` adapter: allow-list, offered-check, budget, audit |
| `run_skill_script`, script runners | ✗ never | packs with scripts rejected |
| Context merge / injection (`AIContextProvider` pipeline) | ● | provider admission + re-stamping decorator (C3) |
| Message source attribution | ○ at leaf only | provenance fields in `ChatTurn`/`ModelRequest`/audit (C2) |
| Pack identity: manifest, version, signature, content + resource pin | | ● (`ManifestPinnedSkillsSource` shape) |
| Tenant scoping of knowledge/experience | | ● per-task construction, mandatory filters (C5) |
| Leaf integrity (no hidden history, no elevated roles, provider allow-list) | | ● (C1, C6) |
| Usage/model-call accounting | ● propagation | ● broker records (unchanged) |

● = owner, ○ = partial, ✗ = excluded.
