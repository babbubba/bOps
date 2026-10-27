# BOPS-0128 — cost and visibility of `groupSummary` (slices A and B)

Offline measurement, 2026-10-27. Method: `Bops0128ReplayTests` (read-only, `BOPS_REPLAY_CAMPAIGN=<campaign dir>`) rebuilds the collector snapshot
from the captured `system.crashes` / `system.events` results of earlier campaigns and runs the tools of this branch on it. "Before" is the captured output
(previous tool version), "after" is the replayed output. No model call, no live run, no campaign file is modified or copied; only aggregate numbers are quoted.
Campaigns replayed: the two B02 C/D reruns (6 runs each) and the B02/B03 diagnostic-fix campaign (3 runs).

## What the summary costs

`groupSummary` is a bounded node placed **before** `groups`. It lists each group by identity, count and first/last seen, and declares what it leaves out
(`observedEntries`, `listedEntries`, `omittedEntries`, `omittedCount`).

| `system.crashes` (51 groups, 7 bugcheck + 2 live-dump codes) | Before | After |
|---|---|---|
| Output size | 20 532 chars | 28 016 chars (+7.5 K) |
| Summary size | none | 8 235 bytes, starts at char ~1 760 |
| First `groups` entry starts at | char ~1 800 | char ~10 000 |
| Typed facts | 13 | 16 (bound: 16 facts / 4096 bytes, unchanged) |
| Kernel codes in typed facts | 4 | 9 of 9 |
| Groups listed in the summary | n/a | 48 of 51; 3 omitted and declared |

| `system.events` (budget 32 768, unchanged) | Before | After |
|---|---|---|
| Output size | within budget | within budget (asserted by the replay for every result) |
| Groups with `distinctMessages` | 4–10 (message-distribution allowance) | 28–31 in the summary |
| Groups observed → listed in the summary | n/a | 82 → 31, 55 → 31, 40 → 28, 38 → 28, 28 → 28 |

For events the summary is **inside** the budget: when it does not fit, the groups give way first, then the summary entry by entry. It is dropped only as a last resort.

## Visibility after compaction

The R01 diagnosis saw about the first 6 500 characters of the crashes output. Groups named (kind plus code or application) within that head:

| | Before | After |
|---|---|---|
| Groups named, 51-group result | 25 / 51 | 36 / 51 |
| Kernel codes named | 6 / 9 | 9 / 9 |
| Groups named, 52-group result | 24 / 52 | 35 / 52 |
| Groups named, 41-group result | 19 / 41 | 31 / 41 |
| Kernel codes named, 41-group result | 2 / 3 | 3 / 3 |

The head now carries more groups, and every kernel code, but with less detail per group. Be exact about the trade-off:

- Gained in the head: identity, count and first/last seen of 11 to 12 more groups, all kernel codes first.
- Lost in the head: the per-group fields `module`, `dumpReferenceCount` and `uncorrelatedCount`, because `groups` now starts around char 10 000 instead of char 1 800. They are still in the output, reachable by EvidenceRead past the head, and the kernel codes are also typed facts, which do not depend on the head.
- The measure is an approximation (a group counts as named when its kind and its code or application each appear as a value in the head); it is meant for the before/after comparison, not as an absolute figure.

## Behaviour when groups are omitted

- Crashes: kernel groups first, then the others in the existing order; entries leave from the end. In the 51-group result 3 application groups are omitted and the node says `omittedEntries: 3` and `omittedCount`.
- Events: severity first. In an 82-group result 51 are omitted and declared. The typed fact for unlisted (source, event id) pairs still states how many pairs are not listed.
- A group that is omitted from the summary is never silently absent: the node's `observedEntries = listedEntries + omittedEntries` is asserted by the tests, and a result with no group carries no summary.

## Residual limits

- The crashes output grows by about 7.5 K, which is within its 65 536-byte budget; a host that reads only the first 6 500 characters sees part of the summary and none of the detailed groups.
- An events result with more than about 30 groups still omits groups from the summary (declared).
- Captures that were themselves cut by a limit cannot be replayed whole, so they are not in these numbers.
