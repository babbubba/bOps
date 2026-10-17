# ADR-0058 — Local Agent knowledge pilot: an opt-in Knowledge composition in the CLI

Status: **Proposed — 2026-10-17. Implemented as an experimental pilot** under the operator's written instruction of 2026-10-17 to
propose and implement a *circumscribed revision of D-049*, for an opt-in Knowledge integration in the local CLI only. The operator's
instruction authorises the pilot; it is not the formal acceptance of this ADR (that is a separate act, recorded as D-050 when it
happens). No gate below is declared passed by this ADR.
Date: 2026-10-17
Base: worktree `bOps-knowbench01a`, branch `codex/know-bench-01a`, HEAD `784ac9c` plus the uncommitted KNOW-BENCH changes. Every
statement about the repository below was checked in that tree on the date above.
Refines: [ADR-0057](0057-knowledge-discovery-index.md) §I and §L (composition in a production host) · [ADR-0056](0056-governed-knowledge-read-and-durable-resume.md) §4
("nothing is activated … no configuration key") · [ADR-0055](0055-trusted-knowledge-packs-and-provider-isolation.md) consequences ("nothing is composed by a production host")
Preserves: D-048 / [ADR-0052](0052-microsoft-agent-framework-bops-boundary.md) (bOps keeps operational authority), D-049 / ADR-0057 (everything it decides),
ADR-0038, ADR-0040, ADR-0046, ADR-0054 G2-1…G2-6 as implemented in tree (the ADR text itself is on `docs/arch-03-maf-convergence-decision`),
invariants I-1…I-10 of ADR-0053.

## 1. What was decided before, exactly

The earlier decisions never *forbade* composing knowledge; they withheld the authorisation, and an architecture test turned the
withholding into a hard stop:

| Source | Wording that matters here |
|---|---|
| D-049 (`agentic/06-decisions.md`) | "Not decided / not authorised … composing the index in a production host" and "supersedes no pending gate". |
| ADR-0057 §I (out of M1) | "Composizione in un host di produzione o chiave di configurazione." Gate **B2** blocks the live path, "not M1 (nessun host di produzione lo compone)". |
| ADR-0056 §4 | "Nothing is activated: the host, policy and session classes are internal to `bOps.AgentFramework`, no production host composes them, and no configuration key exists." Its report lists the prerequisites for production activation. |
| ADR-0054 §4 (G2), still Proposed | "Until then KNOW-01 content providers remain CONDITIONAL and disabled **by composition (not by configuration flag alone)**." |
| Architecture tests | `KnowledgeReadRuntimeBoundaryTests` (the host contract is used by the runner and the adapter only; nothing builds `KnowledgeTaskHost`), `KnowledgePackBoundaryTests` (no configuration key can enable knowledge). |

This ADR changes the last row and part of the first four, for one host, under the conditions of §3. It does not touch the rest.

## 2. Gate review (nothing is silently bypassed)

State verified on 2026-10-17. "Blocks" means: a dependency the roadmap or an ADR still treats as binding for *wider* use of knowledge.

| Gate | Formal state in the tree | Treatment in this ADR |
|---|---|---|
| **ARCH-01** (ADR-0053, ownership matrix, `e7ffab6`) | Documentation on `docs/arch-01-maf-boundaries`; **not an ancestor of HEAD**. Invariants I-1…I-10 are followed by the code and cited by ADR-0055…0057. | Not a blocker for a pilot that adds no MAF authority; stays open as a documentation merge. |
| **ARCH-02** (MAF parity spike) | Spikes archived as tags; never merged, by design. | Evidence only. The pilot needs no parity claim: it does not use a MAF Agent or Workflow. |
| **ARCH-03** (ADR-0054, `fd91279`) | **Proposed**, on `docs/arch-03-maf-convergence-decision`; not an ancestor of HEAD; its acceptance and the "Refined by ADR-0057" reconciliation are open (D-049, open dependencies). | **Not declared passed.** G2-1…G2-6 are implemented and architecture-tested in tree (AF-CONTEXT-01, AF-TURN-01B, KNOW-01A/01B) and are rerun for this change, but ADR-0054's rule that content providers stay "disabled by composition" is **deliberately deviated from** for the CLI pilot, by operator decision, behind an opt-in switch that ships off. |
| **BENCH-00** (official 3×3 A/B) | **Not executed.** The 1×3 smoke ran on AF-05G/AF-05H (n = 1 per scenario). `docs/benchmarks/bench00-maf-compatibility-report.md` (2026-10-16): live "NON ESEGUITO". | No result of any benchmark is claimed or used. BENCH-00 measures the API runtime, which composes no knowledge; it neither gates nor validates the CLI pilot. |
| **AF-05H2-R1 … AF-05H2** (replan recovery) | In the tree (`3d2afb7` … `998e952` are ancestors of HEAD). | Satisfied for the code this pilot sits on. |
| **BENCH-01, AF-EVID-01, AF-05H4-VERIFY** | Queued in `agentic/_plans/2026-10-11-maf-knowledge-convergence.md`; no completion recorded in tree. | Open. The pilot changes none of EvidenceRead, replan or disclosure behaviour. |
| **PR #100** | Open; untouched. | Untouched. |
| **ADR-0057 B1** (revocation inside one attempt) | Open; accepted risk. | Unchanged: a withdrawn grant or revoked key is seen at the next resume (§5). |
| **ADR-0057 B2** (two consecutive `user` messages on a real server and template) | **Open and unverified** on any real server. | Unchanged and *worse for a pilot than for a test*: with knowledge enabled the index is the first `user` message and the goal the second on every step request. A server whose template demands strict alternation can reject the request. The operator must check this before relying on the pilot (guide §6); the ADR does not authorise merging the index into the goal turn. |
| **ADR-0057 §H.4** (conditions for a live test) | Not met (no explicit authorisation, no n ≥ 3 per arm). | No live test is run or authorised here. The pilot is not a benchmark and produces no quality claim. |
| **KNOW-01 (rest), KNOW-02, KNOW-03** | Not started: Experience search, playbooks, A/B/C evaluation. | Out of scope. |

## 3. Decision

1. **One composition, one host.** `bOps.Cli` composes knowledge for the Local Agent, for a goal (`bops "<goal>"`) and for `bops resume`
   only. `bops delegate` (a delegated role never receives a session, ADR-0056), `recover`, `reconcile`, `plugin`, `vault` and `audit`
   open none. `bOps.Api`, `bOps.Hosting`, `bOps.PluginHost`, the packages and the AppHost stay without knowledge. The list is closed
   and enforced (`LocalKnowledgePilotBoundaryTests`, `KnowledgeReadRuntimeBoundaryTests`).
2. **Opt-in, off by default, fail-closed.** `Knowledge:Enabled` is `false` in the shipped `appsettings.json` and is the only key there.
   When it is `false` or the section is absent the CLI builds the runner exactly as before (a test compares every request, byte for
   byte). When it is `true`, every unusable value is an error that ends the command with exit code 2 before the model is asked:
   unknown or misspelt keys (even while disabled), missing or overlapping directories, an unreadable trust store or one that lists no
   key or carries private-key material, no grants, no binding, a kind other than `Knowledge`, an out-of-range budget. There is no
   fallback to a run without knowledge.
3. **No MAF turn broker is required — shown by the call graph, not assumed.**
   `AgentRunner` → `IKnowledgeTaskHost` (a `bOps.Runtime` contract) → `KnowledgeTaskHost` (adapter, internal) →
   `KnowledgeBridgeAdmission` → `KnowledgeAdmissionService` (`bOps.Knowledge`) and `KnowledgeReadBridge` (adapter, over the framework's
   `AgentSkillsProvider` / `AgentFileSkillsSource`, invoked by bOps, no script runner). The runner puts the index and the served reads
   into the `ModelContextRequest`; the `ModelInvocationBroker` + `MafContextProjector` the CLI already used project it. Nothing on this
   path touches `MafGovernedTurnBroker`, `GovernedChatClient`, `ContextGate`, `BopsContextProvider`, `ChatClientAgent` or any
   `AIContextProvider`; the architecture test forbids the CLI to name them, and the end-to-end process test runs without them. MAF
   components actually needed: `Microsoft.Agents.AI` 1.24.0 `AgentSkillsProvider` and `AgentFileSkillsSource` (already pinned, already
   behind the adapter). The experimental turn broker stays experimental and uncomposed.
4. **The Local Agent tenant, nothing else.** `LocalAgentTenantAuthority` resolves the operating-system user running the process to
   `TenantId.Local` — the tenant `KnowledgeTaskScope.ForLocalAgent` issues (tested equal) — and every other identity to no tenant, hence
   no scope and no knowledge. The provider registration, the grants and the policy all name `TenantId.Local` and nothing else; the
   configuration has no tenant key (an unknown key is an error); no tenant is invented in the API.
5. **Installed ≠ authorised ≠ used.** *Installed*: a pack directory under `Knowledge:PacksDirectory`. *Authorised*: it is listed in
   `Knowledge:Grants` by exact id, version and kind, **and** signed by a key in the operator's trust store, **and** the engine range
   fits; installing grants nothing, and a granted but unbound version is never opened. *Bound*: `Knowledge:Binding` names the one pack new
   tasks use (single pack per task, ADR-0056). *Used*: the model chose to call `runtime.knowledge_read`; each read is audited. The
   runner never forces a read.
6. **Authority is the existing authority.** Admission order and every check of ADR-0055/0056 are unchanged: provider registration →
   grant → read bytes → signature against the trust store → manifest and per-file hashes → kind → pin → tenant snapshot; on every
   read the current authority and the snapshot are checked again; a resume re-admits the pinned content or fails closed with
   `KnowledgeUnavailable`. Knowledge is attributed data (S5, ADR-0057 §D); it grants no tool, changes no policy and completes no step.
   A pack that says "call service.restart" changes nothing (test).
7. **No private key in the runtime.** The runtime verifies with public keys from `Knowledge:TrustStorePath`. Signing is publisher tooling
   outside the runtime (`scripts/New-KnowledgeSigningKey.ps1`, `scripts/Publish-KnowledgePack.ps1`); the repository's test keys are
   in-memory and never in a configuration. The shipped configuration names no key and no pack.
8. **The read budget is configurable inside ceilings** (`MaxReads` 1–8, `MaxRejections` 1–8, `MaxCharactersPerRead` 500–16000;
   defaults 4 / 4 / 8000 = ADR-0057's), so an operator cannot lift the bounds beyond what the tests cover.
9. **Testability without a second path.** The CLI's runner construction moved, unchanged, from `Program.cs` into
   `CliRunnerComposition`, which `Program.cs` and the tests both call.

## 4. What changed in the product

| File | Change |
|---|---|
| `src/core/bOps.AgentFramework/LocalKnowledgeComposition.cs` (new) | Options DTOs, `LocalKnowledgeComposition.Create`, `LocalAgentTenantAuthority`, `LocalKnowledgePolicy`. Builds registry + one directory source + admission service + `KnowledgeTaskHost`. The only builder of the host. |
| `src/core/bOps.Knowledge/KnowledgeTrustStore.cs` | `KeyCount` (so an empty trust store is a configuration error). |
| `src/core/bOps.Cli/CliKnowledge.cs` (new) | Strict reading of the `Knowledge` section (`ErrorOnUnknownConfiguration`), host creation, one log line. |
| `src/core/bOps.Cli/CliRunnerComposition.cs` (new) | The runner construction extracted from `Program.cs`, plus the optional `knowledgeHost`. |
| `src/core/bOps.Cli/Program.cs` | Builds the host for goal/resume only, exit 2 on a configuration error, uses `CliRunnerComposition`. |
| `src/core/bOps.Cli/appsettings.json` | `"Knowledge": { "Enabled": false }`. |
| `scripts/New-KnowledgeSigningKey.ps1`, `scripts/Publish-KnowledgePack.ps1` (new) | Publisher tooling. Not part of the runtime or of any build. |
| Tests | `bOps.Cli.Tests` (composition, configuration, process end to end, publisher scripts; now full globalization like the CLI), `bOps.AgentFramework.Tests` (identity, scope, policy), `bOps.Architecture.Tests` (the closed perimeter). |

`bOps.Runtime`, `bOps.Abstractions`, the policy engine, the tool registry, the audit schema and the API are unchanged.

## 5. Guarantees that stay exactly as they were

No generic execution tool and no new tool (the function is a runtime control function, never registered, never planned). Policy and
approval for every tool call are unchanged. Planning and replanning receive no index and no function. A delegated role gets no session.
Every admission, index publication and read — refusals included — is audited by identifier and hash, never by content. Tool output and
pack content are data. Resume never resolves "latest", never changes tenant, never accepts changed bytes. Revocation of a grant, key or
publisher is seen at the next resume or admission, not inside an attempt (B1). The core names no package, tool or provider.

## 6. Criteria before this can extend to the API (not met; not designed in detail)

1. ARCH-03 / ADR-0054 accepted by the operator and reconciled with ADR-0057; the "disabled by composition" rule replaced by an explicit one.
2. A tenant model for the API (authenticated principal → `ITenantAuthority`; grants from entitlement, not configuration) — a V1.4-D / Coordinator decision, not a CLI one.
3. B1 decided (live reload of grants and trust store, or an authorisation epoch) and B2 verified on the servers and templates in use.
4. Win/Linux CI green for the above; ADR-0057 §H.4 satisfied before any live comparison; BENCH-00 3×3 and BENCH-01 recorded.
5. API and UI exposure of `KnowledgeUnavailable` causes and of the admission/read trail; snapshot retention policy; pack distribution channel.
6. A decision on how a task's pack is chosen when more than one is available (the host contract receives only the actor and the scope today).

## 7. Implemented, and only designed

**Implemented and tested offline:** everything in §3 and §4. **Designed only:** binding a pack to a task *or capability* (the host
contract has no goal or capability input; the pilot binds one configured pack to every CLI task and lets the model decide from the
index); several packs per task; Experience packs and tenant-partitioned sources; a `bops knowledge` verification command; live reload
of trust and grants; API composition.

## 8. Risks accepted

B2 (above); B1; a pack costs index tokens on every step request even when never read; on Linux the CLI needs `libicu` for Knowledge
(`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` makes Knowledge report itself unavailable instead of running degraded); the trust store and
packs are files protected by the operator's file permissions (S8: detected, not prevented, if the process's own user rewrites them);
the effect of the index and the reads on diagnosis quality is **unmeasured**.

## 9. Verification record (2026-10-17, uncommitted tree, offline only)

No live inference, no real Windows capture, no network beyond a loopback endpoint scripted by the test. Debug on Windows 11, plus Release on
WSL Ubuntu-24.04 from a copy of the working tree. New tests: 56 composition/configuration cases (`KnowledgeCli*Tests`, theory rows counted), 5 process
cases that run the real `bops` executable against a scripted OpenAI-compatible endpoint, 2 cases for the publisher scripts, 3 adapter cases and
5 architecture cases; 3 older architecture tests were rewritten for the closed exception. Each guarantee in §3 was also checked by a deliberate
defect (policy never binds, identity resolves to the Local tenant for everyone, implicit grant of the binding, non-strict binding, swallowed
configuration error, overlapping directories): each made at least one test fail.

| Suite | Windows | Linux |
|---|---|---|
| `bOps.Knowledge.Tests` | 308 / 308 | 308 / 308 |
| `bOps.Architecture.Tests` | 207 / 207 | 205 passed, 2 skipped |
| `bOps.Cli.Tests` | 187 / 187 | 187 / 187 on two reruns; the first run had one failure of the pre-existing `CliProcessTests.Resume_OfANonResumableTask…` that passed alone and on both reruns (transient, not investigated further) |
| `bOps.AgentFramework.Tests` | 426 passed, 8 skipped | the 56 Knowledge runner/index/composition tests pass; 28 pre-existing Windows-only canary/benchmark tests fail with `The diagnostic canary requires Windows` (not touched here) |
| `bOps.Runtime.Tests` | 2164 / 2166 — the 2 failures are the stale expectations already recorded in the 2026-10-16 BENCH-00 report (`ToolCallProtocolTests.TheToolCatalog_…`, `ModelFailureEndToEndTests.ATransientProviderFailure_…`); `bOps.Runtime` is not changed here | not run |
| `bOps.Audit.Tests`, `bOps.Memory.Tests`, `bOps.Policy.Tests` | 31, 101, 308 — all pass | not run |
| `bOps.Api.Tests` | 512 / 512 (the API is unchanged and composes no knowledge) | not run |

The publisher-script tests need `pwsh`; the WSL image has none, so on Linux they return without running (CI images have it). B2 is **not**
verified by any of this: the process test shows what our adapter sends (index and goal as consecutive `user` messages), not that a given server accepts it.

## 10. Alternatives rejected

Reusing the benchmark's MAF turn broker "because the benchmark uses it" (not a prerequisite; §3.3). Granting every installed signed
pack (breaks the entitlement model). A configurable tenant (the API tenant question in disguise). Degrading to a run without knowledge
on a bad configuration (silent loss of what the operator asked for). Putting keys or packs in `appsettings.json`. Binding by goal text
or capability by extending the host contract in this change (a runtime-contract change the pilot does not need). Composing in the API
"disabled by default" (would put the first tenant decision in a file).
