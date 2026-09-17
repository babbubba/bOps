# Agent task history view in the local UI

Status: **planned**
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

- [ ] Add a status filter (at least `Running` / `Completed` / `Failed`, matching
      `AgentTaskStatus`) to the existing task list in `dashboard.html`/`dashboard.ts`, defaulting to
      the current `Running`-only behavior so nothing regresses for the live-monitoring use case.
- [ ] Extend `TasksStore` with a method to fetch tasks for a given status on demand (reusing
      `BOpsApiClient.listTasks`, which already accepts a status argument) instead of only the fixed
      3-second `Running` poll.
- [ ] Selecting a non-`Running` task from history must not open a live SSE watch
      (`watchTaskEvents`) — it already has its final `Steps`/`Status` in one `getTask` response, per
      the existing branch in `TasksStore.selectTask`.
- [ ] Keep the completed/failed task list a plain on-demand fetch, not another 3-second poll — there
      is nothing live to watch once a task is terminal.
- [ ] Reasonable bound on how much history is requested/rendered at once (the store method already
      has no server-side page size — decide whether that needs a limit here or is acceptable as-is
      given expected task volume).

## Tests and documentation

- [ ] `dashboard.spec.ts`/`tasks.store.spec.ts` coverage for switching status filters, selecting a
      terminal task without opening a stream, and the default view staying `Running`-only.
- [ ] Angular production build and headless tests.
- [ ] No API or `bOps.Abstractions` change is expected; if one turns out to be necessary, stop and
      say why before proceeding (this task assumes the existing endpoint is sufficient).

## Definition of Done

- [ ] An operator can view and reopen a task after it has completed or failed, from the dashboard,
      without querying `tasks.db` or the API directly.
- [ ] The default dashboard view and the live SSE behavior for a running task are unchanged.
- [ ] No new server-side endpoint, DTO or persistence change was needed, or the deviation is
      explained.
