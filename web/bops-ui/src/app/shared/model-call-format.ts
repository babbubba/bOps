// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { ModelCallRecord } from '../core/api/models';

/**
 * The model a call asked for and the one that answered. They differ when the provider routes: asking for
 * `openrouter/free` is answered by whichever model the router picked, and that is the one worth knowing.
 *
 * How long a call took is formatted by `I18n.duration`, since the unit words and the decimal separator follow the language.
 */
export function modelServed(call: ModelCallRecord): { requested: string; actual: string | null } {
  const actual = call.actualModel && call.actualModel !== call.requestedModel ? call.actualModel : null;
  return { requested: call.requestedModel, actual };
}
