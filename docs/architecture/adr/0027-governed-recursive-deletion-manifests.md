# ADR-0027 — Governed recursive deletion with hash-bound approval

Status: Accepted
Date: 2026-09-17

## Context

V1.1-D adds permanent recursive and batch deletion. A path-based approval is insufficient: a tree
can change after preview, an overlapping batch can name the same entry more than once, and a
filesystem cannot make a multi-entry delete atomic. The V1.1-C inventory store supplies complete,
scope-bound snapshots, but ADR-0026 deliberately leaves destructive approval, execution state,
reconciliation and operator paging to this batch.

`RiskLevel.High` alone also cannot express the task's “approval for every execution” invariant.
Policy may legitimately configure another High-risk tool as automatic. Recursive deletion must
remain approval-gated even under that configuration without hard-coding a filesystem tool name in
the runtime.

## Decision

### Public contract and approval enforcement

`ToolManifest` gains the additive `RequiresExplicitApproval` flag, defaulting to false. After
ordinary policy evaluation, the runtime treats an automatic decision for a manifest carrying this
flag as an approval decision; `Forbidden` remains forbidden. A manifest may therefore tighten but
never loosen policy. The effective decision and its reason are audited.

`IApprovalBoundTool` is an optional additive interface. After a human answers and before target
execution, the runtime gives such a tool the host-owned `ToolExecutionContext` and the exact
`ApprovalDecision`. The callback may update only approval/preflight bookkeeping and validate that
the approved arguments still identify a consumable object. A failed approved binding prevents
execution. A rejected decision is still audited and never calls `ExecuteAsync`.

`ToolParameterType.PathList` represents a JSON array of path strings. Provider schemas expose it
as an array of strings and runtime argument validation rejects non-arrays, non-string members,
unknown arguments and all other declared type mismatches before approval or execution. This keeps
batch roots typed instead of embedding a second JSON document or a delimiter grammar in a string.

### Preflight and manifest identity

The read-only `fs.delete_tree.prepare` tool accepts a bounded path list and builds a
`DeletionManifest` through the V1.1-C inventory service. The API exposes the same service for an
authenticated operator preparing a browser workflow. `fs.delete_tree` accepts only `manifestId`
and `approvalHash`; it never accepts a root path, glob or recursive flag.

Raw roots containing glob metacharacters are rejected. Roots are fully resolved under S11,
required to satisfy both read and write policy, deduplicated using platform path comparison, sorted
deterministically and reduced so a descendant of an already selected root is not enumerated or
deleted twice. The reduced canonical root list is shown as a warning-bearing part of the preview.

A deletion manifest has a cryptographically random opaque id and is scoped to node, task and the
actor that prepared it. Its canonical SHA-256 approval hash uses length-prefixed binary encoding
and covers:

- schema and permanent-delete operation semantics, including no-follow-links and children-first;
- manifest id, node, task and owner actor;
- creation and expiry timestamps;
- every canonical root in deterministic order;
- entry, file, directory and link counts plus total bytes;
- every canonical absolute entry path and its type, size, timestamps, attributes and link target.

The random id and scope intentionally make the approval hash instance-specific, unlike ADR-0026's
repeatable content hash. The full list remains in SQLite. Tool output and pending-approval DTOs
carry only bounded summaries, id, approval hash, counts and bytes. An optional host-bounded
`IToolAuditSummaryProvider` lets this tool add those same aggregates to `ToolCallAuditEvent`
without copying arbitrary output or entry paths into the audit log; the runtime drops summaries
over 8 KiB.

### Bounds, freshness and retention

Defaults are 10,000 entries, 32 roots, depth 32, a 30-second build budget and a 10-minute approval
window. Hard ceilings are 100,000 entries, 64 roots, depth 128 and two minutes. The 10,000-entry
default is selected because the V1.1-C real-filesystem scale test completes that size while keeping
tool output bounded; V1.1-D adds execution and paging measurements at the same scale. Operators may
lower defaults and ceilings. Exceeding any ceiling, incomplete enumeration, inaccessible metadata,
overflow, cancellation or detected mutation produces `rejected` or `incomplete` state and no
approval-ready manifest.

Expiry is checked when approval is bound and again when execution begins. Expiry does not interrupt
an execution that has already atomically entered `executing`; doing so would manufacture additional
partial work. Terminal summaries/results are retained for a configurable seven days, after which
opportunistic cleanup removes them. Unconsumed manifests expire at the approval deadline.

### State machine and one-shot use

The durable states are `building`, `ready`, `approved`, `executing`, `partially_completed`,
`verified`, `refuted`, `inconclusive`, `expired` and `rejected`.

`building -> ready` is possible only after complete inventory and canonical hashing.
`ready -> approved` is an atomic compare-and-set performed by the approval-binding callback after
an affirmative decision whose `manifestId` and `approvalHash` match. A rejection records
`ready -> rejected`. `approved -> executing` is another atomic compare-and-set, making a manifest
one-shot and rejecting duplicate approval or execution. Execution ends in `executing` when every
delete call reported success and awaits verification, or `partially_completed` when interruption,
drift or an operating-system failure follows any completed delete. Verification moves either state
to `verified`, `refuted` or `inconclusive`. Expired unconsumed states move to `expired`.

An approved binding that becomes stale before target execution is rejected without deleting
anything. Approval decisions are not persisted as reusable bearer capabilities across a process
restart; ADR-0022 remains unchanged.

### TOCTOU, execution and verification

Before the first delete, the executor performs a complete reconciliation against the stored set,
including discovering unapproved children. Any missing, added, replaced, renamed, retyped or
metadata-changed entry fails closed with zero deletions. Immediately before each destructive call,
it resolves and rechecks policy plus identity metadata again. Symlinks/reparse points are entries,
never traversal edges.

Entries are processed deepest-first with deterministic ordinal tie-breaking. Files and links are
deleted without following links; directories use non-recursive deletion only after their approved
children. Directory last-write time is part of the initial freshness check but is not compared
after approved child removal because that removal legitimately changes it. On Linux,
`FileSystemInfo.CreationTimeUtc` may expose inode-change time when birth time is unavailable and
therefore changes for the same reason; it too is used in the full zero-delete reconciliation but
not the later parent check. Path resolution, type, attributes and link state are still rechecked
immediately before the directory operation; Windows creation time remains stable and is compared.

Every attempted entry receives one bounded durable result. Cancellation is propagated after
recording reconciliation state and is never described as rollback. The tool returns aggregate
counts and a paged failure reference.

The contextual read-only `fs.delete_tree.verify` tool checks every approved entry after any
execution outcome. Absence confirms an entry. A present matching entry is `remaining`; a present
non-matching entry is `recreated-or-changed`; inaccessible state is `permission-denied` or
`inconclusive`. Only universal absence is `Verified`; any known presence is `Refuted`; an
unobservable path makes the overall result `Inconclusive` unless a known refutation already exists.

### API and browser authorization

Operator endpoints create and browse manifests only with the exact node/task/owner scope. Approval
preview endpoints are addressed through a live pending approval id, require the Approver role and
derive scope from that pending request; possession of a manifest id or task id alone is
insufficient. Cursors are opaque ordinals, bounded by server page-size limits and re-authorized on
every request. Search is server-side. Download streams JSON Lines and never buffers the full list.

An affirmative API response for `fs.delete_tree` requires a separate
`acknowledgePermanentDeletion` boolean. The Angular client displays roots, counts, bytes, hash,
expiry and warnings, pages/filter entries, never renders the complete list as one DOM collection,
and disables approval if summary refresh reports expiry, hash mismatch or a non-ready state.

## Alternatives considered

- **Widen `fs.delete` with a recursive flag.** Rejected: it changes existing semantics and lets a
  single path argument hide an unbounded blast radius.
- **Rely on High-risk policy defaults.** Rejected: defaults are configuration, while mandatory
  approval is an invariant of this operation.
- **Approve roots and enumerate during execution.** Rejected: approval would not identify the set
  later deleted and additions between the two moments would be silently included.
- **Put all paths in approval, model or audit payloads.** Rejected: it is unbounded at every
  presentation boundary and makes 5,000–10,000-entry previews unusable.
- **Use delimited or JSON-encoded text for batch roots.** Rejected: it creates a second parser and
  ambiguous path semantics instead of a typed JSON array.
- **Call recursive operating-system deletion.** Rejected: it can consume entries never approved,
  follows platform-specific rules and cannot produce exact per-entry reconciliation.
- **Promise rollback or atomicity.** Rejected: permanent multi-entry filesystem deletion provides
  neither guarantee.

## Consequences

- `bOps.Abstractions` receives additive SDK surface and provider schema adapters learn one new
  parameter type. Existing tool implementations and manifests remain source/binary compatible.
- Runtime approval semantics become strictly safer for tools that opt into the new flag; core code
  still names no concrete tool or package.
- The Filesystem package owns deletion-manifest/result tables in its existing SQLite database and
  remains independent of `bOps.Memory`.
- API and Angular approval surfaces gain a filesystem-specific preview, but authorization and the
  destructive decision remain server-side.
- Attack tests cover symlink/parent replacement, rename, added/removed entries, altered hash,
  expiry, scope reuse and duplicate consumption on real temporary filesystem trees.
