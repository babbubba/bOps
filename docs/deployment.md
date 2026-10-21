# Installing and running bOps (production hosting)

bOps ships as **one ASP.NET Core process** that serves the web UI and the API on the same endpoint. You need neither Node.js,
the Angular CLI, `npm start`, a development proxy, IIS, Nginx nor Docker to run it; a reverse proxy is only needed when you want
HTTPS for other machines. (Task BOPS-0120, finding F-26; the session design is
[ADR-0043](architecture/adr/0043-browser-web-session.md).)

```text
browser ──► http(s)://host ──► bOps.Api ─┬─ /            the Angular app (wwwroot-ui/, static files + index.html for client routes)
                                         ├─ /api/*       the API (unchanged: Bearer key or browser session)
                                         └─ /healthz     anonymous liveness probe ("ok")
```

## 1. Build and publish

| Command | What it does | Needs Node.js? |
|---|---|---|
| `dotnet build bOps.slnx` / `dotnet test bOps.slnx` | Backend build and tests. **Never** builds or installs the frontend. | no |
| `cd web/bops-ui && npm ci && npm run build` | Compiles the Angular app to `web/bops-ui/dist/bops-ui/browser`. | yes (build machine only) |
| `./scripts/Publish-Bops.ps1 -Runtime linux-x64` (or `win-x64`) | **The full artifact**: Angular build, completeness check, self-contained `dotnet publish` of `bOps.Api` with the SPA in `wwwroot-ui/`, layout check. Output: `artifacts/bops-<runtime>/`. | build machine only |
| `dotnet publish src/core/bOps.Api/bOps.Api.csproj -p:BopsWebUi=false` | A backend-only artifact, on purpose (no UI). | no |
| `./scripts/Test-PublishedArtifact.ps1 -ArtifactDirectory artifacts/bops-<runtime>` | Starts the published artifact **without Node.js on its PATH** and drives it over HTTP (shell, deep link, assets, cache headers, API 401/404, reserved files, login/cookie/CSRF/logout). Run it on the OS of the artifact. | no |

`dotnet publish` of `bOps.Api` **fails with an explicit message** when the compiled SPA is missing or incomplete (no `index.html`,
`main-*.js`, `polyfills-*.js` or `styles-*.css`); a frontend is never dropped silently. Pass `-FrameworkDependent` to
`Publish-Bops.ps1` if the target already has the ASP.NET Core 10 runtime (the default artifact is self-contained).

The publish output holds the binaries, `appsettings.json`, `appsettings.Production.json` and `wwwroot-ui/`. It never contains a
database, audit log, vault, policy or settings file (the publish and the smoke test both check this).
CI runs the publish and the smoke test on Windows and Linux for every change; the release workflow also attaches
`bops-api-win-x64.zip` and `bops-api-linux-x64.zip`. A zip does not carry the Unix execute bit: run `chmod +x bOps.Api` after unzipping.

**Linux prerequisite.** `bOps.Api` runs with full globalization (Knowledge needs Unicode normalization), so the target needs the ICU
library (`libicu`, for example `apt install libicu74` on Ubuntu 24.04; it is not bundled in the self-contained artifact). Without
ICU the host fails at start-up; `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` lets it start with Knowledge reported as unavailable.

## 2. Configuration that matters

Configuration comes from `appsettings.json`, `appsettings.Production.json` (published; the default environment is `Production`) and
environment variables (`Section__Key`), the last one winning. Secrets stay in the environment (or the vault), never in a file in
the installation directory.

| Key | Default | Meaning |
|---|---|---|
| `BOPS_API_KEY` (env) | — | The key you type on the sign-in screen. **Required.** |
| `BOPS_VAULT_MASTER_KEY` (env) | — | ≥ 20 characters; protects the local vault. The API does not start without it. |
| `Kestrel:Endpoints:Http:Url` | `http://localhost:5080` (published) | Where bOps listens. Keep it on loopback unless a firewall protects it. |
| `BrowserSession:Origins` | `http://localhost:5080` (published) | **The exact browser origin(s)** allowed to sign in and to make cookie-authenticated changes: `scheme://host[:port]`, no path, `http` only for `localhost`. Behind HTTPS it is the public origin, e.g. `https://bops.example.com`. Nothing else is trusted. |
| `WebUi:Enabled` | unset = automatic | Unset: the UI is served exactly when `wwwroot-ui/` exists beside the binaries. `true`: required (start-up fails if missing). `false`: never. A `wwwroot-ui/` without `index.html` always fails start-up. |
| `WebUi:RootPath` | `wwwroot-ui` | Relative to the **application** directory, not the working directory. Only this directory is ever served. |
| `ReverseProxy:KnownProxies` / `ReverseProxy:KnownNetworks` | empty | See [section 5](#5-reverse-proxy-and-forwarded-headers). Empty = `X-Forwarded-*` is ignored. |

Session lifetime, cookie attributes and the CSRF rules are unchanged; see [operator configuration §3](operator-configuration.md#3-web-ui).
An invalid value stops the host at start-up with a message naming the key.

## 3. Mode A — local install (no proxy, no TLS)

API and UI on one local endpoint, `http://localhost:5080`, for the person sitting at the machine.

```powershell
# Windows (PowerShell). The data directory is the working directory; ASPNETCORE_CONTENTROOT is the installation directory.
$env:BOPS_API_KEY = '<a-long-random-secret>'
$env:BOPS_VAULT_MASTER_KEY = '<a-secret-of-at-least-20-characters>'
$env:ASPNETCORE_CONTENTROOT = 'C:\bOps'
New-Item -ItemType Directory -Force C:\bOps-data | Out-Null
Set-Location C:\bOps-data
& C:\bOps\bOps.Api.exe
```

```bash
# Linux
export BOPS_API_KEY='<a-long-random-secret>'
export BOPS_VAULT_MASTER_KEY='<a-secret-of-at-least-20-characters>'
export ASPNETCORE_CONTENTROOT=/opt/bops
mkdir -p ~/bops-data && cd ~/bops-data
/opt/bops/bOps.Api
```

Open **`http://localhost:5080`** (exactly this address: `127.0.0.1` is a different origin and is refused) and sign in with
`BOPS_API_KEY`. Browsers accept the `Secure` session cookie on `localhost`, so no certificate is needed here. This mode is for
one person on the machine itself; do not publish port 5080 to a network, and see the threat-model note about other local web
applications sharing the browser profile.

If you run it from the installation directory instead (working directory = installation directory) you do not need
`ASPNETCORE_CONTENTROOT`, but state files are then created next to the binaries and an upgrade must not delete them. The split
above is the recommended layout.

## 4. Mode B — server with HTTPS behind a reverse proxy

```text
browser ─TLS─► proxy (443)  ─plain HTTP─►  bOps.Api on 127.0.0.1:5080
```

* **Kestrel binding.** `Kestrel__Endpoints__Http__Url=http://127.0.0.1:5080`. bOps itself serves plain HTTP on loopback; only the proxy is reachable from the network.
* **TLS termination.** At the proxy (certificate, protocols, HSTS). bOps sets the session cookie `Secure` regardless of the scheme it sees.
* **Same origin.** The proxy serves **everything** (`/` and `/api`) from one `https://host`. The browser then loads the UI and calls `/api` on the same origin, so no CORS exists or is needed. Do not split the UI and the API across two hosts, and do not mount bOps under a sub-path (the UI is built for `/`).
* **Authorized browser origin.** `BrowserSession__Origins=https://bops.example.com`, exactly what the address bar shows. The check compares the browser's `Origin` header only; `Host` and `X-Forwarded-*` are never read for it, so a spoofed forwarded header cannot make an origin trusted.
* **The proxy must not buffer `/api`.** The live task view is a long-lived `text/event-stream`; disable response buffering and use a long read timeout. Allow request bodies of about 70 MiB if you upload plugins (default archive limit 64 MiB).

Samples (adapt names, certificates and paths). CI checks the published application, not these proxy configurations:

| Platform | Files |
|---|---|
| Linux, Nginx + systemd | [`samples/deployment/linux/nginx-bops.conf`](../samples/deployment/linux/nginx-bops.conf), [`samples/deployment/linux/bops.service`](../samples/deployment/linux/bops.service) |
| Windows, IIS (URL Rewrite + ARR) | [`samples/deployment/windows/iis-web.config`](../samples/deployment/windows/iis-web.config) — **not verified on an IIS host**; test before relying on it |
| Both | [`samples/deployment/bops.env.example`](../samples/deployment/bops.env.example) (environment file), [`samples/deployment/windows/Start-Bops.ps1`](../samples/deployment/windows/Start-Bops.ps1) (start script for Windows) |

## 5. Reverse proxy and forwarded headers

By default bOps **ignores** `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host`: they are request headers any client can
set. Nothing in the security model needs them (the origin check, the session and the CSRF gate do not read them). The only benefit is
the per-address sign-in and request rate limit, which otherwise sees every user as the proxy's address.

To honour them, list the proxy explicitly:

```text
ReverseProxy__KnownProxies=127.0.0.1             # comma-separated addresses, and/or
ReverseProxy__KnownNetworks=10.20.0.0/24         # comma-separated CIDR networks (never /0)
```

Only `X-Forwarded-For` and `X-Forwarded-Proto`, one hop, and only when the request comes from a listed address. `X-Forwarded-Host` is
never used. **Conditions for this to be safe:** bOps listens on loopback or a network only the proxy can reach (otherwise a client
connects directly and bypasses the proxy); the proxy **replaces** the two headers instead of appending (the samples do); no other
service you do not trust runs on a listed address or network.

## 6. Data, upgrades and what is served

* **Persistence.** The working directory is the data directory: `sessions.db`, `tasks.db`, `delegations.db`, `system-messages.db`,
  `audit.jsonl`, `vault.dat`, `settings.json`, `policy.yaml`, `plugins/` and the filesystem manifest store are created there (every path
  is configurable, see [operator configuration](operator-configuration.md)). Back this directory up; it is the only state. Install and
  data directories are separate, so an **upgrade is: stop, replace the installation directory, start**. Browser sessions survive the
  restart; changed API keys end them as documented.
* **What is served.** Only `wwwroot-ui/`. The installation directory, the working directory, configuration, databases, the audit log,
  the vault, policy and settings files are never reachable as static files, and the SPA is never served for `/api/*` or `/healthz`. An
  unknown `/api/...` path is `404`, a missing asset is `404`, and any method other than `GET`/`HEAD` on a client route is `404`.
  The protected API still requires its own authorization, whatever the UI is.
* **Caching.** `index.html` and unhashed files are `no-cache` (an upgrade is picked up on the next load); Angular's hashed
  `*.js`/`*.css` are `public, max-age=31536000, immutable`.
* **Headers.** UI responses carry a Content-Security-Policy (`script-src 'self'`, no inline script, `frame-ancestors 'none'`;
  `style-src` allows inline styles, which Angular's component styles need), `X-Content-Type-Options: nosniff`, `Referrer-Policy:
  no-referrer` and `X-Frame-Options: DENY`. HSTS belongs to the TLS terminator. Errors are the fixed problem bodies of ADR-0043 §13:
  no stack trace and no request data.

## 7. Starting at boot and after a restart

**Linux (systemd).** Use [`bops.service`](../samples/deployment/linux/bops.service): it starts at boot, restarts after a crash
(`Restart=on-failure`) and stays stopped after `systemctl stop`. Check with `systemctl status bops`, `journalctl -u bops`,
`curl -fsS http://127.0.0.1:5080/healthz`.

**Windows.** `bOps.Api.exe` is a console host, not a Windows-service-aware executable: `sc create` or `New-Service` on it alone does not
work. Run it through a **Scheduled Task at startup** with [`Start-Bops.ps1`](../samples/deployment/windows/Start-Bops.ps1) (register
command in its header: it reads the environment file, sets the working directory to the data directory and waits for the process, so
the task's restart settings apply). A native Windows service wrapper (for example `UseWindowsService`) is not part of this release.
Third-party wrappers (NSSM, WinSW) can host the same executable with the same working directory and environment, but are not tested here.

After a restart nothing needs to be redone: state is on disk, the policy file is read at start (restart after changing it), and
unfinished tasks follow the normal resume rules ([ADR-0017](architecture/adr/0017-persistent-resumable-tasks.md)).

The service account decides what bOps's tools can do (bOps does not elevate); `policy.yaml` decides what needs approval. See the
comments in `bops.service`.

## 8. Development is unchanged

`npm start` (Angular dev server on `http://localhost:4200` proxying `/api` to `http://localhost:5080`), the Aspire AppHost and the
Playwright E2E gate work as before: a development build has no `wwwroot-ui/`, so `bOps.Api` stays API-only there
(`BrowserSession:Origins` defaults to `http://localhost:4200`). Only a published artifact carries the UI.

## 9. Troubleshooting

| Symptom | Cause |
|---|---|
| Sign-in answers `403` / "refused by the browser-session origin check" | The address in the browser is not exactly one of `BrowserSession:Origins` (`localhost` vs `127.0.0.1`, `http` vs `https`, port, trailing slash in the setting). |
| Start-up fails naming `WebUi:RootPath` | `WebUi:Enabled=true` without the directory, or a `wwwroot-ui/` without `index.html`: republish with `Publish-Bops.ps1`. |
| `/` is `404` | The artifact has no `wwwroot-ui/` (backend-only publish) or `WebUi:Enabled=false`. |
| Start-up fails naming the vault master key | `BOPS_VAULT_MASTER_KEY` unset or shorter than 20 characters. |
| Everything is signed out after an upgrade | The data directory was replaced or the working directory changed: `sessions.db` is relative to the working directory. |
| Live task view stalls behind a proxy | The proxy buffers the event stream; see section 4. |
| Many users share one sign-in rate limit | The proxy is not listed in `ReverseProxy:KnownProxies`; see section 5. |
