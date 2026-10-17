# KNOW-DISC-01 T8 — Knowledge Index M1 gate report

Decision: [ADR-0057](adr/0057-knowledge-discovery-index.md) §E (T8), §H.1, §H.2, §H.3. This report adds no decision and changes no status other than the T8 row.
Scope: offline only. No live inference, no capture, no DGX, no prompt / pack / provider / governance change. No commit.
History: first run on `d0dcfbe` = **FAIL** (one defect, four missing tests). T8-FIX01 (2026-10-17, same worktree, uncommitted) corrected the defect and added the tests; sections marked *(T8-FIX01)* are the update.

## Verdict: M1 offline gate = **PASS**

* The U.2 / S5 defect is corrected and the correction is proved by tests that fail without it (below).
* Every §H.1 row has a passing test on Windows and on Linux (WSL); the four missing tests now exist.
* §H.3: 40 distinct mutants — 38 detected, 2 equivalent (explained and re-verified), 0 undetected.
* Not part of this gate and unchanged: B1 (same-attempt revocation of grants / keys / publishers), B2 (two consecutive `user` messages on a real server), any live run, BENCH-00, KNOW-BENCH.
* Classified, not blocking: 28 `bOps.AgentFramework.Tests` canary / benchmark tests cannot run on Linux (Windows-only fixture, see *Gate runs*). None is a §H.1 test; the §H.1 wire test (`TheRealAdapterSendsTheSameCatalogueNativelyAndInFallback…`) runs and passes on Linux.

## The defect and its correction *(T8-FIX01)*

`AgentRunner.WrapToolOutput` (`src/core/bOps.Runtime/AgentRunner.cs:5065`) neutralized the tool-output, evidence-limitations, grounding and evidence-index delimiters but not the knowledge-index markers.
ADR-0057 §U.2 / TM4 / T6 and the amended S5 require `[knowledge-index`, `[/knowledge-index`, `[knowledge-index-control`, `[/knowledge-index-control` to be neutralized case-insensitively in tool output.
A tool (or a log line it returned) could therefore carry a block that looks like the host's index.

Correction (one method, plus four constants at `AgentRunner.cs:68`–`:71`): four `Replace(..., "«redacted-delimiter»", StringComparison.OrdinalIgnoreCase)` after the existing eight.

* Exact markers checked first against `KnowledgeIndexRenderer` / `KnowledgeIndexPublication.RenderControl`: `[knowledge-index v1 sha256=`, `[/knowledge-index]`, `[knowledge-index-control v1]`, `[/knowledge-index-control]`; the four prefixes cover them in any case.
* Same replacement token as the other delimiters: the result contains no `[`, so a second pass changes nothing (tested) and removing a marker cannot glue its neighbours into a new one (tested).
* Ordinary diagnostic text is untouched: only the matched prefix is replaced (exact-output test).
* Authentic indices are not affected: the host's data turn is a protected turn built outside `WrapToolOutput` and its hash is checked unchanged (`KnowledgeIndexGovernanceTests`).
* Callers inherit it: step observations, replan/final triggers, `DelegationRoleData` and `EvidenceRead` all use this one method.

## Identity

| | |
|---|---|
| Worktree / branch | `C:\source\repos\babbubba\bOps-knowbench01a` / `codex/know-bench-01a` |
| HEAD | `d0dcfbefe2c7cd8af6e643c39459eaecc72b7d89` plus the uncommitted T8-FIX01 changes (the pre-existing untracked `docs/benchmarks/bench00-maf-compatibility-report.md` was not touched) |
| Changed by T8-FIX01 | `src/core/bOps.Runtime/AgentRunner.cs` (the fix); tests: `ToolOutputKnowledgeIndexMarkerTests`, `KnowledgeIndexGovernanceTests`, `KnowledgeGoldenRequestTests` + `Golden/d3da032/*`, `KnowledgeIndexDataTurnBoundaryTests`, one assertion block in `KnowledgeResumeTests`, an optional `output` argument on the test double `RecordingReadTool` / `ComposeCanary`, one `<None>` item in `bOps.AgentFramework.Tests.csproj`; this report, ADR-0057 §E, `HANDOFF.md` |
| Consolidation commits | `c3e395f`, `95122fa`, `37ef356`, `e46ac4d`, `d0dcfbe` (on top of the M1 slices `bfe624f` `93f5e3d` `2e70927` `ac935eb` `eba1cff` `a74e1b6`) |
| SDK | .NET 10.0.401 (Windows and WSL Ubuntu-24.04) |
| MAF (resolved) | `Microsoft.Agents.AI` 1.24.0, `Microsoft.Agents.AI.Abstractions` 1.24.0, `Microsoft.Extensions.AI` 10.10.0, `Microsoft.Extensions.AI.Abstractions` 10.10.1 |
| Gate files untouched | `git diff d3da032 a74e1b6` (M1 range) touches no `ContextGate`, `ToolRegistry`, policy, approval, journal, provider or `PackageReference`; T8-FIX01 touches none of them either (`AgentRunner.cs` only, the component ADR-0057 §E T6 names) |
| Post-M1 provider change | `37ef356` (`Agent:MaxOutputTokens`): outside the M1 range, recorded as separate |
| Planning / replan code | `CreatePlanAsync`, `ReplanAsync`, `BuildPlanningSystemPrompt` unchanged since `d3da032`; now also proved by bytes (row 9) |

## Gate runs *(T8-FIX01, after the fix)*

| Project | Windows (Debug) | Linux / WSL (Debug, copy of the tree) |
|---|---|---|
| `bOps.Knowledge.Tests` | 308 passed | 308 passed |
| `bOps.Architecture.Tests` | 210 passed (+3) | 208 passed, 2 skipped (Windows dump tool) |
| `bOps.Audit.Tests` | 31 passed | 31 passed |
| `bOps.AgentFramework.Tests` | 456 passed, 8 ignored (env-gated live/operator tools) (+8) | 428 passed, 8 skipped, **28 failed** (unchanged) |
| `bOps.Runtime.Tests` | 2206 passed (+20), **2 failed** (unchanged) | 2206 passed, **2 failed** (unchanged) |
| `bOps.Cli.Tests` | 189 passed | 189 passed |

* All 31 new test cases passed on both platforms, including the golden comparison (the checked-in bytes are identical on Windows and Linux).
* **Historical failures (not regressions):** the 2 `bOps.Runtime.Tests` failures (`ToolCallProtocolTests.TheToolCatalog_…`, `ModelFailureEndToEndTests.ATransientProviderFailure_…`) fail identically on a pristine `d3da032` archive, i.e. before M1.
* **Platform failures, Linux:** the 28 `bOps.AgentFramework.Tests` failures are the same 28 as in the first run. They come from `KnowledgeProcess.ComposeCanary` throwing `PlatformNotSupportedException: The diagnostic canary requires Windows`
  (canary / benchmark / replay / discovery-investigation tests, e.g. `KnowledgeDiscoveryInvestigationTests`, `KnowledgeBenchmark*`, `KnowledgeEventsCorpusTests`). They are failures, not skips, and are not masked.
  They are not §H.1 tests: every §H.1 row is covered by tests that run on Linux (matrix). They do not block the gate; they mean the index-through-the-canary investigation tests have no Linux evidence, and they should be made explicit skips by a separate change (not done here, as instructed).
* **Not executable:** full-solution restore on Linux stops at `NU1902` (`MessagePack` 2.5.302) in the two `AppHost` projects only; the six gate projects restore and run. Not touched here. The 8 live tests were not run by design.

## Conformance matrix (§H.1)

PASS = an existing test asserts the invariant and passes on both platforms. "New" = added by T8-FIX01.

| # | Requirement | Implementation | Test (observed) | Result |
|---|---|---|---|---|
| 1 | Catalogue, direct refs, stable hash | `KnowledgeReadBridge.DescribeIndex`, `KnowledgeIndexRenderer` | `KnowledgeIndexBridgeTests.TheIndexIsDeterministic_…`, `KnowledgeIndexRunnerTests.APublishedIndexReaches…`, `…AResumedAttemptRebuilds…` | PASS |
| 2 | Isolation | candidates from the pin | `…TenantsAndTasksSeeOnlyTheirOwnAuthorizedCatalogue_EvenWhenRunConcurrently` (4 tasks, 2 tenants; deviation from "8 tasks / HTTP-body search" accepted in the first run), `…TwoTenantsSeeOnlyTheirOwnEntries…` | PASS |
| 2 | Delegated role gets no index | no session for a delegated run (`RunCoreAsync`: `delegation is null && knowledgeHost is not null`) | **new** `KnowledgeIndexGovernanceTests.ADelegatedRole_ReceivesNoIndexNoControlAndNoFunction_…`: real process, pack and runner; control task publishes the index; the delegated role's three composed model requests (planning, step, final) contain no `knowledge-index` text, no pack description, no `index`/Knowledge provenance turn, no function; the host is not asked; no `KnowledgeIndexAuditEvent`/`KnowledgeReadAuditEvent` for it. Also `KnowledgeHostContractTests.ADelegatedRole_NeverReceivesKnowledge_…` | PASS |
| 2b | Revocation (provider, tenant, snapshot) | `currentAuthority()` in `DescribeIndex` and `HandleAsync` | `…AWithdrawalTheRuntimeCanObserve…`, `…ARevocationObservedAtTheNextRefresh…` | PASS |
| 2c | Resume after revocation | `AttachKnowledgeAsync` | `KnowledgeResumeTests.APackRevokedOrUnentitledAfterThePin_IsNotResumed…` (5 causes): nothing runs, zero model requests, and **new** explicit assertion: the first attempt's index events exist (not vacuous) and none belongs to the resumed attempt | PASS |
| 3 | Wire and gate path | `BuildStepRequest`, `MafGovernedTurnBroker` + `BopsProvider` | `…TheRealAdapterSendsTheSameCatalogueNativelyAndInFallback…` (real LlamaCpp adapter, in-memory); **new** `ContextGateRejection is null` for every model call of the injection test | PASS |
| 4 | Direct read by ref | bridge `TryParse`, ref resolution | `ARefReadsTheItemInOneCall…`, `ARefIsAPointerIntoThePin…` | PASS |
| 5 | No obligation, anti-loop | `ReadsRemaining: not 0` | `…APublishedIndexReaches…`, `…ASpentReadBudgetWithdrawsTheFunction…` | PASS |
| 6 | Malicious descriptions / injection | `KnowledgeTextPolicy` U1–U6; `WrapToolOutput` | `KnowledgeTextPolicyTests`, `…AMaliciousButAdmittedDescriptionIsOnlyData…`; **new** `KnowledgeIndexGovernanceTests.AModelThatObeysInjectedToolOutput_ProposesAnUnofferedTool_…` (forged-index and plain-injection payloads): a tool returns text that looks like a signed index with a granted tool; the scripted model obeys and proposes `ops.wipe_disk` and a read of the forged ref `K9.R1`. Verified on the real runner: the tool turn is a framed `Tool` turn with every marker replaced and the diagnostic text intact; exactly one authentic index turn, byte-identical before and after; one control statement; system prompt and offered tools equal to a control run with harmless output; the unoffered tool is `Denied`/`UnknownTool` and never executed; the forged ref reads nothing; one `KnowledgeIndex` event with the host's hash; gate refusals none; task completes with the goal unchanged | PASS |
| 7 | Native / fallback | C and D outside tool manifests | `…NativeAndFallbackCarry…` | PASS |
| 8 | Governance (arch) | — | `KnowledgeIndexContractTests`, `AgentFrameworkBoundaryTests`, `KnowledgePackBoundaryTests`, `KnowledgeReadRuntimeBoundaryTests`; **new** `KnowledgeIndexDataTurnBoundaryTests` (3 tests): `DataTurn` appears in the runtime in four closed places only (pass-through as the protected turn, provider id in the audit, the verification, the record); its text is read in exactly one place (`KnowledgeIndexPublication.Verify`: a length bound and the input of the SHA-256) and the local is used for nothing else; the hash is compared twice, never stored, logged or formatted; `IndexHash` / `ContentHash` appear only as audit fields, the audit-dedup key and the two comparisons — never in model-visible text | PASS |
| 9 | Byte-identity | planning/replan code unchanged | **new** `KnowledgeGoldenRequestTests` (3 scenarios + reproducibility): planning, replan and every step of a task without a host (`legacy-nohost`) and with a host that binds nothing (`legacy-notbound`), and planning + replan with a bound pack (`planning-replan-bound`), compared character by character with the bytes `d3da032` produced (checked in under `Golden/d3da032/`; produced by running the same test file on a `git archive d3da032` copy with `BOPS_GOLDEN_CAPTURE_DIR`, README there). Fixed task id, scripted model, no time, credential, network, random value or OS name (tool platform lists masked); identical across two baseline processes and across Windows and Linux | PASS |
| 10 | Many skills | selector / renderer | `TwelveSkillsListEightInStableOrder…`, `…AContextOverflowRetryUsesTheAggressiveRendering…`, `EqualScoresOrderRefsNumerically…` | PASS |
| 11 | Recovery | `RenderDiscoverPage` | `DiscoverListsPagesQueriesAndResources…` | PASS |
| 12 | Audit and provenance | `KnowledgeIndexPublication.Verify`, `RefreshKnowledgeIndexAsync` | `ADataTurnThatDoesNotMatchTheBinding…` (22 forgeries), `…AnAuditFailureStopsTheTask…`, `…ASessionThatFailsWhileDescribing…` | PASS |
| U.2 | Marker neutralization | `WrapToolOutput` (fixed) and the bridge `Neutralize` | **new** `ToolOutputKnowledgeIndexMarkerTests` (20 cases): each of the four exact markers, each in four case variants, embedded in diagnostic text, exact deterministic output, idempotence per prefix, no marker assembled across a replacement, benign look-alikes untouched, other delimiters still replaced; **new** `…TheMarkersTheHostActuallyWrites_AreNeutralized…` (the real rendered data turn and control statement, also upper-cased, echoed by a tool); bridge: `…ServedContentCannotForgeAnIndexMarker` | PASS |

## Mutation check (§H.3)

Method: copy of the tree outside the worktree (`C:\t8mut`, synced with the T8-FIX01 tree for the new mutants), one `perl`-patched source mutation at a time, then the pertinent projects (first run: Knowledge + AgentFramework + Architecture + Audit; T8-FIX01 mutants: Runtime filtered to the marker tests + AgentFramework + Architecture), source restored from the worktree after each. Counts are failing tests. The new harness carries no static state between parallel tests.

### Reconciled count

| | Mutants |
|---|---|
| Distinct mutants run | **40** (17 §H.3 classes: 29 first-run variants, of which M01b, M10a/b and 11 U3 variants are sub-mutants; plus 11 new) |
| Detected | **38** |
| Equivalent (no observable behaviour change), re-verified | **2**: #2 and #11-Cs |
| Undetected, non-equivalent | **0** |
| Harness anomaly | **1** run: original M07 (below), resolved by a deterministic re-implementation |

### First run (unchanged results), with the T8-FIX01 re-checks

| § | Mutation | Detected | By (first test) |
|---|---|---|---|
| 1 | render without `currentAuthority()` | yes (3) | `…AWithdrawalTheRuntimeCanObserve…` |
| 1b | read without `currentAuthority()` (extra) | yes (8) | |
| 2 | candidates from the source, not the pin | **no (0): equivalent** | see *Equivalent mutants* |
| 3 | D in `SystemPrompt` | yes (5) | `…ASpentReadBudget…`, adapter test |
| 4 | D after the goal | yes (10) | adapter test |
| 5 | ref from selection order | yes (3) | `TheIndexIsDeterministic…` |
| 6 | string tie-break | yes (5) | `EqualScoresOrderRefsNumericallySoK2PrecedesK10` |
| 7 | function offered in replan | yes (1, first run) — **anomaly**: the second full-suite run gave 0. **Re-run as M07v2: yes (2) three times out of three** | `PlanningAndReplanReceiveNoIndex…` and, new, `KnowledgeGoldenRequestTests` |
| 8 | function offered at budget 0 | yes (4) | `…ASpentReadBudget…` |
| 9 | offer depends on authority | yes (26) | forgery theory |
| 10a/10b | bridge markers not case-insensitive / not neutralized | yes (1 / 1) | `…ServedContentCannotForgeAnIndexMarker` |
| 10c | `WrapToolOutput` markers | **was not realizable (defect). Now: N01–N05 below, all detected** | |
| 11 | one U3 exclusion removed, per category | Cc 10, Cf 28, Co 2, Cn 3, Zl 2, Zp 1, other Zs 7, Mn 11, Mc 1, Me 1: all detected. **Cs: 0, equivalent** | `KnowledgeTextPolicyTests` |
| 12 | U2 (NFC) removed | yes (4) | `ATextThatIsNotInFormCIsRefusedAndNeverNormalized` |
| 13 | audit after the model call | yes (2) | `…AnAuditFailureStopsTheTask…` |
| 14 | H12 verification removed | yes (22) | forgery theory |
| 15 | ref resolved before authority | yes (3) | `…TheBudgetAndTheAuthority_StillDecideBeforeARefIsResolved` |
| 16 | D in replan | yes (1, first run; static field in the harness) — **re-run as M16v2: yes (3)** | `PlanningAndReplanReceiveNoIndex…`, golden, `KnowledgeIndexDataTurnBoundaryTests` |
| 17 | view reused across logical calls | yes (2) | `…ARevocationObservedAtTheNextRefresh…` |

### New mutants *(T8-FIX01)*

| ID | Mutation | Detected | By |
|---|---|---|---|
| N01 | all four index replacements made identity (the original defect) | yes (21) | `ToolOutputKnowledgeIndexMarkerTests` (19), `KnowledgeIndexGovernanceTests` (forged-index, real markers) |
| N02 | the four replacements case-sensitive | yes (7) | `EveryMarker_IsNeutralizedInAnyCase`, exact-output, governance (2) |
| N03 | the two `-control` replacements removed (the index prefixes still remove the marker, leaving the `-control` residue) | yes (1) | `TheResultIsExactlyDeterministic_…` only. Security-equivalent, behaviour-visible (residue text); it pins the four replacements the ADR lists |
| N04 | the two closing markers not replaced | yes (12) | marker tests, governance |
| N05 | the opening index marker not replaced | yes (9) | marker tests, governance |
| P01 | knowledge control statement appended to the planning prompt | yes (8) | `KnowledgeGoldenRequestTests` and several existing planning/boundary tests |
| P02 | one extra character in the replan prompt | yes (3) | `KnowledgeGoldenRequestTests` only (no other test pins the replan prompt byte for byte) |
| M07v2 | function offered in replan (per-task state, no shared static) | yes (2) ×3 runs | `PlanningAndReplanReceiveNoIndex…`, golden |
| M16v2 | data turn in replan (per-task state) | yes (3) | same + `KnowledgeIndexDataTurnBoundaryTests` |
| D01 | runner reads the data turn's text to decide | yes (1) | `…TheDataTurn_IsMentionedInTheRuntimeOnlyWhere…` |
| D02 | verification uses the text beyond length and hash | yes (1) | `…TheTextOfTheTurn_IsReadInExactlyOnePlace…` |
| D03 | index hash appended to the system prompt | yes (3) | `…TheIndexHash_IsOnlyAnAuditField…`, two runner tests |
| R01 | delegated run is given a knowledge session | yes (3) | new `ADelegatedRole_ReceivesNoIndex…`, `KnowledgeHostContractTests…`, an architecture test |

The regression check for planning / replan is real: P02 changes a single character of the replan system prompt and only the golden test sees it.

### Equivalent mutants (re-verified in T8-FIX01)

* **#2 (candidates from the source, not the pin).** `CreateCoreAsync` calls `VerifyOffered(offered, pin)` before `BuildCandidates`: it rejects duplicates and requires the offered names to equal `pin.Skills.Keys` as a set; `BuildCandidates` then looks up `pin.Skills[name]` for each name. Iterating the offered names instead of the pinned ones yields the same set in the same ordinal order for every input that reaches it, and `VerifyPackAsync` re-checks `offered.Count == pin.Skills.Count` and every hash. A source that differs from the pin fails closed before any candidate exists. No observable output differs.
* **#11 category Cs (surrogate).** `CheckCodePoints` decodes with `Rune.DecodeFromUtf16` and returns `MalformedUtf16` (U1) for any unpaired surrogate before the category predicate is called, so `IsAllowedCategory` only ever sees valid scalar values. Probe over all 1,112,064 valid scalar values: none has `UnicodeCategory.Surrogate` (29 categories seen). The added `case` is unreachable.

No test was added to catch either: they change no observable behaviour.

### Harness anomaly (M07)

The first-run M07 and M16 mutants carried the session's tool manifest / data turn in a `static` field that parallel tests overwrite, so the replan of one test could be offered another test's value; the same mutant failed 1 test in one full-suite run and 0 in another. The mutants were re-implemented keyed by task id (`M07v2`, `M16v2`): M07v2 failed the same 2 tests in 3 of 3 runs. The product code has no such field; only the throw-away mutation copy did.

## Windows / Linux differences

* Same results for Knowledge, Audit, Cli, Runtime. Architecture skips 2 Windows-only tests on Linux.
* AgentFramework: 28 Linux failures from the Windows-only canary fixture (listed above); none in `KnowledgeIndex*`, the golden, governance or resume tests. ICU present on the WSL distro, `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` unset, so I5 held for the tested characters. `bOps.Runtime.Tests` runs with invariant globalization by design, so the marker tests do not exercise a Turkish culture (`OrdinalIgnoreCase` is culture-independent; the bridge's `tr-TR` test runs in AgentFramework).

## Residual limits

B1 (no same-attempt revocation of grants / keys / publishers) and B2 (two consecutive `user` messages on a real server) remain open; the live gate was not run. This report does not complete B1, B2, BENCH-00, KNOW-BENCH or any other gate.
The 28 Linux canary failures remain failures (not skips); the Linux run of the discovery-investigation tests is missing.
