# Filesystem troubleshooting

V1.3-F extends the existing `bOps.Packages.Filesystem` package. Every tool resolves its target
and applies the configured `Filesystem:ReadPatterns` or `Filesystem:WritePatterns` immediately
before I/O; a path outside both lists remains denied by default.

## Read diagnostics

- `fs.grep` searches a regular file or, when `recursive:true`, a directory tree without following
  symlinks/reparse points. It streams UTF-8 lines, skips probable binary files, caps one returned
  line at 2 KiB and bounds matches (200/2000) and output (32/128 KiB). Regex matching has a 250 ms
  timeout per match.
- `fs.tail` reads backward from one regular UTF-8 file. `lines` defaults to 100/max 5000 and
  `maxBytes` defaults to 64 KiB/max 1 MiB. Binary or unsupported encodings are explicit failures.
- `fs.permissions` reports metadata, not effective authorization. Windows reports bounded ACL
  evidence via the access-control APIs; Linux reports UID/GID and Unix mode from `stat`.
- `fs.locks` reports visible file holders for the exact resolved file. Windows uses Restart Manager;
  Linux scans visible `/proc/<pid>/fd` entries with process and descriptor ceilings. Restricted
  visibility is returned as `complete:false`, never as a trustworthy empty result.

## Governed changes

- `fs.copy` is Medium risk. It copies a single regular file only when the source is read-allowed,
  the destination and invocation-owned sibling temporary file are write-allowed, and the destination
  does not exist. It re-resolves both paths immediately before I/O, enforces
  `Filesystem:Operations:MaxCopyBytes` (1 GiB default, 10 GiB hard maximum), then stages through a
  temporary sibling and renames without overwrite. Cancellation cleans only that invocation's temp.
  `fs.copy.verify` checks existence, length and SHA-256 for the original source and destination.
- `fs.mkdir` is Low risk and idempotent: an existing directory succeeds, an existing file fails,
  and `fs.stat` verifies the path afterwards.

These tools are typed filesystem operations; none invokes a shell or accepts executable commands.
