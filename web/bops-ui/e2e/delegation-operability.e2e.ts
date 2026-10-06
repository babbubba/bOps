// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { APIRequestContext, Page, expect, request as playwrightRequest, test } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { connect } from 'node:net';
import { join, resolve } from 'node:path';

/**
 * E2E-9 and E2E-10 (ADR-0044 §21) against the real topology of E2E-8: `ng serve` with the repository's proxy in front of the real
 * bOps.Api composition (tests/bOps.Api.E2EHost, whose only substitutions are a fake model and one test Skill package), and the real
 * `bops` CLI writing the very `policy.yaml` the API reads. The API reads that file only at start, so after changing it the test
 * restarts the API in the host process (the control channel's `restart`), as an operator restarts the service. Nothing here
 * prints the key, the cookie or the policy file.
 */

const KEY = process.env['BOPS_E2E_API_KEY']!;
const STATE = process.env['BOPS_E2E_STATE_DIR']!;
const POLICY = join(STATE, 'policy.yaml');
const AUDIT = join(STATE, 'audit.jsonl');
const API = 'http://localhost:5080';
const REPO = resolve(process.cwd(), '..', '..');
const CLI_PROJECT = join(REPO, 'src', 'core', 'bOps.Cli', 'bOps.Cli.csproj');
const CLI = join(REPO, 'src', 'core', 'bOps.Cli', 'bin', 'Debug', 'net10.0', 'bops.dll');
const ROLES = ['Discovery', 'Diagnostic', 'Remediation', 'Verification'];

/** All four roles: the Remediation profile allows `e2e.skill`/`e2e.configure` on target `local` only. */
const FULL_POLICY = `defaults:
  read: automatic
  low: automatic
  medium: approval
  high: approval
  critical: forbidden
delegation:
  roles:
    discovery:
      tools: ["system.cpu"]
      maxRisk: read
      maxBlastRadius: single
      targets: [local]
      environments: [local]
      maxSteps: 15
      maxTokens: 150000
      maxDuration: 00:30:00
    diagnostic:
      skills: [e2e.skill]
      capabilities: [e2e.configure]
      tools: ["system.cpu"]
      maxRisk: read
      maxBlastRadius: single
      targets: [local]
      environments: [local]
      maxSteps: 15
      maxTokens: 150000
      maxDuration: 00:30:00
    remediation:
      skills: [e2e.skill]
      capabilities: [e2e.configure]
      tools: ["system.cpu"]
      maxRisk: high
      maxBlastRadius: single
      targets: [local]
      environments: [local]
      maxSteps: 6
      maxDuration: 00:05:00
    verification:
      tools: ["system.cpu"]
      maxRisk: read
      maxBlastRadius: single
      targets: [local]
      environments: [local]
      maxSteps: 6
      maxDuration: 00:05:00
`;

interface RoleReadiness {
  role: string;
  state: string;
  reasonCode: string;
  reason: string | null;
  dimension: string | null;
}

interface Readiness {
  remediation: boolean;
  ready: boolean;
  roles: RoleReadiness[];
}

function control(command: 'calls' | 'restart'): Promise<string> {
  return new Promise((resolvePromise, reject) => {
    const socket = connect(5099, '127.0.0.1', () => socket.write(`${command}\n`));
    let answer = '';
    socket.setEncoding('ascii');
    socket.on('data', (chunk: string) => {
      answer += chunk;
      if (answer.includes('\n')) {
        socket.end();
        resolvePromise(answer.trim());
      }
    });
    socket.on('error', reject);
  });
}

async function modelCalls(): Promise<number> {
  const answer = await control('calls');
  expect(answer).toMatch(/^ok \d+$/);
  return Number(answer.slice(3));
}

/** Restarts the API in the host so it reads `policy.yaml` again, and waits until it answers. */
async function restartApi(api: APIRequestContext): Promise<void> {
  expect(await control('restart')).toBe('ok');
  await expect.poll(async () => (await api.get(`${API}/api/session/me`)).status(), { timeout: 60_000 }).toBe(200);
}

/** The real CLI, from a fresh working directory, on the API's own policy file. No model provider is configured at all. */
function cli(...args: string[]): { status: number | null; stdout: string; stderr: string } {
  const cwd = join(STATE, 'cli');
  mkdirSync(cwd, { recursive: true });
  const env: NodeJS.ProcessEnv = { ...process.env, Policy__FilePath: POLICY, OTEL_SDK_DISABLED: 'true' };
  for (const name of Object.keys(env)) {
    if (name.toLowerCase().startsWith('modelprovider__')) delete env[name];
  }

  const result = spawnSync('dotnet', [CLI, ...args], { cwd, env, encoding: 'utf8', timeout: 120_000 });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

async function readiness(api: APIRequestContext, remediation: boolean): Promise<Readiness> {
  const response = await api.get(`${API}/api/delegations/readiness?remediation=${remediation}`);
  expect(response.status()).toBe(200);
  return (await response.json()) as Readiness;
}

async function waitForRun(api: APIRequestContext, id: string, status: string): Promise<Record<string, unknown>> {
  let run: Record<string, unknown> = {};
  await expect
    .poll(
      async () => {
        run = (await (await api.get(`${API}/api/delegations/${id}`)).json()) as Record<string, unknown>;
        return run['status'];
      },
      { timeout: 60_000 },
    )
    .toBe(status);
  return run;
}

/** The audit events appended after `from` lines, decoded from the hash-chained envelopes. */
function auditEventsAfter(from: number): Record<string, unknown>[] {
  return readFileSync(AUDIT, 'utf8')
    .split('\n')
    .filter((line) => line.trim() !== '')
    .slice(from)
    .map((line) => {
      const envelope = JSON.parse(line) as Record<string, unknown>;
      return JSON.parse((envelope['EventJson'] ?? envelope['eventJson']) as string) as Record<string, unknown>;
    });
}

function auditLines(): number {
  return existsSync(AUDIT) ? readFileSync(AUDIT, 'utf8').split('\n').filter((line) => line.trim() !== '').length : 0;
}

function field(event: Record<string, unknown>, name: string): unknown {
  return event[name] ?? event[name[0].toLowerCase() + name.slice(1)];
}

async function signInTo(page: Page, path: string): Promise<void> {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Sign in to bOps' })).toBeVisible();
  await page.locator('#api-key').fill(KEY);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('link', { name: 'Dashboard' })).toBeVisible();
  await page.goto(path);
}

function roleItem(page: Page, role: string) {
  return page.getByTestId('readiness').locator(`li[data-role="${role}"]`);
}

/** The panel shows the four roles in order with the server's own state and reason (E2E-10 "same server reasons"). */
async function expectPanelMatches(page: Page, server: Readiness): Promise<void> {
  await expect(page.getByTestId('readiness').locator('li[data-role]')).toHaveCount(4);
  expect(server.roles.map((role) => role.role)).toEqual(ROLES);
  for (const role of server.roles) {
    await expect(roleItem(page, role.role)).toHaveAttribute('data-state', role.state);
    if (role.reason) await expect(roleItem(page, role.role)).toContainText(role.reason);
  }
}

function startButton(page: Page) {
  return page.getByRole('button', { name: 'Start', exact: true });
}

test.describe.configure({ mode: 'serial' });

let api: APIRequestContext;

test.beforeAll(async () => {
  api = await playwrightRequest.newContext({ extraHTTPHeaders: { Authorization: `Bearer ${KEY}` } });
  const build = spawnSync('dotnet', ['build', CLI_PROJECT, '-v', 'q', '-nologo'], { encoding: 'utf8', timeout: 300_000 });
  expect(build.status, 'the bops CLI builds').toBe(0);
});

test.afterAll(async () => {
  // Leave the host as E2E-8 expects it: no policy file, the built-in default.
  rmSync(POLICY, { force: true });
  await restartApi(api);
  await api.dispose();
});

test('E2E-9: after profiles init --read-only, a diagnosis-only delegation completes DiagnosisCompleted using only Read tools', async ({
  browserName,
}) => {
  test.skip(browserName !== 'chromium', 'E2E-9 drives the CLI and the API only; it runs once.');
  rmSync(POLICY, { force: true });
  await restartApi(api);

  // A fresh configuration: readiness is not ready, and says why per role.
  const before = cli('delegate', 'readiness');
  expect(before.status).toBe(2);
  expect(before.stdout).toContain('Discovery role: no usable profile is configured.');

  // Print for review: nothing is written.
  const printed = cli('delegate', 'profiles', 'init', '--read-only');
  expect(printed.status).toBe(0);
  expect(printed.stdout).toContain('discovery:');
  expect(existsSync(POLICY)).toBe(false);

  const written = cli('delegate', 'profiles', 'init', '--read-only', '--write');
  expect(written.status).toBe(0);
  const yaml = readFileSync(POLICY, 'utf8');
  expect(yaml).toContain('discovery:');
  expect(yaml).toContain('diagnostic:');
  expect(yaml).not.toContain('remediation:');
  expect(yaml).not.toContain('verification:');
  expect(yaml).toContain('"system.cpu"');

  expect(cli('delegate', 'profiles', 'check').status).toBe(0);
  expect(cli('delegate', 'readiness').status).toBe(0);
  expect(cli('delegate', 'readiness', '--remediation').status).toBe(2);

  // The API, restarted on that file, agrees.
  await restartApi(api);
  const diagnosis = await readiness(api, false);
  expect(diagnosis.ready).toBe(true);
  expect(diagnosis.roles.map((role) => [role.role, role.state])).toEqual([
    ['Discovery', 'ready'],
    ['Diagnostic', 'ready'],
    ['Remediation', 'notRequired'],
    ['Verification', 'notRequired'],
  ]);

  const auditFrom = auditLines();
  const response = await api.post(`${API}/api/delegations`, { data: { objective: 'Why is the CPU busy?' } });
  expect(response.status()).toBe(202);
  const id = ((await response.json()) as { delegationId: string }).delegationId;
  const run = await waitForRun(api, id, 'DiagnosisCompleted');
  expect((run['roles'] as { role: string }[]).map((role) => role.role)).toEqual(['Discovery', 'Diagnostic']);

  // The audit: every tool call of the run was a Read call to a tool the generated profiles list, and policy never had to
  // forbid or ask for anything.
  const events = auditEventsAfter(auditFrom);
  const toolCalls = events.filter((event) => event['eventType'] === 'toolCall');
  expect(toolCalls.length).toBeGreaterThan(0);
  for (const call of toolCalls) {
    expect(['Read', 0]).toContain(field(call, 'Risk'));
    expect(yaml).toContain(`"${String(field(call, 'Tool'))}"`);
  }

  const decisions = events.filter((event) => event['eventType'] === 'policyDecision');
  for (const decision of decisions) {
    expect(['Automatic', 0]).toContain(field(decision, 'Mode'));
  }
});

test('E2E-10: with no profiles the UI shows the server reasons per role, blocks Submit and sends no POST', async ({ page }) => {
  rmSync(POLICY, { force: true });
  await restartApi(api);
  const server = await readiness(api, false);
  expect(server.ready).toBe(false);
  expect(server.roles.map((role) => role.state)).toEqual(['missing', 'missing', 'notRequired', 'notRequired']);

  const posts: string[] = [];
  page.on('request', (request) => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.startsWith('/api/delegations')) posts.push(request.url());
  });
  const calls = await modelCalls();

  await signInTo(page, '/delegations');
  await expectPanelMatches(page, server);
  await expect(page.getByTestId('readiness-summary')).toContainText('not all usable for this type of delegation');
  await page.getByRole('textbox', { name: 'Objective' }).fill('Why is the CPU busy?');
  await expect(startButton(page)).toBeDisabled();

  expect(posts).toEqual([]);
  expect(await modelCalls()).toBe(calls);
});

test('E2E-10 remediation variant: read-only profiles, "Prepare a change" asks again and blocks Submit; a handcrafted remediation is Denied naming Remediation', async ({
  page,
}) => {
  rmSync(POLICY, { force: true });
  expect(cli('delegate', 'profiles', 'init', '--read-only', '--write').status).toBe(0);
  await restartApi(api);
  const calls = await modelCalls();

  await signInTo(page, '/delegations');
  await expectPanelMatches(page, await readiness(api, false));
  await expect(startButton(page)).toBeDisabled();
  await page.getByRole('textbox', { name: 'Objective' }).fill('Fix the service.');
  await expect(startButton(page)).toBeEnabled();

  const asked = page.waitForRequest((request) => request.url().includes('/api/delegations/readiness?remediation=true'));
  await page.getByRole('checkbox', { name: 'Prepare a change' }).check();
  await asked;
  const server = await readiness(api, true);
  expect(server.ready).toBe(false);
  expect(server.roles.map((role) => role.state)).toEqual(['ready', 'ready', 'missing', 'missing']);
  await expectPanelMatches(page, server);
  await expect(startButton(page)).toBeDisabled();

  const response = await api.post(`${API}/api/delegations`, {
    data: {
      objective: 'Fix the service.',
      remediation: { skillId: 'e2e.skill', capabilityName: 'e2e.configure', target: 'local', environment: 'local', input: { service: 'web' } },
    },
  });
  expect(response.status()).toBe(202);
  const run = await waitForRun(api, ((await response.json()) as { delegationId: string }).delegationId, 'Denied');
  const denial = run['denial'] as { dimension: string; reason: string };
  expect(denial.dimension).toBe('Profile');
  expect(denial.reason).toContain('Remediation');
  expect(await modelCalls()).toBe(calls);
});

test('E2E-10 readiness-vs-submit variant: ready for a change, but a target outside the Remediation profile is refused at submit, beside the form', async ({
  page,
}) => {
  writeFileSync(POLICY, FULL_POLICY, { encoding: 'utf8' });
  await restartApi(api);
  const server = await readiness(api, true);
  expect(server.ready).toBe(true);
  const calls = await modelCalls();

  await signInTo(page, '/delegations');
  await page.getByRole('textbox', { name: 'Objective' }).fill('Change the service setting.');
  await page.getByRole('checkbox', { name: 'Prepare a change' }).check();
  await expectPanelMatches(page, server);
  await expect(page.getByTestId('readiness-summary')).toHaveText('The required role profiles are usable for this type of delegation.');
  await expect(page.getByTestId('readiness-note')).toHaveText('The selected change, target, environment and input are validated when you submit.');

  // The change comes from the catalog, its input from the schema form.
  await page.getByRole('combobox', { name: 'Skill' }).selectOption('e2e.skill');
  await page.getByRole('combobox', { name: 'Capability' }).selectOption('e2e.configure');
  await page.getByRole('textbox', { name: 'service' }).fill('w');
  await expect(page.getByText('service needs at least 2 characters.')).toBeVisible();
  await page.getByRole('textbox', { name: 'service' }).fill('web');
  await page.getByRole('textbox', { name: 'Target' }).fill('elsewhere');
  await page.getByRole('textbox', { name: 'Environment' }).fill('local');
  await expect(startButton(page)).toBeEnabled();

  const accepted = page.waitForResponse((r) => r.request().method() === 'POST' && new URL(r.url()).pathname === '/api/delegations');
  await startButton(page).click();
  expect((await accepted).status()).toBe(202);

  await expect(page.getByTestId('submit-error')).toContainText('Not started — refused on Targets', { timeout: 30_000 });
  await expect(page.getByTestId('readiness-summary')).toHaveText('The required role profiles are usable for this type of delegation.');
  await expect(page.getByTestId('readiness-note')).toBeVisible();
  await expectPanelMatches(page, server);
  await expect(page.locator('body')).not.toContainText('executable');
  expect(await modelCalls()).toBe(calls);
});
