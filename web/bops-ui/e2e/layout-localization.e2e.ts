// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Browser, Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * E2E-12: the authenticated application shell is real (including the real browser-session sign-in), while read-only API
 * responses are deterministic long-value fixtures. This deliberately does not replace session/authentication requests or any
 * mutation and keeps the pseudo-locale and layout instrumentation inside the Playwright harness.
 */

const KEY = process.env['BOPS_E2E_API_KEY']!;
const TASK_ID = '01234567-89ab-cdef-0123-456789abcdef';
const DELEGATION_ID = 'delegation-with-an-intentionally-long-identifier-for-layout-evidence';
const LONG_GOAL =
  'Investigate intermittent synchronization failures across the production service cluster without losing diagnostic evidence';
const LONG_PLUGIN = 'bops.community.extremely-long-observability-and-recovery-plugin-identifier';
const WIDTHS = [375, 768, 1024, 1440] as const;
const LOCALES = ['en', 'it', 'pseudo'] as const;
type Locale = (typeof LOCALES)[number];

const task = {
  id: TASK_ID,
  node: 'e2e-layout-node-with-a-long-name',
  goal: LONG_GOAL,
  status: 5,
  steps: [],
  plans: [],
  createdAtUtc: '2026-10-06T08:15:00Z',
  executionAttempt: 3,
  accounting: { tokensUsed: 123456, lifetimeSteps: 40, lifetimeReplans: 2 },
  origin: 0,
  delegationId: null,
  delegationRole: null,
  terminalReason: { kind: 5 },
  resumedAtUtc: null,
  resumedBy: null,
  executing: false,
  resumable: false,
  resumeBlockedReason: {
    code: 'replan_limit_reached',
    message: 'The replanning lifetime limit was reached.',
  },
};

const delegation = {
  id: DELEGATION_ID,
  status: 'RequiresReconciliation',
  objective:
    'Determine why the production synchronization service repeatedly loses its lease during long-running recovery operations',
  actorId: 'e2e-operator-with-a-long-stable-identifier',
  actorDisplayName: 'E2E Layout Operator With Long Display Name',
  runningInThisHost: false,
  awaitingPlanApproval: false,
  planHash: 'sha256:1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef',
  approval: null,
  roles: [
    {
      role: 'Discovery',
      agentId: 'agent-discovery-with-a-long-stable-identifier',
      status: 'Completed',
      steps: 12,
      tokens: 54321,
      startedAtUtc: '2026-10-06T08:15:00Z',
      completedAtUtc: '2026-10-06T08:20:00Z',
      findings: [
        {
          id: 'finding-with-a-long-identifier',
          summary:
            'The synchronization lease expires while the recovery operation is still collecting evidence.',
          severity: 'High',
          evidenceIds: ['discovery-evidence-with-a-long-identifier'],
          restsOnLimitedEvidence: false,
        },
      ],
      evidence: [
        {
          id: 'discovery-evidence-with-a-long-identifier',
          kind: 'Fact',
          description:
            'Observed a lease timeout while the recovery process remained active on the target node.',
          sourceTool: 'system.extremely.long.diagnostic.tool.identifier',
          observedAtUtc: '2026-10-06T08:18:00Z',
        },
      ],
      planHash: null,
      verification: null,
      errorMessage: null,
      evidenceLimitations: [],
      evidenceLimitationsOmitted: 0,
      findingsReply: { status: 'Valid', problem: '', discardedFindings: 0 },
    },
  ],
  journal: [
    {
      stepIndex: 0,
      tool: 'service.recovery.operation.with.a.long.identifier',
      argumentsHash: 'sha256:abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd',
      intentAtUtc: '2026-10-06T08:19:00Z',
      outcome: 'Succeeded',
      verification: 'Confirmed',
      reconciliation: 'Required',
    },
  ],
  resumeCount: 2,
  denial: null,
  errorMessage: null,
  createdAtUtc: '2026-10-06T08:15:00Z',
  updatedAtUtc: '2026-10-06T08:25:00Z',
};

const plugin = {
  id: LONG_PLUGIN,
  version: '2026.10.6-layout-evidence',
  publisher: 'Community Publisher With A Long Descriptive Name',
  installedAtUtc: '2026-10-06T08:15:00Z',
  enabled: true,
  loaded: true,
  compatible: true,
  signaturePresent: true,
  verified: true,
  trust: 2,
  keyId: 'publisher-key-with-a-long-stable-identifier',
  declaredCapabilities: [
    'observability.collect-extremely-detailed-diagnostic-evidence-with-context',
  ],
  dependencies: [
    { name: 'dependency-with-an-intentionally-long-unbroken-package-name', version: '1.2.3' },
  ],
  declaredMaxRisk: 2,
  effectiveMaxRisk: 2,
  loadError: null,
  lifecycleState: 'Enabled',
  lifecycleETag: '"layout-evidence-v1"',
  lifecycleFailure: null,
  recoveryAvailable: false,
};

const readiness = {
  remediation: false,
  ready: false,
  policy: 'loaded',
  roles: ['Discovery', 'Diagnostic', 'Remediation', 'Verification'].map((role, index) => ({
    role,
    state: index < 2 ? 'ready' : 'notRequired',
    dimension: null,
    reasonCode: index < 2 ? 'ready' : 'not_required',
    reason: null,
  })),
  profileDriftCount: 0,
  evaluatedAtUtc: '2026-10-06T08:15:00Z',
};

async function layoutFixture(route: Route): Promise<void> {
  const request = route.request();
  if (request.method() !== 'GET') {
    await route.continue();
    return;
  }

  const url = new URL(request.url());
  const path = url.pathname;
  let body: unknown;
  if (path === '/api/agents/tasks') body = [task];
  else if (path === `/api/agents/tasks/${TASK_ID}`) body = task;
  else if (path === '/api/delegations') body = [delegation];
  else if (path === '/api/delegations/approvals') body = [];
  else if (path === '/api/delegations/readiness')
    body = { ...readiness, remediation: url.searchParams.get('remediation') === 'true' };
  else if (path === '/api/skills') body = { skills: [] };
  else if (path === '/api/approvals/pending') {
    body = [
      {
        id: 'approval-with-an-intentionally-long-identifier',
        taskId: TASK_ID,
        tool: 'infrastructure.perform-a-long-named-sensitive-maintenance-operation',
        reason:
          'This operation requires a human decision because it can affect multiple production services and their retained evidence.',
        requestedAtUtc: '2026-10-06T08:15:00Z',
        arguments: {},
        permanentDeletion: false,
      },
    ];
  } else if (path === '/api/tools') {
    body = [
      {
        name: 'infrastructure.perform-a-long-named-sensitive-maintenance-operation',
        description: '',
        risk: 3,
        parameters: [],
      },
    ];
  } else if (path === '/api/plugins') body = { entries: [plugin], totalCount: 1 };
  else if (path === '/api/settings') {
    body = {
      vaultVersion: 1,
      activeProviderId: 'provider-with-an-intentionally-long-stable-identifier',
      activeProviderSource: 'Default',
      providers: [
        {
          providerId: 'provider-with-an-intentionally-long-stable-identifier',
          isActive: true,
          hasStoredKey: true,
          keyMaskPrefix: 'abcd',
          keyMaskSuffix: 'wxyz',
          keyPlaintextLength: 40,
          keyUpdatedUtc: '2026-10-06T08:15:00Z',
          baseUrl:
            'https://provider.example.test/an/intentionally/long/api/base/path/for/layout/evidence',
          model: 'model-with-an-intentionally-long-versioned-identifier-for-layout-evidence',
          supportsNativeToolCalling: true,
          extraParameters: null,
          profileUpdatedUtc: '2026-10-06T08:15:00Z',
        },
      ],
    };
  } else {
    await route.continue();
    return;
  }

  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function query(locale: Locale): string {
  return locale === 'pseudo' ? '?bops-test-locale=pseudo' : '';
}

async function setLocale(page: Page, locale: Locale): Promise<void> {
  await page.evaluate((language) => {
    if (language === 'pseudo') localStorage.removeItem('bops-ui-language');
    else localStorage.setItem('bops-ui-language', language);
  }, locale);
}

async function ready(page: Page): Promise<void> {
  await expect(page.locator('main h1, form h1').first()).toBeVisible();
  await page
    .locator('body')
    .evaluate((body) => body.getAnimations().forEach((animation) => animation.finish()));
}

type Overflow = { context: string; clientWidth: number; scrollWidth: number };

async function assertGeometry(
  page: Page,
  surface: string,
  locale: Locale,
  width: number,
): Promise<number> {
  const failures = await page.locator('body').evaluate<Overflow[]>((body) => {
    const describe = (element: Element): string => {
      const html = element as HTMLElement;
      const label = html.getAttribute('aria-label') ?? html.getAttribute('data-testid');
      const heading = html
        .querySelector(':scope > h1, :scope > h2, :scope > h3')
        ?.textContent?.trim();
      return [
        html.tagName.toLowerCase(),
        label && `aria/test=${label}`,
        heading && `heading=${heading.slice(0, 80)}`,
      ]
        .filter(Boolean)
        .join(' ');
    };
    const candidates = [
      document.documentElement,
      document.body,
      ...body.querySelectorAll(
        'main, aside, form, section, article, [role="alert"], [role="status"]',
      ),
    ].filter(
      (element, index, all) =>
        all.indexOf(element) === index && (element as HTMLElement).offsetParent !== null,
    );
    return candidates
      .filter((element) => element.tagName !== 'NAV')
      .map((element) => ({
        context: describe(element),
        clientWidth: (element as HTMLElement).clientWidth,
        scrollWidth: (element as HTMLElement).scrollWidth,
      }))
      .filter((measurement) => measurement.scrollWidth > measurement.clientWidth + 1);
  });

  expect(
    failures,
    `${surface} / ${locale} / ${width}px overflow:\n${JSON.stringify(failures, null, 2)}`,
  ).toEqual([]);
  return await page
    .locator('body')
    .evaluate(
      (body) =>
        [
          document.documentElement,
          document.body,
          ...body.querySelectorAll(
            'main, aside, form, section, article, [role="alert"], [role="status"]',
          ),
        ].filter(
          (element, index, all) =>
            all.indexOf(element) === index &&
            (element as HTMLElement).offsetParent !== null &&
            element.tagName !== 'NAV',
        ).length,
    );
}

async function screenshot(
  page: Page,
  locale: Locale,
  width: number,
  surface: string,
): Promise<void> {
  if (locale === 'pseudo') return;
  const directory = join(
    process.cwd(),
    'test-results',
    'harden-12',
    'screenshots',
    locale,
    String(width),
  );
  mkdirSync(directory, { recursive: true });
  await page.screenshot({
    path: join(directory, `${surface}.png`),
    fullPage: true,
    animations: 'disabled',
  });
}

async function openSurface(page: Page, locale: Locale, surface: string): Promise<void> {
  const suffix = query(locale);
  switch (surface) {
    case 'dashboard-list':
      await page.goto(`/dashboard${suffix}`);
      await ready(page);
      await expect(page.locator('#task-status-filter')).toBeVisible();
      break;
    case 'dashboard-detail':
      await page.goto(`/dashboard?task=${TASK_ID}${suffix ? `&${suffix.slice(1)}` : ''}`);
      await ready(page);
      await expect(page.getByText(LONG_GOAL).last()).toBeVisible();
      break;
    case 'delegations-list':
      await page.goto(`/delegations${suffix}`);
      await ready(page);
      await expect(page.getByText(delegation.objective).first()).toBeVisible();
      break;
    case 'delegation-detail':
      await page.goto(`/delegations${suffix}`);
      await ready(page);
      await page.getByText(delegation.objective).first().click();
      await expect(page.getByText(DELEGATION_ID)).toBeVisible();
      break;
    case 'approvals':
      await page.goto(`/approvals${suffix}`);
      await ready(page);
      await expect(page.getByText(/sensitive-maintenance-operation/)).toBeVisible();
      break;
    case 'plugins':
      await page.goto(`/plugins${suffix}`);
      await ready(page);
      await page.getByText(LONG_PLUGIN).first().click();
      await expect(page.getByRole('heading', { name: LONG_PLUGIN })).toBeVisible();
      break;
    case 'settings':
      await page.goto(`/settings${suffix}`);
      await ready(page);
      await expect(
        page.getByText('provider-with-an-intentionally-long-stable-identifier').first(),
      ).toBeVisible();
      break;
    default:
      throw new Error(`Unknown authenticated surface: ${surface}`);
  }
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Sign in to bOps' })).toBeVisible();
  await page.locator('#api-key').fill(KEY);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('link', { name: 'Dashboard' })).toBeVisible();
}

async function newLoginPage(browser: Browser): Promise<{ page: Page; close: () => Promise<void> }> {
  const context = await browser.newContext();
  const page = await context.newPage();
  return { page, close: () => context.close() };
}

test('E2E-12: localized primary surfaces have no container or page overflow', async ({
  page,
  browser,
  browserName,
}) => {
  test.skip(
    browserName !== 'chromium',
    'E2E-12 is a deterministic geometry and screenshot gate; it runs once in installed Chrome.',
  );
  test.setTimeout(240_000);
  await page.route('**/api/**', layoutFixture);
  await signIn(page);

  const surfaces = [
    'dashboard-list',
    'dashboard-detail',
    'delegations-list',
    'delegation-detail',
    'approvals',
    'plugins',
    'settings',
  ] as const;
  let cases = 0;
  let geometryAssertions = 0;
  let screenshots = 0;

  for (const locale of LOCALES) {
    await setLocale(page, locale);
    for (const width of WIDTHS) {
      await page.setViewportSize({ width, height: 900 });
      for (const surface of surfaces) {
        await openSurface(page, locale, surface);
        geometryAssertions += await assertGeometry(page, surface, locale, width);
        await screenshot(page, locale, width, surface);
        cases++;
        if (locale !== 'pseudo') screenshots++;
      }
    }
  }

  const login = await newLoginPage(browser);
  try {
    for (const locale of LOCALES) {
      await login.page.goto('/');
      await setLocale(login.page, locale);
      for (const width of WIDTHS) {
        await login.page.setViewportSize({ width, height: 900 });
        await login.page.goto(`/${query(locale)}`);
        await ready(login.page);
        geometryAssertions += await assertGeometry(login.page, 'login', locale, width);
        await screenshot(login.page, locale, width, 'login');
        cases++;
        if (locale !== 'pseudo') screenshots++;
      }
    }
  } finally {
    await login.close();
  }

  expect(cases).toBe(8 * WIDTHS.length * LOCALES.length);
  expect(screenshots).toBe(8 * WIDTHS.length * 2);
  console.log(
    `E2E-12 summary cases=${cases} geometryAssertions=${geometryAssertions} screenshots=${screenshots}`,
  );
});

test('E2E-12: Dashboard 320/288 columns, truncation metadata, and mobile navigation regressions', async ({
  page,
  browserName,
}) => {
  test.skip(
    browserName !== 'chromium',
    'E2E-12 is a deterministic geometry gate; it runs once in installed Chrome.',
  );
  await page.route('**/api/**', layoutFixture);
  await signIn(page);
  await page.setViewportSize({ width: 1024, height: 900 });

  for (const locale of LOCALES) {
    await setLocale(page, locale);
    await openSurface(page, locale, 'dashboard-detail');
    const list = page.locator('section').filter({ has: page.locator('#task-status-filter') });
    await list.evaluate((element) => {
      const grid = element.parentElement!;
      grid.style.display = 'block';
    });
    for (const width of [320, 288]) {
      await list.evaluate((element, columnWidth) => {
        (element as HTMLElement).style.width = `${columnWidth}px`;
      }, width);
      const failure = await list.evaluate((element) => ({
        container: { client: element.clientWidth, scroll: element.scrollWidth },
        select: {
          client: element.querySelector('select')!.clientWidth,
          scroll: element.querySelector('select')!.scrollWidth,
        },
        row: {
          client: element.querySelector('li button')!.clientWidth,
          scroll: element.querySelector('li button')!.scrollWidth,
        },
      }));
      expect(
        failure.container.scroll,
        `Dashboard ${locale} ${width}px list container`,
      ).toBeLessThanOrEqual(failure.container.client + 1);
      expect(
        failure.select.scroll,
        `Dashboard ${locale} ${width}px status filter`,
      ).toBeLessThanOrEqual(failure.select.client + 1);
      expect(failure.row.scroll, `Dashboard ${locale} ${width}px task row`).toBeLessThanOrEqual(
        failure.row.client + 1,
      );
    }

    const badge = page.locator('bops-status-badge span').first();
    const badgeLabel = await badge.getAttribute('aria-label');
    expect(badgeLabel).toBeTruthy();
    expect(await badge.getAttribute('title')).toBe(badgeLabel);
    const goal = page.getByTitle(LONG_GOAL).first();
    expect(await goal.getAttribute('aria-label')).toBe(LONG_GOAL);
  }

  await setLocale(page, 'pseudo');
  await page.setViewportSize({ width: 375, height: 900 });
  await openSurface(page, 'pseudo', 'dashboard-list');
  const nav = page.locator('nav');
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    375 + 1,
  );
  for (const entry of await nav.getByRole('link').all()) {
    await entry.focus();
    await entry.evaluate((element) =>
      element.scrollIntoView({ block: 'nearest', inline: 'nearest' }),
    );
    await expect(entry).toBeFocused();
    const visible = await entry.evaluate((element) => {
      const item = element.getBoundingClientRect();
      const container = element.closest('nav')!.getBoundingClientRect();
      return (
        item.left >= container.left - 1 &&
        item.right <= container.right + 1 &&
        item.left >= -1 &&
        item.right <= innerWidth + 1
      );
    });
    expect(visible, `Focused navigation entry ${await entry.textContent()} remains visible`).toBe(
      true,
    );
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      375 + 1,
    );
  }

  await openSurface(page, 'pseudo', 'plugins');
  const longPlugin = page.getByTitle(LONG_PLUGIN).first();
  expect(await longPlugin.getAttribute('aria-label')).toBe(LONG_PLUGIN);
});
