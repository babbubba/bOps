// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { defineConfig } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/**
 * E2E-8 (ADR-0043 §18.1): the supported topology, for real — `ng serve` with the repository's `proxy.conf.json` at
 * http://localhost:4200 in front of the real bOps.Api composition on Kestrel at http://localhost:5080 (the test-only
 * `tests/bOps.Api.E2EHost`, whose only substitution is a gated fake model). The API key is generated here, per run, handed to the
 * host and the test through the environment, and never printed. Tracing, video and HAR are off: they would record request
 * headers, i.e. the session cookie. E2E-9 and E2E-10 (ADR-0044 §21) run on the same topology and state directory, with the real
 * `bops` CLI writing the API's `policy.yaml`.
 */
process.env['BOPS_E2E_API_KEY'] ??= randomBytes(32).toString('base64url');
process.env['BOPS_E2E_STATE_DIR'] ??= mkdtempSync(join(tmpdir(), 'bops-e2e-8-'));

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  outputDir: './test-results',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 240_000,
  expect: { timeout: 20_000 },
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:4200',
    trace: 'off',
    video: 'off',
    screenshot: 'off',
    locale: 'en-US',
  },
  projects: [
    // The installed Google Chrome, as in the ADR-0043 topology measurement.
    { name: 'chrome', use: { browserName: 'chromium', channel: 'chrome' } },
    { name: 'firefox', use: { browserName: 'firefox' } },
  ],
  webServer: [
    {
      command:
        'dotnet run --project ../../tests/bOps.Api.E2EHost/bOps.Api.E2EHost.csproj --no-launch-profile',
      url: 'http://localhost:5080/api/session/me',
      reuseExistingServer: false,
      timeout: 300_000,
      stdout: 'ignore',
      stderr: 'pipe',
      env: {
        BOPS_E2E_API_KEY: process.env['BOPS_E2E_API_KEY']!,
        BOPS_E2E_STATE_DIR: process.env['BOPS_E2E_STATE_DIR']!,
      },
    },
    {
      command: 'npx ng serve --port 4200',
      url: 'http://localhost:4200',
      reuseExistingServer: false,
      timeout: 300_000,
      stdout: 'ignore',
      stderr: 'pipe',
    },
  ],
});
