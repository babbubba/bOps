// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { I18n } from '../core/i18n/i18n';
import { describeError } from './describe-error';

describe('describeError', () => {
  const i18n = () => TestBed.inject(I18n);

  it("keeps the API's own message as sent, whatever the language", () => {
    const refused = new HttpErrorResponse({ status: 400, error: { message: 'No such run.' } });
    expect(describeError(refused, i18n())).toBe('No such run.');
    i18n().setLanguage('it');
    expect(describeError(refused, i18n())).toBe('No such run.');
  });

  it('says the API cannot be reached when nothing answered or a proxy answered for a backend that is down', () => {
    for (const status of [0, 502, 503, 504]) {
      const down = new HttpErrorResponse({ status, statusText: 'Unknown Error', url: '/api/settings' });
      expect(describeError(down, i18n())).withContext(`${status}`).toContain('Cannot reach the bOps API');
    }

    i18n().setLanguage('it');
    expect(describeError(new HttpErrorResponse({ status: 0 }), i18n())).toContain('Impossibile raggiungere l’API di bOps');
  });

  it("uses an ordinary error's message, and a translated fallback for anything else", () => {
    expect(describeError(new Error('boom'), i18n())).toBe('boom');
    expect(describeError('not an error', i18n())).toBe('Something went wrong.');
    i18n().setLanguage('it');
    expect(describeError(undefined, i18n())).toBe('Qualcosa è andato storto.');
  });

  it('leaves any other HTTP failure to the transport text rather than calling it unreachable', () => {
    const forbidden = new HttpErrorResponse({ status: 403, statusText: 'Forbidden', url: '/api/settings' });
    const text = describeError(forbidden, i18n());
    expect(text).not.toContain('Cannot reach');
    expect(text).toContain('HTTP 403');
    expect(describeError(new HttpErrorResponse({ status: 500 }), i18n())).toContain('HTTP 500');
  });
});
