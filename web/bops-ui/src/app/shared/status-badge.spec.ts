// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { TestBed } from '@angular/core/testing';
import { StatusBadge } from './status-badge';

describe('StatusBadge', () => {
  it('keeps a long label on one truncated line and exposes the full accessible text', () => {
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.componentRef.setInput('status', 5);
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('span') as HTMLSpanElement;
    expect(badge.textContent?.trim()).toBe('Replanning limit reached');
    expect(badge.title).toBe('Replanning limit reached');
    expect(badge.getAttribute('aria-label')).toBe('Replanning limit reached');
    expect(badge.classList).toContain('shrink-0');
    expect(badge.classList).toContain('whitespace-nowrap');
    expect(badge.classList).toContain('truncate');
    expect(badge.classList).toContain('max-w-48');
  });
});
