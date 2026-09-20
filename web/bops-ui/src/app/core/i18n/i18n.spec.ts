// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { TestBed } from '@angular/core/testing';
import { en } from './en';
import { I18n } from './i18n';
import { LANGUAGES } from './languages';
import type { MessageKey } from './messages';

const placeholders = (text: string): string[] => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

function service(): I18n {
  return TestBed.inject(I18n);
}

describe('the catalogues', () => {
  const english = en as Record<string, string>;

  for (const [code, language] of Object.entries(LANGUAGES)) {
    const messages = language.messages as Record<string, string>;

    it(`'${code}' has exactly the keys of English, no fewer and no more`, () => {
      expect(Object.keys(messages).sort()).toEqual(Object.keys(english).sort());
    });

    it(`'${code}' uses the same placeholders as English in every message`, () => {
      const different = Object.keys(english).filter(
        (key) => JSON.stringify(placeholders(messages[key])) !== JSON.stringify(placeholders(english[key])),
      );
      expect(different).toEqual([]);
    });

    it(`'${code}' has no empty message`, () => {
      expect(Object.keys(messages).filter((key) => messages[key].trim() === '')).toEqual([]);
    });

    it(`'${code}' has a .one and an .other for every plural message`, () => {
      for (const key of Object.keys(messages).filter((k) => k.endsWith('.other'))) {
        expect(messages[key.replace(/\.other$/, '.one')]).withContext(key).toBeDefined();
      }
    });
  }

  it('Italian is really translated, not English left under an Italian key', () => {
    const italian = LANGUAGES.it.messages as Record<string, string>;
    const untranslated = Object.keys(english).filter(
      (key) => italian[key] === english[key] && /[a-z]{4,}/i.test(english[key]) && !/^(enum\.trust|settings\.profile\.baseUrl)/.test(key),
    );

    // Words that are the same in both languages (a product name, a term operators use as it is) are few and listed here on purpose.
    const sameInBoth = [
      'app.nav.dashboard',
      'dashboard.title',
      'dashboard.step.ok',
      'plugins.version',
      'dashboard.detail.taskId',
      'delegations.start.skill',
      'delegations.start.capability',
      'delegations.plan.budget',
      'delegations.roles.stats',
      'delegations.roles.tokens.one',
      'delegations.roles.tokens.other',
      'common.duration.ms',
      'common.duration.s',
      'common.duration.min',
      'common.duration.minS',
      'approvals.deletion.files',
      'settings.profile.baseUrlPlaceholder',
      'enum.trust.Community',
    ];
    expect(untranslated.filter((key) => !sameInBoth.includes(key))).toEqual([]);
  });
});

describe('I18n', () => {
  afterEach(() => {
    localStorage.removeItem('bops-ui-language');
  });

  describe('the initial language', () => {
    it('is English when nothing says otherwise', () => {
      expect(service().language()).toBe('en');
      expect(document.documentElement.lang).toBe('en');
    });

    it('is the stored choice when there is one', () => {
      localStorage.setItem('bops-ui-language', 'it');
      expect(service().language()).toBe('it');
      expect(document.documentElement.lang).toBe('it');
    });

    it('is Italian when the browser prefers it and nothing is stored', () => {
      Object.defineProperty(navigator, 'language', { value: 'it-IT', configurable: true });
      expect(service().language()).toBe('it');
    });

    it('lets a stored choice win over the browser language', () => {
      Object.defineProperty(navigator, 'language', { value: 'it-IT', configurable: true });
      localStorage.setItem('bops-ui-language', 'en');
      expect(service().language()).toBe('en');
    });

    it('ignores a stored value that is not a language the UI has', () => {
      localStorage.setItem('bops-ui-language', 'klingon');
      expect(service().language()).toBe('en');
    });
  });

  describe('switching', () => {
    it('changes the language, sets <html lang> and remembers it', () => {
      const i18n = service();
      i18n.setLanguage('it');

      expect(i18n.language()).toBe('it');
      expect(document.documentElement.lang).toBe('it');
      expect(localStorage.getItem('bops-ui-language')).toBe('it');

      i18n.setLanguage('en');
      expect(document.documentElement.lang).toBe('en');
      expect(localStorage.getItem('bops-ui-language')).toBe('en');
    });

    it('survives a reload: a new service reads what the last one stored', () => {
      service().setLanguage('it');
      TestBed.resetTestingModule();

      expect(TestBed.inject(I18n).language()).toBe('it');
    });

    it('still works for the session when storage is blocked', () => {
      spyOn(Storage.prototype, 'getItem').and.throwError('blocked');
      spyOn(Storage.prototype, 'setItem').and.throwError('blocked');
      const i18n = service();

      expect(i18n.language()).toBe('en');
      expect(() => i18n.setLanguage('it')).not.toThrow();
      expect(i18n.language()).toBe('it');
      expect(i18n.t('app.signOut')).toBe('Esci');
    });

    it('ignores a language the UI does not have', () => {
      const i18n = service();
      i18n.setLanguage('xx' as never);
      expect(i18n.language()).toBe('en');
    });
  });

  describe('t', () => {
    it('returns the message in the active language', () => {
      const i18n = service();
      expect(i18n.t('app.signOut')).toBe('Sign out');
      i18n.setLanguage('it');
      expect(i18n.t('app.signOut')).toBe('Esci');
    });

    it('fills named placeholders, formatting numbers for the language', () => {
      const i18n = service();
      expect(i18n.t('dashboard.list.showingLatest', { shown: 50, total: 12345 })).toBe('Showing the latest 50 of 12,345.');
      i18n.setLanguage('it');
      expect(i18n.t('dashboard.list.showingLatest', { shown: 50, total: 12345 })).toBe('Mostro gli ultimi 50 su 12.345.');
    });

    it('leaves a placeholder that was not given, rather than printing "undefined"', () => {
      expect(service().t('dashboard.detail.taskId')).toBe('Task {id}');
    });

    it('falls back to English for a key the active language lacks, and says so once', () => {
      const warn = spyOn(console, 'warn');
      const italian = LANGUAGES.it.messages as Record<string, string>;
      const saved = italian['app.signOut'];
      delete italian['app.signOut'];
      try {
        const i18n = service();
        i18n.setLanguage('it');

        expect(i18n.t('app.signOut')).toBe('Sign out');
        expect(i18n.t('app.signOut')).toBe('Sign out');
        expect(warn).toHaveBeenCalledTimes(1);
      } finally {
        italian['app.signOut'] = saved;
      }
    });

    it('never renders a raw key', () => {
      const i18n = service();
      for (const key of Object.keys(en) as MessageKey[]) {
        if (key.endsWith('.one') || key.endsWith('.other')) continue;
        expect(i18n.t(key)).withContext(key).not.toBe(key);
      }
    });
  });

  describe('plurals, by the rules of the active language', () => {
    const meta = (i18n: I18n, count: number) => i18n.t('dashboard.task.meta', { count, time: '10:00' });

    it('English: one step, 0 / 2 / 1000 steps', () => {
      const i18n = service();
      expect(meta(i18n, 1)).toBe('1 step · started 10:00');
      expect(meta(i18n, 0)).toBe('0 steps · started 10:00');
      expect(meta(i18n, 2)).toBe('2 steps · started 10:00');
      expect(meta(i18n, 1000)).toBe('1,000 steps · started 10:00');
    });

    it('Italian: un passo, 0 / 2 / 1000 passi', () => {
      const i18n = service();
      i18n.setLanguage('it');
      expect(meta(i18n, 1)).toBe('1 passo · avviato alle 10:00');
      expect(meta(i18n, 0)).toBe('0 passi · avviato alle 10:00');
      expect(meta(i18n, 2)).toBe('2 passi · avviato alle 10:00');
      // Italian does not group a four-digit number; ten thousand it does.
      expect(meta(i18n, 1000)).toBe(`${new Intl.NumberFormat('it').format(1000)} passi · avviato alle 10:00`);
      expect(meta(i18n, 10000)).toBe('10.000 passi · avviato alle 10:00');
    });

    it('the same rule reaches the other plural messages', () => {
      const i18n = service();
      expect(i18n.t('delegations.detail.resumed', { count: 1 })).toBe('· resumed 1 time');
      expect(i18n.t('delegations.detail.resumed', { count: 3 })).toBe('· resumed 3 times');
      i18n.setLanguage('it');
      expect(i18n.t('delegations.detail.resumed', { count: 1 })).toBe('· ripresa 1 volta');
      expect(i18n.t('delegations.detail.resumed', { count: 3 })).toBe('· ripresa 3 volte');
    });
  });

  describe('dates, numbers and durations follow the chosen language, not the browser', () => {
    const iso = '2026-09-19T19:05:53Z';

    it('formats a time and a date-time with the active language', () => {
      const i18n = service();
      expect(i18n.time(iso)).toBe(new Date(iso).toLocaleTimeString('en'));
      expect(i18n.dateTime(iso)).toBe(new Date(iso).toLocaleString('en'));
      i18n.setLanguage('it');
      expect(i18n.time(iso)).toBe(new Date(iso).toLocaleTimeString('it'));
      expect(i18n.dateTime(iso)).toBe(new Date(iso).toLocaleString('it'));
      expect(i18n.time(iso)).not.toBe(new Date(iso).toLocaleTimeString('en'));
      expect(i18n.dateTime(null)).toBe('');
    });

    it('formats numbers, including 0, 1, 2 and 1000', () => {
      const i18n = service();
      expect([0, 1, 2, 1000, 1234567].map((n) => i18n.number(n))).toEqual(['0', '1', '2', '1,000', '1,234,567']);
      i18n.setLanguage('it');
      expect([0, 1, 2, 1000, 1234567].map((n) => i18n.number(n))).toEqual(['0', '1', '2', new Intl.NumberFormat('it').format(1000), '1.234.567']);
    });

    it('formats sizes in binary units with the language’s decimal separator', () => {
      const i18n = service();
      expect(i18n.bytes(512)).toBe('512 B');
      expect(i18n.bytes(1536)).toBe('1.5 KiB');
      i18n.setLanguage('it');
      expect(i18n.bytes(1536)).toBe('1,5 KiB');
    });

    it('formats a duration in the unit that reads best', () => {
      const i18n = service();
      expect([0, 820, 999.4].map((ms) => i18n.duration(ms))).toEqual(['0 ms', '820 ms', '999 ms']);
      expect([1000, 4321].map((ms) => i18n.duration(ms))).toEqual(['1.0 s', '4.3 s']);
      expect([65_000, 120_000].map((ms) => i18n.duration(ms))).toEqual(['1 min 5 s', '2 min']);
      expect([-1, Number.NaN].map((ms) => i18n.duration(ms))).toEqual(['—', '—']);

      i18n.setLanguage('it');
      expect(i18n.duration(4321)).toBe('4,3 s');
      expect(i18n.duration(65_000)).toBe('1 min 5 s');
    });
  });

  describe('enum labels', () => {
    it('are keyed by the enum name and follow the language', () => {
      const i18n = service();
      expect(i18n.label('risk', 'High')).toBe('High');
      expect(i18n.label('delegationRole', 'Remediation')).toBe('Remediation');
      i18n.setLanguage('it');
      expect(i18n.label('risk', 'High')).toBe('Alto');
      expect(i18n.label('delegationRole', 'Remediation')).toBe('Correzione');
      expect(i18n.label('taskStatus', 'Failed')).toBe('Fallito');
      expect(i18n.label('blastRadius', 'Single')).toBe('Singolo');
    });

    it('show a value the catalogues do not know as the server sent it, and nothing for no value', () => {
      const i18n = service();
      i18n.setLanguage('it');
      expect(i18n.label('delegationStatus', 'SomethingNewer')).toBe('SomethingNewer');
      expect(i18n.label('risk', null)).toBe('');
      expect(i18n.label('risk', undefined)).toBe('');
    });
  });
});
