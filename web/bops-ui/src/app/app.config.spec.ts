// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { appConfig } from './app.config';
import { AuthService } from './core/auth/auth.service';

/** ADR-0043 §14.2: bootstrap waits for the boot `GET /api/session/me`, so nothing polls before the session status is known. */
describe('Application boot', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [...appConfig.providers, provideHttpClientTesting()] });
  });

  it('holds the application initializers until the browser session was asked about, then restores it', async () => {
    const init = TestBed.inject(ApplicationInitStatus);
    const http = TestBed.inject(HttpTestingController);
    const auth = TestBed.inject(AuthService);

    const boot = http.expectOne('/api/session/me');
    expect(init.done).toBeFalse();
    expect(auth.status()).toBe('initializing');
    expect(boot.request.headers.has('Authorization')).toBeFalse();

    boot.flush({ id: 'alice', displayName: 'Alice', roles: ['viewer'] });
    await init.donePromise;

    expect(init.done).toBeTrue();
    expect(auth.status()).toBe('authenticated');
    http.verify();
  });

  it('finishes booting into the sign-in screen when there is no session', async () => {
    const init = TestBed.inject(ApplicationInitStatus);
    const http = TestBed.inject(HttpTestingController);

    http.expectOne('/api/session/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    await init.donePromise;

    expect(TestBed.inject(AuthService).status()).toBe('unauthenticated');
  });
});
