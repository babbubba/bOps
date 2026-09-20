// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { I18n } from '../core/i18n/i18n';

/** Statuses a dev-server proxy or a gateway answers with when nothing is listening behind it. */
const UNREACHABLE_STATUSES = [502, 503, 504];

/**
 * The text a store shows for a failed call. The API answers a refusal with `{ message }` (kept as sent, in English); a call that never
 * reached the API (no answer, or the proxy's 502, 503 or 504) says so in the active language instead of the transport's generic
 * "Http failure response ... 0 Unknown Error"; any other failed request names its HTTP status (a dev-server proxy answers 500 with no body when the backend is down); anything else is the error's own message, or a translated generic fallback.
 */
export function describeError(err: unknown, i18n: I18n): string {
  const body = (err as { error?: { message?: unknown } } | null)?.error;
  if (typeof body?.message === 'string') {
    return body.message;
  }

  if (err instanceof HttpErrorResponse && (err.status === 0 || UNREACHABLE_STATUSES.includes(err.status))) {
    return i18n.t('common.error.unreachable');
  }

  if (err instanceof HttpErrorResponse) {
    return i18n.t('common.error.http', { status: err.status });
  }

  return err instanceof Error ? err.message : i18n.t('common.error.generic');
}
