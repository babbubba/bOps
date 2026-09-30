// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ModelFailureKind, TaskTerminalKind } from '../core/api/models';
import { I18n } from '../core/i18n/i18n';
import { describeError, describeModelFailure, describeRefusal, describeTerminalKind } from './describe-error';

function refusal(status: number, code: string, message = 'server text', headers?: Record<string, string>): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: { code, message }, headers: new HttpHeaders(headers) });
}

describe('describeError', () => {
  const i18n = () => TestBed.inject(I18n);

  it("keeps the API's own message as sent, whatever the language", () => {
    const refused = new HttpErrorResponse({ status: 400, error: { message: 'No such run.' } });
    expect(describeError(refused, i18n())).toBe('No such run.');
    i18n().setLanguage('it');
    expect(describeError(refused, i18n())).toBe('No such run.');
  });

  it('says the API cannot be reached when nothing answered', () => {
    const down = new HttpErrorResponse({ status: 0, statusText: 'Unknown Error', url: '/api/settings' });
    expect(describeError(down, i18n())).toContain('Cannot reach the bOps API');

    i18n().setLanguage('it');
    expect(describeError(new HttpErrorResponse({ status: 0 }), i18n())).toContain('Impossibile raggiungere l’API di bOps');
  });

  it('says the API cannot be reached for a gateway or proxy failure that carries no bOps body', () => {
    for (const status of [502, 503, 504]) {
      for (const error of [null, '', '<html>Bad gateway</html>', {}]) {
        const gateway = new HttpErrorResponse({ status, statusText: 'Unknown Error', url: '/api/agents/tasks', error });
        expect(describeError(gateway, i18n())).withContext(`${status} ${JSON.stringify(error)}`).toContain('Cannot reach the bOps API');
      }
    }
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

  it('says the session expired for a refused credential, not that the API is down', () => {
    const text = describeError(new HttpErrorResponse({ status: 401 }), i18n());
    expect(text).toContain('session expired');
    expect(text).not.toContain('Cannot reach');
    i18n().setLanguage('it');
    expect(describeError(new HttpErrorResponse({ status: 401 }), i18n())).toContain('sessione è scaduta');
  });

  describe('a bOps refusal, recognised by its stable code', () => {
    it('409 task_running and resume_conflict say the task is already running or changed, and point at its current state', () => {
      expect(describeError(refusal(409, 'task_running'), i18n())).toContain('already running');
      expect(describeError(refusal(409, 'resume_conflict'), i18n())).toContain('changed while you were resuming');
      expect(describeError(refusal(409, 'task_running'), i18n())).not.toContain('Cannot reach');
    });

    it('409 task_delegated tells the operator to resume the delegation run', () => {
      expect(describeError(refusal(409, 'task_delegated'), i18n())).toContain('Resume the delegation run');
    });

    it('409 task_completed and task_policy_blocked say what to do instead', () => {
      expect(describeError(refusal(409, 'task_completed'), i18n())).toContain('Start a new task');
      expect(describeError(refusal(409, 'task_policy_blocked'), i18n())).toContain('does not bypass policy');
    });

    it('409 lifetime cap refusals say the lifetime budget is used up and to start a new task', () => {
      for (const code of ['lifetime_steps_exhausted', 'lifetime_replans_exhausted']) {
        const text = describeError(refusal(409, code), i18n());
        expect(text).withContext(code).toContain('over its lifetime');
        expect(text).withContext(code).toContain('Start a new task');
      }
    });

    it('409 token_budget_exhausted says a higher configured cap is required', () => {
      expect(describeError(refusal(409, 'token_budget_exhausted'), i18n())).toContain('higher configured cap');
    });

    it('501 transition_unsupported says resuming is unavailable on this host', () => {
      expect(describeError(refusal(501, 'transition_unsupported'), i18n())).toContain('resuming is unavailable');
    });

    it('503 executor_unavailable says bOps is busy and how long to wait, from Retry-After', () => {
      const text = describeError(refusal(503, 'executor_unavailable', 'Every execution slot is busy', { 'Retry-After': '5' }), i18n());
      expect(text).toContain('bOps is busy');
      expect(text).toContain('5 s');
      expect(text).not.toContain('Cannot reach');
    });

    it('503 executor_unavailable without a usable Retry-After still says busy, without inventing a delay', () => {
      for (const headers of [undefined, { 'Retry-After': 'soon' }, { 'Retry-After': '0' }]) {
        const text = describeError(refusal(503, 'executor_unavailable', 'busy', headers), i18n());
        expect(text).toContain('bOps is busy');
        expect(text).toContain('in a moment');
      }
    });

    it('rounds a fractional Retry-After up to whole seconds', () => {
      expect(describeError(refusal(503, 'executor_unavailable', 'busy', { 'Retry-After': '4.2' }), i18n())).toContain('5 s');
    });

    it('speaks Italian for every stable code', () => {
      i18n().setLanguage('it');
      const codes = [
        'task_running', 'resume_conflict', 'task_delegated', 'resume_origin_unknown', 'task_completed', 'task_policy_blocked',
        'task_status_not_resumable', 'token_budget_exhausted', 'lifetime_steps_exhausted', 'lifetime_replans_exhausted',
        'transition_unsupported', 'executor_unavailable',
      ];
      for (const code of codes) {
        const italian = describeRefusal(code, i18n(), 5)!;
        i18n().setLanguage('en');
        const english = describeRefusal(code, i18n(), 5)!;
        i18n().setLanguage('it');
        expect(italian).withContext(code).toBeTruthy();
        expect(italian).withContext(code).not.toBe(english);
      }
      expect(describeError(refusal(503, 'executor_unavailable', 'busy', { 'Retry-After': '5' }), i18n())).toContain('Riprova tra 5 s');
    });

    it('falls back to the API message as sent for a code it does not know', () => {
      expect(describeError(refusal(409, 'something_new', 'The server says so.'), i18n())).toBe('The server says so.');
    });

    it('falls back to the transport for a body with an unknown code and no message', () => {
      const bare = new HttpErrorResponse({ status: 409, error: { code: 'something_new' } });
      expect(describeError(bare, i18n())).toContain('HTTP 409');
    });

    it('is not fooled by a non-string code', () => {
      const odd = new HttpErrorResponse({ status: 409, error: { code: 7, message: 'kept as sent' } });
      expect(describeError(odd, i18n())).toBe('kept as sent');
    });
  });

  describe('provider failure guidance', () => {
    const kinds: [ModelFailureKind, string][] = [
      [5, 'provider key in Settings'],
      [6, 'no quota or credit'],
      [7, 'rejected the request'],
      [8, 'context limit'],
      [9, 'cannot use'],
      [1, 'stayed unavailable'],
      [2, 'stayed unavailable'],
      [3, 'stayed unavailable'],
      [4, 'stayed unavailable'],
      [0, 'could not classify'],
    ];

    for (const [kind, phrase] of kinds) {
      it(`names the next step for failure kind ${kind}`, () => {
        expect(describeModelFailure(kind, i18n())).toContain(phrase);
      });
    }

    it('reads a missing kind as unknown', () => {
      expect(describeModelFailure(null, i18n())).toContain('could not classify');
      expect(describeModelFailure(undefined, i18n())).toContain('could not classify');
    });

    it('has Italian guidance for every kind, different from the English', () => {
      for (const [kind] of kinds) {
        i18n().setLanguage('en');
        const english = describeModelFailure(kind, i18n());
        i18n().setLanguage('it');
        expect(describeModelFailure(kind, i18n())).withContext(`${kind}`).not.toBe(english);
      }
      expect(describeModelFailure(5, i18n())).toContain('Impostazioni');
    });
  });

  describe('terminal reason', () => {
    it('labels every terminal kind in both languages', () => {
      for (let kind = 0; kind <= 12; kind++) {
        i18n().setLanguage('en');
        const english = describeTerminalKind({ kind: kind as TaskTerminalKind }, i18n());
        i18n().setLanguage('it');
        const italian = describeTerminalKind({ kind: kind as TaskTerminalKind }, i18n());
        expect(english).withContext(`${kind}`).toBeTruthy();
        expect(english).withContext(`${kind}`).not.toMatch(/^\d+$/);
        expect(italian).withContext(`${kind}`).toBeTruthy();
      }
    });

    it('shows a kind a newer server sends as the server sent it', () => {
      expect(describeTerminalKind({ kind: 99 as TaskTerminalKind }, i18n())).toBe('99');
    });
  });
});
