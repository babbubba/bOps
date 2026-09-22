# Filesystem troubleshooting

V1.3-F extends the existing `bOps.Packages.Filesystem` package. Every tool resolves its target
and applies the configured `Filesystem:ReadPatterns` or `Filesystem:WritePatterns` immediately
before I/O; a path outside both lists remains denied by default.

## Read diagnostics

- `fs.grep` searches a regular file or, when `recursive:true`, a directory tree without following
  symlinks/reparse points. It streams UTF-8 lines, skips probable binary files, caps one returned
  line at 2 KiB and bounds matches (default 200/max 2000) and output (default 32 KiB/max 128 KiB).
  Regex matching has a 250 ms timeout per match; a match that times out stops the scan for that
  file instead of hanging.
- `fs.tail` reads backward from one regular file. `lines` defaults to 100/max 5000 and `maxBytes`
  defaults to 64 KiB/max 1 MiB. Decoding is strict UTF-8 (`throwOnInvalidBytes: true`): a binary
  file or any malformed byte sequence is an explicit failure, never a silently substituted
  replacement character. A BOM is stripped only when the whole file was read; when `maxBytes`
  truncated the read window, the partial first line (which may begin mid multi-byte character) is
  dropped rather than misreported as invalid encoding — the same way POSIX `tail` discards a
  partial leading line.
- `fs.permissions` reports metadata, not effective authorization. Windows reads a file's ACL via
  `FileSecurity` and a directory's via `DirectorySecurity` (not the same API for both — a
  directory's ACL semantics differ), capped at 200 entries; if more exist, `complete:false` says
  so instead of silently dropping the rest. Linux reports UID/GID and Unix mode from `stat`.
- `fs.locks` reports visible file holders for the exact resolved file. Windows uses Restart
  Manager and treats any Restart Manager API failure as `complete:false` — an API error is never
  folded into the same empty result as "no holders found". Linux scans visible `/proc/<pid>/fd`
  entries with process and descriptor ceilings (4096 each); hitting either ceiling, hitting the
  caller's own row `limit` before the scan finished, or being denied visibility into another
  user's file descriptors all report `complete:false`. Restricted or truncated visibility is
  always `complete:false`, never a trustworthy empty result.

## Governed changes

- `fs.copy` is Medium risk. It copies a single regular file only when the source is read-allowed,
  the destination and invocation-owned sibling temporary file are write-allowed, and the destination
  does not exist. It re-resolves both paths immediately before I/O, enforces
  `Filesystem:Operations:MaxCopyBytes` (1 GiB default, 10 GiB hard maximum), then stages through a
  temporary sibling and renames without overwrite. Cancellation cleans only that invocation's temp.
  `fs.copy.verify` checks existence, length and SHA-256 for the original source and destination.
- `fs.mkdir` is Low risk and idempotent: an existing directory succeeds, an existing file fails,
  and `fs.stat` verifies the path afterwards. Verification checks that the path exists *as a
  directory*, not merely that something exists there — `exists:true` alone would still be true if
  a race replaced the freshly created directory with a non-directory before `fs.stat` ran.

These tools are typed filesystem operations; none invokes a shell or accepts executable commands.
