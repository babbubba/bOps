# ADR-0049 — Unified prerequisite readiness and operational system messages

Status: **Accepted — 2026-10-08; implemented in three sessions (contracts and persistence; host wiring, plugins, API; fail-closed correctness, replan message, UI)**
Date: 2026-10-08
Amends: rule B4 (`ICapabilityProbe` becomes a compatibility view), [ADR-0025](0025-skill-provider-and-restricted-invocation.md) (Capability
manifests gain prerequisite declarations). Neither accepted ADR is edited; this ADR adds to them.
Task: [`2026-10-08-v1.3z-prerequisite-readiness-system-messages`](../../../agentic/_tasks/2026-10-08-v1.3z-prerequisite-readiness-system-messages.md)
Decision register: D-046

## Context

A tool declares `ToolManifest.Requires`, a list of capability ids. The host registers one boolean check per id on
`CachingCapabilityProbe`, `ToolRegistry.RefreshCapabilitiesAsync` snapshots the booleans, and a tool whose capability is
`false` silently disappears from the planner's catalog. Docker (`docker`, `docker.build-contexts`) and SearXNG
(`web.searxng`) work this way today.

That model cannot express what operators and future packages need:

1. **Registered is not available.** A tool or Capability that bOps knows about but cannot run is indistinguishable from
   one that does not exist. The operator has no place to read *why* `web.search` is missing.
2. **Optional dependencies.** A future `system.dump_analyze` needs Microsoft Debugging Tools (`kd.exe`) and is better
   with, but does not need, `dumpchk.exe`. A boolean `Requires` can only hide the tool or ignore the dependency.
3. **Skills have no equivalent at all.** `CapabilityManifest` cannot declare a prerequisite, so a Capability whose
   external dependency is absent is offered and fails at preparation.
4. **Plugins cannot contribute checks.** Only the host composition root can call `CachingCapabilityProbe.RegisterCheck`;
   an enabled third-party plugin has no route to declare how its database client or daemon is checked.
5. **Nobody is told.** A daemon that stops is visible only as a shorter tool list. Logs are for developers, the audit
   chain records actions, and task evidence is model input; none is an operator inbox.

## Decision

### 1. Registered versus available

Every Tool and Skill Capability has two independent facts: **registered** (bOps loaded and validated it) and
**available** (its required prerequisites are satisfied right now). A component may be registered and unavailable. An
unavailable component is never offered to a model and never resolves for execution. A component whose *optional*
prerequisites are unsatisfied stays available and is **degraded**.

### 2. Prerequisite contracts (`bOps.Abstractions`, additive)

| Type | Purpose |
|---|---|
| `PrerequisiteDescriptor` | Stable id, display name, description, `PrerequisiteKind`, operator remediation text, check timeout |
| `IPrerequisiteCheck` | One package-owned, **read-only**, cancellable check returning a `PrerequisiteCheckOutcome` |
| `IPrerequisiteProvider` | Optional interface a package entry point implements to contribute checks |
| `PrerequisiteCheckOutcome` | What a check observed: state, stable code, message, metadata — no id, no timestamp |
| `PrerequisiteCheckResult` | Host-stamped result: id (from the descriptor) and `CheckedAtUtc` (from `TimeProvider`) added |
| `PrerequisiteState` | `Unknown`, `Available`, `Degraded`, `Unavailable`, `Error` |
| `PrerequisiteRequirement` | `Required`, `Optional` |
| `OperationalMetadata` | Bounded, JSON-native, secret-refusing key/value metadata shared with system messages |

A package never stamps its own id or timestamp onto a result, for the same reason it never stamps its own `PackageId`
(rule A11). `Unknown` is host-internal ("never checked"); a check returning it is recorded as `Error`.

**Contribution.** A package's existing `IToolProvider` or `ISkillProvider` entry point may additionally implement
`IPrerequisiteProvider`. The host discovers it the same way for first-party packages and for enabled plugins, registers
each check under the host-assigned `PackageId`, and unregisters them with the package. There is no package-to-package
resolution (rule A9): a component references a prerequisite only by its string id. A prerequisite id may be registered by
exactly one package; a second registration of the same id is refused, so a plugin cannot shadow a first-party check.

**Ids and codes.** Prerequisite ids are lowercase dotted (`^[a-z0-9][a-z0-9._-]{0,127}$`), the existing capability ids
included. Outcome and message codes are lowercase dotted/kebab (`^[a-z0-9]+([.-][a-z0-9]+)*$`, at most 64 characters).

### 3. Required and optional declarations

- `ToolManifest.Requires` keeps its meaning and becomes, by definition, **required** prerequisites.
- `ToolManifest.OptionalRequires` (new init property, default empty) lists **optional** prerequisites.
- `CapabilityManifest.Requires` and `CapabilityManifest.OptionalRequires` (new init properties, default empty) give
  Skill Capabilities the same two lists. The existing constructor is unchanged.
- One id in both lists of the same manifest is refused at registration; a blank optional id is refused.

The frozen 1.0 surface (ADR-0022) is untouched: only lines are added. `bOps.Abstractions` moves to `1.3.0-preview.3`.

### 4. Host-owned registry and the `ICapabilityProbe` view

`bOps.Runtime.PrerequisiteRegistry` owns registration, execution, timeouts, caching and the current state of every
check. It runs a check under a linked timeout (descriptor timeout, default 10 s, clamped to 1–60 s); a timeout, a thrown
exception or an invalid outcome becomes `Error` with a fixed code (`check-timeout`, `check-failed`,
`check-invalid-result`) and a fixed message — exception text is never copied, because it can carry secrets. Caller
cancellation propagates.

The registry is the **mutable, host-owned** object and does **not** implement `ICapabilityProbe`. What a package receives under
that interface (rule A10) is `PrerequisiteRegistry.AsCapabilityProbe()`: a distinct read-only object whose only member is the
boolean compatibility view — `Available`/`Degraded` → `true`; `Unavailable`/`Error`/`Unknown` and unregistered ids → `false`. It
cannot be cast to the registry or to `IPrerequisiteRegistrar`, the host-only interface (`bOps.Abstractions`) through which the
plugin loader registers an `IPrerequisiteProvider` under a host-assigned `PackageId` and removes it again. The registrar is never
resolvable through `RestrictedPackageServiceProvider`. `ICapabilityProbe` and `CachingCapabilityProbe` are neither removed nor
renamed. A host-side `BooleanPrerequisiteCheck` adapts an existing boolean check so Docker and SearXNG migrate without behavioural
change.

`ToolRegistry` and `SkillRegistry` snapshot the **full `PrerequisiteState`** of each prerequisite from the host-owned
`IPrerequisiteStateSource` (the last recorded result — no I/O), or, for any arbitrary `ICapabilityProbe`, `true` → `Available` and
`false` → `Unavailable`. Required: `Available` → available; `Degraded` → available **and degraded**; `Unavailable`, `Error`,
`Unknown` → not available. Optional: `Available` → no degradation; every other state → available **and degraded**. A tool whose
required prerequisites are not satisfied stays hidden; `GetReadiness()` reports every registered tool, and `SkillRegistry` withholds
from `GetAvailableSkills` and `Resolve` any Capability in that position. A Capability with no `Requires` behaves exactly as before;
one with `Requires` and no probe or source fails closed.

### 5. System messages

A **system message** is an operator-facing operational notice. It is not application logging, not audit (it proves
nothing and is not hash-chained) and not model evidence (it never enters a model context).

`SystemMessage` (`bOps.Abstractions`): `Id`, `TimestampUtc`, `Node`, `Source`, `Severity`, `Code`, `Message`,
`Metadata`, and optional `TaskId`, `ComponentType`, `ComponentId`. `SystemMessageSeverity` is `Information`, `Warning`,
`Error`, `Critical` — no `Debug`/`Trace`. `Source` is `<kind>/<name>` (`prerequisite/docker`, `runtime/agent`,
`plugin/acme.postgres`). `Code` is a stable machine value (`prerequisite.missing`). `Message` is at most 1,024 characters.

### 6. Bounded, secret-refusing metadata

`OperationalMetadata` holds at most 32 entries; keys match `^[A-Za-z][A-Za-z0-9._-]{0,63}$`; values are JSON strings
(≤ 512 characters), numbers, booleans, null, or arrays of at most 32 strings; the compact serialization is at most
4,096 UTF-8 bytes. It refuses keys whose normalized name contains `password`, `passwd`, `secret`, `token`, `apikey`,
`credential`, `privatekey`, `connectionstring`, `authorization` or `cookie`, and string values carrying URL user-info
(`scheme://user:pass@`) or a `Bearer ` prefix. This is defence in depth: the contract states that metadata must never
carry a secret, and the check makes the common accidents fail loudly at construction.

### 7. Transition semantics

Checks do not write messages; transitions do. `PrerequisiteTransitionRecorder` compares each new result with the
persisted state of the same `(node, prerequisite)` by **fingerprint** `state|code`:

| Previous → current | Message |
|---|---|
| none → `Available` | none (quiet first success) |
| none or other → `Unavailable` | `prerequisite.missing` — `Warning` if any component requires it, else `Information` |
| other → `Degraded` | `prerequisite.degraded` — `Warning` if required by any component, else `Information` |
| `Unavailable`/`Degraded`/`Error` → `Available` | `prerequisite.recovered` — `Information` |
| other → `Error` | `prerequisite.check-failed` — `Error` |
| same fingerprint | none |

Only the fingerprint decides; a changed message text or metadata alone never emits. One message is written per
prerequisite transition, never one per affected tool: its metadata carries `affectedComponents` (at most 16 entries) and
`affectedComponentCount`. The state row and the message are written in one SQLite transaction, guarded by a
compare-and-set on the previous fingerprint, so two concurrent identical observations write one message.

### 8. Persistence

System messages and prerequisite state live in one node-local SQLite database owned by `bOps.Memory`
(`SqliteSystemMessageStore`, schema `user_version = 1`), never in `audit.jsonl`, task JSON or log files. A newer schema
version is refused on open. Query (`SystemMessageQuery`): optional inclusive `FromUtc`, inclusive `ToUtc`, exact
`Severity` and case-insensitive `Text` contained in `Message`, all combined with AND; ordering is
`TimestampUtc DESC, Id DESC`; pagination is keyset over that order with an opaque, versioned cursor (never `OFFSET`);
page size defaults to 50 and must be 1–200. Retention is time-based: `PurgeOlderThanAsync`, default 90 days
(`SystemMessageRetention.Default`). Archival and export are out of scope.

### 9. Host wiring (Session 2)

- **Remediation.** The transition recorder is given the registered descriptor: a missing or degraded prerequisite's message reads
  `<DisplayName> is unavailable. <Remediation>` (remediation truncated to 600 characters, message to 1,024) and its metadata carries
  `displayName`, `kind`, `remediation` (≤ 512) and the descriptor's static metadata as `descriptor.*`. The check's own free-text
  message is not copied into the inbox. A changed descriptor never changes the fingerprint.
- **Bounded refresh.** `RefreshAsync` runs at most `Prerequisites:MaxConcurrency` checks at once (default 4, 1–32) on a fixed worker
  set, each check once, results ordered by id; a failing or timed-out check is a result, caller cancellation propagates.
- **One cycle.** `PrerequisiteReadinessService` serialises a cycle: registry refresh → atomic transition and message records →
  Tool and Skill snapshots. The API boot refresh, the periodic coordinator and the CLI's one-shot refresh all call it.
- **Boot order (API).** Compose first-party providers and checks → activate enabled plugins (registers their providers) → one full
  refresh → only then accept work. A missing ordinary prerequisite never stops the host: the components stay registered, are not
  available, and a `Warning` says why.
- **Periodic refresh (API only).** One hosted coordinator, `Prerequisites:RefreshIntervalSeconds` (default 30, 5–3600). The CLI has
  no timer.
- **Plugins.** A tool or Skill plugin's entry point that also implements `IPrerequisiteProvider` is registered under the plugin's
  host-assigned id *before* its tools and Skills (so a later failure rolls it back), and removed before its collectible load context
  is released. A model-provider plugin's providers are not registered.
- **API.** `GET /api/system-messages` (filters `fromUtc`, `toUtc`, `severity`, `contains`, `pageSize`, `cursor`; AND; keyset) and
  `GET /api/prerequisites` (current-state projection of registry and readiness; not a second store), both Viewer role.
- **Retention.** `SystemMessages:RetentionDays` (default 90): one bounded purge when the API starts, then every
  `RetentionIntervalMinutes` (default 360); never on insertion. The store deletes in batches of 1,000.

### 10. Fail-closed correctness (Session 3)

- **Unrecorded is unavailable.** The cycle's order — check, record durably, then snapshot — is also an invariant: a prerequisite
  observation that could not be durably recorded never makes anything more permissive. When `PrerequisiteTransitionRecorder.RecordAsync`
  throws, the host replaces that prerequisite's result in the registry (`PrerequisiteRegistry.AddHostObservation`) with `Error` and the
  host-authored code `state-record-failed` before any Tool or Skill snapshot is taken, so required dependents are not available and
  optional ones are degraded; the successful check result is not reused. The text is fixed — no exception text reaches state, messages or
  the API; the exception is logged server-side. Other prerequisites are still recorded. The host overrides are replaced as a set at the end
  of each cycle (`SetHostObservations`), so a later successful record restores normal behaviour, and an unchanged persisted fingerprint
  writes no new message. If the message store itself is down nothing is written and nothing gains authority.
- **Declared but unregistered is observable.** After the checks run, the cycle computes *declared* prerequisite ids (the `Requires` and
  `OptionalRequires` of every registered Tool and Skill Capability, lower-cased) minus *registered* ids. Each is a host-observed
  `Error`/`not-registered` result recorded through the same recorder and store: persisted, deduplicated by fingerprint across refreshes
  and restarts, one message `prerequisite.not-registered` per prerequisite (not per component; severity `Error` if some component requires it,
  else `Warning`; text *"No package registered a check for prerequisite 'x'. Components depending on it cannot determine readiness."*;
  no remediation is invented without a descriptor). A check registered later transitions normally and its `Available` result writes the
  ordinary recovery message. `GET /api/prerequisites` reports the same state. An id the state store could never hold (a legacy
  `Requires` entry that is not a valid prerequisite id) stays unavailable, is logged, and is not recorded.
- **Plugin lifecycle refreshes immediately.** At the API boundary — not in `bOps.PluginHost`, which stays independent of `bOps.Runtime`,
  and never inside package code — a successful (not replayed) enable or disable runs one `PrerequisiteReadinessService.RefreshAsync`
  before the HTTP response. The lifecycle transaction is already committed and authoritative: a failing refresh is logged, changes
  nothing about the result, and the periodic refresh is the retry. Rejected, stale, unconfirmed, failed and replayed operations, and
  recovery (which only restores an installed-disabled generation), refresh nothing.

### 11. Agent replan threshold message (Session 3)

`AgentRunner` (additive optional `ISystemMessageStore` constructor argument; no widening of the A10 package surface) writes one
`Warning` — source `runtime/agent`, code `agent.replan.threshold`, component `Runtime`/`agent` — when an *accepted* replan brings the
task's lifetime count to `Agent:ReplanWarningThreshold` (equality, hence at most once per task and never again after a resume that is
already past it; an uncommitted or failed replan never counts). Default 3 and, only when unset, capped by `Agent:MaxLifetimeReplans`
so existing configurations keep validating; an explicit value must be 1..`MaxLifetimeReplans`. The message carries the task id,
provider and model ids, lifetime count, threshold, limit and execution attempt — no goal, prompt, model reply, tool argument or output.
Writing it is bounded (5 s), best effort and logged on failure: it never fails the task, changes its state or triggers a retry.
General runtime eventing is not redesigned.

### 12. Operator UI (Session 3)

`/system-messages` (*System messages* / *Messaggi di sistema*) in the Angular UI reads `GET /api/system-messages`: From/To (explicit local →
UTC instant conversion, inclusive as the API defines), Severity, *Text contained* — all sent to the server on **Apply** and AND-combined;
**Reset**; newest first, 50 per page, forward and back through the opaque keyset cursor (Previous re-requests the cursor that produced the
earlier page; no offset, no client filtering or paging); loading, empty and API-error states; severity as word + glyph + accessible label;
expandable details with metadata rendered as escaped JSON text. `GET /api/prerequisites` has no page of its own: the boot and transition
messages already make actionable failures visible there. `SystemMessages:FilePath` (default `system-messages.db`, relative to the process
working directory like the other store paths, in the API and the CLI independently) is unchanged and not migrated.

## Consequences

- Runtime still names no package: prerequisite ids are data supplied by packages and the host (rule A1).
- Session 2 wired the registry into the hosts (replacing `CachingCapabilityProbe` in DI), migrated Docker and SearXNG to
  descriptors, and added the background refresh, retention, plugin `IPrerequisiteProvider` discovery and the API.
  Session 3 made unrecorded and unregistered prerequisites fail closed and observable, refreshed readiness on plugin enable/disable,
  added the replan-threshold message and the UI. PR #91 later declares `kd.exe` required and
  `dumpchk.exe` optional using only these contracts.
- A host that never wires the new registry keeps today's behaviour exactly.

## Rejected

- *Change `ICapabilityProbe` to return a rich result* — breaks the published SDK.
- *Make `Requires` entries carry a requirement flag* — retypes a frozen 1.0 member.
- *One message per affected tool* — floods the inbox for a shared daemon.
- *Persist messages in `audit.jsonl`* — audit is evidence of actions, with different retention and tamper semantics.
- *`OFFSET` pagination* — skips or duplicates rows when a message arrives between pages.
- *Let a check stamp its own id/time* — a plugin could overwrite another package's prerequisite state.
- *Copy exception text into results* — exception messages routinely include connection strings.
