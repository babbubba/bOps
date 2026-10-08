// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Route } from '@angular/router';
import { SystemMessage, SystemMessagePage } from '../../core/api/models';
import { en } from '../../core/i18n/en';
import { I18n } from '../../core/i18n/i18n';
import { it as italian } from '../../core/i18n/it';
import { routes } from '../../app.routes';
import { SystemMessages } from './system-messages';

function message(id: string, overrides: Partial<SystemMessage> = {}): SystemMessage {
  return {
    id,
    timestampUtc: '2026-10-08T09:30:00Z',
    node: 'local',
    source: 'prerequisite/docker',
    severity: 'Warning',
    code: 'prerequisite.missing',
    message: `Message ${id}`,
    metadata: {},
    taskId: null,
    componentType: null,
    componentId: null,
    ...overrides,
  };
}

function page(items: SystemMessage[], nextCursor: string | null = null): SystemMessagePage {
  return { items, nextCursor };
}

describe('SystemMessages page', () => {
  let fixture: ComponentFixture<SystemMessages>;
  let http: HttpTestingController;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SystemMessages],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function create(): void {
    fixture = TestBed.createComponent(SystemMessages);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  }

  /** The one outstanding listing request, with its decoded query. */
  function request(): { call: TestRequest; query: URLSearchParams } {
    const call = http.expectOne((r) => r.url.startsWith('/api/system-messages'));
    return { call, query: new URL(call.request.url, 'http://bops.test').searchParams };
  }

  async function answer(body: SystemMessagePage): Promise<URLSearchParams> {
    const { call, query } = request();
    call.flush(body);
    await settle();
    return query;
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function field(name: string): HTMLInputElement | HTMLSelectElement {
    return root.querySelector(`[name="${name}"]`) as HTMLInputElement | HTMLSelectElement;
  }

  async function type(name: string, value: string): Promise<void> {
    const element = field(name);
    element.value = value;
    element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input'));
    await settle();
  }

  function button(label: string): HTMLButtonElement {
    const found = Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim() === label);
    if (!found) throw new Error(`no button "${label}"`);
    return found as HTMLButtonElement;
  }

  async function click(label: string): Promise<void> {
    button(label).click();
    await settle();
  }

  function rows(): HTMLElement[] {
    return Array.from(root.querySelectorAll<HTMLElement>('[data-testid="system-message"]'));
  }

  const rowText = () => rows().map((row) => row.textContent ?? '');

  describe('route and navigation', () => {
    it('is routed at /system-messages and loads this component', async () => {
      const route = routes.find((r: Route) => r.path === 'system-messages');
      expect(route).toBeDefined();
      expect((await route!.loadComponent!()) as unknown).toBe(SystemMessages);
    });
  });

  describe('first load', () => {
    it('asks for the newest page of 50 with no filter, and shows loading until it arrives', async () => {
      create();
      expect(root.querySelector('[role="status"]')?.textContent).toContain('Loading system messages…');

      const query = await answer(page([message('a'), message('b')]));

      expect(Array.from(query.keys()).sort()).toEqual(['pageSize']);
      expect(query.get('pageSize')).toBe('50');
      expect(rows().length).toBe(2);
      expect(root.querySelector('[role="status"]')).toBeNull();
    });

    it('shows the result in the order the server sent (newest first) with date, severity, source and message', async () => {
      create();
      await answer(page([message('new', { message: 'Newest' }), message('old', { message: 'Oldest' })]));

      expect(rowText()[0]).toContain('Newest');
      expect(rowText()[1]).toContain('Oldest');
      expect(rowText()[0]).toContain('prerequisite/docker');
      expect(rows()[0].querySelector('time')?.getAttribute('datetime')).toBe('2026-10-08T09:30:00Z');
    });

    it('lists the four severities and an All option, and offers Apply and Reset', async () => {
      create();
      await answer(page([]));

      const labels = Array.from((field('severity') as HTMLSelectElement).options).map((o) => o.textContent?.trim());
      expect(labels).toEqual(['All', 'Information', 'Warning', 'Error', 'Critical']);
      expect(button('Apply')).toBeTruthy();
      expect(button('Reset')).toBeTruthy();
    });
  });

  describe('filters (all server-side, applied together with AND)', () => {
    beforeEach(async () => {
      create();
      await answer(page([message('a')]));
    });

    it('sends nothing while typing — only Apply makes a request', async () => {
      await type('contains', 'doc');
      await type('contains', 'docker');
      await type('severity', 'Error');
      http.expectNone((r) => r.url.startsWith('/api/system-messages'));
    });

    it('sends from and to as explicit UTC instants', async () => {
      await type('from', '2026-10-08T09:30:15');
      await type('to', '2026-10-09T10:00:00');
      await click('Apply');

      const { call, query } = request();
      expect(query.get('fromUtc')).toBe(new Date(2026, 9, 8, 9, 30, 15).toISOString());
      expect(query.get('toUtc')).toBe(new Date(2026, 9, 9, 10, 0, 0).toISOString());
      expect(query.get('severity')).toBeNull();
      expect(query.get('contains')).toBeNull();
      call.flush(page([]));
    });

    it('sends the severity', async () => {
      await type('severity', 'Critical');
      await click('Apply');

      const { call, query } = request();
      expect(query.get('severity')).toBe('Critical');
      expect(query.get('pageSize')).toBe('50');
      call.flush(page([]));
    });

    it('sends the contained text, trimmed, and omits it when blank', async () => {
      await type('contains', '  docker  ');
      await click('Apply');
      const first = request();
      expect(first.query.get('contains')).toBe('docker');
      first.call.flush(page([]));
      await settle();

      await type('contains', '   ');
      await click('Apply');
      const second = request();
      expect(second.query.has('contains')).toBeFalse();
      second.call.flush(page([]));
    });

    it('sends every supplied filter together in one request', async () => {
      await type('from', '2026-10-08T00:00:00');
      await type('to', '2026-10-08T23:59:59');
      await type('severity', 'Warning');
      await type('contains', 'searxng');
      await click('Apply');

      const { call, query } = request();
      expect(query.get('fromUtc')).toBe(new Date(2026, 9, 8, 0, 0, 0).toISOString());
      expect(query.get('toUtc')).toBe(new Date(2026, 9, 8, 23, 59, 59).toISOString());
      expect(query.get('severity')).toBe('Warning');
      expect(query.get('contains')).toBe('searxng');
      expect(query.get('pageSize')).toBe('50');
      expect(query.has('cursor')).toBeFalse();
      call.flush(page([]));
    });

    it('refuses a range that ends before it starts without asking the server', async () => {
      await type('from', '2026-10-09T00:00:00');
      await type('to', '2026-10-08T00:00:00');
      await click('Apply');

      http.expectNone((r) => r.url.startsWith('/api/system-messages'));
      expect(root.querySelector('[role="alert"]')?.textContent).toContain('must not be after its end');
    });

    it('Reset clears every field and asks for the unfiltered newest page', async () => {
      await type('from', '2026-10-08T00:00:00');
      await type('severity', 'Error');
      await type('contains', 'x');
      await click('Apply');
      await answer(page([]));

      await click('Reset');

      expect(field('from').value).toBe('');
      expect(field('severity').value).toBe('');
      expect(field('contains').value).toBe('');
      const query = await answer(page([message('z')]));
      expect(Array.from(query.keys()).sort()).toEqual(['pageSize']);
    });
  });

  describe('cursor paging', () => {
    it('follows the server cursor to the next page, keeping the filters, and replaces the rows', async () => {
      create();
      await answer(page([message('a'), message('b')], 'cursor-1'));
      await type('severity', 'Warning');
      await click('Apply');
      await answer(page([message('c'), message('d')], 'cursor-2'));

      await click('Next');
      const second = request();
      expect(second.query.get('cursor')).toBe('cursor-2');
      expect(second.query.get('severity')).toBe('Warning');
      expect(second.query.get('pageSize')).toBe('50');
      second.call.flush(page([message('e'), message('f')]));
      await settle();

      expect(rows().length).toBe(2);
      expect(rowText().join('|')).toContain('Message e');
      expect(rowText().join('|')).not.toContain('Message c');
      expect(root.textContent).toContain('Page 2');
      expect(button('Next').disabled).toBeTrue();
    });

    it('Previous asks for the earlier page again with the cursor that produced it, with no offset', async () => {
      create();
      await answer(page([message('a')], 'cursor-1'));
      await click('Next');
      const next = request();
      next.call.flush(page([message('b')], 'cursor-2'));
      await settle();
      await click('Next');
      const third = request();
      expect(third.query.get('cursor')).toBe('cursor-2');
      third.call.flush(page([message('c')]));
      await settle();

      await click('Previous');
      const back = request();
      expect(back.query.get('cursor')).toBe('cursor-1');
      expect(back.query.has('offset')).toBeFalse();
      back.call.flush(page([message('b')], 'cursor-2'));
      await settle();
      await click('Previous');
      const first = request();
      expect(first.query.has('cursor')).toBeFalse();
      first.call.flush(page([message('a')], 'cursor-1'));
      await settle();

      expect(rowText().join('|')).toContain('Message a');
      expect(button('Previous').disabled).toBeTrue();
    });

    it('never shows the same message twice, within a page or across pages', async () => {
      create();
      await answer(page([message('a'), message('a'), message('b')], 'cursor-1'));
      expect(rows().length).toBe(2);

      await click('Next');
      const next = request();
      next.call.flush(page([message('c')]));
      await settle();

      expect(rows().length).toBe(1);
      expect(rowText()[0]).toContain('Message c');
    });

    it('disables Next on the last page (no cursor) and Previous on the first', async () => {
      create();
      await answer(page([message('a')]));

      expect(button('Next').disabled).toBeTrue();
      expect(button('Previous').disabled).toBeTrue();
    });

    it('ignores a slow response that arrives after a newer request', async () => {
      create();
      await answer(page([message('a')], 'cursor-1'));
      await click('Next');
      const slow = request();
      await type('contains', 'fresh');
      await click('Apply');
      const fresh = request();

      fresh.call.flush(page([message('fresh-1', { message: 'Fresh result' })]));
      await settle();
      slow.call.flush(page([message('stale', { message: 'Stale result' })]));
      await settle();

      expect(rowText().join('|')).toContain('Fresh result');
      expect(rowText().join('|')).not.toContain('Stale result');
    });
  });

  describe('severity rendering', () => {
    it('shows each severity as a word and a glyph with an accessible label, not by colour alone', async () => {
      create();
      await answer(
        page([
          message('i', { severity: 'Information' }),
          message('w', { severity: 'Warning' }),
          message('e', { severity: 'Error' }),
          message('c', { severity: 'Critical' }),
        ]),
      );

      const badges = Array.from(root.querySelectorAll<HTMLElement>('[data-testid="severity"]'));
      expect(badges.map((b) => b.getAttribute('aria-label'))).toEqual([
        'Severity: Information',
        'Severity: Warning',
        'Severity: Error',
        'Severity: Critical',
      ]);
      expect(badges.map((b) => b.children[0].textContent)).toEqual(['ℹ', '⚠', '✖', '‼']);
      expect(badges.map((b) => b.children[1].textContent)).toEqual(['Information', 'Warning', 'Error', 'Critical']);
      for (const badge of badges) {
        expect(badge.querySelector('[aria-hidden="true"]')).not.toBeNull();
      }
      expect(new Set(badges.map((b) => b.className)).size).toBe(4);
    });
  });

  describe('message details', () => {
    it('expands a row to the code, source, timestamp, severity, task, component and metadata', async () => {
      create();
      await answer(
        page([
          message('a', {
            code: 'agent.replan.threshold',
            source: 'runtime/agent',
            severity: 'Warning',
            taskId: '0b8c7a2e-0000-4000-8000-000000000001',
            componentType: 'Runtime',
            componentId: 'agent',
            metadata: { lifetimeReplans: 3, provider: 'fake' },
          }),
        ]),
      );
      const toggle = rows()[0].querySelector('button') as HTMLButtonElement;
      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(rows()[0].querySelector('dl')).toBeNull();

      toggle.click();
      await settle();

      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      const details = rows()[0].querySelector('dl')!.textContent!;
      for (const expected of [
        'agent.replan.threshold',
        'runtime/agent',
        '2026-10-08T09:30:00Z',
        'Warning',
        '0b8c7a2e-0000-4000-8000-000000000001',
        'Runtime',
        'agent',
        '"lifetimeReplans": 3',
        '"provider": "fake"',
      ]) {
        expect(details).toContain(expected);
      }

      toggle.click();
      await settle();
      expect(rows()[0].querySelector('dl')).toBeNull();
    });

    it('omits the task, component and metadata rows when the message has none', async () => {
      create();
      await answer(page([message('a')]));
      (rows()[0].querySelector('button') as HTMLButtonElement).click();
      await settle();

      const terms = Array.from(rows()[0].querySelectorAll('dt')).map((dt) => dt.textContent?.trim());
      expect(terms).toEqual(['Code', 'Source', 'Timestamp (UTC)', 'Severity']);
    });

    it('renders metadata as escaped text — never as markup, a link or a script', async () => {
      create();
      await answer(
        page([
          message('a', {
            message: '<b>bold?</b>',
            metadata: {
              remediation: '<img src=x onerror="window.__pwned=1">',
              link: 'javascript:window.__pwned=1',
              script: '<script>window.__pwned=1</script>',
              href: '<a href="https://evil.example">click</a>',
            },
          }),
        ]),
      );
      (rows()[0].querySelector('button') as HTMLButtonElement).click();
      await settle();

      const pre = rows()[0].querySelector('[data-testid="metadata"]')!;
      expect(pre.textContent).toContain('<img src=x onerror=');
      expect(pre.textContent).toContain('javascript:window.__pwned=1');
      expect(pre.textContent).toContain('<script>window.__pwned=1</script>');
      expect(root.querySelector('img, script, b, a, iframe')).toBeNull();
      expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
      expect(rows()[0].textContent).toContain('<b>bold?</b>');
    });
  });

  describe('states', () => {
    it('shows the empty state, in English', async () => {
      create();
      await answer(page([]));

      expect(root.querySelector('[role="status"]')?.textContent?.trim()).toBe('No system messages match the selected filters.');
      expect(rows().length).toBe(0);
    });

    it('shows the empty state in Italian', async () => {
      TestBed.inject(I18n).setLanguage('it');
      create();
      await answer(page([]));

      expect(root.querySelector('[role="status"]')?.textContent?.trim()).toBe('Nessun messaggio di sistema corrisponde ai filtri selezionati.');
      expect(root.querySelector('h1')?.textContent?.trim()).toBe('Messaggi di sistema');
      expect(button('Applica')).toBeTruthy();
      expect(button('Reimposta')).toBeTruthy();
    });

    it('shows an API error with a retry that asks again', async () => {
      create();
      request().call.flush({ message: "'severity' must be one of Information, Warning, Error, Critical." }, { status: 400, statusText: 'Bad Request' });
      await settle();

      const alert = root.querySelector('[role="alert"]')!;
      expect(alert.textContent).toContain('System messages could not be loaded');
      expect(alert.textContent).toContain("'severity' must be one of");
      expect(rows().length).toBe(0);
      expect(root.querySelector('[role="status"]')).toBeNull();

      await click('Try again');
      await answer(page([message('a')]));
      expect(root.querySelector('[role="alert"]')).toBeNull();
      expect(rows().length).toBe(1);
    });

    it('shows a connectivity error when the API cannot be reached', async () => {
      create();
      request().call.flush('', { status: 503, statusText: 'Service Unavailable' });
      await settle();

      expect(root.querySelector('[role="alert"]')?.textContent).toContain('Cannot reach the bOps API');
    });
  });

  describe('translations', () => {
    const keys = Object.keys(en).filter((key) => key.startsWith('systemMessages.') || key.startsWith('enum.severity.') || key === 'app.nav.systemMessages');

    it('has every system-messages string in both languages', () => {
      expect(keys.length).toBeGreaterThanOrEqual(40);
      for (const key of keys) {
        expect((en as Record<string, string>)[key].trim()).not.toBe('');
        expect((italian as Record<string, string>)[key]?.trim()).toBeTruthy();
      }
      expect(Object.keys(italian).filter((key) => key.startsWith('systemMessages.')).sort()).toEqual(
        Object.keys(en).filter((key) => key.startsWith('systemMessages.')).sort(),
      );
    });

    it('names the navigation entry in both languages', () => {
      expect(en['app.nav.systemMessages']).toBe('System messages');
      expect(italian['app.nav.systemMessages']).toBe('Messaggi di sistema');
    });
  });
});
