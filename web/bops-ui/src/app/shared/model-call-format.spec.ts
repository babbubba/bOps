// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { ModelCallRecord } from '../core/api/models';
import { formatDuration, modelServed } from './model-call-format';

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

describe('formatDuration', () => {
  it('uses milliseconds under a second', () => {
    expect(formatDuration(0)).toBe('0 ms');
    expect(formatDuration(820)).toBe('820 ms');
    expect(formatDuration(999.4)).toBe('999 ms');
  });

  it('uses seconds with one decimal under a minute', () => {
    expect(formatDuration(1000)).toBe('1.0 s');
    expect(formatDuration(4321)).toBe('4.3 s');
  });

  it('uses minutes and seconds from a minute up', () => {
    expect(formatDuration(65_000)).toBe('1 min 5 s');
    expect(formatDuration(120_000)).toBe('2 min');
  });

  it('shows a dash for a value that cannot be a duration', () => {
    expect(formatDuration(-1)).toBe('—');
    expect(formatDuration(Number.NaN)).toBe('—');
  });
});

describe('modelServed', () => {
  it('reports the actual model when a router picked one other than the one requested', () => {
    expect(modelServed(call())).toEqual({ requested: 'openrouter/free', actual: 'vendor/picked-model' });
  });

  it('reports no separate actual model when it is the one requested or is unknown', () => {
    expect(modelServed(call({ actualModel: 'openrouter/free' })).actual).toBeNull();
    expect(modelServed(call({ actualModel: null })).actual).toBeNull();
  });
});
