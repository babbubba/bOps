// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from '../../app';
import { Approvals } from '../../features/approvals/approvals';
import { Dashboard } from '../../features/dashboard/dashboard';
import { Delegations } from '../../features/delegations/delegations';
import { Login } from '../../features/login/login';
import { Plugins } from '../../features/plugins/plugins';
import { Settings } from '../../features/settings/settings';
import { ApprovalsStore } from '../../state/approvals.store';
import { DelegationsStore } from '../../state/delegations.store';
import { PluginsStore } from '../../state/plugins.store';
import { SettingsStore } from '../../state/settings.store';
import { TasksStore } from '../../state/tasks.store';
import { BOpsApiClient } from '../api/bops-api-client';
import { AuthService } from '../auth/auth.service';
import { I18n } from './i18n';

/**
 * The language is switched while a screen is on show: it must re-render in Italian and back without a reload, and what the API
 * returned (a goal, a tool name, a plugin id, a finding) must not change with it.
 */
describe('switching language', () => {
  const identity = (roles: string[]) => signal({ id: 'alice', displayName: 'Alice', roles });
  const text = (fixture: ComponentFixture<unknown>): string => (fixture.nativeElement as HTMLElement).textContent ?? '';

  function switchTo(fixture: ComponentFixture<unknown>, language: 'en' | 'it'): void {
    TestBed.inject(I18n).setLanguage(language);
    fixture.detectChanges();
  }

  it('re-renders the dashboard in Italian and back, leaving the task text as it is', () => {
    TestBed.configureTestingModule({
      imports: [Dashboard],
      providers: [
        {
          provide: TasksStore,
          useValue: {
            statusFilter: signal(0),
            tasks: signal([
              { id: 't1', node: 'n', goal: 'Inspect the production host', status: 0, steps: [{ index: 0 }, { index: 1 }], plans: [], createdAtUtc: '2026-09-15T12:00:00Z' },
            ]),
            selectedTaskId: signal(null),
            selectedTask: signal(null),
            loading: signal(false),
            starting: signal(false),
            error: signal(null),
            refresh: () => Promise.resolve(),
            setStatusFilter: () => Promise.resolve(),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(Dashboard);
    fixture.detectChanges();
    expect(text(fixture)).toContain('Running tasks');
    expect(text(fixture)).toContain('2 steps');

    switchTo(fixture, 'it');
    expect(text(fixture)).toContain('Task in esecuzione');
    expect(text(fixture)).toContain('2 passi');
    expect(text(fixture)).toContain(`avviato alle ${new Date('2026-09-15T12:00:00Z').toLocaleTimeString('it')}`);
    expect(text(fixture)).not.toContain('Running tasks');
    expect(text(fixture)).toContain('Inspect the production host');
    expect((fixture.nativeElement as HTMLElement).querySelector('#goal')?.getAttribute('placeholder')).toBe('es. come sta andando questa macchina?');
    expect(text(fixture)).toContain('In esecuzione');

    switchTo(fixture, 'en');
    expect(text(fixture)).toContain('Running tasks');
    expect(text(fixture)).not.toContain('Task in esecuzione');
  });

  it('re-renders the approvals screen and its risk badge', () => {
    TestBed.configureTestingModule({
      imports: [Approvals],
      providers: [
        {
          provide: ApprovalsStore,
          useValue: {
            pending: signal([{ id: 'a1', taskId: 't', tool: 'docker.stop', reason: 'Stopping needs approval.', requestedAtUtc: '2026-09-15T12:00:00Z', arguments: {}, permanentDeletion: false }]),
            toolRisk: signal({ 'docker.stop': 3 }),
            loading: signal(false),
            error: signal(null),
          },
        },
        { provide: BOpsApiClient, useValue: {} },
      ],
    });
    const fixture = TestBed.createComponent(Approvals);
    fixture.detectChanges();
    expect(text(fixture)).toContain('Approve');
    expect(text(fixture)).toContain('High');

    switchTo(fixture, 'it');
    expect(text(fixture)).toContain('Approva');
    expect(text(fixture)).toContain('Rifiuta');
    expect(text(fixture)).toContain('Alto');
    expect(text(fixture)).toContain('docker.stop');
    expect(text(fixture)).toContain('Stopping needs approval.');
    expect((fixture.nativeElement as HTMLElement).querySelector('input')?.getAttribute('placeholder')).toBe('Nota facoltativa');

    switchTo(fixture, 'en');
    expect(text(fixture)).toContain('Reject');
  });

  it('re-renders the plugins screen, its filters and the plugin details', () => {
    TestBed.configureTestingModule({
      imports: [Plugins],
      providers: [
        {
          provide: PluginsStore,
          useValue: {
            entries: signal([
              {
                id: 'acme.sample', version: '1.0.0', publisher: 'Acme', installedAtUtc: '2026-09-17T12:00:00Z', enabled: true, loaded: false, compatible: true,
                signaturePresent: true, verified: true, trust: 2, keyId: 'k', declaredCapabilities: [], dependencies: [], declaredMaxRisk: 0, effectiveMaxRisk: null, loadError: null,
              },
            ]),
            totalCount: signal(1),
            selectedPluginId: signal('acme.sample'),
            loading: signal(false),
            error: signal(null),
            isAdministrator: signal(false),
            mutating: signal(false),
            notice: signal(null),
            clearNotice: () => undefined,
            refresh: () => Promise.resolve(),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(Plugins);
    fixture.detectChanges();
    expect(text(fixture)).toContain('Installed (1)');
    expect(text(fixture)).toContain('Not loaded');
    expect(text(fixture)).toContain('Verified trust');

    switchTo(fixture, 'it');
    expect(text(fixture)).toContain('Installati (1)');
    expect(text(fixture)).toContain('Non caricato');
    expect(text(fixture)).toContain('Fiducia: Verificato');
    expect(text(fixture)).toContain('Nessuna dichiarata.');
    expect(text(fixture)).toContain('acme.sample');
    expect((fixture.nativeElement as HTMLElement).querySelector('select')?.getAttribute('aria-label')).toBe('Filtra per stato di abilitazione');

    switchTo(fixture, 'en');
    expect(text(fixture)).toContain('Installed (1)');
  });

  it('re-renders the settings screen', () => {
    TestBed.configureTestingModule({
      imports: [Settings],
      providers: [
        {
          provide: SettingsStore,
          useValue: {
            view: signal({
              vaultVersion: 0, activeProviderId: 'Anthropic', activeProviderSource: 'EnvironmentOverride',
              providers: [{ providerId: 'Anthropic', isActive: true, hasStoredKey: false, keyMaskPrefix: null, keyMaskSuffix: null, keyPlaintextLength: null, keyUpdatedUtc: null, baseUrl: null, model: null, supportsNativeToolCalling: true, extraParameters: null, profileUpdatedUtc: null }],
            }),
            loading: signal(false),
            saving: signal(false),
            error: signal(null),
            conflict: signal(false),
            refresh: () => Promise.resolve(),
          },
        },
        { provide: AuthService, useValue: { identity: identity(['administrator']) } },
      ],
    });
    const fixture = TestBed.createComponent(Settings);
    fixture.detectChanges();
    expect(text(fixture)).toContain('Active provider');
    expect(text(fixture)).toContain('Set by the ModelProvider__Provider environment variable');

    switchTo(fixture, 'it');
    expect(text(fixture)).toContain('Provider attivo');
    expect(text(fixture)).toContain('Impostato dalla variabile d’ambiente ModelProvider__Provider');
    expect(text(fixture)).toContain('Non impostata');
    expect(text(fixture)).toContain('Sì');

    switchTo(fixture, 'en');
    expect(text(fixture)).toContain('Not set');
  });

  it('re-renders the delegations screen, its roles and its plurals', () => {
    TestBed.configureTestingModule({
      imports: [Delegations],
      providers: [
        {
          provide: DelegationsStore,
          useValue: {
            runs: signal([
              {
                id: 'r1', status: 'DiagnosisCompleted', objective: 'Why did nginx stop?', actorId: 'alice', actorDisplayName: 'Alice', runningInThisHost: false,
                awaitingPlanApproval: false, planHash: null, approval: null,
                roles: [{ role: 'Discovery', agentId: 'a1', status: 'Completed', steps: 1, tokens: 12, startedAtUtc: null, completedAtUtc: null, findings: [{ id: 'f1', summary: 'It stopped.', severity: 'High', evidenceIds: ['e1'] }], evidence: [], planHash: null, verification: null, errorMessage: null }],
                journal: [], resumeCount: 2, denial: null, errorMessage: null, createdAtUtc: '2026-09-20T10:00:00Z', updatedAtUtc: '2026-09-20T10:00:00Z',
              },
            ]),
            pendingPlans: signal([]),
            loading: signal(false),
            busy: signal(false),
            error: signal(null),
          },
        },
        { provide: AuthService, useValue: { identity: identity(['viewer']) } },
      ],
    });
    const fixture = TestBed.createComponent(Delegations);
    fixture.detectChanges();
    (fixture.componentInstance as unknown as { select(id: string): void }).select('r1');
    fixture.detectChanges();
    expect(text(fixture)).toContain('DiagnosisCompleted');
    expect(text(fixture)).toContain('1 step · 12 tokens');
    expect(text(fixture)).toContain('resumed 2 times');

    switchTo(fixture, 'it');
    expect(text(fixture)).toContain('Diagnosi completata');
    expect(text(fixture)).toContain('Rilevamento');
    expect(text(fixture)).toContain('1 passo · 12 token');
    expect(text(fixture)).toContain('ripresa 2 volte');
    expect(text(fixture)).toContain('[Alto]');
    expect(text(fixture)).toContain('It stopped.');
    expect(text(fixture)).toContain('Why did nginx stop?');

    switchTo(fixture, 'en');
    expect(text(fixture)).toContain('DiagnosisCompleted');
  });

  describe('the shell and the switch', () => {
    function configureApp(): ComponentFixture<App> {
      TestBed.configureTestingModule({
        imports: [App],
        providers: [
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
          { provide: AuthService, useValue: { authenticated: signal(true), identity: identity(['viewer']), signOut: () => undefined } },
        ],
      });
      const fixture = TestBed.createComponent(App);
      fixture.detectChanges();
      return fixture;
    }

    const languageButtons = (fixture: ComponentFixture<unknown>): HTMLButtonElement[] =>
      Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('bops-language-switch button'));

    it('offers one button per language, with aria-pressed on the active one', () => {
      const fixture = configureApp();
      const [english, italian] = languageButtons(fixture);

      expect(languageButtons(fixture).length).toBe(2);
      expect(english.textContent?.trim()).toBe('en');
      expect(english.getAttribute('aria-pressed')).toBe('true');
      expect(italian.getAttribute('aria-pressed')).toBe('false');
      expect(italian.getAttribute('aria-label')).toBe('Switch to Italiano');
      expect((fixture.nativeElement as HTMLElement).querySelector('bops-language-switch [role="group"]')?.getAttribute('aria-label')).toBe('Language');
    });

    it('switches the navigation, the theme toggle and <html lang> when a button is pressed, and remembers the choice', () => {
      const fixture = configureApp();
      expect(text(fixture)).toContain('Approvals');
      expect(text(fixture)).toContain('Sign out');

      languageButtons(fixture)[1].click();
      fixture.detectChanges();

      expect(text(fixture)).toContain('Approvazioni');
      expect(text(fixture)).toContain('Deleghe');
      expect(text(fixture)).toContain('Esci');
      expect(text(fixture)).not.toContain('Sign out');
      expect(document.documentElement.lang).toBe('it');
      expect(localStorage.getItem('bops-ui-language')).toBe('it');
      expect(languageButtons(fixture)[1].getAttribute('aria-pressed')).toBe('true');
      expect(languageButtons(fixture)[0].getAttribute('aria-label')).toBe('Passa a English');

      languageButtons(fixture)[0].click();
      fixture.detectChanges();
      expect(text(fixture)).toContain('Approvals');
      expect(document.documentElement.lang).toBe('en');
    });

    it('is reachable by keyboard: the buttons are ordinary focusable buttons', () => {
      const fixture = configureApp();
      for (const button of languageButtons(fixture)) {
        expect(button.type).toBe('button');
        expect(button.tabIndex).toBe(0);
      }
    });

    it('is on the sign-in screen too, so the language can be chosen before signing in', () => {
      TestBed.configureTestingModule({
        imports: [Login],
        providers: [provideHttpClient(), provideHttpClientTesting()],
      });
      const fixture = TestBed.createComponent(Login);
      fixture.detectChanges();
      expect(text(fixture)).toContain('Sign in to bOps');

      languageButtons(fixture)[1].click();
      fixture.detectChanges();
      expect(text(fixture)).toContain('Accedi a bOps');
      expect((fixture.nativeElement as HTMLElement).querySelector('label')?.textContent).toContain('Chiave API');
    });

    it('translates the login error at display time', async () => {
      TestBed.configureTestingModule({
        imports: [Login],
        providers: [{ provide: AuthService, useValue: { signIn: () => Promise.reject(new Error('nope')) } }],
      });
      const fixture = TestBed.createComponent(Login);
      fixture.detectChanges();
      await (fixture.componentInstance as unknown as { submit(): Promise<void> }).submit();
      fixture.detectChanges();
      expect(text(fixture)).toContain('Authentication failed.');

      switchTo(fixture, 'it');
      expect(text(fixture)).toContain('Autenticazione non riuscita.');
    });
  });
});
