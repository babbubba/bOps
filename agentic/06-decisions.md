# 06 — Decision register

Settled questions. Each entry records what was chosen, what was rejected, and why — so the
alternatives are not re-proposed without new information.

A decision is changed by an ADR that supersedes it, never by an edit to this file.

All entries have status **Accepted**; D-042 was accepted 2026-10-06 (architecture only; implementation not started). D-001–D-012 were decided 2026-09-14, D-013–D-015 on
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
live-reconfiguration of `IChatModelRegistry` was introduced *(restart-to-apply superseded by ADR-0045/D-042)*. See ADR-0029 for the full design,
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

### D-031 — V1.3-D: sockets/routes/neighbors as a sibling native package, three tools needing no OS split

**Decision.** Seven new `Read` tools (ADR-0035): `network.sockets`, `network.routes`, `network.neighbors`,
`network.interface_stats`, `network.dns_query`, `network.traceroute`, `network.ntp_probe`, contributed by new
`bOps.Packages.Network.Native.Core`/`.Windows`/`.Linux` packages, registered alongside the existing, unchanged
`bOps.Packages.Network`. All seven stay `RiskLevel.Read` with no `VerificationSpec`, following the same convention
D-030 restates for `process.*`. `network.dns_query`, `network.traceroute` and `network.ntp_probe` need no
OS-specific collection (system resolver / a minimal typed UDP DNS client, `Ping` with increasing TTL, a minimal
SNTP client) and are concrete classes in `.Core` rather than abstract bases — new for this codebase, called out
explicitly in the ADR. Windows reads socket/route/neighbor tables via `GetExtendedTcpTable`/`GetExtendedUdpTable`/
`GetIpForwardTable2`/`GetIpNetTable2`, parsed by fixed byte offset because both route and neighbor rows embed a
`SOCKADDR_INET` union with no single C# `StructLayout`. Linux reads `/proc/net/{tcp,tcp6,udp,udp6}` plus a bounded
`/proc/<pid>/fd` scan for socket-to-PID mapping, `/proc/net/dev` + `/sys/class/net` for interface counters, and
exactly two fixed, argument-listed invocations — `ip -j route show`/`-6` and `ip -j neighbor show`/`-6` — the task
spec explicitly permits, via `ProcessStartInfo.ArgumentList`, no shell, no model-supplied argument ever appended.

**Reason.** `bOps.Packages.Network`'s own doc comment already states the BCL abstracts routing/interfaces "well
enough" for its existing six tools' shape, but owner-PID socket mapping and the full routing/neighbor tables need
native APIs the BCL does not expose, so this is additive rather than a rework of a package with tools already in
production use. `SOCKADDR_INET`'s union shape cannot be marshalled as one struct without runtime branching on the
family field either way, so fixed-offset reads are the explicit version of what an `[StructLayout(Explicit)]`
attempt would still need to do. `network.route`'s existing default-gateway/connected-subnet summary answers a
cheaper, different question than `network.routes`' full table and is kept, not deprecated.

**Rejected.** *A generic `network.exec` or shelling to `netstat`/`ss`/`route`/`arp`/`tracert`* (rule S1). *Reworking
`bOps.Packages.Network` in place* (breaking change to six shipped tools). *`GetIfEntry2` via a second P/Invoke
struct family for interface stats* (the task spec allows "equivalent BCL counters";
`NetworkInterface.GetIPStatistics()` already covers it). *A full DNS/NTP client dependency* (exposes a raw query
surface this task explicitly excludes). *Multiple probes per traceroute hop* (task specifies one probe per hop in
the first version). *Treating NTP stratum 16 as a valid-with-warning reading* (an unsynchronized server's offset is
not a fact worth reporting as trustworthy).

**Consequences.** `bOps.Packages.Network.Native.Windows` P/Invokes `iphlpapi.dll` directly, no new NuGet dependency.
`bOps.Packages.Network.Native.Linux` runs exactly the two named `ip` invocations and nothing else. An unprivileged
Linux host cannot map every socket to a PID (another user's `/proc/<pid>/fd` is unreadable), reported as
`pidMappingComplete: false`, never a silent zero. No change to the abstractions, policy, runtime, persistence or
audit.

### D-032 — V1.3-M1: product-neutral entitlement boundary (ADR-0036)

**Decision.** Entitlement applicability is explicit, trusted-host-owned and product-neutral. Provider absence or failure
fails closed only for governed operations; unrelated standalone OSS remains usable. Policy, approval, the delegated Tools
envelope and entitlement are independent, monotonic restrictions. The runtime freshly evaluates entitlement immediately
before every governed tool invocation, using a fresh opaque `RequestBinding` echoed by the provider; authorization cannot
be reused across retry, resume, replan or delegation. Post-action verification stays within the applicable delegated
Tools envelope and entitlement boundary. If runtime authorization prevents invoking verification, Runtime returns
Inconclusive without invoking the package evaluator; when verification executes, the package evaluator alone interprets
its result. M2 adds `AuthorizationKind.EntitlementDenied = 5`, preserving existing numeric enum values, and entitlement
denial stops prepared or delegated plan execution. No commercial tier, SKU, vendor token or business logic enters public
bOps.

**Reason.** A host-owned applicability boundary keeps provider health from disabling unrelated OSS operations, while a
fresh provider-bound request at each invocation prevents stale authorization from crossing execution or verification
boundaries. Verification remains governed by the existing envelope and evaluator responsibilities in ADR-0016 and
ADR-0031.

**Consequences.** M2 can add neutral public contracts and execution enforcement under ADR-0036 without redesigning the
architecture or amending prior accepted ADRs. Provider details and commercial entitlement rules remain outside public
bOps.

### D-033 — V1.3-M4: safe local plugin lifecycle (ADR-0037)

**Decision.** Uploaded plugin archives remain untrusted until bounded validation completes; no plugin code executes during upload, staging or validation. Reuse the existing `PluginManager`/`PluginHost` and signature/trust infrastructure. Bound extraction and reject traversal, rooted paths, symlink/reparse entries and collisions. Keep installation separate from activation; install/replace uses staging, atomic promotion and deterministic crash recovery. Persist distinct `Current`, `Activation LKG`, `Transaction Rollback` and `Candidate` generation roles: failed replacement restores pre-transaction `Current` while preserving the true Activation LKG, and only successful activation advances Activation LKG. Enable requires explicit administrator action and activation confirmation. Enabled plugin code runs in-process with host privileges and is not sandboxed. Serialize same-plugin lifecycle mutations and protect them with ETag optimistic concurrency. Reserve idempotency before plugin identity is known at the accepted Node/Actor/IdempotencyKey boundary; operation kind, plugin identity, archive digest and other intent fields form canonical intent, so reuse with different intent conflicts. Invalid or failed candidates never replace the last safe generation. API/UI are non-authoritative for trust and transition legality; the public lifecycle stays product-neutral and contains no commercial/business logic.

**Consequences.** ADR-0037 (Accepted 2026-09-24) governs the V1.3-M plugin lifecycle architecture. M5 implementation and M6 review can proceed without redesign; enabled plugins execute with host privileges.

### D-034 — V1.3.x HARDEN-1: provider wire contract (ADR-0038)

**Decision.** Canonical tool names never change; each provider adapter maps them to strict wire names
(`^[A-Za-z0-9_-]{1,64}$`) through one closed, deterministic, injective per-request alias map (one internal source file
shared by the OpenAI-compatible and Anthropic adapters). A response tool name absent from the map is never passed through:
the call is rejected as an unknown tool. Blank or malformed argument payloads are validation failures, never `{}`. The
assistant turn records every tool call the model emitted; the one executed call keeps its result and the rest are
persisted on `PlanStep.UnexecutedToolCalls` and answered "not executed", so live and resumed history are identical. Plan
and replan calls carry no native tools; they receive a text catalog of tool names, risk and one-line descriptions.

**Consequences.** Three additive `bOps.Abstractions` members (`ModelToolCall.ToolNameError`, `ModelToolCall.ArgumentsError`,
`PlanStep.UnexecutedToolCalls`); no breaking change, no migration. Aliases never reach Runtime, Policy or Audit. Failure
classification, retry and timeouts stay with HARDEN-2.

### D-035 — V1.3.x HARDEN-2: model-call failure containment (ADR-0039)

**Decision.** The runtime owns a per-attempt model-call timeout distinct from the caller's cancellation: a timeout is
an audited `Timeout` failure, a genuine cancellation still propagates. Provider packages classify every failure into a
provider-neutral `ModelFailureKind` (`Transient`, `RateLimited`, `Timeout`, `Unreachable`, `Authentication`,
`QuotaExceeded`, `InvalidRequest`, `ContextOverflow`, `MalformedResponse`, `Unknown`; 402 is `QuotaExceeded`, 413 is
`ContextOverflow` only on explicit evidence) and make one attempt per `CompleteAsync`. `AgentRunner.CallModelAsync`
retries only `Transient`, `RateLimited`, `Timeout` and `Unreachable`, bounded by attempts (3) and a per-call budget
(300 s) that always wins, with jittered backoff and a `Retry-After` honoured only if it fits the maximum delay (30 s)
and the remaining budget. Every attempt gets its own `ModelCallRecord` and `ModelCallAuditEvent` (`ModelAttempt`,
`FailureKind`, `RetryDecision`, `RetryDelayMs`, `ProviderStatusCode`). Reasons are extracted by providers, redacted and
bounded to 500 characters by internal code on both sides; the sanitizer is not public API. A malformed plan/replan
reply (including duplicate keys at any depth) is audited as `MalformedResponse` and keeps the single corrective
re-ask. The API launcher backstop converts only a still-persisted `Running` task to `Failed` and records a narrow
`TaskExecutionFaultAuditEvent`.

**Consequences.** Additive public contract only (ADR-0039 lists it). A model timeout or an escaped runtime exception
can no longer leave a task `Running` in the API host. General task lifecycle audit, resume semantics and
compare-and-set transitions remain HARDEN-3's; context compaction on `ContextOverflow` is HARDEN-8's.

### D-036 — V1.3.x HARDEN-3: resume state machine (ADR-0040)

**Decision.** Resume is a persisted state-machine transition. A task has an `ExecutionAttempt` (initial execution 1,
each accepted resume +1, distinct from ADR-0039's `ModelAttempt`). One runtime function decides resumability in a fixed
order with deterministic refusal codes: `Delegated` and legacy `Unknown`-origin tasks, every persisted `Running` task
(with or without a local executor — no orphan resume), `Completed` and `PolicyBlocked` are refused; `Failed` (with or
without a plan), `Cancelled`, `MaxStepsReached`, `ReplanLimitReached` and `BudgetExceeded` are resumable only while the
lifetime step/replan caps and the currently configured token cap leave headroom, so no accepted resume is zero-work.
Resume acquisition is a real atomic conditional write through the additive `ITaskTransitionStore` capability (SQLite:
one conditional `UPDATE` on status and the new `execution_attempt` column); a store without it makes resume fail
closed, and no load/check/save emulation exists. The API answers 202 only after that transition and the launcher's
admission of the new attempt; a non-admitted attempt is contained `Failed` and answered 503. Every executor,
cancellation, backstop and containment write is fenced on `(Running, ExecutionAttempt)`. `MaxSteps`/`MaxReplans` are
per attempt, `MaxLifetimeSteps` (60) / `MaxLifetimeReplans` (12) are lifetime caps that always win, tokens are
cumulative and never reset, synthetic failure steps never count. Task origin is explicit metadata (`Ordinary`,
`Delegated`), never a text heuristic. A `TaskLifecycleAuditEvent` records starts, resume acceptance/rejection, terminal
writes and superseded executors, always with the execution attempt and without payloads.

**Consequences.** ADR-0040 (Accepted 2026-09-30, with operator amendments) amends ADR-0017 and ADR-0018. Additive
public contract only; one additive SQLite column. Pre-ADR tasks are no longer ordinarily resumable (unknown origin).
Orphan recovery (lease/heartbeat or other ownership proof), delegated resume (HARDEN-11), UI lifecycle (HARDEN-4) and
context notes on resume (HARDEN-8) stay out of scope.

### D-037 — V1.3.x HARDEN-7: typed stability evidence and temporal coverage (ADR-0032 amendment, ADR-0041)

**Decision.** (1) `system.events` and `system.crashes` gain one explicit `mode` argument (`raw` | `aggregate`), the
only aggregation switch of either tool: `system.crashes` defaults to `aggregate` (an intentional default change),
`system.events` keeps `raw` as its default (existing behaviour preserved; aggregation is opt-in). Aggregate returns
deterministic groups with `count`, `firstSeenUtc`, `lastSeenUtc` (events keyed by channel, source, unit, event id and
severity; crashes by kind, `eventName` when the kind is `wer`, code, application, module and `timestampKind`); raw
returns the schema-1 rows plus additive fields, with a stricter `complete` and, for crashes, a corrected `summary`, a
wider `kind` vocabulary and a fixed byte budget. (2) Long horizons are a separate day argument (`windowDays`,
`sinceDays`, 1–180, manifest constraints) valid only in aggregate mode and exclusive with the unchanged minute argument
(1–10080), so raw stays at 7 days through the HARDEN-6 manifest bound; the two cross-field rules are enforced once, in
a fixed order (both arguments; day argument in raw mode; then the rest), in the shared System.Core reader, with no
conditional-schema machinery in `bOps.Abstractions`. (3) Every one of the three tools returns a `coverage`
object (per history store: `oldestAvailableUtc`, `logMaximumBytes`, `state` `complete` | `partial` | `unknown`; directory
stores always `unknown`) and a per-source `examinedFromUtc`; retention coverage is distinct from completeness, but
`complete` (and so `ToolResultCompleteness.Complete`) now also requires `coverage.state == complete`, so a retention gap is
`Partial`, never hidden. (4) New `Read` tool `system.stability` (ADR-0041): eight fixed categories (unexpected shutdown,
kernel crash, kernel fault, hardware error, display fault, storage error, memory exhaustion, minidump), each
`applicable`, `notApplicable` or `notCollected` per platform; Windows reads fixed System-log tuples, the WER 1001
LiveKernelEvent display codes and the minidump directory (name, size, time only); `displayFault` is Display 4101 and
LiveKernelEvent `0x117`, `0x141`, `0x193` — graphics-stack fault evidence, not proof of a reset — and never `0x1a1`
(a win32k watchdog hang signature, listed by `system.crashes` as a kernel live dump); Linux reads the kernel journal
with fixed message rules (panic, oops, MCE/EDAC, OOM kill) and claims no Windows parity; signature groups plus a
timeline whose bucket width (hour, day, week) is derived from `windowDays`; a category or minidump counter whose
sources could not be read is `null`, a partial one a lower bound; no `drillDown` or other hint for another tool.
(5) Every crash and stability time carries `timestampKind` (`occurred` | `reported`); a time not proven to be an
occurrence is `reported`, and the kind is part of every crash and stability aggregation key; a minidump's file time is
`reported` and the date in its name is the descriptive `fileNameLocalDate`, never an occurrence time. A `BlueScreen` Report.wer `EventTime` is `reported` (operator-decided, evidence-driven correction discovered during the HARDEN-7 real-Windows validation, 2026-10-02: it is written after the next boot's Kernel-Power 41 — `0x50` 08:45:27Z vs 08:44:37Z, `0x1e` 14:26:56 vs 14:26:35, `0x3b` 20:59:23 vs 20:59:04); `LiveKernelEvent` and application Report.wer `EventTime` stay `occurred`, and no other time is promoted in its place (ADR-0041 §6). (6) Crash records merge only on a shared WER report GUID; nothing is correlated by proximity or
similarity. (7) `system.crashes` is per-crash evidence, `system.stability` machine-level signals; the two overlaps
(bugchecks, display live dumps) are stated and must not be summed. Schema versions: `system.events` and
`system.crashes` 1 → 2, `system.stability` 1.

**Reason.** The incident's evidence existed but was flooded (100 rows, 7 signatures), capped at 7 days, mislabelled in
time, invisible without provider/event-id knowledge, and silent about log retention. A typed `Complete` over two months of
a six-month request is the false negative this train removes.

**Rejected.** *Raising the minute maximum with a package-only raw check* (schema advertises refused values). *Generic
conditional schema in the SDK.* *Separate summary tools.* *Raw as the `system.crashes` default.* *Aggregate as the
`system.events` default* (independent review). *A boolean `aggregate` argument or any second aggregation switch.*
*Retention only via `Completeness`, or only via `coverage`.* *Caller-chosen providers, event ids or categories* (a
query language). *Fuzzy incident correlation.* *Parsing dumps.* *Inferring Linux unclean shutdowns.* *`0x1a1` as
display evidence; the minidump file-name date as occurrence; a `drillDown` hint* (independent review).

**Consequences.** SDK/API abstractions: additive, no break. Tool results: schema 2 for both amended tools;
`system.crashes` changes its default output shape (schema-1 readers pass `mode: raw`), `system.events` keeps its
default. More results are `Partial` by design. No change to `bOps.Abstractions`, policy, runtime, persistence or the
audit schema; all new contract types are package-local in `bOps.Packages.System.Core`. HARDEN-9 uses this `mode` and
adds no second aggregation parameter. Both ADRs are accepted as an operator decision through the HARDEN-7 architecture
gate; the independent review's blocking findings are resolved in their text. Implementation is HARDEN-7.

### D-038 — V1.3.x HARDEN-9: evidence reasoning and limitation disclosure (ADR-0042, ADR-0032 HARDEN-9 amendment)

**Decision.** Accepted 2026-10-03 (operator decision after the independent architecture review and its delta review,
PASS WITH NON-BLOCKING FINDINGS). (1) **Boundary.** No reasoning engine, no new tool, no relation, cause or confidence
field in code, no `bOps.Abstractions` change: deterministic code establishes and labels evidence, the model interprets
it, the runtime makes the limits visible and asks once for their disclosure, and nothing is rewritten. (2) **Taxonomy.**
Observed (what a tool result states, including its deterministic aggregates and exact-identity merges), derived (what
follows deterministically from observed evidence under stated preconditions), hypothesis, attributed cause (a record's
own causal statement, reported as that record's attribution, never as the model's conclusion), unknown. (3) **No
causality** from correlation, temporal order or proximity, frequency, co-occurrence, absence of other evidence or
partial coverage. (4) **Negative evidence.** No result proves that something did not occur; at most, for a `Complete`
result, no matching records were observed in the readable sources covering the requested window and filters; `Partial`
gives lower bounds; `Unavailable`, `notCollected`, `notApplicable`, `null`, truncated, shortened or excluded evidence is
unknown, never zero. (5) **Time.** ADR-0041 §6 is preserved: `occurred(A) ≤ reported(A)` per event, no promotion,
`system.events` record times never become occurrence times. Ordering different events is derived only on one
comparable clock domain, UTC, with no known clock change or restart between them and a gap above the coarser timestamp
precision (two seconds when unstated); otherwise it is a hypothesis input. (6) **Digest.** A deterministic, versioned
(`EvidenceLimitations/v1`), bounded (16 entries, 4,608 characters) evidence-limitations digest in step prompts, built
only from typed `ToolCallResult` fields and persisted step data, lists steps whose requested result was not obtained or
only partially available: `Partial`/`Unavailable`, non-validation failures (never superseded), shortened observations
(`Observation.StartsWith(Output, Ordinal)` false), and validation failures unless a later step on the same resolved
`ToolCall.ToolName` succeeded — the only supersession rule; unknown-tool rejections appear as `(unknown tool)` and are
never superseded. Typed metadata only selects fixed runtime-authored text (rules S5, C7). (7) **Disclosure.** When the
digest is non-empty and a deterministic detector finds no `Evidence limitations` heading, at most one re-ask
(`EvidenceDisclosure/v1`, `Agent:EvidenceDisclosureRetries` 0–1, default 1) asks for a restatement; it **never executes
a tool**, adds no step or replan, never changes the task status, and keeps the original answer unless the re-ask
returns an answer with a valid heading; the final step's `Description` records the outcome. (8) **`excludeSources`.**
`system.events` gains a comma-separated `String` (≤ 8 entries of ≤ 128 characters, ≤ 1,024 total, `source` character
set, no duplicates, not equal to `source`), matched case-insensitively on the canonical `source`, excluded natively on
Windows (`Provider[@Name!=…]`, before the scan ceiling) plus a shared post-filter, post-filtered on Linux, echoed as an
always-present sorted array; `mode` stays the only aggregation switch; `schemaVersion` stays 2. (9) HARDEN-7 residual
N-2 (`system.crashes` description: `BlueScreen` Report.wer `EventTime` is `reported`) is a mandatory HARDEN-9 item.

**Rejected.** A reasoning or correlation tool; relation, cause or confidence fields; a typed coverage field; parsing
package JSON in the core; appending or persisting limitations outside the model's answer; executing tools during the
re-ask; substring heading detection; a localized heading; superseding non-validation failures; a string-list parameter
type; wildcard or default exclusions; a second aggregation switch.

**Consequences.** Additive only: one runtime option, one `system.events` argument and output field, a longer system
prompt, at most one extra model call per limited task. HARDEN-9 detects and discloses observations shortened by the
observation budget but does not change it; the context-budget economy and the projection of full typed evidence belong
to HARDEN-8, which follows HARDEN-9 and inherits the interface of ADR-0042 §16.

### D-039 — V1.3.x HARDEN-9: delegated Diagnostic structured-output disclosure (ADR-0042 amendment)

**Decision.** Accepted 2026-10-03 (operator decision after independent amendment delta review PASS; previous R1
resolved, no new blocking or non-blocking findings). For a delegated `AgentRoleKind.Diagnostic` final response, HARDEN-9
does not enforce the separate prose `Evidence limitations` section or make a disclosure re-ask: the original structured
JSON final reply is preserved. The typed role gate selects the `EvidenceLimitations/v2` structured-output-safe instruction
while the digest and common evidence rule remain in effect. A runtime limitation qualifies only a materially affected
finding that remains supported by its existing `evidenceIds`; it is not itself a finding. A limitation affecting no
finding stays outside the Diagnostic JSON and remains recoverable from the persisted role task. Ordinary tasks and
Discovery retain disclosure enforcement. This amends ADR-0042's HARDEN-9 final-answer rule and supplements D-038
without rewriting it.

**Rejected.** A synthetic finding solely for a limitation; added, changed or invented `evidenceIds` to carry one; a
`severity` change solely to carry one; attaching an unrelated limitation to a finding; dropping an otherwise supported
finding because of an unrelated limitation; a generic exemption for delegated roles or JSON output.

**Consequences.** The HARDEN-9 runtime correction may proceed but remains unimplemented and unverified. HARDEN-11 owns
evaluation of typed limitation metadata in delegation results and approval visibility, and `FindingsOf` parsing
hardening as defence in depth; HARDEN-9 does not depend on completion of those follow-ups.

### D-040 — V1.3.x HARDEN-10: browser web session (ADR-0043)

**Decision.** Accepted 2026-10-05 (operator decision after the independent security architecture review, CHANGES
REQUIRED with blocker B-1, resolved in `0406469`, and the B-1 delta security review, PASS). The browser exchanges the
API key once (`POST /api/session`, JSON body only, origin-gated, rate-limited) for an opaque 256-bit server-side session
in the cookie `__Host-bops_session` (`HttpOnly`, `Secure` always, `SameSite=Strict`, `Path=/`, no `Domain`;
browser-session cookie by default, `Max-Age` = remaining absolute lifetime with "keep me signed in"). The server stores
only `SHA-256(token)` and a token-keyed binding to the credential id and its current secret, in its own `sessions.db`.
One ordered credential resolution (`ApiCredentialAuthority`) serves Bearer and sessions; a session is valid only while
current Bearer resolution of its credential's secret selects that same configuration entry, and its roles are
re-resolved on every request. Idle 60 min, absolute 12 h, equality expires, absolute never slides. A policy scheme
selects Bearer whenever an `Authorization` header is present (never a downgrade to the cookie). Cookie-authenticated
unsafe requests need `X-bOps-Request: 1` and an `Origin` (or, only without `Origin`, `Referer`) equal to a configured
`BrowserSession:Origins` tuple. Bearer clients are unchanged.

**Rejected.** The key in browser storage; ASP.NET Core cookie authentication with a ticket store; antiforgery tokens;
comparing `Origin` with `Host` or forwarded headers; a peppered token digest; an unkeyed key fingerprint; falling back
from an invalid Bearer to the cookie; role snapshots; a session table in `tasks.db`; token rotation; CORS.

**Consequences.** Additive public contract (two endpoints, a second scheme, a configuration section, a third SQLite
file); the developer exception page is replaced by a request-data-free problem response in every environment; the UI
must be opened at exactly a configured origin. Residual risks: the shared `localhost` cookie jar across ports and an
open polling tab living to the absolute limit (ADR-0043 §16, Residual risks).

### D-041 — V1.3.x HARDEN-11: request-dependent delegation authority and operability (ADR-0044)

**Decision.** Accepted 2026-10-05 (operator decision after the independent HARDEN-11 authority/security architecture
review, PASS, blockers 0, non-blocking N-1…N-13 incorporated; N-1, N-5 and N-7 are mandatory Phase-1 obligations). A
diagnosis-only delegation (`Remediation == null`) requires only Discovery and Diagnostic; a remediation request
(dry run included) still requires all four roles, unchanged and fail-closed. The root envelope is built from the
required roles only (diagnosis-only: no Skills or Capabilities, `MaxRisk ≤ Read`); a not-required role's absence or
valid profile can neither block nor widen the run, while a malformed one still fails the whole policy load. Readiness
(`GET /api/delegations/readiness`, `bops delegate readiness`) is a runtime projection of the reducer reporting
profile/role-shape readiness — four states (`ready`, `missing`, `malformed` = present but not usable, `notRequired`),
snake_case `reasonCode`, unknown codes treated as not ready — not specific-request executability: the selected Skill,
Capability, target, environment and input are validated at submit and start. `bops delegate profiles init --read-only`
lists today's available Read tools by exact name (no wildcard), emits YAML safely and verifies it through
`PolicyConfigLoader` before output, with fixed budgets per role (15 steps, 150,000 tokens, 30 minutes, `read`,
`single`, `[local]`), and `--overwrite` refuses anything but a generator-equivalent file. Typed evidence-limitation
metadata (`EvidenceLimitation`, `DiagnosticReplyOutcome`, on `DelegationRoleRun`) is an additive `bOps.Abstractions`
contract covering role model-loop evidence. `CapabilityManifest.InputSchema` is validated at Skill registration and
before Capability code on every Capability invocation, delegated or not. Diagnostic JSON with a duplicate key at any
depth fails closed with zero findings.

**Rejected.** Inert generated Remediation/Verification profiles; `Optional` R/V in the ADR-0031 table; a root from all
present profiles; partial policy loading; readiness computed in API or UI; a fifth readiness state; request-aware
readiness in HARDEN-11; YAML merge; `FindingsOf` first-complete-object tolerance; limitations as `Evidence`, `Finding`
or `SkillReport` fields.

**Consequences.** A fresh installation can diagnose after one reviewable read-only step; mutation still needs four
hand-written profiles. Additive public contract (four `bOps.Abstractions` types and three init properties, two
`viewer` endpoints, typed `400` codes, three CLI commands, exit code `8`). Capability input validation is a deliberate
tightening outside delegation. Sensitive Capability input stays persisted as plain JSON in the delegation store; no
encrypted secret storage is introduced. HARDEN-11 is not implemented; Phase 1 starts next.

### D-042 — ACCEPTED (architecture; implementation not started) — V1.3.x HARDEN-13: execution-pinned live provider configuration and fallback chain (ADR-0045)

**Decision (operator decisions incorporated; accepted 2026-10-06 after independent architecture review and the targeted B-1 delta review PASS).** Settings-driven provider, endpoint, model and native-tool capability changes publish one immutable
effective configuration for subsequently admitted executions without an API restart. One ordinary task, or one whole
delegated D/D/R/V run, durably pins that non-secret configuration across model calls, approval waits, execution attempts
and restart/resume. Credentials are excluded from the pin and resolved from the existing secret authority for the
pinned candidate on every attempt; rotation/removal therefore affects the next attempt even in an active execution.
Plaintext remains only in the existing vault/secret path and attempt memory. An exact non-secret snapshot that cannot
be reconstructed, or a missing current credential, fails/refuses resume rather than substituting current Settings.
Explicit environment configuration retains precedence and Settings reports persisted-but-shadowed values. The
operator approved in principle an explicit, ordered host-level **fallback chain** that advances only after
same-provider retries exhaust on `Transient`, `RateLimited`, `Timeout` or `Unreachable`, is sticky and monotonic for
that execution, never resets global budgets/deadlines, and never falls back on any other current or future failure kind
by default. Runtime remains provider-neutral and receives one execution-scoped `IChatModel`.

Review B-1 correction: the block-level `ModelProvider:ApiKeySecret` is bound only to the merged
`ModelProvider:Provider` value (before Settings selection) and is never used for any other provider (Settings-selected,
fallback or resumed); every other provider uses only its own provider-keyed vault entry, narrowing ADR-0029's "active
provider" wording. `AgentRunner` remains the sole owner of same-provider retries; `FallbackChatModel` owns only candidate
advancement via a narrow provider-neutral handshake. `AttemptsExhausted`/`RetryAfterExceedsLimit` on an allowed kind may
advance when a candidate and global budget remain; `BudgetExhausted` is terminal. A fallback transition is an additive
append-only audit decision, with independent `ProviderAttempt` and `FallbackOrdinal`. A fallback candidate with no usable
credential fails as `Authentication` without skipping; admission requires the primary credential only.

The operator selected `OpenRouter` / `openrouter/free` as the shipped zero-cost bootstrap default. It requires an
OpenRouter API key and is intentionally non-deterministic: actual models can vary per call and remain visible in
model-call/audit information. Getting Started and Settings must disclose that behavior and recommend an explicitly
selected specific model for important troubleshooting, reproducible analysis and especially remediation. Router mode
is distinct from the fallback chain.

**Alternatives proposed for rejection.** Reload or resolve non-secret provider configuration mid-task; transient
lifetime without an atomic snapshot; pinning per call, role or execution attempt; pinning or preserving historical
credentials; persisting plaintext or an unkeyed secret fingerprint; resuming under current Settings; automatic provider
discovery, scoring or cost/health routing; fallback on permanent or unknown failures; per-call or process-global
fallback stickiness.

**Consequences.** ADR-0045 supersedes only ADR-0029's restart-to-apply consequence. Settings must expose
persisted/effective/shadowed state, task and delegation persistence gains an additive safe pin, model attempt records
gain only additive non-redundant primary/fallback/generation metadata, and the host gains atomic configuration
publication, per-attempt current-credential resolution and `FallbackChatModel`. Fallback support is disabled when its
explicit list is empty. HARDEN-13 Block B implementation is now authorized; none has started.
