// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { localInputToUtcIso } from './system-messages-time';

describe('localInputToUtcIso', () => {
  it('reads the entered wall-clock time in the local zone and sends exactly that instant as ISO UTC', () => {
    expect(localInputToUtcIso('2026-10-08T09:30:15')).toBe(new Date(2026, 9, 8, 9, 30, 15).toISOString());
    expect(localInputToUtcIso('2026-10-08T09:30')).toBe(new Date(2026, 9, 8, 9, 30, 0).toISOString());
    expect(localInputToUtcIso('2026-10-08T09:30')).toMatch(/Z$/);
  });

  it('does not widen the instant to a day or a minute (the API range is inclusive and exact)', () => {
    const to = localInputToUtcIso('2026-10-08T23:59:59')!;
    expect(new Date(to).getTime()).toBe(new Date(2026, 9, 8, 23, 59, 59).getTime());
  });

  it('is undefined for an empty or unreadable value', () => {
    expect(localInputToUtcIso('')).toBeUndefined();
    expect(localInputToUtcIso('   ')).toBeUndefined();
    expect(localInputToUtcIso('not a date')).toBeUndefined();
  });
});
