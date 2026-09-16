// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { AuthService } from '../core/auth/auth.service';
import { ProvidersStore } from './providers.store';

describe('ProvidersStore', () => {
  let api: jasmine.SpyObj<BOpsApiClient>;

  beforeEach(() => {
    api = jasmine.createSpyObj<BOpsApiClient>('BOpsApiClient', ['getProviders']);
    api.getProviders.and.resolveTo({ registeredProviderIds: [], active: null });

    TestBed.configureTestingModule({
      providers: [
        ProvidersStore,
        { provide: BOpsApiClient, useValue: api },
        { provide: AuthService, useValue: { authenticated: signal(true) } },
      ],
    });
  });

  it('loads provider configuration once without polling', fakeAsync(() => {
    api.getProviders.and.resolveTo({
      registeredProviderIds: ['OpenAI', 'Ollama'],
      active: {
        provider: 'OpenAI',
        model: 'gpt-test',
        baseUrl: 'https://example.test/v1',
        hasApiKey: true,
      },
    });

    const store = TestBed.inject(ProvidersStore);
    expect(store.loading()).toBeTrue();
    tick();

    expect(store.registeredProviderIds()).toEqual(['OpenAI', 'Ollama']);
    expect(store.active()?.provider).toBe('OpenAI');
    expect(store.loading()).toBeFalse();

    tick(10_000);
    expect(api.getProviders).toHaveBeenCalledTimes(1);
  }));

  it('exposes an API error and finishes loading', fakeAsync(() => {
    api.getProviders.and.rejectWith(new Error('Provider API unavailable'));

    const store = TestBed.inject(ProvidersStore);
    tick();

    expect(store.error()).toBe('Provider API unavailable');
    expect(store.loading()).toBeFalse();
  }));
});
