// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EffectiveFallback, SettingsView } from '../../core/api/models';
import { AuthService, CurrentIdentity } from '../../core/auth/auth.service';
import { SettingsStore } from '../../state/settings.store';
import { I18n } from '../../core/i18n/i18n';
import { Settings } from './settings';
import { settingsProviderFixture, settingsViewFixture } from './settings.testing';

const provider = settingsProviderFixture;
const settingsView = settingsViewFixture;

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
    setFallbacks: jasmine.Spy;
    clearFallbacks: jasmine.Spy;
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

  // The language is a per-viewer choice kept in localStorage; another spec may have left Italian there.
  const clearLanguage = (): void => localStorage.removeItem('bops-ui-language');
  beforeEach(clearLanguage);
  afterEach(clearLanguage);

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
      setFallbacks: jasmine.createSpy('setFallbacks').and.resolveTo(true),
      clearFallbacks: jasmine.createSpy('clearFallbacks').and.resolveTo(true),
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

  it('does not claim a restart is needed: changes apply to new tasks immediately', () => {
    configure();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain('next restart');
    expect(text).toContain('without a restart');
  });

  describe('router disclosure (ADR-0045 section 10)', () => {
    const routerView = (overrides: Partial<SettingsView> = {}) =>
      settingsView({ activeProviderId: 'OpenRouter', effectiveModel: 'openrouter/free', ...overrides });
    const notice = (): HTMLElement | null => fixture.nativeElement.querySelector('[data-testid="router-notice"]');

    it('warns when the effective primary is OpenRouter / openrouter/free', () => {
      store.view.set(routerView());
      configure();

      expect(notice()).not.toBeNull();
      expect(notice()?.textContent).toContain(
        'The actual model may change on every call; not recommended for troubleshooting sessions.',
      );
      expect(notice()?.textContent).toContain('select a specific model');
    });

    it('does not present a shadowed stored OpenRouter/openrouter-free profile as the effective router', () => {
      store.view.set(
        settingsView({
          activeProviderId: 'Anthropic',
          activeProviderSource: 'EnvironmentOverride',
          effectiveModel: 'claude-sonnet-4-5',
          persistedActiveProviderId: 'OpenRouter',
          persistedActiveProviderShadowed: true,
          providers: [
            provider({ providerId: 'Anthropic', isActive: true }),
            provider({ providerId: 'OpenRouter', model: 'openrouter/free', isPersistedSelectionShadowed: true }),
          ],
        }),
      );
      configure();

      expect(notice()).toBeNull();
      expect(fixture.nativeElement.querySelector('[data-testid="selection-shadowed"]')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('[data-testid="selection-shadowed-badge"]')).not.toBeNull();
    });

    it('does not warn for OpenRouter with a specific model', () => {
      store.view.set(routerView({ effectiveModel: 'anthropic/claude-sonnet-4.5' }));
      configure();

      expect(notice()).toBeNull();
    });

    it('shows the Italian warning after switching language', () => {
      store.view.set(routerView());
      configure();
      TestBed.inject(I18n).setLanguage('it');
      fixture.detectChanges();

      expect(notice()?.textContent).toContain(
        'Il modello effettivo può cambiare a ogni chiamata; non è consigliato per sessioni di troubleshooting.',
      );
      expect(notice()?.textContent).toContain('modello specifico');
    });

    it('keeps the fallback chain and the router as separate concepts', () => {
      store.view.set(routerView());
      configure();

      const chain = fixture.nativeElement.querySelector('[data-testid="fallback-chain"]') as HTMLElement;
      expect(chain.textContent).toContain('not the provider-side routing of a router model');
      expect(notice()?.textContent).not.toContain('Fallback chain');
    });
  });

  describe('fallback chain (ADR-0045)', () => {
    const entries = (list: [string, string][]) => list.map(([p, m]) => ({ provider: p, model: m }));
    const effective = (list: [string, string, boolean][]): EffectiveFallback[] =>
      list.map(([p, m, ok], i) => ({
        ordinal: i + 1, provider: p, model: m, baseUrl: `https://${p.toLowerCase()}.example`, supportsNativeToolCalling: true, credentialAvailable: ok,
      }));
    const el = (): HTMLElement => fixture.nativeElement;
    const rows = (testId: string): HTMLElement[] => Array.from(el().querySelectorAll(`[data-testid="${testId}"]`));
    const button = (testId: string): HTMLButtonElement => el().querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement;
    const rowModels = (): string[] => rows('persisted-fallback').map((row) => (row.querySelector('input') as HTMLInputElement).value);
    const rowProviders = (): string[] => rows('persisted-fallback').map((row) => (row.querySelector('select') as HTMLSelectElement).value);
    const press = (row: number, label: RegExp): void => {
      const target = Array.from(rows('persisted-fallback')[row].querySelectorAll('button')).find((b) => label.test(b.getAttribute('aria-label') ?? ''));
      target!.click();
      fixture.detectChanges();
    };
    const providers = [provider({ providerId: 'Anthropic' }), provider({ providerId: 'OpenAI' }), provider({ providerId: 'OpenRouter' })];
    const loaded = (overrides: Partial<SettingsView> = {}) =>
      store.view.set(settingsView({ providers, settingsRevision: 7, ...overrides }));

    it('renders the stored entries in their stored order', () => {
      loaded({ persistedFallbacks: entries([['OpenAI', 'gpt-4.1'], ['Anthropic', 'claude-sonnet-4-5']]) });
      configure();

      expect(rowModels()).toEqual(['gpt-4.1', 'claude-sonnet-4-5']);
      expect(rowProviders()).toEqual(['OpenAI', 'Anthropic']);
    });

    it('renders the effective chain in order with its source and credential readiness', () => {
      loaded({
        persistedFallbacks: entries([['OpenAI', 'gpt-4.1']]),
        effectiveFallbackSource: 'settings',
        effectiveFallbacks: effective([['OpenAI', 'gpt-4.1', true], ['Anthropic', 'claude-sonnet-4-5', false]]),
      });
      configure();

      const shown = rows('effective-fallback').map((row) => row.textContent!.replace(/\s+/g, ' ').trim());
      expect(shown.length).toBe(2);
      expect(shown[0]).toMatch(/^1\.\s*OpenAI\s*gpt-4\.1\s*Credential available$/);
      expect(shown[1]).toMatch(/^2\.\s*Anthropic\s*claude-sonnet-4-5\s*No usable credential found$/);
      expect(el().querySelector('[data-testid="effective-fallback-source"]')?.textContent).toContain('Taken from the list stored in Settings');
      expect(el().querySelector('[data-testid="fallback-shadowed"]')).toBeNull();
    });

    it('states plainly when host configuration owns the chain and the stored list is not in use', () => {
      loaded({
        persistedFallbacks: entries([['OpenAI', 'gpt-4.1']]),
        persistedFallbacksShadowed: true,
        effectiveFallbackSource: 'configuration',
        effectiveFallbacks: effective([['Anthropic', 'claude-sonnet-4-5', true]]),
      });
      configure();

      expect(el().querySelector('[data-testid="effective-fallback-source"]')?.textContent).toContain('Owned by host configuration (ModelProvider:Fallbacks)');
      const shadowed = el().querySelector('[data-testid="fallback-shadowed"]');
      expect(shadowed?.textContent).toContain('Not in use');
      expect(shadowed?.textContent).toContain('does not change which fallbacks new tasks use');
      const text = el().textContent!;
      expect(text).not.toMatch(/stored list[^.]*(is active|currently used)/i);
      expect(rows('effective-fallback').length).toBe(1);
      expect(rowModels()).toEqual(['gpt-4.1']);
    });

    it('shows an empty effective chain without calling anything active', () => {
      configure();

      expect(el().querySelector('[data-testid="effective-fallback-empty"]')).not.toBeNull();
      expect(el().querySelector('[data-testid="fallback-shadowed"]')).toBeNull();
    });

    it('adds, reorders, edits and removes candidates, then saves the complete ordered list', async () => {
      loaded({ persistedFallbacks: entries([['OpenAI', 'gpt-4.1']]) });
      configure();

      button('fallback-add').click();
      fixture.detectChanges();
      const component = fixture.componentInstance;
      component['updateFallback'](1, 'provider', 'Anthropic');
      component['updateFallback'](1, 'model', '  claude-sonnet-4-5  ');
      fixture.detectChanges();
      button('fallback-add').click();
      fixture.detectChanges();
      component['updateFallback'](2, 'provider', 'OpenRouter');
      component['updateFallback'](2, 'model', 'meta/llama');
      fixture.detectChanges();
      expect(rowModels()).toEqual(['gpt-4.1', '  claude-sonnet-4-5  ', 'meta/llama']);

      press(2, /earlier/); // OpenRouter moves before Anthropic
      press(0, /later/); // OpenAI moves after OpenRouter
      press(1, /Remove/i); // OpenAI (now second) is removed
      expect(rowProviders().length).toBe(2);

      await component['saveFallbacks']();

      expect(store.setFallbacks).toHaveBeenCalledTimes(1);
      expect(store.clearFallbacks).not.toHaveBeenCalled();
      const sent = store.setFallbacks.calls.mostRecent().args[0] as { provider: string; model: string }[];
      expect(sent.map((e) => e.provider)).toEqual(['OpenRouter', 'Anthropic']);
      expect(sent.map((e) => e.model)).toEqual(['meta/llama', 'claude-sonnet-4-5']);
    });

    it('enforces the three-candidate maximum in the editor', () => {
      loaded({ persistedFallbacks: entries([['OpenAI', 'a'], ['Anthropic', 'b']]) });
      configure();

      expect(button('fallback-add').disabled).toBeFalse();
      button('fallback-add').click();
      fixture.detectChanges();

      expect(rows('persisted-fallback').length).toBe(3);
      expect(button('fallback-add').disabled).toBeTrue();
      expect(el().querySelector('[data-testid="fallback-limit-reached"]')).not.toBeNull();

      fixture.componentInstance['addFallback']();
      fixture.detectChanges();
      expect(rows('persisted-fallback').length).toBe(3);
    });

    it('cannot save a candidate with a blank model', () => {
      loaded();
      configure();

      button('fallback-add').click();
      fixture.detectChanges();

      expect(button('fallback-save').disabled).toBeTrue();
    });

    it('clears the stored chain through the clear operation, not an empty replace', async () => {
      loaded({ persistedFallbacks: entries([['OpenAI', 'gpt-4.1']]) });
      configure();

      button('fallback-clear').click();
      await fixture.whenStable();

      expect(store.clearFallbacks).toHaveBeenCalledTimes(1);
      expect(store.setFallbacks).not.toHaveBeenCalled();
    });

    it('treats saving an emptied editor as the clear operation', async () => {
      loaded({ persistedFallbacks: entries([['OpenAI', 'gpt-4.1']]) });
      configure();

      press(0, /Remove/i);
      await fixture.componentInstance['saveFallbacks']();

      expect(store.clearFallbacks).toHaveBeenCalledTimes(1);
      expect(store.setFallbacks).not.toHaveBeenCalled();
    });

    it('keeps the unsaved edit when the save is rejected, and drops it once saved', async () => {
      loaded({ persistedFallbacks: entries([['OpenAI', 'gpt-4.1']]) });
      store.setFallbacks.and.resolveTo(false);
      configure();
      const component = fixture.componentInstance;

      component['updateFallback'](0, 'model', 'gpt-4.1-mini');
      await component['saveFallbacks']();
      fixture.detectChanges();
      expect(rowModels()).toEqual(['gpt-4.1-mini']);

      store.setFallbacks.and.resolveTo(true);
      await component['saveFallbacks']();
      loaded({ persistedFallbacks: entries([['OpenAI', 'gpt-4.1-mini']]) });
      fixture.detectChanges();
      expect(rowModels()).toEqual(['gpt-4.1-mini']);
      expect(button('fallback-save').disabled).toBeTrue();
    });

    it('represents a missing fallback credential safely, without any secret material', () => {
      loaded({
        persistedFallbacks: entries([['Anthropic', 'claude-sonnet-4-5']]),
        effectiveFallbackSource: 'settings',
        effectiveFallbacks: effective([['Anthropic', 'claude-sonnet-4-5', false]]),
      });
      configure();

      const text = el().querySelector('[data-testid="fallback-chain"]')!.textContent!;
      expect(text).toContain('No usable credential found');
      expect(text).toContain('advisory only');
      expect(el().querySelector('[data-testid="fallback-chain"] input[type="password"]')).toBeNull();
      expect(text).not.toMatch(/sk-|apiKey|secret|hash|vault/i);
    });

    it('keeps its controls labelled for assistive technology and free of fixed widths that overflow narrow layouts', () => {
      loaded({ persistedFallbacks: entries([['OpenAI', 'gpt-4.1'], ['Anthropic', 'claude-sonnet-4-5']]) });
      configure();

      const controls = Array.from(
        el().querySelectorAll('[data-testid="persisted-fallback"] button, [data-testid="persisted-fallback"] select, [data-testid="persisted-fallback"] input'),
      );
      expect(controls.length).toBe(10);
      expect(controls.every((c) => (c.getAttribute('aria-label') ?? '').length > 0)).toBeTrue();
      expect(rows('persisted-fallback').every((row) => row.className.includes('flex-col'))).toBeTrue();
    });
  });
});
