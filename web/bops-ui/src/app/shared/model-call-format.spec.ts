// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { ModelCallRecord } from '../core/api/models';
import { modelServed, stepModelCalls } from './model-call-format';

function call(overrides: Partial<ModelCallRecord> = {}): ModelCallRecord {
  return {
    provider: 'OpenRouter',
    requestedModel: 'openrouter/free',
    actualModel: 'vendor/picked-model',
    startedAtUtc: '2026-09-19T19:05:53Z',
    durationMs: 1500,
    outcome: 0,
    usage: { promptTokens: 10116, completionTokens: 788, estimatedCostUsd: null },
    finishReason: 'stop',
    errorMessage: null,
    payloadTruncated: false,
    ...overrides,
  };
}

describe('modelServed', () => {
  it('reports the actual model when a router picked one other than the one requested', () => {
    expect(modelServed(call())).toEqual({ requested: 'openrouter/free', actual: 'vendor/picked-model' });
  });

  it('reports no separate actual model when it is the one requested or is unknown', () => {
    expect(modelServed(call({ actualModel: 'openrouter/free' })).actual).toBeNull();
    expect(modelServed(call({ actualModel: null })).actual).toBeNull();
  });
});

describe('stepModelCalls', () => {
  it('lists a step’s own calls, then the replan calls that followed a rejected premature final', () => {
    const own = call({ finishReason: 'stop' });
    const replan = call({ outcome: 1, finishReason: 'length', errorMessage: 'malformed' });
    expect(stepModelCalls({ modelCalls: [own], earlyFinalReplanCalls: [replan] })).toEqual([own, replan]);
  });

  it('reads steps stored without either collection', () => {
    expect(stepModelCalls({})).toEqual([]);
    expect(stepModelCalls({ modelCalls: null, earlyFinalReplanCalls: null })).toEqual([]);
  });
});
