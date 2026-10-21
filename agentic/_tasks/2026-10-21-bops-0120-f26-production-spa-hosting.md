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

## Out of scope

Docker packaging, a new web server, changing the authentication model, CORS, release tagging.
