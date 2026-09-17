# Governed recursive deletion

`fs.delete_tree` permanently deletes a batch of files, links and directories only after bOps has
built a complete exact manifest and a human has approved that manifest's instance-specific
SHA-256 hash. It does not use the recycle bin or trash, cannot roll back completed deletions and
never accepts a recursive path or glob directly.

## Safe workflow

1. `fs.delete_tree.prepare` receives an explicit JSON array of roots. It resolves read and write
   policy, rejects glob metacharacters, removes duplicates and overlapping descendants, and uses
   the `fs.size` exact enumerator to persist every entry in SQLite.
2. The operator reviews roots, warnings, counts, bytes, expiry, approval hash and cursor-paged
   entries. The Angular approval page keeps at most one server page in the DOM and can filter or
   stream the complete manifest as NDJSON.
3. Approval requires the separate permanent-deletion acknowledgement. `fs.delete_tree` always
   requires a human decision even if policy would otherwise make High-risk tools automatic.
4. Execution atomically consumes the manifest once, reconciles the complete set, then re-resolves
   policy and identity immediately before each non-recursive delete. Children are processed before
   parents. Any pre-execution drift causes zero deletions; later failure or cancellation is stored
   as visible partial completion.
5. `fs.delete_tree.verify` checks every approved entry. Universal absence is `verified`; a
   remaining or recreated path is `refuted`; an unobservable path is `inconclusive`.

The hash covers operation semantics, manifest id, node/task/actor scope, creation and expiry,
canonical roots, counts, bytes and every entry's canonical path and identity metadata. Possession
of a manifest id or hash is not authorization. Manifests and pages are re-authorized against their
host-owned node, task and actor scope on every access.

## Bounds and expiry

The defaults are 32 roots, depth 32, 10,000 total entries, a 30-second enumeration budget, a
10-minute approval window, 200 entries per page and seven-day result retention. Hard ceilings are
64 roots, depth 128, 100,000 entries, two minutes and 1,000 entries per page. Operators may lower
these values. Requests above a ceiling, incomplete enumeration, inaccessible metadata, arithmetic
overflow or mutation during preflight fail closed and never become approval-ready.

Deletion settings extend `Filesystem:Inventory`:

```json
{
  "DefaultDeletionMaxRoots": 32,
  "MaximumDeletionRoots": 64,
  "DefaultDeletionDuration": "00:00:30",
  "DeletionApprovalWindow": "00:10:00",
  "DeletionResultRetention": "7.00:00:00",
  "DefaultManifestPageSize": 200,
  "MaximumManifestPageSize": 1000
}
```

The inventory settings in [`filesystem-inventory.md`](filesystem-inventory.md) supply the shared
depth, entry and duration bounds and `ManifestStorePath`. Keep that database in a host-owned
directory outside deletion roots.

## API surfaces

Authenticated Operators can prepare and inspect a manifest under
`/api/filesystem/deletion-manifests`; summary, cursor-paged entries, server-side search and NDJSON
download remain scoped to the same task and actor. Approvers preview the exact manifest only
through a live pending approval id under `/api/approvals/{approvalId}/deletion-manifest`. A raw
manifest id cannot open the approval surface.

An affirmative response for permanent deletion must include
`acknowledgePermanentDeletion: true`. The server rechecks that the manifest is still `ready`,
unexpired and matches the pending hash before accepting the decision.

## Partial-failure recovery

Never interpret `partially_completed`, cancellation or a failed tool result as rollback. Open the
stored entry results to distinguish `deleted`, `failed` and drift outcomes, then run verification.
If approved paths remain, inspect the operating-system error and current identity before preparing
a new manifest. A new attempt always requires a fresh enumeration, hash and human approval; an old
manifest is one-shot and cannot be retried.

Audit events contain the manifest id, approval hash, aggregate entry/byte/deleted/failure counts,
tool outcome and verification status. They intentionally do not contain thousands of entry paths;
the exact retained list stays behind the scoped paged/download endpoints.

See ADR-0027 for the canonical state machine, hash encoding, TOCTOU rules and residual guarantees.
