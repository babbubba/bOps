# KNOW-01B — Governed knowledge read and durable resume

Base: KNOW-01A docs commit `b027459` (MAF `Microsoft.Agents.AI` pinned `[1.24.0]`, unchanged; no NuGet upgrade). Decision: [ADR-0056](adr/0056-governed-knowledge-read-and-durable-resume.md)
(Proposed), continuing [ADR-0055](adr/0055-trusted-knowledge-packs-and-provider-isolation.md). Offline only: scripted model, temporary data, no benchmark, no remote
call, no subagent, nothing composed by a production host, no configuration key.

## What exists

| Piece | Where | Role |
|---|---|---|
| Control function | `KnowledgeReadBridge.Manifest` (`runtime.knowledge_read`, `Risk = Read`) | Offered by the runner beside the step's tool view, never in the `ToolRegistry` (it reserves `runtime.`), never plannable, never completes a step. Operations `discover`, `load`, `read_resource`, served by the framework's `AgentSkillsProvider` invoked by bOps; no new parser or engine |
| Runtime contracts | `bOps.Runtime/KnowledgeRead.cs`: `IKnowledgeTaskHost`, `IKnowledgeReadSession`, `KnowledgeReadAuthority`, `KnowledgeReadOutcome`, `KnowledgeTaskOpening` | The only thing `bOps.Runtime` knows. It still references only `bOps.Abstractions` and names no pack, tool or skills function (architecture-tested) |
| Runner wiring | `AgentRunner` (`knowledgeHost` ctor argument, `AttachKnowledgeAsync`, the hook at the top of `ResolveStepEvidenceReadsAsync`) | Opens the session before the first save (new task) or before the attempt runs (resume); shows each reply to the session first; serves, answers every call id, then makes the next model call through the same governed path; disposes the session when the attempt ends. A delegated role never gets one |
| Pin in the task | `TaskState.KnowledgeBinding` (`TaskKnowledgeBinding`, additive in `bOps.Abstractions`) | Authorising identity, tenant, pack id and version, kind, publisher, key, manifest hash, every payload-file hash, snapshot id. Written from the host's admission in the same first save as the task, before any model call; never from a model value |
| Host | `KnowledgeTaskHost`, `IKnowledgeTaskPolicy` (internal to `bOps.AgentFramework`) | Tenant from the host's authority for the acting identity; pack from host policy; admission (provider, grant, signature, hashes, snapshot) with one audit event; on resume the pin is the expected content |
| Current authority | `KnowledgeAdmissionService.Reauthorize`, `KnowledgeBridgeAdmission(authorizeEveryRead)` | Asked before every read: registered provider for the tenant, exact grant of pack, version and kind, trust store (unknown or revoked key, revoked publisher), then the snapshot's integrity. A pin is never the answer |
| Terminal state | `TaskTerminalKind.KnowledgeUnavailable` | A bound task whose knowledge cannot be admitted again ends `Failed` before planning, with the cause code on the failure step and in the audit trail |
| Provenance | `ContextProvenance.Operation/Item/ContentHash` (optional), `KnowledgeReadAuditEvent.CauseCode`, `KnowledgeReadResultCode.NotAuthorized/SnapshotUnavailable` | Origin by signed kind, pack, version, hash, tenant and provider were already there; now also operation, item and content hash. Model-call audit grouping ignores the three read-level members, so its schema is unchanged |

`ContextGate`, `BopsContextProvider`, `MafGovernedTurn`, the policy/approval/journal/fencing paths and the `ToolRegistry` are unchanged.

## Behaviour that was decided

- **Reads.** Order inside the session: unsupported or framework name → not offered → malformed (closed argument set; no tenant, pack or version argument exists) → **current authority and snapshot** → read budget → pinned skill → pinned resource → hash of what the framework returned against the pin → size. The first runtime control call of a reply wins (ADR-0038); every other call id is answered `Not executed`; the planned step's expected tool is not completed; the next model call is the ordinary governed one.
- **Resume.** `ExecuteAcquiredResumeAsync` re-runs the whole admission with the stored pin as the expected content, for the tenant the *resuming* actor has now. It never resolves "latest", admits a different pack, changes tenant or accepts changed content.
- **Revoked pack or unavailable snapshot: fail closed.** Cause → `KnowledgeAdmissionCode`: revoked publisher `PublisherRevoked`, revoked key `KeyRevoked`, unknown key `UnknownKey`, withdrawn grant or changed kind `NotEntitled`, another tenant `TenantNotAllowed`, republished or altered content `PinMismatch`, snapshot altered on disk `SnapshotTampered`, source no longer supplying the pack `SourceUnavailable`, provider unregistered `ProviderNotAdmitted`, no knowledge host composed or a host failure `NotAdmitted`. Each is audited (admission event, then the terminal lifecycle event with `KnowledgeUnavailable`) and nothing runs: no model call, no tool. The pin is kept, so fixing the authority makes the same task resumable again.
- **Snapshot gone but the signed pack still supplied:** the verified bytes are written again to the same content-addressed id and compared with the pin; the resume proceeds on identical content. This is availability of the verified bytes, not a second admission of different ones.
- **A running task** that loses its authority (provider unregistered, snapshot altered) gets `NotAuthorized` / `SnapshotUnavailable` for the next read, with the cause in the audit and nothing of the pack in the reply; repeated attempts hit the rejection bound and end the task as a control-protocol failure.

## Verification matrix

| # | Requirement | Result | Evidence |
|---|---|---|---|
| 1 | `runtime.knowledge_read` Read, usable by the real `AgentRunner`: discover, load, read_resource authorised | **PASS** | `KnowledgeRunnerIntegrationTests.DiscoverLoadAndRead…` (6 scripted model calls, real broker and MAF turn path, SQLite store) |
| 2 | Tool outside plan or manifest; framework and script functions | **PASS** | `AFrameworkSkillsFunctionOrAScriptRunner…` (3 names), `AnotherRuntimeFunctionName…`, `AToolOutsideThePlan…`, `APlanThatNamesTheKnowledgeFunction…`; `NoScriptCanRunOrBeOffered…` |
| 3 | Model cannot gain privileges, name a tenant, pack or version | **PASS** | `ArgumentsTheModelInvents…` (Malformed ×3, UnknownSkill ×2; source opened once; the granted second pack unreachable) |
| 4 | Pack not admitted to the task / tenant without grant | **PASS** | `ATenantWithoutAGrant…` (refused before the source is read or the model called, nothing bound, cause audited) |
| 5 | Alteration or revocation while running; at resume | **PASS** | `ASnapshotAlteredAfterThePin…`, `AProviderWithdrawnWhileTheTaskRuns…`, `AResumedAttemptThatLosesItsAuthorityMidWay…`; resume theory (publisher, key, unknown key, grant, kind), `APackRepublished…`, `ASnapshotAlteredOnDisk…` |
| 6 | Different tenant | **PASS** | `AResumeByAnActorOfAnotherTenant…` (cause `TenantNotAllowed`, other tenant's source never opened); `TwoTenantsRunningTheSamePackIdAndVersionConcurrently…` (8 tasks) |
| 7 | First turn, persistence, resume (admission → read → persist → destroy → rebuild → read) | **PASS** | `ATaskIsAdmittedPinnedReadPersistedDestroyedRebuiltAndReadsTheSameVerifiedSnapshot`: new registry, service, host, store and runner over the same disk; version 2.0.0 existed and was never opened; one snapshot directory |
| 8 | Snapshot absent after resume | **PASS** | `ASnapshotThatIsGoneAndASourceThatNoLongerSupplies…` (`SourceUnavailable`), `…StillSupplied…RebuiltAsTheSame…` (same id) |
| 9 | Two tool calls in the same turn; `UnexecutedToolCalls` unchanged | **PASS** | `TwoCallsInOneReply…` (both orders), `TwoOperationalCallsAfterAKnowledgeRead…` |
| 10 | No script execution; `<available_scripts />` grants nothing | **PASS** | `NoScriptCanRunOrBeOffered…`, architecture tests (no runner, no script extension, one file source) |
| 11 | Provenance and audit persisted; no content | **PASS** | assertions on turns and events in the end-to-end test; real `JsonLinesAuditSink` file with a verified hash chain and no resource text; `JsonLinesAuditSinkTests` (cause, read-level provenance, terminal kind) |
| 12 | No hidden model calls; all through the broker | **PASS** | `EveryModelCallGoesThroughTheBroker…` (provider requests = MAF composed turns = audited model calls) |
| 13 | Host contract: host failure, wrong manifest, content swap, dropped pin, disposal, delegated role | **PASS** | `KnowledgeHostContractTests` (9) |
| 14 | Legacy task without a pin keeps today's behaviour | **PASS** | `ALegacyTask…`, `ALegacyTaskResumedUnderAHostThatNowBindsPacks…` (source never opened) |
| 15 | Mutation checks | **PASS** | seven deliberate defects (resume without expected pin, no per-read authority, any binding accepted, `NotBound` accepted for a pinned task, tenant check removed, manifest check removed, delegated roles allowed) each failed at least one test |
| 16 | MAF 1.24.0 real APIs, no `FunctionInvokingChatClient`, tool surface unchanged | **PASS** | unchanged 01A/01B architecture tests plus `KnowledgeReadRuntimeBoundaryTests` |
| 17 | ContextGate not weakened | **PASS** | no change to it; no gate rejection in any run |
| 18 | Linux | **PASS** | green from a `git archive` copy in WSL Ubuntu-24.04 |
| 19 | Web UI label for the new terminal kind | **NOT DETERMINED** | edited (`models.ts`, `en.ts`, `it.ts`); the Angular build and unit tests were not run |

The completed targeted suites were green: `Api.Tests` **512/512** and `Cli.Tests` **124/124**. This report does not claim host integration is complete; production host composition remains a prerequisite below.

## Residual risks

- **Single pack per task.** The binding holds one pack. Several packs, Experience search and a pack picker are not designed here.
- **The host policy is an input.** How a Coordinator or the Local Agent chooses the pack for a task, the tenant authority, grants source, trust-store location and the engine version given to the verifier are host work and are not built.
- **Per-attempt read budget.** The reads of one attempt share one budget; a resume starts a new one. Knowledge turns are context of one logical call only, so a later step re-reads what it needs.
- **Source must be reachable on resume.** Admission always reads the source (it is where revocation lives). A source outage fails the resume closed; it does not fall back to the stored snapshot.
- **Rejection bound.** A model that keeps asking after revocation ends the task through the existing control-protocol failure; it is not a distinct terminal kind.
- **A refusal before the model's first call** audits the admission but there is no knowledge-read event, because no read was asked.
- **Immutability is still by verification** (S8): a process with the host's own privileges can rewrite a snapshot; it is detected before anything is served.
- **No live trust-store or grant reload.** A new `KnowledgeAdmissionService` is what changes authority; the per-read check sees it only through the instances the host holds.
- **Windows junction instead of symbolic link** for link tests (KNOW-01A); CI remains authoritative.

## Prerequisites for production activation

1. A deliberate host change that composes `KnowledgeTaskHost` (and updates `KnowledgePackBoundaryTests` and `KnowledgeReadRuntimeBoundaryTests` with it) — today none exists and no key enables it.
2. A tenant authority per host (Coordinator principal; `local` for the Local Agent), a grants source, an operator-owned trust store and its revocation workflow, the registered pack source(s) and the snapshot directory with operator-only permissions.
3. Publisher signing tooling and pack distribution; a snapshot retention policy.
4. A decision on how a task's pack is chosen (policy), who may change it, and what the operator sees when a task ends `KnowledgeUnavailable` (API and UI exposure of the cause).
5. CI on Windows and Linux; ADR-0054, 0055 and 0056 accepted; the AF-TURN equivalence suite accepted for the MAF turn path the knowledge flow rides on.
