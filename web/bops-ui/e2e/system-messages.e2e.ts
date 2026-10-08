// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { expect, test } from '@playwright/test';

/**
 * ADR-0049 smoke on the real topology: the authenticated shell links the System Messages page, the page reads the real
 * `GET /api/system-messages` through the browser session, applies a server-side text filter, and resets. What messages exist depends
 * on the host (a missing Docker daemon or SearXNG endpoint announces itself at boot), so the assertions are about behaviour, not text.
 */

const KEY = process.env['BOPS_E2E_API_KEY']!;
const EMPTY = 'No system messages match the selected filters.';

test('E2E-13: the System Messages page loads through the session, filters on the server and resets', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Sign in to bOps' })).toBeVisible();
  await page.locator('#api-key').fill(KEY);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('link', { name: 'Dashboard' })).toBeVisible();

  await page.getByRole('link', { name: 'System messages' }).click();
  await expect(page).toHaveURL(/\/system-messages$/);
  await expect(page.getByRole('heading', { name: 'System messages', level: 1 })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Loading' })).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0);

  const queries: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/system-messages')) queries.push(new URL(request.url()).search);
  });

  await page.getByLabel('Text contained in message').fill('zzz-no-such-message-zzz');
  await page.getByRole('button', { name: 'Apply' }).click();
  await expect(page.getByRole('status')).toHaveText(EMPTY);
  expect(queries.at(-1)).toContain('contains=zzz-no-such-message-zzz');
  expect(queries.at(-1)).toContain('pageSize=50');

  await page.getByRole('button', { name: 'Reset' }).click();
  await expect(page.getByLabel('Text contained in message')).toHaveValue('');
  await expect.poll(() => queries.at(-1)).not.toContain('contains=');
  await expect(page.getByRole('alert')).toHaveCount(0);
});
