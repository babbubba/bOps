import { TaskState, TaskStatusRunning } from '../api/models';

/**
 * Opens the SSE stream bOps.Api serves at GET /api/agents/tasks/{id}/events (ADR-0018): a
 * TaskState snapshot every time the step count changes, until the task reaches a terminal status.
 * Closes the EventSource itself once a terminal snapshot arrives — native EventSource otherwise
 * auto-reconnects on the server closing the connection, which would just re-open the same
 * already-finished stream.
 */
export function watchTaskEvents(taskId: string, onSnapshot: (task: TaskState) => void, onError?: () => void): () => void {
  const source = new EventSource(`/api/agents/tasks/${taskId}/events`);

  source.addEventListener('snapshot', (event: MessageEvent<string>) => {
    const task = JSON.parse(event.data) as TaskState;
    onSnapshot(task);
    if (task.status !== TaskStatusRunning) {
      source.close();
    }
  });

  source.addEventListener('error', () => {
    // A real stream-level error (task not found, network drop) — never treated as a terminal
    // task status; the caller decides whether to fall back to a plain GET.
    onError?.();
    source.close();
  });

  return () => source.close();
}
