// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActiveProviderInfo } from '../../core/api/models';
import { ProvidersStore } from '../../state/providers.store';
import { Settings } from './settings';

describe('Settings', () => {
  let fixture: ComponentFixture<Settings>;
  let registeredProviderIds: ReturnType<typeof signal<string[]>>;
  let active: ReturnType<typeof signal<ActiveProviderInfo | null>>;
  let loading: ReturnType<typeof signal<boolean>>;
  let error: ReturnType<typeof signal<string | null>>;

  beforeEach(async () => {
    registeredProviderIds = signal(['OpenAI', 'Ollama']);
    active = signal<ActiveProviderInfo | null>({
      provider: 'OpenAI',
      model: 'gpt-test',
      baseUrl: 'https://example.test/v1',
      hasApiKey: false,
    });
    loading = signal(false);
    error = signal<string | null>(null);

    await TestBed.configureTestingModule({
      imports: [Settings],
      providers: [{
        provide: ProvidersStore,
        useValue: { registeredProviderIds, active, loading, error },
      }],
    }).compileComponents();

    fixture = TestBed.createComponent(Settings);
    fixture.detectChanges();
  });

  it('renders active-provider details, missing-key state and the active marker', () => {
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('OpenAI');
    expect(text).toContain('gpt-test');
    expect(text).toContain('https://example.test/v1');
    expect(text).toContain('Missing');
    expect(text).toContain('· active');
    expect(text).toContain('Ollama');
  });

  it('renders loading, error and unconfigured states', () => {
    active.set(null);
    registeredProviderIds.set([]);
    loading.set(true);
    error.set('Provider API unavailable');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Provider API unavailable');
    expect(fixture.nativeElement.textContent).toContain('Loading…');

    loading.set(false);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('this host cannot run a task yet');
  });
});
