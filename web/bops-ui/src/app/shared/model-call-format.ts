// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { ModelCallRecord } from '../core/api/models';

/** How long a model call took, in the unit that reads best: "820 ms", "4.3 s", "1 min 5 s". */
export function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) {
    return '—';
  }

  if (ms < 1000) {
    return `${Math.round(ms)} ms`;
  }

  const seconds = ms / 1000;
  if (seconds < 60) {
    return `${seconds.toFixed(1)} s`;
  }

  const wholeSeconds = Math.round(seconds);
  const minutes = Math.floor(wholeSeconds / 60);
  const rest = wholeSeconds % 60;
  return rest === 0 ? `${minutes} min` : `${minutes} min ${rest} s`;
}

/**
 * The model a call asked for and the one that answered. They differ when the provider routes: asking for
 * `openrouter/free` is answered by whichever model the router picked, and that is the one worth knowing.
 */
export function modelServed(call: ModelCallRecord): { requested: string; actual: string | null } {
  const actual = call.actualModel && call.actualModel !== call.requestedModel ? call.actualModel : null;
  return { requested: call.requestedModel, actual };
}
