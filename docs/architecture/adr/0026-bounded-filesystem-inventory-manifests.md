# ADR-0026 — Bounded filesystem inventory and scoped immutable manifests

Status: Accepted
Date: 2026-09-17

## Context

V1.1-C adds `fs.size` and the exact inventory foundation that V1.1-D will use to bind a
recursive-deletion approval to one complete target set. A directory tree can contain millions of
entries, mutate during traversal, contain inaccessible paths or symlinks, and exceed either the
tool timeout or the model context. Returning the complete list from a tool would violate the
bounded-output and telemetry rules; retaining it in process memory would make the safety feature
least reliable on the trees that need it most.

An exact inventory is durable runtime state and its authorization scope includes the node, actor
and task that created it. The existing `ITool.ExecuteAsync` contract carries only arguments and a
cancellation token. Letting model-supplied arguments nominate that security context would make
the scope forgeable, while changing `ITool.ExecuteAsync` would break the frozen 1.0 SDK contract.

## Decision

### Additive execution context

`bOps.Abstractions` adds `ToolExecutionContext` (`Node`, `TaskId`, `Actor`) and the optional
`IContextualTool : ITool` interface. The runtime invokes the contextual overload when a resolved
tool implements it; all existing `ITool` implementations keep their current call path unchanged.
The legacy `ITool.ExecuteAsync` member remains mandatory and a contextual tool fails closed there
when its operation requires host-owned context. This is an additive minor-version SDK change, not
a reinterpretation of existing tools.

`AgentRunner` creates the context from the same host-owned values already written to audit events.
Neither a model nor a package may supply or replace them. Verification and restricted Skill
evidence calls preserve the original task and actor context.

### Traversal and bounded summary

`fs.size` remains a `Read` tool. Persisting derived inventory state is internal bookkeeping, not a
mutation of the administered target. Its normalized inputs are one path, maximum depth, maximum
entries, top-N count, maximum collection duration and an exact-inventory flag. Defaults and hard
ceilings are host configuration; requests outside them fail rather than being silently coerced.

Traversal is iterative and does not follow symbolic links or reparse points. Every path is
resolved and checked against the read policy immediately before metadata or children are read.
File and directory counts include the requested root. A top-N accumulator retains only the
requested number of largest files; ordinary summaries never retain every observed entry.
Warnings and response bytes are bounded.

Limit exhaustion, policy denial, inaccessible entries, arithmetic overflow, cancellation or
detected mutation makes the result incomplete. An incomplete result is labelled `partial`, never
`complete` or approval-ready, and never returns an exact manifest reference.

### Durable exact manifests

Exact entries are inserted incrementally into a package-owned SQLite store. A manifest starts in
`building`, becomes `ready` only after complete enumeration and final validation, and otherwise
becomes `incomplete`. Startup/opportunistic cleanup removes expired rows and abandoned building
rows. The store uses WAL, a busy timeout, restrictive Unix permissions and parameterized SQL.

Manifest identifiers contain 128 bits from a cryptographically secure random GUID and are opaque.
Every manifest row is scoped to the host-supplied node, actor kind/id and task id. Future paging or
deletion APIs must match the complete scope before returning or consuming entries; possession of
an id alone is insufficient authorization.

Entries use normalized root-relative paths and record type, byte length where applicable,
creation/last-write timestamps, attributes and link target metadata. This tuple detects ordinary
staleness without hashing file contents. V1.1-D still must re-resolve every path under S11 and
compare this metadata immediately before deletion; an inventory is evidence, not a lock on the
filesystem.

After enumeration, entries are read from SQLite in ordinal relative-path order and hashed through
a length-prefixed canonical binary encoding. The content hash covers the canonical root,
traversal semantics and every entry, but excludes random id, creation/expiry and authorization
scope so repeated exact inventories of an unchanged tree have the same content hash. V1.1-D's
approval hash will wrap this content hash together with manifest id, scope, creation/expiry and
the destructive operation semantics required by D-018.

The tool result contains only bounded counts, totals, top entries, warnings, completion state,
collection elapsed time and — for a ready exact inventory — id, content hash and expiry. Exact
entries never enter normal tool output, model context, telemetry or a single audit event.

## Alternatives considered

- **Add node/actor/task parameters to `fs.size`.** Rejected: model-supplied authorization context
  is forgeable and would disagree with the audit identity.
- **Break `ITool.ExecuteAsync` to add context.** Rejected: ADR-0022 freezes the 1.0 SDK and existing
  third-party tools must continue to load.
- **Ambient static/`AsyncLocal` context.** Rejected: hidden mutable state is difficult to test and
  can leak across nested or concurrent calls.
- **JSON lines per manifest.** Rejected: incremental writes are simple, but deterministic ordering,
  paging, scoped lookup, abandoned-build cleanup and later per-entry reconciliation would require
  rebuilding a database around the files.
- **Keep exact entries in memory and write once.** Rejected: memory then scales with the target set
  and a crash loses the entire preflight.
- **Hash every file's contents.** Rejected: it changes a metadata inventory into unbounded I/O and
  still cannot lock the tree against later changes.

## Consequences

- `bOps.Packages.Filesystem` gains the same direct `Microsoft.Data.Sqlite` dependency pattern used
  by `bOps.Memory`; it does not depend on that concrete core project.
- Hosts configure store path, retention, defaults and ceilings and must keep the database outside
  read/write target patterns where practical.
- Runtime tests cover contextual dispatch before the filesystem feature relies on it. Filesystem
  tests use real directories/files on Windows and Linux; deterministic fault seams may advance
  time or mutate those real targets but do not replace the operating system.
- V1.1-D owns paging/authorization endpoints, approval-hash composition, adversarial identity
  strengthening if measurements require native file identifiers, and destructive reconciliation.
