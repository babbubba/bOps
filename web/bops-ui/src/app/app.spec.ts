// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { computed, signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { AuthService, AuthStatus } from './core/auth/auth.service';
import { I18n } from './core/i18n/i18n';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            status: signal('authenticated'),
            authenticated: signal(true),
            signOutFailed: signal(false),
            identity: signal({ id: 'test-user', displayName: 'Test User', roles: ['viewer'] }),
            signOut: jasmine.createSpy('signOut'),
          },
        },
      ],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('renders the primary navigation', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('Dashboard');
    expect(compiled.textContent).toContain('Approvals');
  });

  it('links System messages in the navigation, in English and Italian', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const link = (fixture.nativeElement as HTMLElement).querySelector('a[href="/system-messages"]');
    expect(link?.textContent?.trim()).toBe('System messages');

    TestBed.inject(I18n).setLanguage('it');
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('a[href="/system-messages"]')?.textContent?.trim()).toBe('Messaggi di sistema');
  });
});

/** ADR-0043 §14.2: the shell renders by the browser-session status. */
describe('App by session status', () => {
  let status: ReturnType<typeof signal<AuthStatus>>;
  let signOut: jasmine.Spy;
  let signOutFailed: ReturnType<typeof signal<boolean>>;

  beforeEach(() => {
    status = signal<AuthStatus>('initializing');
    signOut = jasmine.createSpy('signOut').and.resolveTo();
    signOutFailed = signal(false);
    TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            status,
            authenticated: computed(() => status() === 'authenticated'),
            sessionExpired: computed(() => status() === 'session-expired'),
            unavailable: computed(() => status() === 'unavailable'),
            signingOut: signal(false),
            signOutFailed,
            identity: signal({ id: 'test-user', displayName: 'Test User', roles: ['viewer'] }),
            signOut,
            restore: () => Promise.resolve(),
          },
        },
      ],
    });
  });

  function render(): HTMLElement {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders nothing while the session is still being asked about', () => {
    expect(render().textContent?.trim()).toBe('');
  });

  it('renders the sign-in screen, without an expired banner, when boot found no session', () => {
    status.set('unauthenticated');
    const element = render();
    expect(element.querySelector('bops-login')).not.toBeNull();
    expect(element.querySelector('[role="status"]')).toBeNull();
    expect(element.textContent).not.toContain('Dashboard');
  });

  it('renders the sign-in screen with the API-unreachable banner when the API could not be asked', () => {
    status.set('unavailable');
    const element = render();
    expect(element.querySelector('bops-login [role="alert"]')?.textContent).toContain('unreachable');
  });

  it('renders the sign-in screen with the expired banner after an expiry', () => {
    status.set('session-expired');
    expect(render().querySelector('bops-login [role="status"]')?.textContent).toContain('session expired');
  });

  it('signs out through the service and says so when the server did not confirm it', () => {
    status.set('authenticated');
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const button = Array.from(element.querySelectorAll('button')).find((candidate) => candidate.textContent?.includes('Sign out'))!;

    button.click();
    expect(signOut).toHaveBeenCalledTimes(1);

    signOutFailed.set(true);
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Sign-out could not be confirmed');
  });
});
