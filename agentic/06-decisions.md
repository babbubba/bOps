# 06 — Decision register

Settled questions. Each entry records what was chosen, what was rejected, and why — so the
alternatives are not re-proposed without new information.

A decision is changed by an ADR that supersedes it, never by an edit to this file.

All entries: decided **2026-09-14**, status **Accepted**.

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
