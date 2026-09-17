// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PluginCatalogEntry } from '../../core/api/models';
import { PluginsStore } from '../../state/plugins.store';
import { Plugins } from './plugins';

function plugin(overrides: Partial<PluginCatalogEntry> = {}): PluginCatalogEntry {
  return {
    id: 'acme.sample-plugin',
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
    declaredCapabilities: ['sample.read'],
    dependencies: [{ name: 'acme-dep', version: '2.0.0' }],
    declaredMaxRisk: 0,
    effectiveMaxRisk: 0,
    loadError: null,
    ...overrides,
  };
}

describe('Plugins', () => {
  let fixture: ComponentFixture<Plugins>;
  let store: {
    entries: ReturnType<typeof signal<PluginCatalogEntry[]>>;
    totalCount: ReturnType<typeof signal<number>>;
    selectedPluginId: ReturnType<typeof signal<string | null>>;
    loading: ReturnType<typeof signal<boolean>>;
    error: ReturnType<typeof signal<string | null>>;
    refresh: jasmine.Spy;
    setEnabledFilter: jasmine.Spy;
    setTrustFilter: jasmine.Spy;
    selectPlugin: jasmine.Spy;
    clearSelection: jasmine.Spy;
  };

  beforeEach(async () => {
    store = {
      entries: signal([plugin()]),
      totalCount: signal(1),
      selectedPluginId: signal<string | null>(null),
      loading: signal(false),
      error: signal<string | null>(null),
      refresh: jasmine.createSpy('refresh').and.resolveTo(),
      setEnabledFilter: jasmine.createSpy('setEnabledFilter'),
      setTrustFilter: jasmine.createSpy('setTrustFilter'),
      selectPlugin: jasmine.createSpy('selectPlugin'),
      clearSelection: jasmine.createSpy('clearSelection'),
    };

    await TestBed.configureTestingModule({
      imports: [Plugins],
      providers: [{ provide: PluginsStore, useValue: store }],
    }).compileComponents();

    fixture = TestBed.createComponent(Plugins);
    fixture.detectChanges();
  });

  it('refreshes the catalog on init', () => {
    expect(store.refresh).toHaveBeenCalled();
  });

  it('never renders an enable, disable or upload button', () => {
    store.selectedPluginId.set('acme.sample-plugin');
    fixture.detectChanges();

    const buttonLabels = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).map((button) => button.textContent?.toLowerCase() ?? '');
    expect(buttonLabels.some((label) => label.includes('enable'))).toBeFalse();
    expect(buttonLabels.some((label) => label.includes('disable'))).toBeFalse();
    expect(buttonLabels.some((label) => label.includes('upload'))).toBeFalse();
  });

  it('selects a plugin from the list', () => {
    const button = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((btn) => btn.textContent?.includes('acme.sample-plugin'));

    button?.click();

    expect(store.selectPlugin).toHaveBeenCalledOnceWith('acme.sample-plugin');
  });

  it('shows installed, enabled, loaded and compatible as distinct badges, never merged', () => {
    store.selectedPluginId.set('acme.sample-plugin');
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Enabled');
    expect(text).toContain('Loaded');
    expect(text).toContain('Compatible');
  });

  it('shows a load-error banner only when one is present', () => {
    store.entries.set([plugin({ loadError: 'Manifest is no longer valid.' })]);
    store.selectedPluginId.set('acme.sample-plugin');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Manifest is no longer valid.');
  });

  it('shows the empty state when no plugin is selected', () => {
    expect(fixture.nativeElement.textContent).toContain('Select a plugin on the left');
  });

  it('forwards the empty-string filter as null, clearing it', () => {
    const component = fixture.componentInstance;

    component['onEnabledFilterChange']('');
    component['onTrustFilterChange']('');

    expect(store.setEnabledFilter).toHaveBeenCalledOnceWith(null);
    expect(store.setTrustFilter).toHaveBeenCalledOnceWith(null);
  });

  it('parses filter changes into their typed values', () => {
    const component = fixture.componentInstance;

    component['onEnabledFilterChange']('true');
    component['onTrustFilterChange']('3');

    expect(store.setEnabledFilter).toHaveBeenCalledOnceWith(true);
    expect(store.setTrustFilter).toHaveBeenCalledOnceWith(3);
  });
});
