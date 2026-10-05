// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { authInterceptor, isBopsApiRequest } from './auth.interceptor';
import { AuthService } from './auth.service';

describe('authInterceptor (ADR-0043 §14.4)', () => {
  let http: HttpTestingController;
  let client: HttpClient;
  let auth: AuthService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    auth = TestBed.inject(AuthService);
    auth.identity.set({ id: 'alice', displayName: 'Alice', roles: ['viewer'] });
    auth.status.set('authenticated');
  });

  afterEach(() => http.verify());

  function send(method: string, url: string): Promise<unknown> {
    return firstValueFrom(client.request(method, url, { body: method === 'GET' ? undefined : {} })).catch(() => undefined);
  }

  for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
    it(`adds X-bOps-Request: 1 to an unsafe (${method}) relative /api/ request, and never Authorization`, async () => {
      const done = send(method, '/api/agents/tasks');
      const request = http.expectOne('/api/agents/tasks');
      expect(request.request.headers.get('X-bOps-Request')).toBe('1');
      expect(request.request.headers.has('Authorization')).toBeFalse();
      expect(request.request.withCredentials).toBeFalse();
      request.flush({});
      await done;
    });
  }

  for (const method of ['GET', 'HEAD', 'OPTIONS']) {
    it(`adds nothing to a safe (${method}) request`, async () => {
      const done = send(method, '/api/agents/tasks');
      const request = http.expectOne('/api/agents/tasks');
      expect(request.request.headers.has('X-bOps-Request')).toBeFalse();
      expect(request.request.headers.has('Authorization')).toBeFalse();
      request.flush({});
      await done;
    });
  }

  for (const url of ['https://example.test/api/agents/tasks', '//example.test/api/x', 'http://localhost:5080/api/session', 'assets/api/x', '/apiary']) {
    it(`never adds bOps headers to a request outside the relative /api/ space (${url})`, async () => {
      const done = send('POST', url);
      const request = http.expectOne(url);
      expect(request.request.headers.has('X-bOps-Request')).toBeFalse();
      expect(request.request.headers.has('Authorization')).toBeFalse();
      request.flush({});
      await done;
    });
  }

  it('classifies only relative /api/ URLs as bOps API requests', () => {
    expect(isBopsApiRequest('/api/session')).toBeTrue();
    expect(isBopsApiRequest('https://evil.test/api/session')).toBeFalse();
    expect(isBopsApiRequest('//evil.test/api/session')).toBeFalse();
    expect(isBopsApiRequest('/apiary')).toBeFalse();
  });

  it('turns a 401 from the bOps API into session-expired while signed in', async () => {
    const done = send('GET', '/api/agents/tasks');
    http.expectOne('/api/agents/tasks').flush(null, { status: 401, statusText: 'Unauthorized' });
    await done;

    expect(auth.status()).toBe('session-expired');
  });

  it('ignores a 401 from another origin', async () => {
    const done = send('GET', 'https://example.test/api/agents/tasks');
    http.expectOne('https://example.test/api/agents/tasks').flush(null, { status: 401, statusText: 'Unauthorized' });
    await done;

    expect(auth.status()).toBe('authenticated');
  });

  it('passes the error on to the caller', async () => {
    const result = firstValueFrom(client.get('/api/agents/tasks'));
    http.expectOne('/api/agents/tasks').flush(null, { status: 401, statusText: 'Unauthorized' });

    await expectAsync(result).toBeRejected();
  });
});
