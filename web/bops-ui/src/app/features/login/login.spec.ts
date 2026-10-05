// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../core/auth/auth.service';
import { I18n } from '../../core/i18n/i18n';
import { Login } from './login';

const SECRET = 'login-spec-secret-key';

describe('Login', () => {
  let sessionExpired: ReturnType<typeof signal<boolean>>;
  let unavailable: ReturnType<typeof signal<boolean>>;
  let signIn: jasmine.Spy<(key: string, keep: boolean) => Promise<void>>;
  let restore: jasmine.Spy;
  let fixture: ComponentFixture<Login>;

  beforeEach(() => {
    sessionExpired = signal(false);
    unavailable = signal(false);
    signIn = jasmine.createSpy('signIn').and.resolveTo();
    restore = jasmine.createSpy('restore').and.resolveTo();
    TestBed.configureTestingModule({
      imports: [Login],
      providers: [{ provide: AuthService, useValue: { sessionExpired, unavailable, signIn, restore } }],
    });
    fixture = TestBed.createComponent(Login);
    fixture.detectChanges();
  });

  const element = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const notice = (): HTMLElement | null => element().querySelector('[role="status"]');
  const keyInput = (): HTMLInputElement => element().querySelector('#api-key') as HTMLInputElement;
  const keepBox = (): HTMLInputElement => element().querySelector('#keep-signed-in') as HTMLInputElement;

  async function typeAndSubmit(key: string, keep = false): Promise<void> {
    // NgModel registers with the form asynchronously; type only once the controls exist.
    await fixture.whenStable();
    keyInput().value = key;
    keyInput().dispatchEvent(new Event('input'));
    if (keep) {
      keepBox().click();
    }

    fixture.detectChanges();
    await fixture.whenStable();
    element().querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('says nothing about a session on an ordinary sign-in', () => {
    expect(notice()).toBeNull();
  });

  it('says the session expired, and that the task being followed will update again, after the API refused the credential', () => {
    sessionExpired.set(true);
    fixture.detectChanges();

    expect(notice()!.textContent).toContain('session expired');
    expect(notice()!.textContent).toContain('will start updating again');
  });

  it('says it in Italian', () => {
    TestBed.inject(I18n).setLanguage('it');
    sessionExpired.set(true);
    fixture.detectChanges();

    expect(notice()!.textContent).toContain('La sessione è scaduta');
  });

  it('offers "Keep me signed in on this device", unchecked by default, and explains the key is not kept', () => {
    expect(keepBox().checked).toBeFalse();
    expect(element().textContent).toContain('Keep me signed in on this device');
    expect(element().textContent).toContain('exchanged once for a browser session');
    expect(element().textContent).not.toContain("kept only in this page's memory");
  });

  it('signs in with the key and the checkbox, then clears the key field', async () => {
    await typeAndSubmit(SECRET, true);

    expect(signIn).toHaveBeenCalledOnceWith(SECRET, true);
    expect(keyInput().value).toBe('');
  });

  it('sends keepSignedIn false unless the box was ticked', async () => {
    await typeAndSubmit(SECRET);

    expect(signIn).toHaveBeenCalledOnceWith(SECRET, false);
  });

  it('clears the key field after a failed attempt too, and keeps it out of browser storage', async () => {
    signIn.and.rejectWith(new HttpErrorResponse({ status: 401, error: { code: 'invalid_credential', message: 'x' } }));
    const writes = [spyOn(localStorage, 'setItem').and.callThrough(), spyOn(sessionStorage, 'setItem').and.callThrough()];

    await typeAndSubmit(SECRET);

    expect(keyInput().value).toBe('');
    expect(element().textContent).toContain('Authentication failed.');
    for (const write of writes) expect(write).not.toHaveBeenCalled();
    expect(JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage })).not.toContain(SECRET);
  });

  const failures: [string, HttpErrorResponse, string][] = [
    ['401', new HttpErrorResponse({ status: 401, error: { code: 'invalid_credential' } }), 'Authentication failed.'],
    ['403 viewer', new HttpErrorResponse({ status: 403, error: { code: 'viewer_role_required' } }), 'viewer role required'],
    ['403 origin', new HttpErrorResponse({ status: 403, error: { code: 'csrf_rejected' } }), 'Open bOps at its configured address'],
    [
      '429',
      new HttpErrorResponse({ status: 429, error: { code: 'rate_limited' }, headers: new HttpHeaders({ 'Retry-After': '42' }) }),
      'try again in 42 s',
    ],
    ['0', new HttpErrorResponse({ status: 0 }), 'Cannot reach the bOps API'],
    ['503', new HttpErrorResponse({ status: 503 }), 'Cannot reach the bOps API'],
    ['500', new HttpErrorResponse({ status: 500 }), 'HTTP 500'],
  ];

  for (const [name, error, text] of failures) {
    it(`explains a ${name} refusal`, async () => {
      signIn.and.rejectWith(error);

      await typeAndSubmit(SECRET);

      expect(element().textContent).toContain(text);
      expect(element().textContent).not.toContain(SECRET);
    });
  }

  it('shows an unreachable banner with a retry while the API is unavailable', () => {
    unavailable.set(true);
    fixture.detectChanges();

    const alert = element().querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('unreachable');
    expect(notice()).toBeNull();

    (alert.querySelector('button') as HTMLButtonElement).click();
    expect(restore).toHaveBeenCalledTimes(1);
  });
});
