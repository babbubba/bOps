// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { ModelCallRecord, PlanStep } from '../core/api/models';

/** Every model call a step records, oldest first: its own calls, then any replan calls that followed a rejected premature final. */
export function stepModelCalls(step: Pick<PlanStep, 'modelCalls' | 'earlyFinalReplanCalls'>): ModelCallRecord[] {
  return [...(step.modelCalls ?? []), ...(step.earlyFinalReplanCalls ?? [])];
}

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
