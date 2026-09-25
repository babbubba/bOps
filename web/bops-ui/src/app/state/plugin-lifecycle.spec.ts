// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PluginCatalogEntry } from '../core/api/models';
import { AuthService } from '../core/auth/auth.service';
import { PluginsStore } from './plugins.store';

const ETAG = '"plv-7"';

function entry(overrides: Partial<PluginCatalogEntry> = {}): PluginCatalogEntry {
  return {
    id: 'acme.sample',
    version: '1.2.0',
    publisher: 'Acme',
    installedAtUtc: '2026-09-17T12:00:00Z',
    enabled: false,
    loaded: false,
    compatible: true,
    signaturePresent: true,
    verified: true,
    trust: 2,
    keyId: 'k',
    declaredCapabilities: [],
    dependencies: [],
    declaredMaxRisk: 0,
    effectiveMaxRisk: null,
    loadError: null,
    lifecycleState: 'InstalledDisabled',
    lifecycleETag: ETAG,
    lifecycleFailure: null,
    recoveryAvailable: false,
    ...overrides,
  };
}

function zip(name = 'whatever-name.zip', content = 'PK'): File {
  return new File([content], name, { type: 'application/zip', lastModified: 1 });
}

/** Lets the store's post-mutation `await refresh()` reach its HTTP call. */
const tick = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('PluginsStore lifecycle mutations', () => {
  let http: HttpTestingController;
  let store: InstanceType<typeof PluginsStore>;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthService).identity.set({ id: 'a', displayName: 'A', roles: ['administrator'] });
    store = TestBed.inject(PluginsStore);
  });

  afterEach(() => {
    http.match(() => true); // drop any unflushed reloads; each test asserts what it cares about
  });

  /** Answers the catalog reload the store performs after every mutation outcome. */
  async function flushReload(entries: PluginCatalogEntry[] = [entry({ lifecycleETag: '"plv-8"' })]): Promise<void> {
    await tick();
    http.expectOne((r) => r.url === '/api/plugins' && r.method === 'GET').flush({ entries, totalCount: entries.length });
  }

  // ---- role ----

  it('projects the administrator role from the authenticated identity and fails closed without one', () => {
    expect(store.isAdministrator()).toBeTrue();

    TestBed.inject(AuthService).identity.set({ id: 'o', displayName: 'O', roles: ['viewer', 'operator'] });
    expect(store.isAdministrator()).toBeFalse();

    TestBed.inject(AuthService).identity.set(null);
    expect(store.isAdministrator()).toBeFalse();
  });

  // ---- install ----

  it('installs with application/zip, If-None-Match: * and an Idempotency-Key, without using the file name as identity', async () => {
    const file = zip('renamed-by-someone.zip');
    const done = store.installArchive(file);

    const req = http.expectOne('/api/plugins/archives');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('Content-Type')).toBe('application/zip');
    expect(req.request.headers.get('If-None-Match')).toBe('*');
    expect(req.request.headers.has('If-Match')).toBeFalse();
    expect(req.request.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(req.request.body).toBe(file);
    expect(req.request.urlWithParams).not.toContain('renamed-by-someone');

    req.flush({ pluginId: 'acme.sample', version: '1.2.0', state: 'InstalledDisabled' }, { status: 201, statusText: 'Created' });
    await flushReload();
    expect(await done).toBeTrue();
    expect(store.notice()?.kind).toBe('success');
  });

  // ---- replace ----

  it('replaces with the current opaque If-Match and an Idempotency-Key, and never fetches a fresher ETag first', async () => {
    const file = zip();
    const done = store.replaceArchive(entry({ lifecycleETag: 'W/"opaque-not-a-number"' }), file);

    const req = http.expectOne('/api/plugins/acme.sample/archive');
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('Content-Type')).toBe('application/zip');
    expect(req.request.headers.get('If-Match')).toBe('W/"opaque-not-a-number"');
    expect(req.request.headers.has('If-None-Match')).toBeFalse();
    expect(req.request.headers.get('Idempotency-Key')).toBeTruthy();

    req.flush({ pluginId: 'acme.sample', version: '1.3.0', state: 'InstalledDisabled' });
    await flushReload();
    expect(await done).toBeTrue();
  });

  it('does nothing when the plugin has no server-issued ETag', async () => {
    expect(await store.replaceArchive(entry({ lifecycleETag: null }), zip())).toBeFalse();
    expect(await store.enable(entry({ lifecycleETag: null }), '1.2.0')).toBeFalse();
    expect(await store.disable(entry({ lifecycleETag: null }))).toBeFalse();
    expect(await store.recover(entry({ lifecycleETag: null }))).toBeFalse();
    http.expectNone(() => true);
  });

  // ---- enable / disable / recover ----

  it('enable sends the exact confirmed version and the current If-Match', async () => {
    const done = store.enable(entry(), '1.2.0');

    const req = http.expectOne('/api/plugins/acme.sample/enable');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ confirmedVersion: '1.2.0' });
    expect(req.request.headers.get('If-Match')).toBe(ETAG);
    expect(req.request.headers.get('Idempotency-Key')).toBeTruthy();

    req.flush({ pluginId: 'acme.sample', version: '1.2.0', state: 'Enabled' });
    await flushReload([entry({ enabled: true, lifecycleState: 'Enabled', lifecycleETag: '"plv-8"' })]);
    expect(await done).toBeTrue();
    // The ETag and state now come from the reloaded catalog, not from local simulation.
    expect(store.entries()[0].lifecycleETag).toBe('"plv-8"');
    expect(store.entries()[0].lifecycleState).toBe('Enabled');
  });

  it('disable sends the current If-Match and reloads authoritative state', async () => {
    const done = store.disable(entry({ enabled: true, lifecycleState: 'Enabled' }));

    const req = http.expectOne('/api/plugins/acme.sample/disable');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe(ETAG);
    expect(req.request.headers.get('Idempotency-Key')).toBeTruthy();

    req.flush({ pluginId: 'acme.sample', version: '1.2.0', state: 'InstalledDisabled' });
    await flushReload([entry({ lifecycleETag: '"plv-9"' })]);
    expect(await done).toBeTrue();
    expect(store.entries()[0].lifecycleETag).toBe('"plv-9"');
  });

  it('recover sends confirmed=true with the current If-Match and never calls enable', async () => {
    const done = store.recover(entry({ lifecycleState: 'RecoveryRequired', recoveryAvailable: true }));

    const req = http.expectOne('/api/plugins/acme.sample/recover');
    expect(req.request.body).toEqual({ confirmed: true });
    expect(req.request.headers.get('If-Match')).toBe(ETAG);

    req.flush({ pluginId: 'acme.sample', version: '1.2.0', state: 'InstalledDisabled' });
    await flushReload();
    expect(await done).toBeTrue();
    http.expectNone((r) => r.url.endsWith('/enable'));
  });

  // ---- double submit ----

  it('a second submit while one is in flight issues no second request', async () => {
    const first = store.disable(entry({ enabled: true, lifecycleState: 'Enabled' }));
    const second = store.disable(entry({ enabled: true, lifecycleState: 'Enabled' }));

    expect(store.mutating()).toBeTrue();
    expect(await second).toBeFalse();
    const req = http.expectOne('/api/plugins/acme.sample/disable'); // exactly one
    req.flush({ pluginId: 'acme.sample', version: null, state: 'InstalledDisabled' });
    await flushReload();
    await first;
    expect(store.mutating()).toBeFalse();
  });

  // ---- idempotency key semantics ----

  it('reuses the key for an ambiguous transport retry of the same intent, and uses a new one when intent changes', async () => {
    const keys: string[] = [];
    const attempt = async (e: PluginCatalogEntry, respond: (r: ReturnType<HttpTestingController['expectOne']>) => void) => {
      const done = store.disable(e);
      const req = http.expectOne('/api/plugins/acme.sample/disable');
      keys.push(req.request.headers.get('Idempotency-Key')!);
      respond(req);
      await flushReload();
      await done;
    };

    const enabled = entry({ enabled: true, lifecycleState: 'Enabled' });
    await attempt(enabled, (r) => r.error(new ProgressEvent('error'))); // network ambiguity (status 0)
    await attempt(enabled, (r) => r.flush({}, { status: 503, statusText: 'x' })); // unchanged intent: same key
    expect(keys[1]).toBe(keys[0]);

    await attempt(entry({ enabled: true, lifecycleState: 'Enabled', lifecycleETag: '"plv-9"' }), (r) =>
      r.flush({ pluginId: 'acme.sample', version: null, state: 'InstalledDisabled' }),
    ); // ETag changed: new intent
    expect(keys[2]).not.toBe(keys[0]);

    await attempt(entry({ enabled: true, lifecycleState: 'Enabled', lifecycleETag: '"plv-9"' }), (r) =>
      r.flush({ pluginId: 'acme.sample', version: null, state: 'InstalledDisabled' }),
    ); // a new operator action after success: new key
    expect(keys[3]).not.toBe(keys[2]);
  });

  it('a 409 idempotency conflict is surfaced and never retried with another key', async () => {
    const done = store.disable(entry({ enabled: true, lifecycleState: 'Enabled' }));
    http
      .expectOne('/api/plugins/acme.sample/disable')
      .flush({ message: 'The Idempotency-Key was already used for a different request.', category: 'idempotency_conflict' }, { status: 409, statusText: 'Conflict' });
    await flushReload();

    expect(await done).toBeFalse();
    expect(store.notice()?.kind).toBe('error');
    expect(store.notice()?.text).toContain('already used');
    http.expectNone((r) => r.method === 'POST');
  });

  // ---- errors ----

  it('a 412 reloads state, tells the operator, and does not resubmit', async () => {
    const done = store.enable(entry(), '1.2.0');
    http
      .expectOne('/api/plugins/acme.sample/enable')
      .flush({ message: 'stale', category: 'stale_lifecycle_version' }, { status: 412, statusText: 'Precondition Failed' });
    await flushReload([entry({ lifecycleETag: '"plv-99"' })]);

    expect(await done).toBeFalse();
    expect(store.notice()?.kind).toBe('stale');
    expect(store.entries()[0].lifecycleETag).toBe('"plv-99"');
    http.expectNone((r) => r.method === 'POST');
  });

  it('a 428 reloads state with a neutral message and does not mutate again', async () => {
    const done = store.disable(entry({ enabled: true, lifecycleState: 'Enabled' }));
    http
      .expectOne('/api/plugins/acme.sample/disable')
      .flush({ message: 'If-Match is required', category: 'precondition_required' }, { status: 428, statusText: 'Precondition Required' });
    await flushReload();

    expect(await done).toBeFalse();
    expect(store.notice()?.kind).toBe('stale');
    http.expectNone((r) => r.method === 'POST');
  });

  it('a 422 shows the sanitized backend message with its category and stage as plain text', async () => {
    const done = store.installArchive(zip());
    http
      .expectOne('/api/plugins/archives')
      .flush(
        { message: 'The plugin publisher is not trusted on this host.', category: 'publisher_untrusted', stage: 'Trust' },
        { status: 422, statusText: 'Unprocessable Entity' },
      );
    await flushReload();

    expect(await done).toBeFalse();
    expect(store.notice()).toEqual({
      kind: 'error',
      text: 'The plugin publisher is not trusted on this host. (publisher_untrusted · Trust)',
    });
  });

  it('an activation failure reloads the plugin so failure and recovery availability are authoritative', async () => {
    const done = store.enable(entry(), '1.2.0');
    http
      .expectOne('/api/plugins/acme.sample/enable')
      .flush({ message: 'The plugin could not be activated and remains disabled.', category: 'activation_failed' }, { status: 422, statusText: 'x' });
    await flushReload([
      entry({ lifecycleState: 'ActivationFailed', lifecycleFailure: 'boom', recoveryAvailable: true, lifecycleETag: '"plv-8"' }),
    ]);

    await done;
    expect(store.entries()[0].lifecycleFailure).toBe('boom');
    expect(store.entries()[0].recoveryAvailable).toBeTrue();
  });

  it('exposes no delete or remove operation', () => {
    const names = Object.keys(store).concat(Object.keys(TestBed.inject(PluginsStore)));
    expect(names.some((n) => /delete|remove|uninstall/i.test(n))).toBeFalse();
  });
});
