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
    lifecycleState: 'InstalledDisabled',
    lifecycleETag: '"plv-1"',
    lifecycleFailure: null,
    recoveryAvailable: false,
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
    isAdministrator: ReturnType<typeof signal<boolean>>;
    mutating: ReturnType<typeof signal<boolean>>;
    notice: ReturnType<typeof signal<{ kind: 'success' | 'error' | 'stale'; text: string } | null>>;
    enable: jasmine.Spy;
    disable: jasmine.Spy;
    recover: jasmine.Spy;
    installArchive: jasmine.Spy;
    replaceArchive: jasmine.Spy;
    clearNotice: jasmine.Spy;
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
      isAdministrator: signal(true),
      mutating: signal(false),
      notice: signal<{ kind: 'success' | 'error' | 'stale'; text: string } | null>(null),
      enable: jasmine.createSpy('enable').and.resolveTo(true),
      disable: jasmine.createSpy('disable').and.resolveTo(true),
      recover: jasmine.createSpy('recover').and.resolveTo(true),
      installArchive: jasmine.createSpy('installArchive').and.resolveTo(true),
      replaceArchive: jasmine.createSpy('replaceArchive').and.resolveTo(true),
      clearNotice: jasmine.createSpy('clearNotice'),
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

  function buttons(): HTMLButtonElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>);
  }

  function button(label: string): HTMLButtonElement | undefined {
    return buttons().find((b) => b.textContent?.trim().startsWith(label));
  }

  function select(): void {
    store.selectedPluginId.set('acme.sample-plugin');
    fixture.detectChanges();
  }

  it('offers no lifecycle mutation path to a non-administrator', () => {
    store.isAdministrator.set(false);
    store.entries.set([plugin({ recoveryAvailable: true })]);
    select();

    const labels = buttons().map((b) => b.textContent?.toLowerCase() ?? '');
    for (const word of ['enable', 'disable', 'recover', 'install', 'replace']) {
      expect(labels.some((l) => l.includes(word))).withContext(word).toBeFalse();
    }
    expect(fixture.nativeElement.querySelector('input[type="file"]')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('administrator role is required');
  });

  it('shows an administrator the lifecycle controls', () => {
    store.entries.set([plugin({ enabled: false })]);
    select();

    expect(button('Enable')).toBeDefined();
    expect(button('Install archive')).toBeDefined();
    expect(button('Replace archive')).toBeDefined();
  });

  it('never offers a delete or remove action', () => {
    store.entries.set([plugin({ recoveryAvailable: true, lifecycleState: 'RecoveryRequired' })]);
    select();

    const labels = buttons().map((b) => b.textContent?.toLowerCase() ?? '');
    expect(labels.some((l) => /delete|remove|uninstall/.test(l))).toBeFalse();
  });

  it('renders the lifecycle state, trust, compatibility and risk', () => {
    store.entries.set([plugin({ lifecycleState: 'ActivationFailed', declaredMaxRisk: 1, effectiveMaxRisk: 2 })]);
    select();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Activation failed');
    expect(text).toContain('Verified trust');
    expect(text).toContain('Compatible');
    expect(text).toContain('Effective max risk');
  });

  it('shows the lifecycle failure from lifecycleFailure, not from loadError, and keeps them distinct', () => {
    store.entries.set([plugin({ lifecycleFailure: 'Activation threw.', loadError: null })]);
    select();
    expect(fixture.nativeElement.textContent).toContain('Lifecycle failure');
    expect(fixture.nativeElement.textContent).toContain('Activation threw.');

    store.entries.set([plugin({ lifecycleFailure: null, loadError: 'Runtime diagnostic.' })]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('Lifecycle failure');
    expect(fixture.nativeElement.textContent).toContain('Runtime diagnostic.');
  });

  it('renders backend text as text, never as HTML', () => {
    store.entries.set([plugin({ lifecycleFailure: '<img src=x onerror=alert(1)>' })]);
    select();

    expect(fixture.nativeElement.querySelector('img')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('<img src=x');
  });

  it('offers Recover only when the backend says recoveryAvailable, regardless of state', () => {
    store.entries.set([plugin({ lifecycleState: 'RecoveryRequired', recoveryAvailable: false })]);
    select();
    expect(button('Recover')).toBeUndefined();

    store.entries.set([plugin({ lifecycleState: 'InstalledDisabled', recoveryAvailable: true })]);
    fixture.detectChanges();
    expect(button('Recover')).toBeDefined();
  });

  it('enable needs an explicit confirmation showing the in-process warning and the exact version', async () => {
    store.entries.set([plugin({ version: '2.5.1', enabled: false })]);
    select();

    button('Enable')!.click();
    fixture.detectChanges();

    expect(store.enable).not.toHaveBeenCalled();
    const dialog = fixture.nativeElement.querySelector('[role="alertdialog"]') as HTMLElement;
    expect(dialog.textContent).toContain('acme.sample-plugin');
    expect(dialog.textContent).toContain('v2.5.1');
    expect(dialog.textContent).toContain('in-process');
    expect(dialog.textContent).toContain('not sandboxed');

    (Array.from(dialog.querySelectorAll('button')).find((b) => b.textContent?.includes('Enable v2.5.1')) as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(store.enable).toHaveBeenCalledOnceWith(jasmine.objectContaining({ id: 'acme.sample-plugin', lifecycleETag: '"plv-1"' }), '2.5.1');
  });

  it('cancelling the enable confirmation issues no request', () => {
    store.entries.set([plugin({ enabled: false })]);
    select();
    button('Enable')!.click();
    fixture.detectChanges();

    (Array.from(fixture.nativeElement.querySelectorAll('[role="alertdialog"] button') as NodeListOf<HTMLButtonElement>).find((b) =>
      b.textContent?.includes('Cancel'),
    ) as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(store.enable).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('[role="alertdialog"]')).toBeNull();
  });

  it('disables the confirm button while a mutation is in flight', () => {
    store.entries.set([plugin({ enabled: false })]);
    select();
    button('Enable')!.click();
    store.mutating.set(true);
    fixture.detectChanges();

    const confirmButton = fixture.nativeElement.querySelector('[role="alertdialog"] button:last-of-type') as HTMLButtonElement;
    expect(confirmButton.disabled).toBeTrue();
  });

  it('disable acts on the displayed entry', () => {
    store.entries.set([plugin({ enabled: true, lifecycleState: 'Enabled' })]);
    select();

    button('Disable')!.click();

    expect(store.disable).toHaveBeenCalledOnceWith(jasmine.objectContaining({ id: 'acme.sample-plugin', lifecycleETag: '"plv-1"' }));
  });

  it('recover needs an explicit confirmation, states it does not enable, and never calls enable', async () => {
    store.entries.set([plugin({ lifecycleState: 'RecoveryRequired', recoveryAvailable: true })]);
    select();

    button('Recover')!.click();
    fixture.detectChanges();
    expect(store.recover).not.toHaveBeenCalled();
    const dialog = fixture.nativeElement.querySelector('[role="alertdialog"]') as HTMLElement;
    expect(dialog.textContent).toContain('does not enable');

    (Array.from(dialog.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Recover') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(store.recover).toHaveBeenCalledTimes(1);
    expect(store.enable).not.toHaveBeenCalled();
  });

  it('an unknown lifecycle state renders generically and offers no enable, disable or replace', () => {
    store.entries.set([plugin({ lifecycleState: 'QuantumSuperposition' })]);
    select();

    expect(fixture.nativeElement.textContent).toContain('QuantumSuperposition');
    expect(button('Enable')).toBeUndefined();
    expect(button('Disable')).toBeUndefined();
    expect(button('Replace archive')).toBeUndefined();
  });

  it('a plugin without a server ETag offers no mutation', () => {
    store.entries.set([plugin({ lifecycleETag: null })]);
    select();

    expect(button('Enable')).toBeUndefined();
    expect(button('Replace archive')).toBeUndefined();
  });

  it('shows the mutation notice', () => {
    store.notice.set({ kind: 'stale', text: 'This plugin changed since you loaded it.' });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="alert"]')?.textContent).toContain('changed since you loaded it');
  });

  it('advisory validation rejects a non-zip file without any request', () => {
    const input = fixture.nativeElement.querySelector('input[type="file"]') as HTMLInputElement;
    const transfer = new DataTransfer();
    transfer.items.add(new File(['x'], 'notes.txt'));
    input.files = transfer.files;
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Select a .zip archive.');
    expect(button('Install archive')!.disabled).toBeTrue();
    expect(store.installArchive).not.toHaveBeenCalled();
  });

  it('installs the selected archive through the store', async () => {
    const input = fixture.nativeElement.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File(['PK'], 'anything.zip', { type: 'application/zip' });
    const transfer = new DataTransfer();
    transfer.items.add(file);
    input.files = transfer.files;
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    button('Install archive')!.click();
    await fixture.whenStable();

    expect(store.installArchive).toHaveBeenCalledOnceWith(file);
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
