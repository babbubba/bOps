// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Injectable, inject } from '@angular/core';
import { TestBed, fakeAsync, flush, tick } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { AuthService, RESTORE_TIMEOUT_MS } from './auth.service';

const ALICE = { id: 'alice', displayName: 'Alice', roles: ['viewer', 'operator'] };
const SECRET = 'secret-key-value-that-must-not-stay';

/** A plain caller of the API, standing in for any store. */
@Injectable({ providedIn: 'root' })
class HttpClientForTest {
  private readonly http = inject(HttpClient);

  get(url: string): Promise<unknown> {
    return firstValueFrom(this.http.get(url));
  }
}

/** Every place the page could keep a value without a server: the key must be in none of them. */
function browserStorageText(): string {
  return JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage });
}

describe('AuthService (ADR-0043 browser session)', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function restoreWith(status: number, body: object | null = null): Promise<void> {
    const restoring = auth.restore();
    const request = http.expectOne('/api/session/me');
    if (status === 200) {
      request.flush(body ?? ALICE);
    } else {
      request.flush(body, { status, statusText: 'x' });
    }

    await restoring;
  }

  async function signIn(keepSignedIn = false): Promise<void> {
    const signedIn = auth.signIn(SECRET, keepSignedIn);
    http.expectOne('/api/session').flush(ALICE);
    await signedIn;
  }

  describe('boot', () => {
    it('starts initializing and restores the identity from an existing session', async () => {
      expect(auth.status()).toBe('initializing');

      await restoreWith(200);

      expect(auth.status()).toBe('authenticated');
      expect(auth.identity()).toEqual(ALICE);
    });

    it('sends no Authorization header and no credential at all to ask', async () => {
      const restoring = auth.restore();
      const request = http.expectOne('/api/session/me');
      expect(request.request.headers.has('Authorization')).toBeFalse();
      expect(request.request.method).toBe('GET');
      request.flush(ALICE);
      await restoring;
    });

    it('is unauthenticated without an expired banner when the API says 401', async () => {
      await restoreWith(401);

      expect(auth.status()).toBe('unauthenticated');
      expect(auth.sessionExpired()).toBeFalse();
      expect(auth.identity()).toBeNull();
    });

    for (const status of [0, 429, 500, 502, 503, 504]) {
      it(`is unavailable, not signed out, when the API answers ${status}`, async () => {
        await restoreWith(status);

        expect(auth.status()).toBe('unavailable');
        expect(auth.sessionExpired()).toBeFalse();
      });
    }

    it('is unavailable after the 10 s timeout and recovers on the retry schedule', fakeAsync(() => {
      void auth.restore();
      http.expectOne('/api/session/me');
      tick(RESTORE_TIMEOUT_MS);
      expect(auth.status()).toBe('unavailable');

      tick(2000);
      http.expectOne('/api/session/me').flush(ALICE);
      flush();
      expect(auth.status()).toBe('authenticated');
    }));

    it('retries at once when the browser comes back online', fakeAsync(() => {
      void auth.restore();
      http.expectOne('/api/session/me').flush(null, { status: 0, statusText: 'Unknown Error' });
      flush();
      expect(auth.status()).toBe('unavailable');

      window.dispatchEvent(new Event('online'));
      http.expectOne('/api/session/me').flush(null, { status: 401, statusText: 'Unauthorized' });
      flush();
      expect(auth.status()).toBe('unauthenticated');
    }));

    it('never rejects', async () => {
      const restoring = auth.restore();
      http.expectOne('/api/session/me').error(new ProgressEvent('error'));
      await expectAsync(restoring).toBeResolved();
    });
  });

  describe('sign-in', () => {
    beforeEach(async () => restoreWith(401));

    it('posts the key once as JSON with keepSignedIn and the CSRF header, never as Authorization', async () => {
      const signedIn = auth.signIn(`  ${SECRET} `, true);
      const request = http.expectOne('/api/session');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ apiKey: SECRET, keepSignedIn: true });
      expect(request.request.headers.get('X-bOps-Request')).toBe('1');
      expect(request.request.headers.has('Authorization')).toBeFalse();
      expect(request.request.url).not.toContain(SECRET);
      request.flush(ALICE);
      await signedIn;

      expect(auth.status()).toBe('authenticated');
      expect(auth.identity()).toEqual(ALICE);
    });

    it('asks for a browser-session cookie (keepSignedIn false) by default', async () => {
      const signedIn = auth.signIn(SECRET);
      const request = http.expectOne('/api/session');
      expect(request.request.body).toEqual({ apiKey: SECRET, keepSignedIn: false });
      request.flush(ALICE);
      await signedIn;
    });

    it('keeps the key in no field, signal or browser storage once signed in', async () => {
      await signIn();

      expect(JSON.stringify(auth.identity())).not.toContain(SECRET);
      expect(Object.values(auth as unknown as Record<string, unknown>).some((value) => value === SECRET)).toBeFalse();
      expect(browserStorageText()).not.toContain(SECRET);
    });

    it('rejects and stays unauthenticated on a refused key, without calling it an expiry', async () => {
      const attempt = auth.signIn('wrong');
      http.expectOne('/api/session').flush({ code: 'invalid_credential', message: 'x' }, { status: 401, statusText: 'Unauthorized' });
      await expectAsync(attempt).toBeRejected();

      expect(auth.status()).toBe('unauthenticated');
      expect(auth.sessionExpired()).toBeFalse();
    });

    it('never touches localStorage, sessionStorage or IndexedDB', async () => {
      const writes = [spyOn(localStorage, 'setItem').and.callThrough(), spyOn(sessionStorage, 'setItem').and.callThrough()];
      const open = spyOn(indexedDB, 'open').and.callThrough();

      await signIn(true);

      for (const write of writes) expect(write).not.toHaveBeenCalled();
      expect(open).not.toHaveBeenCalled();
    });
  });

  describe('expiry and sign-out', () => {
    beforeEach(async () => {
      await restoreWith(401);
      await signIn();
    });

    it('turns a 401 from a bOps API request while signed in into session-expired, exactly once', async () => {
      const client = TestBed.inject(HttpClientForTest);
      const first = client.get('/api/agents/tasks');
      const second = client.get('/api/approvals/pending');
      http.expectOne('/api/agents/tasks').flush(null, { status: 401, statusText: 'Unauthorized' });
      http.expectOne('/api/approvals/pending').flush(null, { status: 401, statusText: 'Unauthorized' });
      await Promise.allSettled([first, second]);

      expect(auth.status()).toBe('session-expired');
      expect(auth.sessionExpired()).toBeTrue();
      expect(auth.identity()).toBeNull();
    });

    it('stays session-expired across a failed attempt to sign in again, then clears it on success', async () => {
      auth.expireSession();
      const again = auth.signIn('wrong');
      http.expectOne('/api/session').flush(null, { status: 401, statusText: 'Unauthorized' });
      await expectAsync(again).toBeRejected();
      expect(auth.sessionExpired()).toBeTrue();

      await signIn();
      expect(auth.status()).toBe('authenticated');
    });

    it('signs out with DELETE /api/session and the CSRF header; 204 means unauthenticated, not expired', async () => {
      const signingOut = auth.signOut();
      const request = http.expectOne('/api/session');
      expect(request.request.method).toBe('DELETE');
      expect(request.request.headers.get('X-bOps-Request')).toBe('1');
      request.flush(null, { status: 204, statusText: 'No Content' });
      await signingOut;

      expect(auth.status()).toBe('unauthenticated');
      expect(auth.sessionExpired()).toBeFalse();
    });

    it('treats a 401 on sign-out as signed out, never as an expiry', async () => {
      const signingOut = auth.signOut();
      http.expectOne('/api/session').flush(null, { status: 401, statusText: 'Unauthorized' });
      await signingOut;

      expect(auth.status()).toBe('unauthenticated');
      expect(auth.sessionExpired()).toBeFalse();
    });

    it('stays signed in, with an error, when the server did not confirm the sign-out', async () => {
      const signingOut = auth.signOut();
      http.expectOne('/api/session').flush(null, { status: 503, statusText: 'Unavailable' });
      await signingOut;

      expect(auth.status()).toBe('authenticated');
      expect(auth.signOutFailed()).toBeTrue();
    });

    it('ignores an expiry in any status other than authenticated', async () => {
      const signingOut = auth.signOut();
      http.expectOne('/api/session').flush(null, { status: 204, statusText: 'No Content' });
      await signingOut;

      auth.expireSession();
      expect(auth.status()).toBe('unauthenticated');
    });
  });
});
