# BOPS-0124 VAULT-MULTIPROC-01 — VaultStore multi-process concurrency (evaluation, deferred)

| | |
|---|---|
| Task | `BOPS-0124` (register: `agentic/_tasks/register.json`) |
| Status | `DEFERRED`, not authorized: it needs an evaluation and an operator decision before any implementation |
| Origin | Residual risk recorded while fixing `BOPS-0123` |

## Problem

`BOPS-0123` serializes reads and writes inside one process. ADR-0029 allows several processes (for example the CLI and the API host) to touch the same vault file. On Windows
a reader in another process still makes the writer's atomic replace fail with `UnauthorizedAccessException`, and the in-process `SemaphoreSlim` gives no protection.

## Evaluation to perform before any change (not just `File.Replace`)

Measured on Windows (scratch test): `File.Replace` succeeds only if the reader opened the file with `FileShare.Delete`; with `FileShare.Read` it throws `IOException`;
`File.Move(overwrite)` throws `UnauthorizedAccessException` in every case. So a fix needs the readers' sharing mode and the replace primitive designed together, and:

1. Inter-process lock (named mutex or a lock file) versus lock-free reads with delete-sharing: failure modes, crash recovery, stale locks, Linux/Windows parity, permission behaviour.
2. Optimistic concurrency across processes: `Version` read and `Set` are still two steps; the token must keep detecting a stale writer, and a reader must never see a torn file.
3. Atomicity and durability: temp-file-plus-replace semantics, ACL/Unix-mode preservation, behaviour after a crash between steps.
4. Security: no widening of file permissions, no secret in lock names or temp file names, exceptions for real permission errors are never masked, no blanket retry.
5. Whether the multi-process scenario is actually supported, or should be refused explicitly.

## Reopen condition

Operator decision after the evaluation above; implementation is a separate authorized task.
