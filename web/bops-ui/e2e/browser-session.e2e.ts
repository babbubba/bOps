// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import {
  APIRequestContext,
  BrowserContext,
  Page,
  expect,
  request as playwrightRequest,
  test,
} from '@playwright/test';
import { Server, createServer } from 'node:http';
import { connect } from 'node:net';

/**
 * E2E-8 (ADR-0043 §18.1). One scenario per browser against the real topology: sign in, nothing secret in the page, a running
 * task, F5, the session and the watch come back, the watch follows the task to its end, sign out, the old cookie replays as 401,
 * forged same-site and cross-site requests create nothing, a server-side revocation shows "Session expired" and signing in again
 * restores the watch, and a Bearer client is unaffected. Assertion messages never contain the key or the cookie value.
 */

const KEY = process.env['BOPS_E2E_API_KEY']!;
const COOKIE = '__Host-bops_session';
const API = 'http://localhost:5080';
const TASK_ID = /[?&]task=([0-9a-f-]{36})(?:&|$)/;

/** One line to the test host's loopback control channel (tests/bOps.Api.E2EHost). */
function control(command: 'release' | 'waiting' | 'revoke'): Promise<string> {
  return new Promise((resolve, reject) => {
    const socket = connect(5099, '127.0.0.1', () => socket.write(`${command}\n`));
    let answer = '';
    socket.setEncoding('ascii');
    socket.on('data', (chunk: string) => {
      answer += chunk;
      if (answer.includes('\n')) {
        socket.end();
        resolve(answer.trim());
      }
    });
    socket.on('error', reject);
  });
}

/** Waits until the gated model holds a call, so a release always lands on the step the test means. */
async function modelHeld(): Promise<void> {
  await expect.poll(async () => control('waiting'), { timeout: 30_000 }).toBe('ok 1');
}

async function releaseStep(): Promise<void> {
  await modelHeld();
  expect(await control('release')).toBe('ok');
}

/** A step of the selected task, as the detail panel titles it ("Step 1 · system.cpu"). */
function stepTitle(page: Page, number: number, tool: string) {
  return page
    .locator('ol > li')
    .filter({ hasText: `Step ${number}` })
    .filter({ hasText: tool })
    .first();
}

/** The status badge of the selected task's detail panel. */
function detailStatus(page: Page) {
  return page.locator('section:last-child bops-status-badge').first();
}

async function signIn(page: Page): Promise<void> {
  await expect(page.getByRole('heading', { name: 'Sign in to bOps' })).toBeVisible();
  await page.locator('#api-key').fill(KEY);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('link', { name: 'Dashboard' })).toBeVisible();
  await expect(page.locator('#api-key')).toHaveCount(0);
}

async function sessionCookie(context: BrowserContext) {
  return (await context.cookies('http://localhost:4200')).find((cookie) => cookie.name === COOKIE);
}

async function bearer(): Promise<APIRequestContext> {
  return playwrightRequest.newContext({ extraHTTPHeaders: { Authorization: `Bearer ${KEY}` } });
}

async function taskCount(api: APIRequestContext): Promise<number> {
  let total = 0;
  for (const status of [0, 1, 2, 3, 4, 5, 6, 7]) {
    const response = await api.get(`${API}/api/agents/tasks?status=${status}`);
    expect(response.status()).toBe(200);
    total += ((await response.json()) as unknown[]).length;
  }

  return total;
}

/** A page on another origin, served for real on the loopback address, with an iframe a forged form can target. */
function serveAttacker(port: number): Promise<Server> {
  return new Promise((resolve, reject) => {
    const server = createServer((_, response) => {
      response.writeHead(200, { 'Content-Type': 'text/html' });
      response.end('<!doctype html><title>forgery</title><iframe name="sink"></iframe>');
    });
    server.once('error', reject);
    server.listen(port, '127.0.0.1', () => resolve(server));
  });
}

function secretFree(text: string, secrets: string[], where: string): void {
  for (const secret of secrets) {
    expect(text.includes(secret), `a secret was found in ${where}`).toBe(false);
  }
}

test('E2E-8: browser session across reload, watch restoration, CSRF, logout and revocation', async ({
  page,
  context,
  browserName,
}) => {
  const meStatuses: number[] = [];
  page.on('response', (response) => {
    if (new URL(response.url()).pathname === '/api/session/me') meStatuses.push(response.status());
  });
  const api = await bearer();

  // 1–2. Open the UI and sign in with the key (checkbox off).
  await page.goto('/');
  await signIn(page);

  // 3. Nothing secret in the page: storage, IndexedDB names, URL, history state, document.cookie. The cookie is host-only,
  // HttpOnly, Secure, SameSite=Strict, Path=/ and a browser-session cookie.
  const cookie = await sessionCookie(context);
  expect(cookie, 'the session cookie is set').toBeDefined();
  expect(cookie!.httpOnly).toBe(true);
  expect(cookie!.secure).toBe(true);
  expect(cookie!.sameSite).toBe('Strict');
  expect(cookie!.path).toBe('/');
  expect(cookie!.domain).toBe('localhost');
  expect(cookie!.expires).toBe(-1);
  expect(
    /^[A-Za-z0-9_-]{43}$/.test(cookie!.value),
    'the cookie value is a 43-character base64url token',
  ).toBe(true);
  const visible = await page.evaluate(async () => ({
    local: JSON.stringify({ ...localStorage }),
    session: JSON.stringify({ ...sessionStorage }),
    idb: JSON.stringify(((await indexedDB.databases?.()) ?? []).map((database) => database.name)),
    url: location.href,
    history: JSON.stringify(history.state),
    cookie: document.cookie,
  }));
  secretFree(
    Object.values(visible).join('\n'),
    [KEY, cookie!.value],
    'browser storage, URL, history or document.cookie',
  );
  expect(visible.cookie.includes(COOKIE)).toBe(false);
  console.log(
    `E2E-8 evidence ${browserName} keep=false: ${JSON.stringify({ ...cookie!, value: '<redacted>' })}`,
  );

  // 4. Start a task; the gated model holds it Running; the URL carries ?task=<id>.
  const tasksBefore = await taskCount(api);
  await page.locator('#goal').fill(`E2E-8 ${browserName} watch me`);
  await page.getByRole('button', { name: 'Start task' }).click();
  await expect(page).toHaveURL(TASK_ID);
  const taskId = TASK_ID.exec(page.url())![1];
  expect(await taskCount(api)).toBe(tasksBefore + 1);

  // 5. Release one model step; the UI shows step 1.
  await releaseStep();
  await expect(stepTitle(page, 1, 'system.cpu')).toBeVisible();

  // 6–8. F5: no sign-in screen, GET /api/session/me answers 200, the same task is selected and its watch polls again.
  meStatuses.length = 0;
  const polled = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === `/api/agents/tasks/${taskId}` &&
      response.status() === 200,
  );
  await page.reload();
  await expect(page.getByRole('link', { name: 'Dashboard' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Sign in to bOps' })).toHaveCount(0);
  expect(meStatuses).toContain(200);
  await expect(page).toHaveURL(new RegExp(`task=${taskId}`));
  await polled;
  await expect(page.getByText(`E2E-8 ${browserName} watch me`).first()).toBeVisible();
  await expect(stepTitle(page, 1, 'system.cpu')).toBeVisible();

  // 9. The restored watch follows the task without interaction, to its end, then stops.
  await releaseStep();
  await expect(stepTitle(page, 2, 'system.memory')).toBeVisible();
  await releaseStep();
  await expect(detailStatus(page)).toContainText('Completed');
  let pollsAfterEnd = 0;
  const countPolls = (response: { url(): string }) => {
    if (new URL(response.url()).pathname === `/api/agents/tasks/${taskId}`) pollsAfterEnd++;
  };
  await page.waitForTimeout(500);
  page.on('response', countPolls);
  await page.waitForTimeout(6_000);
  page.off('response', countPolls);
  expect(pollsAfterEnd).toBe(0);

  // 10. Sign out: the sign-in screen, and the cookie is gone from the jar.
  const old = cookie!.value;
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Sign in to bOps' })).toBeVisible();
  expect(await sessionCookie(context)).toBeUndefined();

  // 11. Replaying the old cookie is refused.
  const replay = await playwrightRequest.newContext();
  const replayed = await replay.get('http://localhost:4200/api/session/me', {
    headers: { Cookie: `${COOKIE}=${old}` },
  });
  expect(replayed.status()).toBe(401);
  await replay.dispose();

  // 12. CSRF: signed in again, forged requests from a same-site page (another localhost port) and a cross-site page create nothing.
  // The attacker pages are served by real local HTTP servers, so the browsers apply their own cookie, CORS and local-network rules.
  await signIn(page);
  const forgedBefore = await taskCount(api);
  const forged: { origin: string; custom: boolean; status: number }[] = [];
  const attackers = await Promise.all([serveAttacker(4300), serveAttacker(4301)]);
  try {
    for (const origin of ['http://localhost:4300', 'http://127.0.0.1:4301']) {
      const attacker = await context.newPage();
      attacker.on('response', (response) => {
        if (
          response.url() === 'http://localhost:4200/api/agents/tasks' &&
          response.request().method() === 'POST'
        ) {
          forged.push({
            origin,
            custom: 'x-bops-request' in response.request().headers(),
            status: response.status(),
          });
        }
      });
      await attacker.goto(`${origin}/`);
      await attacker.evaluate(async () => {
        const target = 'http://localhost:4200/api/agents/tasks';
        await fetch(target, {
          method: 'POST',
          mode: 'no-cors',
          credentials: 'include',
          headers: { 'Content-Type': 'text/plain' },
          body: '{"goal":"forged"}',
        }).catch(() => undefined);
        await fetch(target, {
          method: 'POST',
          mode: 'cors',
          credentials: 'include',
          headers: { 'Content-Type': 'application/json', 'X-bOps-Request': '1' },
          body: '{"goal":"forged"}',
        }).catch(() => undefined);
        const form = document.createElement('form');
        form.method = 'POST';
        form.action = target;
        form.enctype = 'text/plain';
        form.target = 'sink';
        const field = document.createElement('input');
        field.name = '{"goal":"forged","x":"';
        field.value = '"}';
        form.appendChild(field);
        document.body.appendChild(form);
        form.submit();
      });
      await attacker.waitForTimeout(2_000);
      await attacker.close();
    }
  } finally {
    await Promise.all(attackers.map((server) => new Promise((resolve) => server.close(resolve))));
  }

  const evidence = JSON.stringify(forged);
  expect(await taskCount(api), `forged requests created a task: ${evidence}`).toBe(forgedBefore);
  // Whatever reached the API was refused with no effect: 403 by the CSRF gate when the same-site page's request carried the cookie;
  // a 4xx without any credential when the cross-site page's did not (401, or 415 from routing's content-type match for the JSON
  // endpoint, which answers before authentication). A request carrying the custom header never reaches the API: its preflight gets
  // no CORS answer.
  for (const record of forged) {
    expect(
      record.status >= 400 && record.status < 500,
      `a forged request was not refused: ${evidence}`,
    ).toBe(true);
    if (record.origin === 'http://localhost:4300') {
      expect(
        record.status,
        `a same-site forgery was not refused by the CSRF gate: ${evidence}`,
      ).toBe(403);
    }

    expect(
      record.custom,
      `a forged request carrying X-bOps-Request reached the API: ${evidence}`,
    ).toBe(false);
  }
  expect(
    forged.some((record) => record.origin === 'http://localhost:4300' && record.status === 403),
    `no same-site refusal: ${evidence}`,
  ).toBe(true);
  console.log(`E2E-8 ${browserName} forged requests: ${evidence}`);

  // 13. Server-side revocation while a task is watched: the UI says the session expired; signing in again restores the watch.
  await page.locator('#goal').fill(`E2E-8 ${browserName} second task`);
  await page.getByRole('button', { name: 'Start task' }).click();
  await expect(page).toHaveURL(TASK_ID);
  const secondId = TASK_ID.exec(page.url())![1];
  await releaseStep();
  await expect(stepTitle(page, 1, 'system.cpu')).toBeVisible();

  expect(await control('revoke')).toMatch(/^ok [1-9]/);
  await expect(
    page.getByText(
      'Your session expired. Sign in again: the task you were following will start updating again.',
    ),
  ).toBeVisible();
  await expect(page).toHaveURL(new RegExp(`task=${secondId}`));
  await signIn(page);
  await expect(page.getByText(`E2E-8 ${browserName} second task`).first()).toBeVisible();
  await releaseStep();
  await expect(stepTitle(page, 2, 'system.memory')).toBeVisible();
  await releaseStep();
  await expect(detailStatus(page)).toContainText('Completed');

  // Bearer clients are unaffected: identity, and an unsafe request without any browser header.
  const me = await api.get(`${API}/api/session/me`);
  expect(me.status()).toBe(200);
  expect(((await me.json()) as { id: string }).id).toBe('e2e-operator');
  const cancel = await api.delete(`${API}/api/agents/tasks/00000000-0000-0000-0000-000000000001`);
  expect(cancel.status()).toBe(404);
  const proxied = await api.get('http://localhost:4200/api/session/me');
  expect(proxied.status()).toBe(200);
  await api.dispose();
});
