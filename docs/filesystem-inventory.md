# Filesystem inventory

`fs.size` is a cross-platform Read tool for bounded file and directory sizing. It never follows
symbolic links or reparse points and applies the configured filesystem read policy to every
resolved path immediately before metadata or directory children are read.

## Result semantics

Counts include the requested root. `fileCount`, `directoryCount`, `linkCount` and `totalBytes` are
complete only when `status` is `complete`. `topEntries` contains at most the requested number of
largest files and uses ordinal relative-path ordering to break size ties. Warnings and the entire
JSON response are host-bounded.

`status: partial`, `complete: false` and `truncated: true` are returned when any depth, entry or
duration bound is reached, a path becomes inaccessible or leaves policy, arithmetic overflows, or
the tree changes during collection. A partial result is useful as an observation but is never an
exact deletion preflight.

The optional `exact: true` mode writes entries incrementally to SQLite, revalidates them, then
computes a SHA-256 content hash over their canonical ordinal ordering. A ready exact result returns
only the opaque manifest id, content hash, expiry and count. Entry paths remain in the manifest
store; they do not enter model context, ordinary telemetry or a single audit event. Incomplete
exact attempts return no manifest reference and `approvalReady` remains false.

Manifest ids are random and each row is bound to the host-owned node, task and actor identity.
Possession of an id is not authorization. Governed deletion matches that full scope, includes
instance metadata in its approval hash and re-resolves/revalidates each path before a destructive
action. See ADR-0026, ADR-0027 and [`governed-recursive-deletion.md`](governed-recursive-deletion.md).

## Inputs and defaults

The tool accepts:

- `path` (required);
- `maxDepth` (root is depth zero);
- `maxEntries` (including the root);
- `topEntries`;
- `maxDurationMilliseconds`;
- `exact` (default `false`).

Omitted bounds use host defaults. A request outside a host ceiling fails; values are never silently
raised or clamped.

## Host configuration

CLI and API read the following under `Filesystem:Inventory`:

```json
{
  "ManifestStorePath": "filesystem-inventory.db",
  "ManifestRetention": "1.00:00:00",
  "DefaultMaxDepth": 32,
  "MaximumDepth": 128,
  "DefaultMaxEntries": 10000,
  "MaximumEntries": 100000,
  "DefaultTopEntries": 10,
  "MaximumTopEntries": 100,
  "DefaultDuration": "00:00:20",
  "MaximumDuration": "00:02:00",
  "MaximumWarnings": 32,
  "MaximumOutputBytes": 32768
}
```

Keep the manifest database in a host-owned directory, outside administered target trees where
practical. SQLite uses WAL and a busy timeout; the main database receives owner-only permissions
on Unix. Expired ready, incomplete and abandoned-building manifests are deleted opportunistically
before a new exact inventory. The default retention is 24 hours.

The entry ceiling is the primary durable-storage bound. Choose it from measured local filesystem
performance and free disk space; increasing it also increases the maximum preflight and later
revalidation work. The runtime's ordinary per-tool timeout remains an independent outer bound.
