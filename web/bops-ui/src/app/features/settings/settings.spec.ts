// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SettingsProviderView, SettingsView } from '../../core/api/models';
import { AuthService, CurrentIdentity } from '../../core/auth/auth.service';
import { SettingsStore } from '../../state/settings.store';
import { Settings } from './settings';

function provider(overrides: Partial<SettingsProviderView> = {}): SettingsProviderView {
  return {
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
    ...overrides,
  };
}

function settingsView(overrides: Partial<SettingsView> = {}): SettingsView {
  return {
    vaultVersion: 0,
    activeProviderId: null,
    activeProviderSource: 'Default',
    providers: [provider()],
    ...overrides,
  };
}

describe('Settings', () => {
  let fixture: ComponentFixture<Settings>;
  let store: {
    view: ReturnType<typeof signal<SettingsView | null>>;
    loading: ReturnType<typeof signal<boolean>>;
    saving: ReturnType<typeof signal<boolean>>;
    error: ReturnType<typeof signal<string | null>>;
    conflict: ReturnType<typeof signal<boolean>>;
    refresh: jasmine.Spy;
    setProviderKey: jasmine.Spy;
    clearProviderKey: jasmine.Spy;
    setProviderProfile: jasmine.Spy;
    setActiveProvider: jasmine.Spy;
  };
  let identity: ReturnType<typeof signal<CurrentIdentity | null>>;

  function configure(): void {
    TestBed.configureTestingModule({
      imports: [Settings],
      providers: [
        { provide: SettingsStore, useValue: store },
        { provide: AuthService, useValue: { identity } },
      ],
    });
    fixture = TestBed.createComponent(Settings);
    fixture.detectChanges();
  }

  beforeEach(() => {
    store = {
      view: signal<SettingsView | null>(settingsView()),
      loading: signal(false),
      saving: signal(false),
      error: signal<string | null>(null),
      conflict: signal(false),
      refresh: jasmine.createSpy('refresh').and.resolveTo(),
      setProviderKey: jasmine.createSpy('setProviderKey').and.resolveTo(true),
      clearProviderKey: jasmine.createSpy('clearProviderKey').and.resolveTo(true),
      setProviderProfile: jasmine.createSpy('setProviderProfile').and.resolveTo(true),
      setActiveProvider: jasmine.createSpy('setActiveProvider').and.resolveTo(true),
    };
    identity = signal<CurrentIdentity | null>({ id: 'admin-1', displayName: 'Admin', roles: ['administrator'] });
  });

  it('shows an access-denied message and never refreshes for a non-administrator', () => {
    identity.set({ id: 'op-1', displayName: 'Operator', roles: ['viewer', 'operator'] });
    configure();

    expect(fixture.nativeElement.textContent).toContain('administrator role is required');
    expect(store.refresh).not.toHaveBeenCalled();
  });

  it('refreshes on init for an administrator', () => {
    configure();

    expect(store.refresh).toHaveBeenCalled();
  });

  it('renders the active provider and its source', () => {
    store.view.set(settingsView({ activeProviderId: 'Anthropic', activeProviderSource: 'Settings' }));
    configure();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Anthropic');
    expect(text).toContain('Selected here.');
  });

  it('shows the key mask, never a raw key, when one is stored', () => {
    store.view.set(settingsView({ providers: [provider({ hasStoredKey: true, keyMaskPrefix: 'sk-ant', keyMaskSuffix: 'wxyz' })] }));
    configure();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('sk-ant');
    expect(text).toContain('wxyz');
    expect(text).not.toContain('sk-antwxyz');
  });

  it('expands a provider to show its editable key and profile form', () => {
    configure();

    const configureButton = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>)
      .find((button) => button.textContent?.includes('Configure'));
    configureButton?.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('input[type="password"]')).not.toBeNull();
  });

  it('sets a new key and clears the draft afterward', async () => {
    configure();
    const component = fixture.componentInstance;
    component['toggleExpanded']('Anthropic');
    component['setApiKeyDraft']('Anthropic', 'sk-new-key-0123456789');

    await component['saveKey']('Anthropic');

    expect(store.setProviderKey).toHaveBeenCalledOnceWith('Anthropic', 'sk-new-key-0123456789');
    expect(component['apiKeyDraft']('Anthropic')).toBe('');
  });

  it('does not submit an empty key', async () => {
    configure();
    const component = fixture.componentInstance;

    await component['saveKey']('Anthropic');

    expect(store.setProviderKey).not.toHaveBeenCalled();
  });

  it('asks for confirmation before clearing a stored key', async () => {
    spyOn(window, 'confirm').and.returnValue(false);
    configure();

    await fixture.componentInstance['clearKey']('Anthropic');

    expect(window.confirm).toHaveBeenCalled();
    expect(store.clearProviderKey).not.toHaveBeenCalled();
  });

  it('clears the key once confirmed', async () => {
    spyOn(window, 'confirm').and.returnValue(true);
    configure();

    await fixture.componentInstance['clearKey']('Anthropic');

    expect(store.clearProviderKey).toHaveBeenCalledOnceWith('Anthropic');
  });

  it('saves the endpoint and model profile', async () => {
    configure();
    const component = fixture.componentInstance;
    component['toggleExpanded']('Anthropic');
    component['profileDrafts'].set('Anthropic', {
      baseUrl: 'https://api.anthropic.com',
      model: 'claude-sonnet-4-5',
      supportsNativeToolCalling: true,
    });

    await component['saveProfile']('Anthropic');

    expect(store.setProviderProfile).toHaveBeenCalledOnceWith('Anthropic', {
      baseUrl: 'https://api.anthropic.com',
      model: 'claude-sonnet-4-5',
      supportsNativeToolCalling: true,
      extraParameters: null,
    });
  });

  it('makes a provider active', async () => {
    configure();

    await fixture.componentInstance['makeActive']('Anthropic');

    expect(store.setActiveProvider).toHaveBeenCalledOnceWith('Anthropic');
  });

  it('shows a restart-required notice', () => {
    configure();

    expect(fixture.nativeElement.textContent).toContain('next restart');
  });
});
