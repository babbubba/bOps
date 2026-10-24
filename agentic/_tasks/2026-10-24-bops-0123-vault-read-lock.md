# BOPS-0123 VAULT-READ-LOCK-01 — Serialize VaultStore reads with writes

| | |
|---|---|
| Task | `BOPS-0123` (register: `agentic/_tasks/register.json`), Issue and Project #6 item recorded there |
| Origin | Pre-existing red Windows test `VaultStoreTests.ConcurrentSets_ForDifferentProviders_BothSucceed_WithoutLosingAnEntry` on `main` (CI run 39911666984) and on PR #129 (run 39989503982) |
| Priority / effort | P2 / small |
| Depends on | nothing |
| Related | `BOPS-0124` (multi-process concurrency, deferred, not part of this task); `BOPS-0122` / PR #129 and PR #124 are untouched |

## Defect

`VaultStore` serializes writers with `_gate`, but `Version`, `List`, `Find` and `Resolve` call `LoadAll()` without it, and `LoadAll()` reads the file with
`File.ReadAllText` (`FileShare.Read`). On Windows `File.Move(temp, vault, overwrite: true)` fails with `UnauthorizedAccessException` while any handle is open on
the destination, so a read that overlaps `SaveAll` makes the write throw (and the read can fail while the file is being replaced). Linux `rename` is unaffected.
Measured in a scratch harness (4 reader threads + 1 writer, 10 s, Windows): before 747 writer `UnauthorizedAccessException` and 520 reader errors; after 0 and 0.

## Scope

In-process only: every public read takes the same `SemaphoreSlim` as writes (`LoadAllSerialized`); `Set`/`Clear` keep calling the unlocked `LoadAll` because the gate is not
reentrant. No change to the atomic temp-file-plus-rename, the AES-GCM encryption, the version token, the file format, Unix file modes or exception types. No retry,
no catch, no platform branch. Decryption in `Resolve` runs outside the lock.

Behaviour note: reading after `Dispose()` now throws `ObjectDisposedException` (it used to read the file). No production caller reads after disposal.

## Acceptance criteria

- A1 `ReadsRunningAlongsideWrites_NeverFail_AndEveryWriteLands` fails on Windows before the fix and passes after, repeatedly.
- A2 `MixedSetClearAndReads_FromManyThreads_CompleteWithoutDeadlock` and `ARead_ThatThrows_ReleasesTheLock_SoLaterCallsAreNotBlocked` pass: no deadlock, a throwing read releases the lock.
- A3 The original `ConcurrentSets_ForDifferentProviders_...` test is unchanged and green; no test is skipped or loosened.
- A4 Release build 0 warnings; `bOps.Runtime.Tests`, `bOps.Api.Tests`, `bOps.Cli.Tests`, `bOps.Architecture.Tests` green on Windows; Runtime and Architecture green on Linux (WSL).
- A5 CI of the PR head green on Windows and Linux.

## Out of scope

Multi-process readers and writers (`BOPS-0124`).
