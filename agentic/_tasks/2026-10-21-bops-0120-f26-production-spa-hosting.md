# BOPS-0120 | F-26 — Production SPA hosting (single-host ASP.NET Core + Angular)

| | |
|---|---|
| Register | `BOPS-0120`, milestone `V1.3`, area `Release`, priority P1 |
| Origin | hardening plan finding F-26 (`agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md`, §15), pre-release packet "F-25 / F-26 status" |
| Constraints | ADR-0043 (browser session) and ADR-0044 unchanged; no new ADR |
| Authorization | operator, 2026-10-21 |
| Gates | offline tests (.NET, Angular, E2E), CI on Windows and Linux, smoke test of the published artifact on both platforms |

## Problem

The only supported UI topology is `ng serve` + `proxy.conf.json` + a separately started API. A stable V1.3 must be installable and
startable on Windows and Linux with one ASP.NET Core process, without Node.js, the Angular dev server or a dev proxy.

## Design (conservative)

- `bOps.Api` serves the compiled Angular build with the native ASP.NET Core static-file middleware and an `index.html` fallback for
  client-side routes. Nothing new is added to the dependency set.
- The fallback is limited to `GET`/`HEAD` requests that accept HTML, never matches `/api/*`, `/healthz` or a path with a file
  extension, so an unknown API path stays `404`.
- Hosting is **opt-in and configured**: `WebUi:Enabled` / `WebUi:RootPath`. Default off, so the API-only test hosts, the Angular dev
  topology and the E2E host are unchanged. The published artifact turns it on through its own `appsettings.json` and the SPA is
  copied to `wwwroot-ui/` beside the binaries.
- Only that one directory is ever exposed (a dedicated `PhysicalFileProvider`; the content root, SQLite files, audit log, vault,
  policy and settings are never served). Startup fails if hosting is enabled and the directory has no `index.html`.
- Cache: `index.html` and every non-hashed file `no-cache`; Angular hashed files `public, max-age=31536000, immutable`.
- Security headers on UI responses (CSP without inline script, `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options` /
  `frame-ancestors`). API responses keep their own headers. HSTS is left to the TLS terminator.
- Proxy support is opt-in (`ReverseProxy:KnownProxies` / `KnownNetworks`); `X-Forwarded-*` is never trusted by default. CSRF and the
  session never read forwarded headers (ADR-0043 §9); only the per-address rate limiter benefits.
- Build: `scripts/Publish-Bops.ps1` builds Angular (`npm ci && npm run build`), verifies the output and runs `dotnet publish` with
  `-p:BopsWebUiDir=...`; publish **fails** if the directory or its `index.html` / hashed bundles are missing. A plain `dotnet build`
  never calls Node. A smoke script starts the *published* artifact with no Node on `PATH`.

## Acceptance criteria

1. `/` serves the SPA; deep links refresh; assets are returned with correct types and cache headers.
2. `/api/*` works; an unknown `/api/*` path is `404` (not `index.html`); `/healthz` is not intercepted.
3. Reserved files (`appsettings.json`, `*.db`, `audit.jsonl`, `vault.dat`, `policy.yaml`, `settings.json`) are not served.
4. Login, logout, session cookie and CSRF/Origin checks behave exactly as before; an unauthorized Origin is refused.
5. `npm start` / dev topology and the Playwright E2E gate still work.
6. Publish output for `win-x64` and `linux-x64` contains the SPA; publish fails explicitly when it does not.
7. The published artifact starts and serves the UI without Node.js (smoke test, CI on Windows and Linux).
8. Operator documentation for local install and TLS reverse proxy (IIS / Nginx), with sample configuration.

## IIS validation

### Offline and direct-artifact evidence

On 2026-11-07, the Windows Release API fixture tests passed for SPA hosting and settings, proxy trust and browser sessions (188/188).
The two task-event fixture tests passed, as did 43 focused Angular tests for authentication and task-session state in Chrome Headless.
The Chrome E2E-8 test passed with the gated model fixture and Angular development proxy, covering session reload, watch restoration, CSRF, logout and revocation.
The MSBuild publish guard rejected a missing SPA and an incomplete SPA with the expected diagnostics.
Static assertions on the IIS sample covered path capture, the loopback rewrite target, forwarded variables, request size and HSTS.
The self-contained `win-x64` artifact built from `170b9d9` passed the structure and direct loopback smoke checks without Node.js on the application PATH.
These checks provide evidence for the application and configuration sample. The live IIS gate has its own outcome below.

The local IIS runner's result parser also passed its offline self-test with a complete fixture and seven fail-closed
cases. That evidence belongs to the runner in the local `bOps-iis-validation` worktree. The [Nginx sample smoke](../../docs/deployment.md#nginx-sample-smoke)
is separate historical evidence for the Nginx profile.

### Live gate

**Gate outcome — BLOCKED / NOT_RUN (2026-11-07).** The available Windows host has IIS Web Server disabled.
W3SVC, WAS and the URL Rewrite and ARR module binaries in the IIS directory were absent. The session lacked elevation and
Docker was running Linux containers. The operator confirmed that no prepared IIS test host was available.

This gate stays open for BOPS-0120. The [supervised V1.3 preview](../../docs/preview-02.md) uses direct ASP.NET Core
on loopback as its supported deployment profile. The first release-workflow run and operator acceptance are separate gates.

### Reference test environment

- A disposable x64 Windows Server 2025 test host with IIS 10, WAS and W3SVC running, plus ARR 3 and URL Rewrite 2.1.
  Enable the ARR proxy. Allow `HTTP_X_FORWARDED_FOR` and `HTTP_X_FORWARDED_PROTO` for the test site's rewrite rule.
  [ARR download](https://www.iis.net/downloads/microsoft/application-request-routing) ·
  [URL Rewrite download](https://www.iis.net/downloads/microsoft/url-rewrite) ·
  [Microsoft reverse-proxy walkthrough](https://learn.microsoft.com/en-us/iis/extensions/url-rewrite-module/reverse-proxy-with-url-rewrite-v2-and-application-request-routing).
- Use the published self-contained `win-x64` artifact from the revision under test, a scratch data directory and test-only keys.
  Bind Kestrel to `http://127.0.0.1:5080` and set `BrowserSession__Origins` to the exact HTTPS test origin.
  Use a dedicated IIS site with the sample `web.config` and a test certificate trusted by the probe client.
  [Manual IIS validation and four-scenario runbook](../../docs/deployment.md#manual-iis-validation).

### Live checks and expected results

| Check through IIS | Expected result |
| --- | --- |
| TLS and SPA | The client validates the certificate and sees HSTS. `/` and a direct deep link return the SPA shell with `no-cache` and CSP. Hashed assets return `200` with the expected media type and immutable caching. |
| Routing and isolation | `/healthz` returns `200` and `ok`. An unauthenticated protected endpoint returns `401` and an unknown API path returns `404` without the SPA shell. Configuration, database, audit, vault and policy paths return no file contents. |
| Session and CSRF | Sign-in from the configured Origin with `X-bOps-Request` succeeds. The cookie is `__Host-bops_session`, Secure, HttpOnly, SameSite=Strict, Path=/ and has no Domain attribute. A foreign Origin or missing request header returns `403`. An authenticated session works and returns `401` after logout. |
| Event streaming | Run a controlled task with the model fixture held and read `/api/agents/tasks/{id}/events` without client-side buffering. The response is `text/event-stream`. Observe Running before model release, abort the client stream, and reconnect to the same running task. After releasing the fixture, observe Completed and clean EOF. |

All four scenarios must pass. Keep the runner summary with the tested commit, package version and SHA-256,
Windows build, IIS and module versions, deployed sample digest, effective ARR settings, certificate metadata,
scenario results and SSE milestone timings. Record relevant sanitized headers and keep sanitized IIS or Failed
Request Tracing output for failures. Remove cookies, keys and secrets from all evidence.

## Out of scope

Docker packaging, a new web server, changing the authentication model, CORS, release tagging.
