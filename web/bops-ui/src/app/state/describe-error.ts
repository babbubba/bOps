// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { ModelFailureKind, TaskTerminalKindName, TaskTerminalReason } from '../core/api/models';
import { I18n } from '../core/i18n/i18n';
import type { MessageKey } from '../core/i18n/messages';

/** Statuses a dev-server proxy or a gateway answers with when nothing is listening behind it. */
const UNREACHABLE_STATUSES = [502, 503, 504];

/**
 * The stable codes of bOps.Api.TaskErrorResponse (ADR-0040 §3 and §9) and the operator text for each. Presentation only: which of
 * them applies to a task is decided by the server, never here.
 */
const REFUSAL_KEYS: Record<string, MessageKey> = {
  task_running: 'task.refusal.running',
  resume_conflict: 'task.refusal.conflict',
  task_delegated: 'task.refusal.delegated',
  resume_origin_unknown: 'task.refusal.originUnknown',
  task_completed: 'task.refusal.completed',
  task_policy_blocked: 'task.refusal.policyBlocked',
  task_status_not_resumable: 'task.refusal.notResumable',
  token_budget_exhausted: 'task.refusal.tokenBudget',
  lifetime_steps_exhausted: 'task.refusal.lifetimeSteps',
  lifetime_replans_exhausted: 'task.refusal.lifetimeReplans',
  transition_unsupported: 'task.refusal.transitionUnsupported',
};

const EXECUTOR_UNAVAILABLE = 'executor_unavailable';

/** What a provider failure means for the operator, by ADR-0039's provider-neutral kind. */
const FAILURE_KEYS: Record<ModelFailureKind, MessageKey> = {
  0: 'task.failure.unknown',
  1: 'task.failure.unavailable',
  2: 'task.failure.unavailable',
  3: 'task.failure.unavailable',
  4: 'task.failure.unavailable',
  5: 'task.failure.authentication',
  6: 'task.failure.quota',
  7: 'task.failure.invalidRequest',
  8: 'task.failure.contextOverflow',
  9: 'task.failure.malformed',
};

/** The `Retry-After` of a response, in whole seconds, when it sent a usable one. */
function retryAfterSeconds(err: unknown): number | null {
  if (!(err instanceof HttpErrorResponse)) return null;
  const seconds = Number(err.headers?.get('Retry-After'));
  return Number.isFinite(seconds) && seconds > 0 ? Math.ceil(seconds) : null;
}

/**
 * The text for a stable bOps refusal code, in the active language, or `null` for a code this UI does not know (a newer server).
 * `retryAfter` only matters for `executor_unavailable`.
 */
export function describeRefusal(code: string, i18n: I18n, retryAfter: number | null = null): string | null {
  if (code === EXECUTOR_UNAVAILABLE) {
    return retryAfter === null
      ? i18n.t('task.refusal.executorBusyNoDelay')
      : i18n.t('task.refusal.executorBusy', { seconds: retryAfter });
  }

  const key = REFUSAL_KEYS[code];
  return key === undefined ? null : i18n.t(key);
}

/**
 * What to do about a failed model call, by its provider-neutral kind. A missing kind (a task stored before failures were
 * classified) reads as unknown.
 */
export function describeModelFailure(kind: ModelFailureKind | null | undefined, i18n: I18n): string {
  return i18n.t(FAILURE_KEYS[kind ?? 0] ?? 'task.failure.unknown');
}

/** Why the latest execution attempt of a task ended, as a short label in the active language. */
export function describeTerminalKind(reason: TaskTerminalReason, i18n: I18n): string {
  return i18n.label('terminalKind', TaskTerminalKindName[reason.kind] ?? String(reason.kind));
}

/**
 * The text a store shows for a failed call. A bOps refusal (`{ code, message }`) is recognised by its code first, whatever the HTTP
 * status: a stable code is shown as the operator text of the active language, an unknown one with the API's own message as sent. A
 * call that never reached the API (no answer) or that a proxy or gateway answered with 502, 503 or 504 and no bOps body says so in
 * the active language instead of the transport's generic "Http failure response ... 0 Unknown Error"; a refused credential (401)
 * says the session expired; any other failed request names its HTTP status; anything else is the error's own message, or a
 * translated generic fallback.
 */
export function describeError(err: unknown, i18n: I18n): string {
  const body = (err as { error?: { code?: unknown; message?: unknown } | null } | null)?.error;
  if (typeof body?.code === 'string') {
    const known = describeRefusal(body.code, i18n, retryAfterSeconds(err));
    if (known !== null) {
      return known;
    }
  }

  if (typeof body?.message === 'string') {
    return body.message;
  }

  if (err instanceof HttpErrorResponse && (err.status === 0 || UNREACHABLE_STATUSES.includes(err.status))) {
    return i18n.t('common.error.unreachable');
  }

  if (err instanceof HttpErrorResponse && err.status === 401) {
    return i18n.t('common.error.sessionExpired');
  }

  if (err instanceof HttpErrorResponse) {
    return i18n.t('common.error.http', { status: err.status });
  }

  return err instanceof Error ? err.message : i18n.t('common.error.generic');
}
