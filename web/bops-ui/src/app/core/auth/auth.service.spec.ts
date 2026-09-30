// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from './auth.service';

describe('AuthService session expiry', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  async function signIn(key = 'the-key'): Promise<void> {
    const signedIn = auth.signIn(key);
    http.expectOne('/api/session/me').flush({ id: 'alice', displayName: 'Alice', roles: ['operator'] });
    await signedIn;
  }

  it('goes back to signed-out with the reason kept when the session is expired', async () => {
    await signIn();
    expect(auth.authenticated()).toBeTrue();

    auth.expireSession();

    expect(auth.authenticated()).toBeFalse();
    expect(auth.token()).toBeNull();
    expect(auth.sessionExpired()).toBeTrue();
  });

  it('keeps saying the session expired across a failed attempt to sign in again', async () => {
    await signIn();
    auth.expireSession();

    const again = auth.signIn('wrong-key');
    http.expectOne('/api/session/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    await expectAsync(again).toBeRejected();

    expect(auth.authenticated()).toBeFalse();
    expect(auth.token()).toBeNull();
    expect(auth.sessionExpired()).toBeTrue();
  });

  it('clears the reason once the operator has signed in again', async () => {
    await signIn();
    auth.expireSession();

    await signIn('another-key');

    expect(auth.authenticated()).toBeTrue();
    expect(auth.sessionExpired()).toBeFalse();
  });

  it('does not call a deliberate sign-out an expiry', async () => {
    await signIn();
    auth.expireSession();
    auth.signOut();

    expect(auth.sessionExpired()).toBeFalse();
  });

  it('keeps no credential in browser storage', async () => {
    await signIn('secret-key-value');
    auth.expireSession();

    const stored = JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage });
    expect(stored).not.toContain('secret-key-value');
  });
});
