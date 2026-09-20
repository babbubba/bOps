// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { SettingsView } from '../core/api/models';
import { SettingsStore } from './settings.store';

function view(overrides: Partial<SettingsView> = {}): SettingsView {
  return {
    vaultVersion: 0,
    activeProviderId: null,
    activeProviderSource: 'Default',
    providers: [
      {
        providerId: 'Anthropic',
        isActive: false,
        hasStoredKey: false,
        keyMaskPrefix: null,
        keyMaskSuffix: null,
        keyPlaintextLength: null,
        keyUpdatedUtc: null,
        baseUrl: null,
        model: null,
        supportsNativeToolCalling: null,
        extraParameters: null,
        profileUpdatedUtc: null,
      },
    ],
    ...overrides,
  };
}

describe('SettingsStore', () => {
  let api: jasmine.SpyObj<BOpsApiClient>;

  beforeEach(() => {
    api = jasmine.createSpyObj<BOpsApiClient>('BOpsApiClient', [
      'getSettings', 'setProviderKey', 'clearProviderKey', 'setProviderProfile', 'setActiveProvider',
    ]);
    api.getSettings.and.resolveTo(view());

    TestBed.configureTestingModule({
      providers: [SettingsStore, { provide: BOpsApiClient, useValue: api }],
    });
  });

  it('starts empty until refresh is called', () => {
    const store = TestBed.inject(SettingsStore);

    expect(store.view()).toBeNull();
    expect(api.getSettings).not.toHaveBeenCalled();
  });

  it('loads the view on refresh', fakeAsync(() => {
    const store = TestBed.inject(SettingsStore);

    void store.refresh();
    tick();

    expect(store.view()?.providers[0].providerId).toBe('Anthropic');
    expect(store.loading()).toBeFalse();
  }));

  it('reports an API error without throwing', fakeAsync(() => {
    api.getSettings.and.rejectWith(new Error('unreachable'));
    const store = TestBed.inject(SettingsStore);

    void store.refresh();
    tick();

    expect(store.error()).toBe('unreachable');
  }));

  it('explains a 404 as Settings not being available on this host, and drops what it showed before', fakeAsync(() => {
    const store = TestBed.inject(SettingsStore);
    void store.refresh();
    tick();
    expect(store.view()).not.toBeNull();

    api.getSettings.and.rejectWith(new HttpErrorResponse({ status: 404, statusText: 'Not Found', url: '/api/settings' }));
    void store.refresh();
    tick();

    expect(store.view()).toBeNull();
    expect(store.error()).toContain('Vault:MasterKeySecret');
  }));

  it('says the API cannot be reached when the backend does not answer', fakeAsync(() => {
    api.getSettings.and.rejectWith(new HttpErrorResponse({ status: 504, statusText: 'Gateway Timeout', url: '/api/settings' }));
    const store = TestBed.inject(SettingsStore);

    void store.refresh();
    tick();

    expect(store.error()).toContain('Cannot reach the bOps API');
  }));

  it('setProviderKey sends the current vaultVersion and refreshes on success', fakeAsync(() => {
    api.setProviderKey.and.resolveTo();
    const store = TestBed.inject(SettingsStore);
    void store.refresh();
    tick();

    let result: boolean | undefined;
    void store.setProviderKey('Anthropic', 'sk-abc').then((r) => (result = r));
    tick();

    expect(api.setProviderKey).toHaveBeenCalledOnceWith('Anthropic', 'sk-abc', 0);
    expect(result).toBeTrue();
    expect(api.getSettings).toHaveBeenCalledTimes(2);
  }));

  it('setProviderKey reports a conflict on 409 and refreshes to the latest state', fakeAsync(() => {
    api.setProviderKey.and.rejectWith(new HttpErrorResponse({ status: 409, error: { message: 'stale version' } }));
    const store = TestBed.inject(SettingsStore);
    void store.refresh();
    tick();

    let result: boolean | undefined;
    void store.setProviderKey('Anthropic', 'sk-abc').then((r) => (result = r));
    tick();

    expect(result).toBeFalse();
    expect(store.conflict()).toBeTrue();
    expect(store.error()).toBe('stale version');
    expect(api.getSettings).toHaveBeenCalledTimes(2);
  }));

  it('clearProviderKey sends the current vaultVersion', fakeAsync(() => {
    api.clearProviderKey.and.resolveTo();
    const store = TestBed.inject(SettingsStore);
    void store.refresh();
    tick();

    void store.clearProviderKey('Anthropic');
    tick();

    expect(api.clearProviderKey).toHaveBeenCalledOnceWith('Anthropic', 0);
  }));

  it('setProviderProfile forwards the request and refreshes', fakeAsync(() => {
    api.setProviderProfile.and.resolveTo();
    const store = TestBed.inject(SettingsStore);
    void store.refresh();
    tick();

    void store.setProviderProfile('Anthropic', {
      baseUrl: 'https://api.anthropic.com',
      model: 'claude-sonnet-4-5',
      supportsNativeToolCalling: true,
      extraParameters: null,
    });
    tick();

    expect(api.setProviderProfile).toHaveBeenCalledOnceWith('Anthropic', {
      baseUrl: 'https://api.anthropic.com',
      model: 'claude-sonnet-4-5',
      supportsNativeToolCalling: true,
      extraParameters: null,
    });
  }));

  it('setActiveProvider forwards the provider id', fakeAsync(() => {
    api.setActiveProvider.and.resolveTo();
    const store = TestBed.inject(SettingsStore);
    void store.refresh();
    tick();

    void store.setActiveProvider('Anthropic');
    tick();

    expect(api.setActiveProvider).toHaveBeenCalledOnceWith('Anthropic');
  }));
});
