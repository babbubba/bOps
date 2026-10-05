// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { BrowserContext, Page, expect, test } from '@playwright/test';

/**
 * ADR-0043 §18.2, automated part: the browser's own cookie row for `__Host-bops_session` after a default sign-in and after a
 * "Keep me signed in" sign-in, printed with the value redacted, plus the names of everything in Local and Session Storage.
 * The DevTools screenshot itself remains a manual step.
 */

const KEY = process.env['BOPS_E2E_API_KEY']!;

async function signIn(page: Page, keep: boolean): Promise<void> {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Sign in to bOps' })).toBeVisible();
  await page.locator('#api-key').fill(KEY);
  if (keep) await page.locator('#keep-signed-in').check();
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('link', { name: 'Dashboard' })).toBeVisible();
}

async function redactedRow(context: BrowserContext) {
  const cookie = (await context.cookies('http://localhost:4200')).find(
    (candidate) => candidate.name === '__Host-bops_session',
  );
  expect(cookie, 'the session cookie is set').toBeDefined();
  const { value, ...metadata } = cookie!;
  expect(value.length).toBe(43);
  return { ...metadata, value: '<redacted>' };
}

// The default (browser-session cookie) row is asserted and printed by browser-session.e2e.ts step 3; one sign-in fewer keeps the run
// well inside the 10-per-minute sign-in limit.
test('persistent cookie row and storage after a "Keep me signed in" sign-in', async ({
  page,
  context,
  browserName,
}) => {
  const before = Date.now() / 1000;
  await signIn(page, true);
  const row = await redactedRow(context);

  expect(row.httpOnly).toBe(true);
  expect(row.secure).toBe(true);
  expect(row.sameSite).toBe('Strict');
  expect(row.path).toBe('/');
  expect(row.domain).toBe('localhost');
  // Max-Age = the remaining absolute lifetime (12 h by default).
  expect(row.expires).toBeGreaterThan(before + 12 * 3600 - 120);
  expect(row.expires).toBeLessThan(before + 12 * 3600 + 120);

  const storage = await page.evaluate(() => ({
    local: Object.keys(localStorage),
    session: Object.keys(sessionStorage),
  }));
  for (const name of [...storage.local, ...storage.session])
    expect(['bops-ui-language', 'bops-ui-theme']).toContain(name);

  console.log(
    `E2E-8 evidence ${browserName} keep=true: ${JSON.stringify(row)} storage=${JSON.stringify(storage)}`,
  );
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Sign in to bOps' })).toBeVisible();
});
