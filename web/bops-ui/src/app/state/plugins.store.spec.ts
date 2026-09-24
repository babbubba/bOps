// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { PluginCatalogEntry } from '../core/api/models';
import { AuthService } from '../core/auth/auth.service';
import { PluginsStore } from './plugins.store';

function plugin(id: string, overrides: Partial<PluginCatalogEntry> = {}): PluginCatalogEntry {
  return {
    id,
    version: '1.0.0',
    publisher: 'Acme',
    installedAtUtc: '2026-09-17T12:00:00Z',
    enabled: true,
    loaded: true,
    compatible: true,
    signaturePresent: true,
    verified: true,
    trust: 2,
    keyId: 'test-key',
    declaredCapabilities: [],
    dependencies: [],
    declaredMaxRisk: 0,
    effectiveMaxRisk: 0,
    loadError: null,
    ...overrides,
  };
}

describe('PluginsStore', () => {
  let api: jasmine.SpyObj<BOpsApiClient>;

  beforeEach(() => {
    api = jasmine.createSpyObj<BOpsApiClient>('BOpsApiClient', ['listPlugins', 'getPlugin']);
    api.listPlugins.and.resolveTo({ entries: [], totalCount: 0 });

    TestBed.configureTestingModule({
      providers: [PluginsStore, { provide: BOpsApiClient, useValue: api }, { provide: AuthService, useValue: { identity: () => null } }],
    });
  });

  it('starts empty until refresh is called', () => {
    const store = TestBed.inject(PluginsStore);

    expect(store.entries()).toEqual([]);
    expect(api.listPlugins).not.toHaveBeenCalled();
  });

  it('loads the catalog on refresh', fakeAsync(() => {
    api.listPlugins.and.resolveTo({ entries: [plugin('acme.sample-plugin')], totalCount: 1 });
    const store = TestBed.inject(PluginsStore);

    void store.refresh();
    tick();

    expect(store.entries()).toEqual([plugin('acme.sample-plugin')]);
    expect(store.totalCount()).toBe(1);
    expect(store.loading()).toBeFalse();
  }));

  it('reports an API error without throwing', fakeAsync(() => {
    api.listPlugins.and.rejectWith(new Error('unreachable'));
    const store = TestBed.inject(PluginsStore);

    void store.refresh();
    tick();

    expect(store.error()).toBe('unreachable');
    expect(store.loading()).toBeFalse();
  }));

  it('re-fetches with the enabled filter when it changes', fakeAsync(() => {
    const store = TestBed.inject(PluginsStore);

    store.setEnabledFilter(true);
    tick();

    expect(api.listPlugins).toHaveBeenCalledOnceWith({ enabled: true, trust: undefined });
  }));

  it('re-fetches with the trust filter when it changes', fakeAsync(() => {
    const store = TestBed.inject(PluginsStore);

    store.setTrustFilter(3);
    tick();

    expect(api.listPlugins).toHaveBeenCalledOnceWith({ enabled: undefined, trust: 3 });
  }));

  it('tracks which plugin is selected without watching anything live', () => {
    const store = TestBed.inject(PluginsStore);

    store.selectPlugin('acme.sample-plugin');
    expect(store.selectedPluginId()).toBe('acme.sample-plugin');

    store.clearSelection();
    expect(store.selectedPluginId()).toBeNull();
  });
});
