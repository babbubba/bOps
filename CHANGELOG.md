# Changelog

All notable changes to bOps are documented here. Versions follow Semantic Versioning.

## [Unreleased]

### Released

- `v1.2.0-preview.8` (commit `5cd9046`): V1.2 multi-agent delegation preview. The release workflow passed on Windows and Linux (run
  `35516050493`) with attested runtime archives, the `bOps.Abstractions` `1.2.0-preview.8` package, SBOMs and checksums, published as a
  GitHub pre-release. Not a NuGet publication.
- `v1.1.0-preview.2` (commit `c81ffab`): first tag whose release workflow passed on Windows and
  Linux, with attested runtime archives, SDK package, SBOMs and checksums (run `35383901325`).

### Fixed

- The UI no longer stalls on a running task with "HTTP 429". The task watcher retries transient failures (429, 502–504, no answer)
  with exponential backoff and `Retry-After` instead of stopping at the first one, and polls every 2 s instead of 500 ms. The API's
  rate limiter now keeps the strict 120-token bucket for mutations and unauthenticated calls, and gives authenticated `GET`/`HEAD`
  reads their own 600-token bucket (300/min), so the UI's read-only polling cannot exhaust the budget that protects writes.

### Added

- V1.3-C advanced process diagnostics (ADR-0034): `process.inspect` additionally reports `parentPid`, `executablePath`,
  `commandLine`, `user`, `privateMemoryMb`, `virtualMemoryMb`, `handleOrFdCount`, `cpuTotalMs`, `ioReadBytes` and
  `ioWriteBytes`, each nullable, so one counter this identity may not read costs its own field and not the observation. Three
  read-only tools join it on both systems: `process.metrics` (two samples over 200–5000 ms, default 500; host-normalized
  `cpuPercent`, memory, threads, handles or file descriptors, I/O and page-fault rates, `partial` when anything was unreadable and
  `exists:false, partial:true` when the process exits mid-sample), `process.tree` (depth-first from a `rootPid` or from every
  visible root, `maxDepth` default 4/max 16, `limit` default 200/max 2000, with `skipped`, `truncated` and `complete`, and a
  missing root reported as `rootFound:false` rather than an empty machine) and `process.modules` (unique by path, `limit` default
  200/max 1000, `maxOutputBytes` default 32768/max 131072, with a refused read reported as `status:"unavailable"` rather than an
  empty list). Windows reads `Win32_Process` through `System.Management` plus three kernel32 counters; Linux reads `/proc`
  directly and resolves UIDs from the local `/etc/passwd` only. No `process.start`, no shell, and no environment variables
  anywhere — both remain permanent non-goals, now also enforced by an architecture test over the whole source tree. See
  [`docs/process-diagnostics.md`](docs/process-diagnostics.md).
- V1.3-B Docker image, build and volume management (ADR-0033): nine typed tools next to the existing eight, which are unchanged:
  `docker.image.inspect`, `docker.volumes` and `docker.volume.inspect` (read; a missing image or volume is `exists: false`, and
  volumes never show their mount point), `docker.image.pull`, `docker.image.tag` and `docker.volume.create` (verified), and
  `docker.image.remove`, `docker.volume.remove` and `docker.build` (High risk, always approved, verified). A build runs only from
  directories listed in `Docker:Build:Contexts` (empty by default, and the tool is hidden until it is set), refuses links and a
  `.dockerignore`, and bounds files and bytes while the archive is written. Nothing is forced or pruned, there are no build
  arguments or registry credentials, and an unreachable daemon is a failed result in every Docker tool. See
  [`docs/docker-management.md`](docs/docker-management.md).
- V1.3-D sockets, routes and network diagnostics (ADR-0035): seven new read-only tools contributed by a new
  `bOps.Packages.Network.Native.{Core,Windows,Linux}` package family, registered alongside the existing, unchanged
  `bOps.Packages.Network`. `network.sockets` maps TCP/UDP sockets to owning PID and process name (`protocol`, `state`, `pid`,
  `localPort` filters, `limit` default 500/max 5000; UDP rows report no remote endpoint or state; a PID this identity could not
  map stays `null` with `pidMappingComplete:false`). `network.routes` and `network.neighbors` report the full routing table and
  ARP/NDP cache (`network.route` is unchanged). `network.interface_stats` samples every interface twice (`sampleMilliseconds`
  100–5000, default 500) and reports per-second byte/packet/error/drop rates, link speed and operational status, with an
  unsupported counter `null` rather than zero. `network.dns_query` resolves A/AAAA/PTR via the system resolver or, with an
  explicit `server`, a minimal typed UDP DNS client (no raw query type, class or option). `network.traceroute` traces a path
  with `Ping` and an increasing TTL/hop-limit — no `tracert`/`traceroute` executable. `network.ntp_probe` sends one SNTP
  request and reports offset, round-trip time, stratum and version, rejecting an unsynchronized or kiss-of-death reply as
  `valid:false` rather than a trustworthy-looking offset. Windows P/Invokes `iphlpapi.dll` directly; Linux reads `/proc`
  directly and runs exactly two fixed, argument-listed `ip -j route|neighbor show[/-6]` invocations, no shell, no
  model-supplied argument. See [`docs/network-diagnostics.md`](docs/network-diagnostics.md).
- V1.3-A `system.events`: one read-only tool over the Windows Event Log (`EventLogReader`) and journald (`journalctl` run
  directly, with fixed switches and values as separate arguments), with the same arguments and result on both systems: a
  time window, minimum severity, source, event id, channel and message text, newest first, bounded in events, bytes and
  scanned records. The result says whether it is complete, a source that cannot be read (permissions, a missing channel or
  `journalctl`, a timeout) is reported and never mistaken for an empty log, and event messages stay out of audit and telemetry
  (aggregate audit summary only). ADR-0032; `docs/system-events.md`.
- Multilingual UI (Italian and English): a dependency-free translation service with two typed catalogues (`en.ts` is the shape, `it.ts`
  must satisfy it), a `t` pipe, a visible `EN | IT` switch that re-renders without a reload and is remembered in `localStorage`
  (`<html lang>` follows it), plural rules, dates, numbers, sizes and durations in the chosen language, and enum labels keyed by name.
  English stays the default. Every screen, including Delegations, is translated; `npm run lint:i18n` fails CI when a template holds
  text outside the translation. API data and the API's own messages are not translated. No API, SDK or persistence change.
- V1.1 preview contracts for Capability, Evidence, Finding, SkillReport and immutable, canonically
  hashed ExecutionPlan artifacts (ADR-0023).
- Governed ExecutionPlan orchestration through the existing policy, approval, verification and
  audit pipeline (ADR-0024).
- Executable `ICapability`/`ISkillProvider` contracts, a node-scoped Skill registry and
  invocation-scoped same-package Read-only `IToolInvoker` evidence collection (ADR-0025).
- Exact contextual Skill policy, correlated Skill-run audit events and an end-to-end signed sample
  Skill covering evidence, findings, hash-bound approval, action and independent verification.
- Persistent V1.1 plan/task tracking and a stable `agentic/00-bootstrap.md` entry point.
- Bounded cross-platform `system.apps` and `system.devices` inventories with deterministic JSON,
  explicit source completeness and native Windows registry/Linux dpkg+sysfs collectors.
- Additive hardware-model reporting in `system.info`, with an explicit `unknown` fallback.
- Bounded cross-platform `fs.size` summaries plus scoped, expiring SQLite exact manifests with
  deterministic content hashes and no full entry list in model, telemetry or audit payloads.
- Additive host-owned `ToolExecutionContext` / `IContextualTool` dispatch for tools that persist
  task- and actor-scoped derived state without breaking existing `ITool` implementations.
- Governed permanent recursive/batch deletion with complete hash-bound manifests, mandatory
  approval, one-shot children-first execution, durable per-entry reconciliation and independent
  verification (ADR-0027).
- Scoped deletion-manifest API paging/search/NDJSON download and an Angular approval preview with
  expiry/hash invalidation and explicit permanent-deletion acknowledgement.
- Optional bounded tool audit summaries, used by deletion to retain hash/count/outcome evidence
  without copying complete path lists into the append-only audit log.
- `bOps.Packages.Web`: `web.search` against one operator-configured SearXNG JSON endpoint (no API
  key, no HTML-scraping fallback) and a hardened `web.fetch` that resolves and validates every
  connection's destination address at connect time, denying loopback/link-local/private/
  carrier-grade-NAT/multicast/unspecified addresses by default and closing the DNS-rebinding TOCTOU
  window by construction (ADR-0028).
- Bounded redirect following, decompression-bomb-safe response reading (the byte cap applies to
  decompressed output, not wire bytes), and an allowlisted textual content-type/charset boundary
  for `web.fetch`.
- Read-only plugin catalog: `GET /api/plugins` and `GET /api/plugins/{id}`, and a lazy-loaded
  Angular Plugins page showing installed/enabled/loaded/compatible state, signature/trust,
  declared capabilities and dependencies, and declared-vs-effective maximum risk as distinct
  values — no enable, disable or upload control in this batch.
- `bOps.Api` now activates the operator's already-enabled plugins at start-up, the same way
  `bOps.Cli` already did — it runs its own `AgentRunner` and needs the same plugin-contributed
  tools/Skills visible to it.
- Writable Settings: an administrator-only encrypted local vault (AES-256-GCM, HKDF-SHA256-derived
  key from an externally supplied master secret) for provider API keys, a separate non-secret store
  for each provider's endpoint/model/tool-calling profile and the active-provider selection, and
  `GET/PUT/DELETE /api/settings/*` endpoints with optimistic-concurrency version checks on every key
  write (ADR-0029). The vault is opt-in — absent `Vault:MasterKeySecret` configuration, the Settings
  key-management surface does not exist and every existing CLI/environment deployment is unaffected.
  A configured-but-unresolvable master key refuses to start rather than run unprotected. A new
  `administrator` role/policy gates every mutation; a new `bops vault rotate-key` CLI command
  rotates the master key without exposing rotation through the API. The Angular Settings page lets
  an administrator choose the active provider and fully manage each provider's endpoint, model,
  tool-calling support and API key — the key input is always write-only and a stored key is never
  returned in plaintext, only as a `first six...last four` display mask captured at write time.
- New `bops.administrator` role/policy (`bOps.Api.ApiAuthorization`), required by every Settings
  mutation endpoint; the shipped local-dev credential now carries it alongside the three existing
  roles.
- Dashboard task history: a status selector over every `AgentTaskStatus` (default `Running`) lets an
  operator browse and reopen completed, failed and other terminal tasks; history is fetched on demand,
  never polled, newest first and capped at 50 rows. UI-only; no API change.
- V1.1-H release-gate tests (`V11ReleaseGateTests`): the API role matrix for the plugin catalog and
  Settings with each role on its own, a guarantee that the catalog exposes no mutation route,
  Settings persistence across a genuine host restart on the same state directory and vault master
  key, and a governed system-evidence-then-Web-research workflow proving an unsafe fetch
  destination stays denied and audited.

- V1.2-B delegation contracts in `bOps.Abstractions` (`1.2.0-preview.1`, ADR-0030), all additive and
  dependency-free: `AgentId`/`AgentIdentity`/`AgentRoleKind`, the reduce-only `AuthorityEnvelope` with its
  budgets, maintenance window, reduction result and canonical `DelegationHasher`, the durable
  `DelegationRun` aggregate with its step journal and reconciliation records, `VerificationReport`,
  `IDelegationStore`, four delegation audit events, an optional `Delegation` correlation block on every
  audit event and optional runtime-stamped provenance on `Evidence`. Both new members are omitted from the
  JSON when null, so events and evidence that never delegate serialize exactly as before. No runtime
  behaviour changes yet; the orchestrator, enforcement and persistence follow in V1.2-C to V1.2-K.
- `FrozenContractValuesTests` pins every member of the ten 1.0 enums to its 1.0 value, and
  `AbstractionsStaysDependencyFreeTests` asserts the SDK references only the .NET base class library.
- ADR-0031 (Accepted) amends ADR-0030 §3 with a per-role table of required, optional and not-applicable
  envelope dimensions, and D-027 records the operator's three choices. Documentation only; no behaviour
  changes until V1.2-C implements it.
- V1.2-C1 role profiles and envelope reduction (`bOps.Abstractions` `1.2.0-preview.2`, ADR-0031): the SDK
  gains `RoleProfile` (a profile that grants a dimension its role cannot use is refused at construction),
  `DelegationAuthorityRequest` (narrows only, never grants) and `IRoleProfileSource`; `EnvelopeDimension`
  gains a trailing `Profile` value for a missing or malformed profile. The runtime gains an internal
  `EnvelopeReducer` that derives the objective's root envelope and each role's envelope as
  parent ∩ profile ∩ request under the per-role table. Nothing calls it yet: enforcement in
  `ExecuteStepAsync` follows in V1.2-C2 and the `policy.yaml` loader in V1.2-C3.
- V1.2-C2 authority-envelope enforcement (`bOps.Abstractions` `1.2.0-preview.3`, ADR-0030 §3, ADR-0031 §4):
  every step of a delegated agent is checked against its envelope in `ExecuteStepAsync`, before argument
  validation and before policy, and the envelope can only deny (tool, risk ceiling, maintenance window,
  and for a step with a Skill scope the Skill, Capability, blast radius, target and environment). A delegated
  step that carries no envelope, an envelope other than the recorded one, or one granted to another
  operator is refused, and so is a step above `Read` that is not a step of an approved plan. A Capability is
  refused before its code runs when the envelope does not allow it, and an approved plan is checked whole
  before its first step. `PolicyContext` gains optional `Delegation` and `Envelope`, and every audit event the
  runner writes for a delegated role carries the correlation block. The delegated entry points are internal
  and nothing calls them until V1.2-D, so a run that is not delegated is unchanged.
- V1.2-C3 `policy.yaml` `delegation` section (ADR-0031 §5): an optional section configures one profile per role
  (tools, risk and blast-radius ceilings, targets, environments, steps, tokens, duration, window), read strictly:
  an unknown key, a number or comma list where a name is expected, a bare duration or an instant without a zone
  is an error, and so is a grant a role cannot use, reported with the line and key. A malformed section fails the
  load, so the hosts fall back to `AllForbidden`, which ships no profile and denies delegation. `bOps.Policy` gains
  `PolicyRoleProfileSource`, an `IRoleProfileSource` over the loaded profiles, and `PolicyConfig.RoleProfiles`.
  `SafeDefault` has none, so delegation stays off until an operator grants it. Nothing is wired into the hosts
  until V1.2-D, and the SDK is unchanged.
- V1.2-D deterministic orchestrator (`bOps.Abstractions` `1.2.0-preview.4`, ADR-0030 sections 2 to 5): `DelegationRunner`
  runs Discovery, Diagnostic, a human approval of the plan's hash, Remediation and Verification in that order and no
  other, over the existing paths. Each role is a new agent with its own envelope, derived from the root, its profile and
  the request when it starts; a model reasons only inside Discovery and Diagnostic, over a tool view narrowed to what
  the envelope allows; what one role hands the next is only Evidence, Findings (kept only if they cite recorded
  evidence) and the plan, delivered as delimited data. Remediation executes exactly the approved plan with no model
  call, and Verification reads the system itself through its own envelope instead of trusting Remediation. A run ends as
  completed, a completed diagnosis, rejected, denied, policy blocked, verification failed, failed or deadline exceeded,
  and never reports success on an inconclusive verification. The SDK gains `IPlanApprovalProvider` and
  `PlanApprovalRequest` (a human approves a plan by its hash; a decision from an agent, the runtime or one of the run's
  own agents is refused), `DelegationStage.PlanDecided` and `DelegationLifecycleAuditEvent.PlanHash`. The run is kept in
  memory and nothing is wired into Cli or Api yet; budgets, durable state and resume follow in V1.2-E and V1.2-F.
- V1.2-E budgets, deadlines and cancellation (ADR-0030 section 6; no SDK change): a delegated run now counts what each role
  spends. The run's budget is the sum of the roles' and each role's is reserved from what is left of it when the role
  starts, so a role can never be granted what an earlier one used; what the role spent (steps, and the tokens of every
  model call, plan, replan and retry) is reconciled when it ends, persisted on its `DelegationRoleRun.Consumed` and
  audited as a `BudgetConsumed` event. A role stops at its own deadline while it runs, even inside a model call or a tool.
  A run with no steps or tokens left for the next role ends as `BudgetExceeded` (not as a denial), and a plan the
  Remediation role has too few steps to finish is refused before a human is asked and before anything changes. One
  cancellation token tree covers the run (the caller's token and the run's deadline, so an approval nobody gives cannot
  hold a run past it), each role (its own deadline) and each step (the tool's timeout). Cancelling an already running
  run now ends it as `Cancelled` and returns it, audited, instead of throwing; `DeadlineExceeded` and `BudgetExceeded`
  stay distinct. A side-effecting step cancelled while it runs is audited as an unknown outcome (`StepOutcomeKind.Cancelled`
  in a `DelegationJournalAuditEvent`), never as a failed tool call; the durable journal and reconciliation are V1.2-F.
- V1.2-F durable delegation (`bOps.Abstractions` `1.2.0-preview.6`, additive, ADR-0030 section 7): `SqliteDelegationStore`
  in `bOps.Memory` stores a `DelegationRun` as one row (the whole aggregate as JSON, replaced in one transaction, synced to disk
  on every commit, owner-only file on Linux), with the status, actor and idempotency key as columns so a start is idempotent
  and what is resumable or waiting for an operator is found without reading a run. `DelegationRunner`, given a store, saves
  the run at every transition and journals each side-effecting step in two durable writes, its intent before the step runs
  and its outcome after; a step whose intent could not be committed does not run. `ResumeAsync` continues a run a crash left
  running without repeating a completed side effect: a finished role is not run again, an interrupted read-only role restarts
  from its beginning and is charged everything it was granted (a restart never gives a budget back), a step the journal shows
  as done is skipped, and a step with an intent but no outcome, or a cancelled or timed-out one, is settled by its own
  declared verification (confirmed is done by reconciliation; refuted or inconclusive ends the run as
  `RequiresReconciliation`, never retried). Approvals are never persisted: a plan with steps left is put to a human again by
  the same hash. Resumes are bounded (`3` by default). `ReconcileAsync` lets a human accept the unsettled steps as done or
  abandon the run, audited. `StartAsync` takes an idempotency key. The SDK gains `DelegationRun.Authority` and
  `DelegationRun.Remediation` (the request the run started with, so it can resume without its caller),
  `DelegationRemediationRequest` and `DelegationStage.Resumed`; runs stored before this read with none of them.
- V1.2-G separation of duties (runtime only, ADR-0030 section 5): no role approves or verifies its own work. In a delegated
  run a step approval decided by an agent, by the runtime or in the name of one of the run's own agents is now refused and
  audited as a refusal (the plan-level approval already was); before, only the plan's approver was checked. A run in which two
  roles would share an agent identity ends as `Failed` before the second is granted anything, and a stored run that fails the
  same rule is not resumed. `VerifyPlanAsync` no longer skips a plan step it cannot verify (an unregistered tool, or a tool that
  declares no verification but is not a Read): it counts as `Inconclusive`, so the confirmation of the other steps cannot
  stand for it (rule S4). The verdict of a plan with several steps is now covered as its own guarantee: the worst of its
  steps, and only `Confirmed` is a success. No SDK change.
- V1.2-H audit correlation and provenance (`bOps.Abstractions` `1.2.0-preview.7`, additive, ADR-0030 section 8):
  `DelegationLifecycleAuditEvent.EvidenceIds` lists, on a role's `RoleCompleted` event, the ids of the evidence that role itself
  gathered (never what it says), so with the acting agent on the event's correlation block the chain from observation to
  verdict, and who gathered each piece, can be rebuilt from the audit log alone. The rest of the section was already in place
  and is now proven end to end: every audit event of a delegated run carries the block of that run, whichever of nine ways it
  ends and across a resume and a reconciliation, and a run that is not delegated writes none; the whole run (roles, agents,
  envelope hashes and their parent, budgets, the human decision, the journal, the end) is rebuilt from the log in a test;
  envelope contents, arguments and tool output reach neither a delegation event nor telemetry; and the hash chain of a file that
  mixes old and new events verifies, and an altered delegation event breaks it at its sequence number.
- V1.2-M integration and release gate: `V12ReleaseGateTests` takes one objective end to end over HTTP with the real system and filesystem tools and
  the real policy engine (discovery, diagnosis, approval of the plan by its hash and of the write, remediation, independent verification by
  distinct agents, a verified audit chain), and kills a host between a write and the record of its outcome to show a new host resumes the run,
  settles the step by its own verification and does not write twice. `PublicSurfaceSnapshotTests` turns the manual comparison with the frozen
  1.0 surface of `bOps.Abstractions` into a permanent test, by member, signature and enum value, taken from the V1.0 close-out build.
- V1.2-L documentation and alignment (no code or SDK change): `docs/agents/delegation.md` (the model: roles, authority reduction, approval,
  budgets, resume and reconciliation, audit, surfaces, and what it does not do: no parallelism, no agent approval, no microservices, no remote
  guarantees), `docs/agents/delegation-policy.md` (the `delegation` section of `policy.yaml` for operators, with a working example that a
  test loads from the document itself), and a threat-model section on the limits of the authority envelope. README, roadmap, bootstrap and
  task index now say V1.2 A–L are implemented and the gate (M) is open.
- V1.2-K Delegations view in the dashboard (`web/bops-ui`, no API or SDK change): a list of runs and, for the one selected, its roles
  in pipeline order with agent id, status and consumption, the findings and the evidence they cite (never what a tool returned), the
  verification verdict, the plan hash and who approved it, the step journal, a denial with the dimension it was refused on, and a
  banner for a step awaiting reconciliation. A plan waiting for a decision is shown with its steps and arguments, findings and the
  authority it would run under, and is approved or rejected by its hash; a start form takes an objective and, optionally, a change.
  Approve is offered to the approver role, cancel and resume to the operator role, reconcile to the administrator role, and the API
  checks each again. Everything a run carries is untrusted text and is only interpolated, never bound as HTML. The nav gets a
  Delegations entry with a badge for plans waiting.
- V1.2-J delegations API (no SDK change, ADR-0030 section 9; `docs/agents/delegations-api.md`): `POST /api/delegations` (operator,
  `Idempotency-Key` honoured through the delegation store, so it holds across a restart), `GET /api/delegations` and
  `GET /api/delegations/{id}` (viewer), `POST /api/delegations/{id}/cancel` and `/resume` (operator), `POST /api/delegations/{id}/reconcile`
  (administrator), `GET /api/delegations/approvals` and `POST /api/delegations/{id}/approval` (approver). A plan is decided through an
  approval queue (`ApiPlanApprovalProvider`) beside the step approval queue, by the plan's hash: an answer for another hash decides
  nothing. The host builds the role profiles from the same loaded `policy.yaml` as its policy engine, so a missing or broken file ends a
  delegation `Denied` on the `Profile` dimension before any model call. A run's view omits the data of every piece of evidence, the
  authority of each role and the model calls. While a plan waits, the run is `Running` with `awaitingPlanApproval: true` (the runtime
  never writes `AwaitingApproval`). The threat model gains the delegated-run surface and the inter-agent data flow.
- V1.2-I `bops delegate` (`bOps.Abstractions` `1.2.0-preview.8`, additive, ADR-0030 section 9): `bops delegate "<objective>"`
  runs an objective through Discovery, Diagnostic, Remediation and Verification, diagnosing only unless `--skill`,
  `--capability`, `--target` and `--environment` name a change; `bops delegate status|resume|cancel <run-id>` and
  `bops delegate reconcile <run-id> --accept|--abandon` read, continue, end or settle a stored run. `bops "<goal>"` and
  `bops resume` are unchanged. The role profiles come from the same loaded `policy.yaml` the policy engine does, so a missing
  or broken file has none and a start ends `Denied` on the `Profile` dimension before any model call. The plan is approved at
  the console by its hash, with its steps, findings and the authority it will run under (`PlanApprovalRequest.Authority`, new);
  anything but an explicit yes, including a closed input, is a no. Every end has its own exit code (0 completed or
  diagnosed, 1 failed or a usage error, 2 denied or blocked by policy, 3 rejected or abandoned, 4 requires reconciliation,
  5 budget or deadline exceeded, 6 verification did not confirm, 10 not finished, 130 cancelled) and Ctrl+C cancels the run
  under way. `DelegationRunner.CancelAsync` ends a stored run no process is executing, audited with who did it.
  Two defects the new end-to-end tests found are fixed: `SqliteDelegationStore` could not save any run whose evidence carried
  provenance (the source generator in `bOps.Memory` could not see the non-public setter, and the runtime tests used an
  in-memory store, so it was never exercised), and a stored run with journaled steps but no stored plan is now refused on
  resume instead of diagnosing again over a step already taken.
- Model calls can be troubleshot from the task store (`bOps.Abstractions` `1.2.0-preview.5`, additive). Every call the
  runtime makes to a model, for a step, a plan or a replan, is kept as a `ModelCallRecord` on the `PlanStep` or
  `AgentPlan` it produced: the provider, the model asked for and the one the provider says answered (`openrouter/free`
  is answered by whichever model the router picked), when it started, how long it took, tokens sent and received, the
  finish reason, the error of a failed call, and the exact request and reply bodies, each bounded by the new
  `Agent:MaxModelPayloadCharacters` (default 200000, `0` keeps no bodies; a cut body is marked). A failed call keeps the
  bodies its adapter had (`ModelProtocolException.Details`). `ModelCallAuditEvent` gains `ActualModel` and `DurationMs`,
  omitted when unknown so an older event is unchanged. The OpenAI-compatible and Anthropic adapters fill these in (they
  now read the reply as text before parsing it, so the body can be kept); the OpenAI-compatible adapter now says
  "no choices" instead of failing with an index error when a provider returns none. The API sends the model, time and
  tokens but never the bodies, on task reads, lists and the events stream. The dashboard shows them behind a "?" on
  each step. The OpenAI-compatible adapter has its own test project for the first time.

### Changed

- `bOps.Abstractions` now identifies the in-progress additive SDK surface as
  `1.1.0-preview.1` rather than publishing V1.1 contracts under the stable 1.0 version.
- Project status documentation now distinguishes V1.0 implementation completion from the
  still-unexecuted release-candidate workflow.
- Planning is consolidated into one active `agentic` roadmap with granular effort-rated task files;
  superseded plans and specification inputs are preserved in an agent-ignored historical archive.
- Dynamic plugin activation now registers combined Skill/Tool providers atomically and requires
  their declared Capability names to match the activated provider exactly.
- V1.1 Skill runs are explicitly terminal and non-resumable; interruption requires fresh
  preparation and approval rather than unsafe partial-plan replay.
- Filesystem inventory limits, output ceilings, manifest path and retention are host configuration;
  out-of-range requests fail instead of being silently clamped.
- `PluginManager.LoadAllEnabled` isolates each plugin's activation failure instead of letting one
  bad plugin crash the whole host at start-up; it now returns the sanitized per-plugin failures
  (install path scrubbed from the message) instead of throwing past the first one. `bOps.Cli` logs
  these as warnings instead of ignoring them.
- Runtime argument validation now rejects undeclared names and JSON type mismatches before policy,
  approval or execution; tool manifests can require explicit human approval even when policy would
  otherwise allow automatic execution.

### Fixed

- A model that ends a step with no text and no tool call no longer completes the task with an empty "Final response"
  (rule S3). `AgentRunner` asks again once (`Agent:EmptyFinalResponseRetries`, default 1), telling the model its reply
  was empty and leaving the empty turn out of the conversation, and if it stays empty the task fails with the model,
  the finish reason and the tokens generated in its message. Found on a task run against `openrouter/free`, whose
  last call generated 788 tokens and returned no `content`.
- Policy fails closed on a value that is not a name (rule S3). `policy.yaml` enums were read with
  `Enum.TryParse`, so a number or a comma list (`read: "approval, forbidden"`, `read: 3`) loaded as a `PolicyMode`
  that does not exist and `AgentRunner` executed that tool unattended, and `"low, medium"` loaded as a different
  `RiskLevel` than written. The loader now accepts member names only, in `defaults`, `tools`, `packages` and
  `skills`, and refuses anything else naming the section and key. As defence in depth, `AgentRunner` treats any
  mode outside the enum, whatever the policy engine returned, as a Forbidden decision (audited like any denial).
- `bOps.Api`'s committed `appsettings.json` again ships the documented safe filesystem defaults (no
  readable paths, `MaximumEntries` 100000, `MaximumOutputBytes` 32768); broader local values had
  crept in with V1.1-F and now live in the git-ignored `appsettings.Development.json`.
- `scripts/Add-SpdxHeaders.ps1` recognised an existing header only at the very start of a file, so
  re-running it would have prepended a second header to every source file; it now detects the SPDX
  identifier in the file's first lines and also covers `samples/`, whose four files lacked the
  header.
- API composition tests no longer inherit a developer's model-provider secret from the host
  environment.
- The AppHost dependency lock now includes the centrally configured SourceLink dependency, so the
  release workflow can restore the complete solution in locked mode.
- `bOps.AppHost` no longer uses a package lock file: the Aspire SDK adds RID-specific Dashboard and
  DCP packages for the restoring machine, so no single lock could satisfy locked restore on both
  Windows and Linux (`v1.1.0-preview.1` Linux release job failed with NU1004).
- The release workflow installs the UI dependencies before generating the SBOM and stamps the SBOM
  with the SDK package version instead of the script's stale `1.0.0` default.
- `bOps.Abstractions` moves to `1.1.0-preview.2` so the package version matches the corrected
  release tag; `v1.1.0-preview.1` was published but its release run failed and produced no artifacts.
- `ToolParameterType.PathList` had been inserted between `Path` and `Duration` in V1.1, renumbering
  `Duration` (5 to 6) and `Enum` (6 to 7) and so breaking the additive-only promise for any package built
  against 1.0, whose compiler inlined the old values. It is now the last member with the explicit value 7,
  restoring the 1.0 values. Source-compatible; a binary built against a 1.1 preview must be recompiled.
  The V1.1-H API diff compared member names and could not see this.

## [1.0.0-rc.1] - 2026-09-16

### Added

- Stable `bOps.Abstractions` 1.0 contracts, including host-resolved `SecretReference` and
  `ISecretProvider` types.
- Bearer-key API authentication with `viewer`, `operator` and `approver` roles; approval actors
  are derived from authenticated claims.
- Actor-scoped idempotent task starts, cancellation, API rate limits and bounded concurrent runs.
- Detached RSA-PSS/SHA-256 plugin signatures, operator-owned publisher trust and recorded package
  provenance, re-verified before every activation.
- Operator-facing `bops audit verify`, Unix-restrictive state/audit file permissions and a full
  V1.0 threat model.
- Locked dependency restore and a Windows/Linux release workflow that verifies reproducible
  publish output, creates deterministic archives, SBOMs and checksums, and attests artifacts.

### Changed

- Provider credentials are configured only by secret reference and are excluded from serialized
  options, logs and status responses.
- Provider HTTP adapters retry only bounded transient transport/status failures.
- The Angular UI authenticates requests with an in-memory credential and uses authenticated task
  polling instead of placing credentials in an `EventSource` URL.
- Plugin manifest parsing is size-bounded and fail-closed for malformed or traversal-prone input.

### Security

- Unsigned, unknown-key, invalidly signed or modified plugins cannot be enabled.
- API reads, task operations and approval decisions are separately authorized.
- Pending approvals are intentionally not persisted across crashes; resumed execution requires a
  fresh decision from a currently authenticated approver.
