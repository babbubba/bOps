// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../core/auth/auth.service';
import { I18n } from '../../core/i18n/i18n';
import { Login } from './login';

describe('Login', () => {
  let sessionExpired: ReturnType<typeof signal<boolean>>;
  let fixture: ComponentFixture<Login>;

  beforeEach(() => {
    sessionExpired = signal(false);
    TestBed.configureTestingModule({
      imports: [Login],
      providers: [{ provide: AuthService, useValue: { sessionExpired, signIn: () => Promise.resolve() } }],
    });
    fixture = TestBed.createComponent(Login);
    fixture.detectChanges();
  });

  const notice = (): HTMLElement | null => fixture.nativeElement.querySelector('[role="status"]');

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
});
