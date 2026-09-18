# Agent task history view in the local UI

Status: **complete** (local validation; not yet through CI)
Recommended model effort: **basso**
Repository: `bOps`
Depends on: existing `GET /api/agents/tasks?status=` endpoint and `ITaskStore.ListByStatusAsync`
(both already implemented, ADR-0018); not part of the ordered V1.1 delivery chain — pick up
alongside or after whichever V1.1 batch is active when scheduled.

## Origin

Found during interactive use on 2026-09-17: an operator ran several `bops` agent tasks from the
dashboard and, minutes later, could only see 4 of them. Root cause is not a bug in the poll or the
store: `TasksStore.refresh()` calls `listTasks(TaskStatusRunning)` on a 3-second interval
(`web/bops-ui/src/app/state/tasks.store.ts`), so a task drops out of the "Running tasks" sidebar
within seconds of reaching `Completed` or `Failed`, even though `SqliteTaskStore.ListByStatusAsync`
already returns every row for any requested status and `tasks.db` retains full history. The API
already supports what is missing; only the UI has no way to ask for it.

## Outcome

Let an authenticated operator browse and reopen tasks that have already finished, not only the
ones still `Running`, from the same dashboard — without changing how live tasks are watched.

## Angular work

- [x] Add a status filter (at least `Running` / `Completed` / `Failed`, matching
      `AgentTaskStatus`) to the existing task list in `dashboard.html`/`dashboard.ts`, defaulting to
      the current `Running`-only behavior so nothing regresses for the live-monitoring use case.
- [x] Extend `TasksStore` with a method to fetch tasks for a given status on demand (reusing
      `BOpsApiClient.listTasks`, which already accepts a status argument) instead of only the fixed
      3-second `Running` poll.
- [x] Selecting a non-`Running` task from history must not open a live SSE watch
      (`watchTaskEvents`) — it already has its final `Steps`/`Status` in one `getTask` response, per
      the existing branch in `TasksStore.selectTask`.
- [x] Keep the completed/failed task list a plain on-demand fetch, not another 3-second poll — there
      is nothing live to watch once a task is terminal.
- [x] Reasonable bound on how much history is requested/rendered at once (the store method already
      has no server-side page size — decide whether that needs a limit here or is acceptable as-is
      given expected task volume).

## Tests and documentation

- [x] `dashboard.spec.ts`/`tasks.store.spec.ts` coverage for switching status filters, selecting a
      terminal task without opening a stream, and the default view staying `Running`-only.
- [x] Angular production build and headless tests.
- [x] No API or `bOps.Abstractions` change is expected; if one turns out to be necessary, stop and
      say why before proceeding (this task assumes the existing endpoint is sufficient).

## Definition of Done

- [x] An operator can view and reopen a task after it has completed or failed, from the dashboard,
      without querying `tasks.db` or the API directly.
- [x] The default dashboard view and the live SSE behavior for a running task are unchanged.
- [x] No new server-side endpoint, DTO or persistence change was needed, or the deviation is
      explained.

## Delivered

- The dashboard's task list has a status selector over all eight `AgentTaskStatus` values, default
  `Running`. `Running` keeps the 3-second poll; any other status is one on-demand fetch plus a
  **Refresh** button, never polled. `TasksStore.setStatusFilter` discards a response that arrives
  after the operator has already switched filters.
- History is shown newest first and capped at 50 rendered rows, with a "Showing the latest 50 of N"
  note; the API has no page size, and expected volume does not justify a server change here.
- Selecting a terminal task reuses the existing `getTask`-only branch (no SSE watch).
- No API, DTO, persistence or `bOps.Abstractions` change.
- Validation: Angular production build and headless tests 55/55 (5 new: filter fetch and no
  polling, stale-response guard, default live view, filter change, capped newest-first history).
  Not exercised in a real browser against a running API.
