# 06 — Decision register

Settled questions. Each entry records what was chosen, what was rejected, and why — so the
alternatives are not re-proposed without new information.

A decision is changed by an ADR that supersedes it, never by an edit to this file.

All entries have status **Accepted**. D-001–D-012 were decided 2026-09-14, D-013–D-015 on
2026-09-15, D-016–D-020 on 2026-09-16, D-021–D-023 on 2026-09-17, D-024–D-026 on 2026-09-18, and
D-027 on 2026-09-19.

---

### D-001 — Local-only execution now, remote-capable contract

**Decision.** bOps runs on the machine it administers. A controller/remote-agent topology is
wanted eventually, so the contract is shaped for it from V0.1 without building any transport.

**Rejected.** *Controller + remote agents now* — doubles the project surface (inter-node auth,
certificates, protocol versioning, fleet management) and pushes V0.1 out by months.
*SSH-based remote execution* — would demolish the design: tools become wrappers over textual
remote commands, typed platform access and the local Docker socket disappear, and "no generic
execution tool" becomes impossible to hold.

**Consequences.** Architecture rules A2–A6: serializable tool boundary, `NodeId` everywhere,
per-node registries and probes, policy enforced where execution happens, audit written
locally then shipped. These cost close to nothing today and are the entire difference between
adding remote execution and rewriting for it.

---

### D-002 — .NET Aspire from V0.5, for development only

**Decision.** Introduce `bOps.AppHost` at V0.5/V0.6 to orchestrate Ollama, `llama-server`,
Linux test targets and, from V0.9, the API and UI. The Aspire dashboard provides the
OpenTelemetry view for free.

**Rejected.** *Aspire from V0.1* — at V0.1 the deliverable is a CLI reading `/proc` and
`Process.GetProcesses()`; there is nothing to orchestrate, and it adds a dependency before
the runtime is validated. *docker-compose* — loses the integrated telemetry dashboard and
service discovery. *No orchestration* — makes the cross-OS, multi-provider integration tests
manual, which is what V0.5 is meant to automate.

**Consequences.** bOps itself is **never** containerized for its real work: a container cannot
restart the host's systemd services. Aspire orchestrates dependencies and test targets, never
the production runtime.

---

### D-003 — Dynamic package loading over Native AOT

**Decision.** Drop `PublishAot`. Ship self-contained with partial trimming.

**Reason.** The two are technically incompatible — an AOT binary cannot load assemblies
dynamically — and the package ecosystem is the identity of the project (principle 7). AOT
would also constrain EF Core and `Docker.DotNet`.

**Rejected.** *Two hosts* (`bops-lite` AOT + `bops` full) — two build matrices, two test sets,
and "why won't my plugin load" as the permanent top question. *AOT with statically compiled
packages only* — kills closed-source third-party distribution and most of the vision.

**Consequences.** Binary in the tens of MB, startup around 80 ms instead of 15 ms. Irrelevant
for a tool that then waits seconds for a model response.

---

### D-004 — Apache-2.0

**Decision.** Apache-2.0 for the repository.

**Reason.** Permissive enough to allow commercial closed-source packages on top, which the
package model explicitly intends, with an explicit patent grant — the de facto standard for
infrastructure adopted inside companies with a legal department.

**Rejected.** *MIT* — equivalent in practice but without the patent grant. *AGPL-3.0* —
banned by policy at many companies and creates friction over whether a closed-source plugin
is a derivative work, in direct tension with the package model. *Dual Apache + commercial* —
would require a CLA from every contributor starting today; not chosen, and therefore
relicensing later would require contributor consent.

---

### D-005 — Operating systems are packages; the tool is the OS-specific thing

**Decision.** Each OS package contributes its own complete tools, selected by the registry's
platform filter. `ISystemProvider` and `IServiceProvider2` do not exist. Shared tool
machinery lives in a plain library (`bOps.Packages.System.Core`) referenced by each OS
package.

**Rejected.** *Packages contributing platform services* — would require a package to consume a
service provided by another package across the assembly isolation boundary: a cross-plugin DI
container, load ordering with dependencies, and `ISystemProvider` becoming a public versioned
contract. *Providers internal to a single System package* — the current plan's approach, but
it means adding macOS requires forking and republishing the System package, so operating
systems would not actually be packages.

**Consequences.** Windows-only APIs are never shipped to Linux machines. macOS becomes a
package anyone can write, with zero core changes. Output format becomes part of the contract
and is enforced by a shared conformance suite, because the LLM reads that output.

---

### D-006 — Verification: declared in the manifest, evaluated by the package

**Decision.** A non-`Read` tool must declare a `VerificationSpec` **and** implement
`IVerifiableTool`. The registry rejects registration otherwise.

**Reason.** The declarative half makes verification inspectable — the operator can be told,
before approving, what will be checked afterwards — and lets the registry *enforce* principle
3 structurally. The code half keeps non-trivial checks expressible.

**Rejected.** *A hardcoded map in the runtime* (the plan's approach) — violates principle 7
and makes verification impossible for third-party packages. *Code only* — cannot be shown to
an operator, cannot be enforced. *Declarative only* — needs a mini-DSL, and cannot express
checking for an absent PID or comparing a hash.

---

### D-007 — Model contract: fix what is breaking now, defer what is additive

**Decision.** `ChatTurn` carries `ToolCallId` and `ToolCalls`; `ModelResponse` carries 0..N
tool calls. Streaming arrives in Phase 2 as `IStreamingChatModel : IChatModel`.

**Reason.** A flat `(Role, Content)` pair cannot represent native OpenAI-style tool calling and
would silently degrade every provider. Those shapes are breaking to change later. A separate
optional interface is additive, so streaming can wait without cost.

**Consequences.** The V0.1 runtime executes one tool call per iteration and returns the rest
unexecuted. That is a runtime choice and reversible; the contract shape is not.

---

### D-008 — Audit content from V0.1

**Decision.** From V0.1: actor identity (who launched, who approved), secret redaction via
`ToolParameter.Sensitive`, `NodeId` on every event, and a distinct event type for model calls
carrying provider, model and token usage. Hash-chaining is deferred to V0.3, alongside the
policy engine.

**Reason.** Adding these later means migrating logs already written and schemas already
deployed. An audit log that cannot say *who* answers the second question of an incident
review, not the first.

**Consequences.** Until V0.3 the log is append-only by convention, not tamper-evident. It must
be described that way, in those words.

---

### D-009 — OpenTelemetry from V0.1

**Decision.** `ActivitySource` and `Meter` from the first commit, OTLP exporter.

**Reason.** The agent loop is a tree of nested steps — it is literally a trace. Retrofitting
touches every point in the loop, after those points have grown more complex. From V0.5 the
Aspire dashboard consumes it with no further work, which is most of what the Phase 2 UI needs
to show anyway.

**Consequences.** Arguments and tool output never appear in telemetry; they are audit
material, with different retention and a different audience.

---

### D-010 — Testing: TDD on the core, real targets for packages

**Decision.** Core components are written test-first, without exception. Platform packages are
tested against real operating systems and real daemons, never mocks. The planner is made
deterministic with a `FakeChatModel` replaying recorded responses.

**Reason.** Tests written after the fact on a policy engine confirm the behaviour that exists
rather than define the behaviour that was wanted — backwards for the components whose job is
to say no. A mocked `/proc` confirms only that mocks can be written.

---

### D-011 — Analyzer strictness escalates; nullable and warnings-as-errors do not wait

**Decision.** `Nullable` and `TreatWarningsAsErrors` on from the first commit.
`AnalysisLevel` starts at `recommended` with the known-noisy rules suppressed, and escalates
to `all` at V0.3.

**Reason.** The cost people associate with warnings-as-errors comes from retrofitting onto an
existing codebase. On an empty repository each warning appears alone, as it is written.
`AnalysisLevel=all` is the setting that produces volume without proportional early value.

**Consequences.** Suppressions are allowed but must stay visible: scoped, justified, and
listed in `docs/architecture/suppressions.md`. With coding agents, an invisible suppression
becomes the default escape route.

---

### D-012 — `bOps.Abstractions` stays on `0.x` until V1.0

**Decision.** Publish the SDK, but keep it on `0.x` with an explicit instability notice until
the V1.0 milestone.

**Reason.** The plan promises rigorous semantic versioning while the contract is still being
discovered. Promising stability the project cannot keep is worse than declaring instability.

---

### D-013 — Open core: `bOps` stays Apache-2.0 public; commercial work lives in a separate private repository

**Decision.** Decided 2026-09-15 and retained by the consolidated roadmap (the roadmap is the
primary source for delivery scope; this entry is the durable record of the boundary). The public
`bOps` repository — core, SDK, generic packages, first-party LLM providers, the local UI, and a
purely-demonstrative sample Skill — stays Apache-2.0, forever, for anyone, including commercial
use, per D-004. Everything commercially sensitive — official Skills, their knowledge/playbook
content, the Control Plane, the Portal, and commercial entitlement logic — is built in a single
private monorepo, `bOps.Commercial`, never in this repository. Full detail:
[`docs/licensing.md`](../docs/licensing.md).

**Reason.** The package model (principle 7) already made bOps's commercial story "sell Skills and
services on top," not "sell the core." Open-core makes that explicit and durable: nothing about
adopting bOps commits an operator to a vendor relationship for the parts that matter most to
them (the runtime, the safety model, the audit trail), while the actual differentiated,
expensive-to-build value — vertical DBA knowledge, multi-tenant governance, an enterprise
portal — has a place to live that isn't Apache-2.0.

**Rejected.** *Everything in one repository, commercial code gated by a license check* —
license checks on visible source are not a real boundary (rule S8's point about in-process trust
applies to the whole repo, not just plugins), and it would put every operator one `git log` away
from proprietary knowledge whether or not they paid for it. *Fully closed-source core* — would
have meant no third-party package ecosystem, no trust from operators who need to read what they
run against production, and no way to build a community around the free tier. *A single
multi-license repository with path-based licensing (e.g., a `commercial/` folder under a
different SPDX identifier)* — GitHub's license detection and most compliance tooling assume one
license per repository; splitting by directory invites exactly the "did this leak into the OSS
tree" mistake the two-repository boundary exists to make structurally hard.

**Consequences.** The public repository's CI, contribution process and this project's own
`agentic/` rules apply only to the OSS side; `bOps.Commercial` will have its own, likely stricter,
rules once it exists. `bOps.Commercial` may depend on packages published from `bOps`; it must
never fork or duplicate a public contract internally — a boundary change needed by private code
is designed and published in the OSS repository first, generically, before the private repository
consumes it. The Control Plane (in `bOps.Commercial`, once it exists at V1.4) may send a node
only typed objectives/plans/capabilities — never raw commands — and the node re-checks policy,
approval and entitlement locally regardless of what the Control Plane says; a remote decision
never substitutes for a local one. See rule A1 alongside this: the core still never names a
concrete package, and that now extends to never assuming a *commercial* package's identity
either.

---

### D-014 — Copyright holder is Fabio Cavallari; `bSoft` is an unregistered brand, not a legal entity

**Decision.** The copyright holder recorded in `LICENSE`, `NOTICE`, and every source header added
under V0.9.1 is the individual **Fabio Cavallari**, not a company. `bSoft` is used only as a
project/commercial brand name; it is not a legal entity, must never be written as the copyright
holder, and — since it is not currently a registered trademark — must never be shown with the ®
symbol. Trademark clearance and any registration decision are explicitly deferred, not assumed.

**Reason.** The initial materials named `bSoft` informally; getting the *legal* copyright holder
right from the first published header avoids a mechanical rewrite of every file's notice later,
and avoids implying trademark protection that does not exist yet.

**Rejected.** *Attributing copyright to `bSoft`* — not a legal person or entity able to hold
copyright under the facts as given. *Using ® now* — false, and reversible reputational and legal
exposure for no benefit before an actual registration exists.

**Consequences.** `docs/licensing.md` and `NOTICE` are the durable explanation; source headers
just say `Copyright 2026 Fabio Cavallari`. If `bSoft` (or `bOps`) is later registered as a
trademark, that is a new fact recorded in its own update to `docs/licensing.md`, not a silent
edit to this entry.

---

### D-015 — Contributor License Agreement, not DCO-only, for the public repository

**Decision.** External contributions to the public `bOps` repository require a signed CLA before
merge (individual, plus a corporate path when the contributor's employer holds the rights) — not
only a Developer Certificate of Origin `Signed-off-by` line. The CLA's actual legal text is not
written by this project's engineering process; it must be drafted or reviewed by IP/software
counsel before it is binding, per the consolidated roadmap. The outbound license for
whatever is merged stays Apache-2.0 regardless — a CLA changes the relationship between a
contributor and the maintainer, never the license everyone downstream receives.

**Reason.** A CLA (rather than DCO alone) gives the project a clear, explicit basis to use
accepted contributions in the commercial offerings described in D-013 without a separate
negotiation per contribution, while a DCO alone only attests provenance and says nothing about
that.

**Rejected.** *DCO only* — attests the contributor had the right to submit the code, but creates
genuine ambiguity about whether a contribution can be relied on inside `bOps.Commercial`'s
consumption of public packages. *Copyright assignment* — stronger than needed, and a harder ask
of contributors than the project's stated goal (permissive reuse rights, not ownership transfer)
requires.

**Consequences.** No external contribution merges until the CLA process is actually operational
and verifiable (a real signing flow, not just a written policy) — `CONTRIBUTING.md` documents the
intended process; it is not itself the binding agreement.

---

### D-016 — Three-repository topology with private `bOps.Workspace`

**Decision.** `bOps` remains the public Apache-2.0 repository. Commercial implementation lives in
a separate private `bOps.Commercial` repository. The private `bOps.Workspace` repository contains
both as Git submodules under `repos/bOps` and `repos/bOps.Commercial`, pins reviewed commits and
provides cross-repository bootstrap and validation instructions. It contains no duplicated product
source. All three repositories are hosted by the GitHub owner `babbubba`.

**Reason.** An authorized coding agent needs one secure checkout from which it can inspect and
coordinate both sides quickly, while the public/private source, license, access, history, CI and
release boundaries remain structurally enforceable.

**Rejected.** *Put commercial code in a private folder of the public repository* — violates D-013
and makes accidental disclosure likely. *Copy the public source into the private monorepo* — creates
contract forks and unclear fixes. *Use only adjacent independent clones with no root* — preserves
separation but provides no reproducible cross-repository version pin or coordination entry point.

**Consequences.** Product commits land in the owning repository first; the root then updates the
submodule pointer. Public CI never requires private source. The coordination repository itself is
private and cannot become a back door for secrets or proprietary artifacts into `bOps`.

---

### D-017 — Writable local secrets use an encrypted vault with an external master key

**Decision.** UI-written provider API keys are persisted in a versioned encrypted local vault. Its
master key is supplied externally through environment/service secret configuration and is never
stored alongside the vault. There is no plaintext fallback. The full secret is never returned;
display metadata supports only the approved `first6...last4` mask. Existing environment and
development user-secret inputs remain supported with deterministic precedence.

**Reason.** A cross-platform/headless deployment needs one predictable storage model. Encrypting
the local file separates stolen state from the externally provisioned key while preserving current
CLI configuration paths.

**Rejected.** *OS-native credential stores as the only backend* — strong on interactive desktops
but inconsistent for containers and headless Linux services. *Plain JSON protected only by file
permissions* — does not meet the requirement for secure persistent UI writes. *Return the stored
secret to render the mask* — breaks write-only secret providers and expands disclosure paths.

**Consequences.** An ADR must define the standard AEAD envelope, atomic writes, permissions,
rotation, recovery, precedence and short-secret masking before code. Missing/wrong keys and tamper
fail closed. Mask metadata is captured on write and is not a secret-retrieval path.

---

### D-018 — Recursive deletion approval is bound to a complete immutable manifest

**Decision.** Permanent recursive or batch deletion uses a distinct High-risk tool and a complete,
canonical, immutable manifest. Approval binds to its hash. The full exact entry list remains
server-side and is available through authorized pagination/download; model, browser and audit
responses carry bounded summaries and references. Paths are re-resolved and identity is checked
immediately before deletion. Any stale/incomplete/over-limit manifest fails closed.

**Reason.** A text preview can differ from what is later deleted, while rendering thousands of
entries in one response makes the safety feature unusable. Hashing the complete set preserves exact
consent; pagination preserves usability for trees containing 5,000, 10,000 or more entries within
an operator-configured finite ceiling.

**Rejected.** *Unbound preview followed by path-based deletion* — permits time-of-check/time-of-use
drift. *Put the complete list in the approval/audit/model payload* — creates oversized contexts,
browser stalls and huge immutable audit events. *Claim atomic recursive deletion* — filesystems do
not provide that guarantee across a tree and partial completion must be represented honestly.

**Consequences.** Inventory and deletion need durable bounded manifest storage, expiry/cleanup,
cursor APIs, deterministic canonicalization, failure reconciliation and an ADR before public
contract changes. A scale test must cover at least 10,000 entries.

---

### D-019 — `web.search` uses an operator-configured SearXNG JSON endpoint

**Decision.** The first `web.search` adapter targets a configured SearXNG JSON search endpoint and
requires no API key in its tool contract. It does not silently scrape public search HTML and does
not hard-code a third-party public instance. `web.fetch` remains a separate tool.

**Reason.** SearXNG provides an explicit HTTP search interface without binding the product to a
paid key, while an operator-owned endpoint gives a stable configuration and privacy boundary.
HTML scraping would be brittle and difficult to test as a contract.

**Rejected.** *DuckDuckGo or another HTML scraper as the primary backend* — markup, blocking and
terms can change without an API contract. *Couple search and fetch into one tool* — obscures policy,
limits and evidence provenance. *Pick a public SearXNG instance automatically* — transfers data and
availability to an unapproved operator.

**Consequences.** JSON output must be enabled on the configured instance. The Web package requires
an ADR and SSRF/output threat-model work; all returned content remains untrusted tool data under S5.

---

### D-020 — Skill preparation uses a host-bound restricted invoker and terminal runs

**Decision.** `ISkillProvider` extends `IToolProvider` and contributes deterministic
`ICapability` implementations. During one preparation, the host passes an invocation-scoped
`IToolInvoker` bound to the task, actor, Skill, Capability and host-assigned package. It exposes
only visible `Read` tools owned by that package. Preparation and execution are separate; every
non-Read plan needs affirmative approval of its exact canonical hash, then its steps still use the
ordinary Tool policy and verification path. V1.1 Skill runs are terminal and non-resumable.

**Reason.** Package code needs real evidence before it can build a plan, but registry or service
provider access would permit cross-package discovery and identity forgery. Separating preparation
from execution makes the approved artifact inspectable and immutable. Pretending a partially
executed plan can resume without durable effect reconciliation would create a false safety claim.

**Rejected.** *Constructor-injected process-wide `IToolInvoker`* — invocation identity would be
missing or caller-forgeable. *Give a Skill `IToolRegistry`* — crosses package boundaries and leaks
concrete tools. *Prepare and execute in one call* — no opportunity to approve the exact plan hash.
*Persist only a step index* — does not prove which side effects occurred or were verified.

**Consequences.** Activated Skill providers register through the existing signature/trust
boundary, contextual Skill policy matches exact fields and fails closed, and interruption requires
a new preparation and approval. Durable Skill reconciliation needs a future ADR. See ADR-0025.

---

### D-021 — Exact filesystem inventories are scoped SQLite state reached through contextual tools

**Decision.** `fs.size` streams bounded summaries and optionally writes exact entries
incrementally to a package-owned SQLite store. A new additive `IContextualTool` contract receives
host-owned node, actor and task identity without changing `ITool`. Exact manifests are opaque,
scope-bound, expiring and become ready only after complete deterministic enumeration and hashing.

**Reason.** Model-supplied scope is forgeable, changing `ITool` would break the 1.0 SDK, and a
complete target set cannot safely live in model output, audit payloads or process memory. SQLite
also supplies the ordering, paging and crash-visible building state required by D-018.

**Rejected.** *Scope fields in tool arguments* — caller-controlled authorization. *Ambient
execution context* — hidden cross-call state. *One JSONL file per manifest* — poor scoped paging
and cleanup. *Content-hash every file* — unbounded I/O without preventing later drift.

**Consequences.** The runtime dispatches optional contextual tools with the same identity it
audits; legacy tools remain unchanged. Incomplete inventories never return approval-ready
references. V1.1-D must bind destructive approval to manifest instance metadata as well as the
deterministic content hash and must revalidate every target. ADR-0026 is normative.

---

### D-022 — Tools can tighten approval policy and emit bounded audit summaries

**Decision.** A manifest may require explicit human approval even when configured policy would
otherwise permit automatic execution; it can never override `forbidden`. An optional
approval-binding callback atomically consumes durable preflight state before execution. An
independent optional audit-summary callback may add at most 8 KiB of aggregate, non-sensitive JSON
to the tool-call audit event; the runtime never audits an arbitrary full tool output.

**Reason.** Permanent recursive deletion must not become automatic through a broad High-risk
policy rule, and its approval must consume exactly one fresh manifest. Investigation still needs
hash, counts and outcome without placing thousands of paths in an append-only event. Generic
opt-in contracts preserve the package boundary and avoid naming a filesystem tool in the runtime.

**Rejected.** *Hard-code `fs.delete_tree` in policy/runtime* — violates package independence.
*Trust policy defaults for mandatory approval* — configuration could weaken a safety invariant.
*Audit complete tool output* — may be large, sensitive or attacker-controlled. *Place every path
in tool arguments* — bloats model, approval and audit boundaries.

**Consequences.** `ToolManifest.RequiresExplicitApproval`, `IApprovalBoundTool` and
`IToolAuditSummaryProvider` are additive V1.1 preview SDK surface. Runtime argument types are
validated before these hooks. Summary-provider failures cannot fail execution and oversized
summaries are discarded. ADR-0027 is normative.

---

### D-023 — `web.fetch` validates destination addresses at connect time, not before it

**Decision.** SSRF and DNS-rebinding defense for `web.fetch` is enforced inside
`SocketsHttpHandler.ConnectCallback`: the callback itself resolves the target host, validates every
resolved address against a deny-by-default IP-range policy, and connects only to an address it just
validated. `AllowAutoRedirect` is disabled; the package's own bounded redirect loop revalidates each
hop through the same connect path, and a scheme downgrade on redirect is rejected by default.
`web.search` calls only one fixed, operator-configured SearXNG endpoint and does not go through this
path — that endpoint is operator-trusted configuration, not model-influenced input.

**Reason.** A check performed before the connection (resolve, validate, then call
`HttpClient.SendAsync`) leaves a window where the name can resolve to a different, disallowed
address by the time the connection is actually made — the same time-of-check/time-of-use shape S11
already names for filesystem paths. Validating inside the callback that performs the connection
closes that window by construction, and gets redirect-target revalidation for free, since a
cross-host redirect always opens a new connection.

**Rejected.** *Validate once before sending, trust the client's own connect.* Rejected: the TOCTOU
window this ADR exists to close. *Inspect the final response URI after redirects complete.*
Rejected: detection after the request already reached a denied host is not prevention. *Block by
hostname/domain blocklist.* Rejected: trivially bypassed by any name resolving into a denied range.
*Let automatic decompression run and cap only final text length.* Rejected: decompression happens
before any size check would run.

**Consequences.** `bOps.Packages.Web` needs no change to `bOps.Abstractions`; `RiskLevel.Read` and
the existing `ToolManifest.Requires` capability-probe mechanism already cover both tools. Attack
tests require a real local HTTP listener and an injectable `IDnsResolver` rather than only recorded
fixtures. ADR-0028 is normative.

---

### D-024 — `bOps.Api` activates plugins too, and one plugin's failure never takes the host down

**Decision.** `bOps.Api` now wires `PluginManager` and calls `LoadAllEnabled()` at start-up, the
same composition `bOps.Cli` already used (`Plugins:StorePath`/`Plugins:RootPath` configuration, the
same `IToolRegistry`/`ISkillRegistry`/`IChatModelRegistry` this process already owns per rule A4).
Separately, `LoadAllEnabled()` now isolates each plugin's activation: a `PluginOperationException`
or `PluginValidationException` from one record is caught, its message has the plugin's own
`InstallPath` scrubbed to a fixed placeholder, and the loop continues to the next plugin instead of
propagating past the first failure. The per-plugin results are exposed as
`PluginManager.StartupLoadErrors`; `PluginManager.IsActivated(id)` answers "is this plugin's
package actually registered in this process right now," distinct from `PluginRecord.Enabled`
(persisted operator intent).

**Reason.** `bOps.Api` runs its own `AgentRunner` for every task started from the dashboard
(`AgentsEndpoints`/`AgentTaskLauncher`) — it needed the same plugin-contributed tools/Skills the CLI
already sees, not a second, parallel loading mechanism. Separately, V1.1-F's read-only catalog
needs "enabled but did not load" to be an observable, survivable state — before this change, one
plugin with a revoked trust key or a corrupted install directory crashed the entire host at
start-up (on both CLI and API), which is exactly the moment an operator most needs the catalog to
still work.

**Rejected.** *Duplicate a slimmed-down plugin loader inside `bOps.Api`.* Rejected: would fork the
one place ADR-0020's trust/signature/activation logic lives, for no reason — every other package
(Filesystem, Web, Docker, …) is already wired independently per host process; plugins were simply
the one exception. *Catch `Exception` broadly in `LoadAllEnabled`.* Rejected by
`agentic/02-coding-standards.md`'s error model — only the two exception types this path is
documented to throw are caught; anything else is a broken invariant and still propagates.

**Consequences.** No `bOps.Abstractions`, risk-model, policy-semantics or audit-schema change —
`PluginManager.LoadAllEnabled`'s return type changes from `void` to
`IReadOnlyDictionary<string, string>`, a source change internal to `bOps.PluginHost` and its two
callers (`bOps.Cli`, `bOps.Api`). No ADR is required by the subject table in `05-workflow.md`.

---

### D-025 — ADR-0029 lands; provider configuration is fully UI-managed, not just key insertion

**Decision.** V1.1-G implements ADR-0029 as designed (encrypted local vault, AES-256-GCM,
HKDF-SHA256 master-key derivation, `expectedVersion` optimistic concurrency, fail-closed startup)
with one scope extension made during implementation, on explicit operator direction: a provider's
full non-secret configuration — endpoint, model, tool-calling support, and an open extras bag — is
persisted and administrator-managed from the Angular Settings page (`SettingsStore`/
`ProviderProfile` in `bOps.Runtime`), not only its API key. The key still lives only in the
encrypted vault, keyed by the same provider id; the profile lives in a separate plain, non-secret
`settings.json`, since it holds nothing that benefits from encryption.

**Reason.** Exploration surfaced a real design gap the task file's "provider selection" wording
left implicit: every first-party provider package requires an operator-supplied `BaseUrl` with no
built-in default, so switching the active provider while leaving `BaseUrl`/`Model` pinned to
whatever the single `appsettings.json` `ModelProvider` block configured would point the newly
selected provider's key at the wrong endpoint. Asked how to resolve this, the operator chose full
UI-managed provider configuration over the narrower alternatives (key-only management with a
read-only provider switch, or extending the mutation surface to accept raw endpoint values without
persisting them structurally).

**Rejected.** *Restrict "provider selection" to read-only, key-only management.* Rejected by
explicit operator instruction — would not resolve the underlying BaseUrl/Model mismatch and leaves
the feature unable to do what was asked. *Wire `ExtraParameters` into `ChatModelOptions`/
`IModelProviderPackage.Create`.* Rejected for this batch: no first-party provider package accepts
anything beyond `ChatModelOptions`'s existing fields, and `ChatModelOptions` is a
`bOps.Abstractions` type — extending it speculatively, for no provider that concretely needs it
today, is exactly the premature-generality the coding standard warns against. `ExtraParameters` is
persisted and returned by the API so a future provider has somewhere to read it from, but affects
no runtime behavior yet — a documented, deliberate gap.

**Consequences.** `GET /api/settings` describes two independently-present halves per provider (key
metadata from the vault, profile from `settings.json`) joined only by provider id — a caller must
read both to fully describe one provider. Provider selection and profile/key changes take effect on
the next restart, matching the existing composition-root-only resolution of `ModelProvider` — no
live-reconfiguration of `IChatModelRegistry` was introduced. See ADR-0029 for the full design,
including the precedence rule (`ProviderResolution`), the masking formula, and the documented
Windows ACL-hardening gap.

---

### D-026 — V1.2 multi-agent: fixed pipeline, human-only approval, side-effect journal, all surfaces

**Decision.** Four choices, taken by the operator on 2026-09-18 before ADR-0030 was drafted:
(1) the orchestrator is a deterministic runtime pipeline — Discovery, Diagnostic, human approval,
Remediation, Verification — and the model reasons only inside Discovery and Diagnostic; (2) agents
never approve an action, approvals stay human and hash-bound; (3) only steps with side effects are
journaled (intent before, outcome after), read-only roles restart from their beginning, and an
ambiguous step is reconciled by its declared verification and otherwise fails closed with no automatic
retry; (4) V1.2 ships the runtime, CLI, API and UI surfaces together.

**Reason.** A fixed pipeline keeps the delegation graph testable and closes the path by which poisoned
tool output could steer which role runs (S5). Human-only approval keeps separation of duties true by
construction. A side-effect journal is the smallest state that answers "did this action happen" after a
crash, which V1.2's resume goal requires. The operator chose full surfaces over runtime + CLI so an
operator can drive and inspect delegations from the dashboard.

**Rejected.** *Model-proposed delegation.* *Skill-only roles with no model.* *Agent approval, including
low-risk auto-approval.* *Journal of every step of every role.* *Resume only at role boundaries.*
*Runtime + CLI only, or runtime only.* Full reasons are in ADR-0030.

**Consequences.** ADR-0030 (Accepted 2026-09-18) governs V1.2. The
scope now includes an HTTP and UI surface, so the API authorization matrix and threat model grow.
`bOps.Abstractions` moves to `1.2.0-preview.1`, additive only. No per-role model, parallelism, agent
approval or wildcard envelope is in scope.

---

### D-027 — V1.2 envelope: a per-role requirement table, ADR-0031, and profile contracts in the SDK

**Decision.** Three choices, taken by the operator on 2026-09-19 when V1.2-C started. (1) ADR-0030's
"an empty intersection in any dimension is a denial" is refined by a per-role table: each envelope
dimension is required (empty is `DelegationDenied`), optional (empty means nothing permitted) or not
applicable (forced to empty or zero) for each of Discovery, Diagnostic, Remediation and Verification.
(2) The refinement is recorded as a new ADR-0031 that amends ADR-0030 §3, not as an edit to ADR-0030.
(3) Role profiles, the operator's authority request and a read-only profile source interface are
additive contracts in `bOps.Abstractions`; `bOps.Policy` implements the source from `policy.yaml`.

**Reason.** The literal rule grants authority no role uses and, once Discovery and Diagnostic have
spent the token budget, would deny an approved plan for a resource Remediation and Verification never
consume. A fixed table keeps least privilege by construction and makes the fixed pipeline of ADR-0030 §2
testable. `agentic/05-workflow.md` forbids changing an accepted ADR's meaning by editing it, and
ADR-0019 is the precedent for amending with a new one. The contracts sit in the SDK because
`bOps.Runtime` cannot reference `bOps.Policy`, and this mirrors `IPolicyEngine`.

**Rejected.** *The literal reading.* *Requirements declared per dimension in the profile*, which turns an
architectural fact into operator configuration. *A clarification section inside ADR-0030.*
*Runtime-owned profile types with a host adapter*, which duplicates one concept in Api and Cli.

**Consequences.** ADR-0031 (Accepted 2026-09-19) governs V1.2-C enforcement.
`bOps.Abstractions` moves to `1.2.0-preview.2`. Targets and environments are not matched per call for
plain Read calls, because tools declare none; that limit is stated in ADR-0031 and carried into the
threat model (V1.2-L).

### D-028 — V1.3-A: one `system.events` tool, journalctl run directly, no new privilege

**Decision.** Four choices made while implementing V1.3-A (ADR-0032). (1) One `Read` tool, `system.events`, with the same
manifest, arguments and result on Windows and Linux; no `service.logs`, `journalctl.*` or `eventlog.*`. (2) Linux collects
with `journalctl` started directly (fixed switches, values as separate arguments, fixed environment), not with a libsystemd
binding. (3) Out-of-range arguments are rejected, never clamped, and names accept a small fixed character set. (4) bOps asks for
no extra privilege: what the host identity cannot read is reported as a gap (`partial` or `unavailable`), never as an empty log.

**Reason.** A binding adds a native dependency that minimal images lack, for a query `journalctl` already answers in structured
JSON, and the choice stays isolated behind the Linux tool. A clamped request is a wrong answer that looks right. Event messages
are attacker-influenceable text, so they stay data inside the delimited tool result and never reach audit or telemetry.

**Rejected.** *A libsystemd binding* (revisit if the process cost matters). *PowerShell or `wevtutil`* (a command surface). *A
`minSeverity` default of `warning`* (hides information events). *Raw native records* (unbounded, OS-shaped).

**Consequences.** `complete` is the only signal that an empty result can be trusted. Linux source and text filtering runs after a
10,000-record scan ceiling, so a rare source in a long window can be reported truncated. No change to the abstractions, policy,
runtime or persistence.

### D-029 — V1.3-B: nine Docker tools, builds only from allowed directories, no `.dockerignore`, no links

**Decision.** Five choices made while implementing V1.3-B (ADR-0033). (1) Nine typed tools are added and the existing eight are left
alone; removal of an image, removal of a volume and `docker.build` are High risk and always need a human, whatever the policy says.
(2) `docker.build` builds only from directories listed in `Docker:Build:Contexts`, which is empty by default and hides the tool
until it is set. (3) A build context containing any symbolic link or a `.dockerignore` is refused; bOps does not apply
`.dockerignore`. (4) Nothing is forced or pruned, an existing tag is never moved, registry credentials and build arguments do not
exist, and volumes never show their mount point or options. (5) Verification arguments keep the verifier's names, so the tag tool
takes `source` and `image`.

**Reason.** A build runs instructions and sends a directory to the daemon, and a volume removal destroys data: each needs an explicit
human decision and a narrow contract. A half-implemented `.dockerignore` would send files the operator meant to exclude, and links
would make the context depend on what the daemon does with them. The runtime carries verification arguments by name and this batch
does not change the runtime.

**Rejected.** *A generic Engine API or a `docker` CLI process.* *Honouring `.dockerignore` now* (deferred). *Following links inside
the context.* *Build arguments and secrets* (need their own threat model). *Prune, push, Compose, container create and exec.*
*Extending `docker.images`* (a frozen tool).

**Consequences.** Building needs one line of configuration and a longer tool timeout. Contexts with links or a `.dockerignore` are
prepared by the operator. Growing the surface later (prune, credentials, build arguments) is a new ADR each time.

### D-030 — V1.3-C: three new `Read` process tools, an additive `process.inspect`, WMI for what `Process` cannot see

**Decision.** Five choices made while implementing V1.3-C (ADR-0034). (1) `process.inspect` gains ten nullable fields and keeps
every existing one; three tools are added — `process.metrics`, `process.tree`, `process.modules` — and all four stay
`RiskLevel.Read` with no `VerificationSpec`, which is the convention every read-only `system.*`/`process.*` tool already follows
and which the conformance suite already asserts. (2) Windows reads the parent PID, executable path, command line and owner from
`Win32_Process` through `System.Management`, added to the Windows package only; Linux reads `/proc` directly. (3) CPU is
host-normalized across every processor, a process that exits between the two samples is `exists:false, partial:true`, and a rate
is never negative. (4) The tree is a depth-first walk with children in PID order, bounded in depth and rows, and a `rootPid` that
is not running is `rootFound:false`, never an empty tree. (5) Neither the environment of a process nor a `process.start` exists,
here or later.

**Reason.** Verification confirms an effect; an observation has none, and a read tool that declared one would be asserting that
reading a live machine twice gives the same answer. `Win32_Process` is the only supported way to read another process's parent,
command line and owner without a toolhelp snapshot that would give neither the command line nor the owner; it is the managed CIM
client, not a command surface. Normalizing CPU across processors makes 100 mean the machine rather than one core. An environment
block routinely carries credentials, and a redaction list is a blacklist whose one miss is the one that matters.

**Rejected.** *A generic `process.start` or a shell running `ps`/`tasklist`* (rule S1; this batch exists to make it unnecessary).
*Returning a redacted environment.* *A toolhelp snapshot instead of WMI* (revisit if WMI's per-call cost dominates).
*`NtQueryInformationProcess` and a PEB walk* (undocumented, and one field from the environment block). *Separate
`process.parent`/`process.children` tools.* *Folding sampling into `process.inspect`*, which verifies `process.stop` and
`process.kill` and must stay instantaneous. *Clamping out-of-range arguments* (D-028). *A `maxOutputBytes` on `process.tree`*,
which the task's contract did not name.

**Consequences.** The Windows package gains one Windows-only NuGet dependency. `process.tree` costs one WMI query plus one owner
lookup per returned row, which is what the default 200-row bound keeps inside the tool timeout. On Linux an unprivileged host
reports `null` I/O and executable path for other users' processes, and `privateMemoryMb` is `null` on kernels without `RssAnon`.
No change to the abstractions, policy, runtime, persistence or audit.
