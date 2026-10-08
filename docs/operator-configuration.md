# Operator configuration

How to configure and run bOps locally through the CLI, the local API and the web UI: credentials,
the encrypted vault, the filesystem read policy and key rotation. It is a configuration guide, not a
deployment guide, and it describes only what the current code and shipped `appsettings.json` files do.

## The four credentials, at a glance

They are different secrets for different processes. Do not reuse one variable for another.

| Variable | Read by | Purpose |
|---|---|---|
| `BOPS_MODEL_API_KEY` | CLI (`src/core/bOps.Cli/appsettings.json`) | Model-provider API key for CLI runs |
| `BOPS_MODELPROVIDER_API_KEY` | API (`src/core/bOps.Api/appsettings.json`) | Model-provider API key for tasks run through the API |
| `BOPS_API_KEY` | API | The key clients (the UI, scripts) present to the API |
| `BOPS_VAULT_MASTER_KEY` | API and `bops vault rotate-key` | Master key of the encrypted vault |

The variable names are just the defaults in the shipped configuration (`ApiKeySecret`, `Secret`,
`MasterKeySecret` each hold a `Provider: environment` + `Name` reference). The configuration never
contains the secret itself; never commit one.

Configuration sources are the `appsettings.json` next to the project, then environment variables,
which override JSON. Nested keys use a double underscore: `Filesystem__ReadPatterns__0`.

## 1. CLI configuration

The CLI loads its shipped `appsettings.json` from the current directory, and creates `audit.jsonl`,
`policy.yaml` lookups and plugin files relative to it. Run it from its project directory:

```bash
cd src/core/bOps.Cli
export BOPS_MODEL_API_KEY='<your-model-provider-key>'
dotnet run -- "list the files under this directory and tell me what is taking up the most space"
```

```powershell
cd src/core/bOps.Cli
$env:BOPS_MODEL_API_KEY = '<your-model-provider-key>'
dotnet run -- "list the files under this directory and tell me what is taking up the most space"
```

(The built executable is named `bops`; the README examples use that name.) The shipped defaults use the
OpenRouter provider with model `openrouter/free`; change `ModelProvider` in the JSON or override it with
`ModelProvider__Provider`, `ModelProvider__BaseUrl`, `ModelProvider__Model`.

The CLI does **not** load `appsettings.Development.json` unless you set `DOTNET_ENVIRONMENT=Development`.
It has no `Vault` section of its own (see [section 7](#7-vault-key-rotation)).

Without a `policy.yaml`, bOps uses a built-in safe default: read and low-risk tools run automatically,
medium and high risk ask for approval, critical is forbidden. That default has no delegation role
profiles, so delegation is off. `bops delegate readiness`, `bops delegate profiles init --read-only` and
`bops delegate profiles check` need no model provider and no `ModelProvider` section
([delegation setup](agents/delegation-policy.md)).

## 2. API configuration

Run the API from its project directory so its shipped configuration is loaded; its launch profile
(`src/core/bOps.Api/Properties/launchSettings.json`) sets `ASPNETCORE_ENVIRONMENT=Development` and
`http://localhost:5080`.

```bash
export BOPS_API_KEY='<a-local-secret>'
export BOPS_MODELPROVIDER_API_KEY='<your-model-provider-key>'
export BOPS_VAULT_MASTER_KEY='<a-secret-of-at-least-20-characters>'
cd src/core/bOps.Api
dotnet run
```

```powershell
$env:BOPS_API_KEY = '<a-local-secret>'
$env:BOPS_MODELPROVIDER_API_KEY = '<your-model-provider-key>'
$env:BOPS_VAULT_MASTER_KEY = '<a-secret-of-at-least-20-characters>'
cd src/core/bOps.Api
dotnet run
```

CLI and API clients authenticate with `Authorization: Bearer <BOPS_API_KEY>`. Query-string credentials are
not supported. The shipped configuration defines one key, `local-operator`, holding the roles
`viewer,operator,approver,administrator`. The web UI signs in with the same key once and then uses a
browser session (see [section 3](#3-web-ui)); a request that carries an `Authorization` header is always
authenticated as Bearer, never by the session cookie.

Because `BOPS_VAULT_MASTER_KEY` is referenced by the shipped configuration, the API will not start
without it. To run without the vault, see [section 5](#5-encrypted-vault).

Tasks run through the API persist to `tasks.db`, `audit.jsonl`, `settings.json`, `vault.dat` and the
plugin files in the API's working directory (all paths are configurable in `appsettings.json`).

**One `policy.yaml` for both hosts.** `Policy:FilePath` (default `policy.yaml`, relative to the working
directory) is read by the API and by the CLI independently; point both at the same file (for example
`Policy__FilePath` with an absolute path) so `bops delegate profiles init --read-only --write` writes the
file the API uses. The API reads it **only at start** and logs its resolved path and load state
(`NoFile`, `Loaded` or `LoadFailed`, never the file's contents): restart the API after changing it.
`GET /api/delegations/readiness` and `bops delegate readiness` then report the same role readiness when
both hosts load the same file and plugins. Nothing creates or changes this file automatically
([delegation setup](agents/delegation-policy.md)).

## 3. Web UI

The Angular UI in `web/bops-ui` is a client of the local API. `npm start` serves it on
`http://localhost:4200` and proxies `/api` to `http://localhost:5080` (`proxy.conf.json`), so start the
API first. The dev server with this same-origin proxy is the only supported browser topology: bOps does
not host the built SPA itself (production SPA hosting is deferred, F-26), and no CORS policy exists.

**Signing in (ADR-0043).** On the sign-in screen enter the value of `BOPS_API_KEY`. The UI sends it once
to `POST /api/session` and receives a browser session in the cookie `__Host-bops_session` (`HttpOnly`,
`Secure`, `SameSite=Strict`, `Path=/`, no `Domain`); the key is not kept by the page and never enters
browser storage or a URL. A refresh restores the session through `GET /api/session/me`, and the dashboard
keeps the task you are following in its URL (`/dashboard?task=<id>`), so its live view resumes after F5.
The key needs the `viewer` role to open the UI.

- **Session lifetime.** A session ends on sign-out, after `BrowserSession:IdleTimeout` without an
  accepted request (default 1 hour; the UI's background polling counts, so an open dashboard stays
  signed in) and at `BrowserSession:AbsoluteTimeout` at the latest (default 12 hours; never extended).
  By default the cookie is a browser-session cookie; **Keep me signed in on this device** makes it
  survive a browser restart, up to the remaining absolute lifetime. Leave it off on a shared computer.
- **Removing or rotating a key** (and restarting the API) ends that key's browser sessions at their next
  request, as does any change that makes the key resolve to a different configured credential. Role
  changes apply to existing sessions at the next start, exactly as for Bearer.
- **Open the UI at exactly a configured origin.** Cookie-authenticated changes (and sign-in itself) are
  accepted only from `BrowserSession:Origins` (default `http://localhost:4200`); `http://127.0.0.1:4200`
  is a different origin and is refused unless you add it.
- **Revoke every browser session:** stop the API and delete `sessions.db` (`BrowserSession:FilePath`);
  browsers simply sign in again. The file holds no key and no usable token.
- **Sign-in attempts** are limited to 10 per minute per client address.

| Key | Default | Valid values |
|---|---|---|
| `BrowserSession:Origins` | `http://localhost:4200` | One comma-separated string (not an array) of 1–8 origins, each exactly `scheme://host[:port]`; `http` only for `localhost`, `127.0.0.1` or `[::1]` |
| `BrowserSession:IdleTimeout` | `01:00:00` | `00:05:00`–`1.00:00:00`, whole seconds, not longer than `AbsoluteTimeout` |
| `BrowserSession:AbsoluteTimeout` | `12:00:00` | `00:15:00`–`7.00:00:00`, whole seconds |
| `BrowserSession:FilePath` | `sessions.db` | Non-empty path |

An invalid value stops the API at start-up with a message naming the key. There is no setting to relax the
cookie attributes or the origin check. Browsing untrusted local web applications (any `http://localhost`
port) in the same browser profile exposes the session cookie to them; see the
[threat model](security/threat-model.md#api-spoofing-and-privilege-escalation).

The Settings page is visible to administrators only and needs the vault.

## 4. Provider configuration

The `ModelProvider` section names the provider id, `BaseUrl`, `Model`, and the `ApiKeySecret` reference.
Supported providers and their status are listed in the README's
[Providers](../README.md#providers) section; the wire contract is in
[ADR-0038](architecture/adr/0038-provider-wire-contract.md).

On the API there are three distinct states
([ADR-0045](architecture/adr/0045-execution-pinned-provider-configuration-and-fallback-chain.md)):

- **Persisted** configuration is what Settings stores (`settings.json`, the vault). A persisted value can be
  *shadowed* by the host and then is not in use.
- **Effective** configuration is the single immutable snapshot the API publishes: primary provider, endpoint,
  model, tool-calling capability, ordered fallback chain, a generation number and where each value came from.
  `GET /api/settings` returns it next to the persisted values, and the Settings page labels shadowed values as
  *not in effect* rather than active.
- **Pinned** configuration is the non-secret part of one effective generation copied into one task or one whole
  delegation run when it is admitted. It survives approval waits, resume and API restarts.

Resolution of the effective configuration:

- If the real environment variable `ModelProvider__Provider` is set, that environment configuration wins
  and the Settings profile is not consulted; the stored selection is reported as shadowed.
- Otherwise the provider chosen in Settings supplies its endpoint and model, falling back field by field
  to the `ModelProvider` block. A real environment variable for one field (`ModelProvider__BaseUrl`,
  `ModelProvider__Model`, `ModelProvider__SupportsNativeToolCalling`, `ModelProvider__RequestTimeout`) overrides
  only that field.
- **Fallback chain precedence:** if `ModelProvider:Fallbacks` is present in host configuration it owns the whole
  ordered list and the list stored in Settings is shadowed (still saved, but not effective, and editing it does
  not change what new tasks use). Otherwise the Settings list applies, otherwise there is no fallback.
- The block-level `ModelProvider:ApiKeySecret` (`BOPS_MODELPROVIDER_API_KEY`) belongs only to the provider named
  by the merged `ModelProvider:Provider`; every other provider (a Settings-selected one, every fallback candidate)
  uses only its own key in the vault. A key is never sent to a different provider.

### When a change takes effect

Settings changes **no longer require an API restart**:

| Change | Takes effect |
|---|---|
| Active provider, endpoint, model, tool-calling capability | The next task or delegation run started. A task already running (including one waiting for plan approval, or resumed later) keeps the configuration it was pinned with. |
| Fallback chain (Settings) | The next task started, unless host `ModelProvider:Fallbacks` shadows it. |
| API key set, rotated or removed | The **next model attempt**, including in an execution already running: credentials are late-bound and never pinned, so a revoked key stops being used immediately. A removed key makes the next attempt fail with an authentication error; it never triggers fallback. |
| Environment variables, `appsettings.json` | Host configuration reload or API restart (the environment is not polled). |

A syntactically valid but unreachable provider is accepted and fails at execution time; saving makes no
network call. `ExtraParameters` is stored and returned by the API but no provider consumes it, so it has no
effect today.

### Router default and fallback chain

The shipped default is OpenRouter / `openrouter/free`, a provider-side **router**: the upstream model can change
on every call, and Settings warns about it. Use it to get started; select a specific model for serious,
reproducible or remediating work. The requested and actual model of each call are recorded in the dashboard and
in the audit log (`Model`, `ActualModel`).

The **fallback chain** is a different mechanism: at most three administrator-configured `{ Provider, Model }`
candidates tried in order, only after the current candidate exhausted its own retries with a transient,
rate-limited, timeout or unreachable failure. Authentication, quota, invalid-request, context-overflow,
malformed-response and unknown failures never fall back. Once an execution moves to a later candidate it stays
there. Fallback candidates hold no credentials; a candidate without a usable key is shown as such in Settings
(advisory only) and fails with an authentication error if a run reaches it. Audit records carry
`PrimaryProvider`/`PrimaryModel`, the candidate actually called, `FallbackOrdinal`, `ProviderAttempt`, the
configuration generation and a `Fallback` retry decision at each transition.

## 5. Encrypted vault

The vault (`vault.dat`, [ADR-0029](architecture/adr/0029-encrypted-local-vault-and-master-key.md))
stores the provider API keys entered on the Settings page, encrypted with AES-256-GCM. Provider profiles
and the active provider are stored, unencrypted, in `settings.json`. Stored keys are never returned by
an endpoint; responses carry only a masked prefix/suffix.

The master key comes from outside the repository: the environment variable named by
`Vault:MasterKeySecret` (default `BOPS_VAULT_MASTER_KEY`), at least **20 characters**. It is never stored
next to `vault.dat`.

Startup behaviour of the API:

| Situation | Result |
|---|---|
| No `Vault:MasterKeySecret` in the configuration | Vault off: `/api/settings/*` is not mapped (`404`), the Settings page says the vault is not configured; everything else works. |
| Configured, environment variable unset or empty | The API **refuses to start**, naming the variable. |
| Configured, value shorter than 20 characters | The API **refuses to start**. |
| Configured and valid | Vault on. |

To turn the vault off, remove `MasterKeySecret` from the `Vault` section of the API configuration (or
use a configuration file that leaves it out).

`Vault:FilePath` is optional and defaults to `vault.dat` in the API's working directory. If the API is
started by a launcher (an Aspire AppHost, a service, a container), set the variable in that launcher's
environment; a variable set in another terminal does not reach it. To confirm it works, call
`GET /api/settings` with your API key (an `administrator` key); it answers `200`.

## 6. Vault key generation

Any high-entropy secret of at least 20 characters. Keep it in a password manager, not in the repository.

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

```bash
openssl rand -base64 32
```

## 7. Vault key rotation

Rotation re-encrypts `vault.dat` under a new master key. It is a CLI-only action; it is deliberately not
exposed through the API.

The command is:

```bash
bops vault rotate-key <NEW_KEY_ENVIRONMENT_VARIABLE>
```

It takes the **name** of the environment variable that holds the new key, not the key itself. The CLI has
no `Vault` section in its shipped configuration, so tell it where the current key lives and where the
vault file is with the same environment variables the API configuration uses, and run it where the
relative `vault.dat` path resolves to the API's vault (or set `Vault__FilePath` to the path):

```bash
export Vault__MasterKeySecret__Provider=environment
export Vault__MasterKeySecret__Name=BOPS_VAULT_MASTER_KEY
export Vault__FilePath=/path/to/src/core/bOps.Api/vault.dat
export BOPS_VAULT_MASTER_KEY='<current-key>'
export BOPS_NEW_VAULT_MASTER_KEY='<new-key>'
bops vault rotate-key BOPS_NEW_VAULT_MASTER_KEY
```

```powershell
$env:Vault__MasterKeySecret__Provider = 'environment'
$env:Vault__MasterKeySecret__Name = 'BOPS_VAULT_MASTER_KEY'
$env:Vault__FilePath = 'C:\path\to\src\core\bOps.Api\vault.dat'
$env:BOPS_VAULT_MASTER_KEY = '<current-key>'
$env:BOPS_NEW_VAULT_MASTER_KEY = '<new-key>'
bops vault rotate-key BOPS_NEW_VAULT_MASTER_KEY
```

On success it prints how many stored keys were rotated. Afterwards set `BOPS_VAULT_MASTER_KEY` to the new
key for the API and restart it. The rotate command does not check the length of the new key, but the API
will refuse to start with one shorter than 20 characters. Back up `vault.dat` before rotating.

## 8. Backup and recovery

Backup is file-level. Back up the files the API keeps in its working directory that you care about:
`vault.dat`, `settings.json`, and for plugins `plugins.json`, `publisher-trust.json` and the `plugins/`
directory (default names; check `Vault:FilePath`, `Settings:FilePath` and `Plugins:*` if you changed
them), plus `audit.jsonl` if you keep the audit chain (`bops audit verify` checks it).

- The master key is deliberately **not** stored with `vault.dat`; keep it separately, in your password
  manager.
- Losing only the vault file: the stored provider keys are gone; re-enter them on the Settings page.
- Losing only the master key: `vault.dat` cannot be decrypted, so it is unusable; you must start a new
  vault and re-enter the provider keys. A copied vault file alone is inert.
- Losing `plugins.json` means reinstalling plugins.
- `sessions.db` (browser sessions) needs no backup: it holds no secret usable without a cookie, and losing
  it only signs browsers out.

No recovery beyond this is promised.

## 9. Filesystem access policy

Filesystem tools are deny-by-default: they read nothing until `Filesystem:ReadPatterns` lists what they
may read (`WritePatterns` likewise for writes). The simplest way to allow a path is an environment
variable on the process (CLI or API):

```bash
export Filesystem__ReadPatterns__0='<your-pattern>'
```

```powershell
$env:Filesystem__ReadPatterns__0 = '<your-pattern>'
```

Alternatively put the patterns in an `appsettings.Development.json` next to `appsettings.json`:

```json
{ "Filesystem": { "ReadPatterns": [ "<your-pattern>" ] } }
```

That file is loaded by the API under its launch profile (`ASPNETCORE_ENVIRONMENT=Development`), but **not
by the CLI** unless you also set `DOTNET_ENVIRONMENT=Development`. See
[filesystem inventory](filesystem-inventory.md) and [troubleshooting](filesystem-troubleshooting.md).

## 10. Running CLI, API and UI

```bash
# CLI
cd src/core/bOps.Cli
dotnet run -- "<goal>"

# API (terminal 1)
cd src/core/bOps.Api
dotnet run

# UI (terminal 2, from the repository root)
cd web/bops-ui
npm ci
npm start        # http://localhost:4200
```

Other npm scripts: `npm run build`, `npm test`, `npm run lint:i18n`, and `npm run e2e` — the E2E-8
browser-session gate (Playwright, installed Google Chrome and Playwright's Firefox; `npx playwright install
firefox` once). It starts its own test-only API host (`tests/bOps.Api.E2EHost`, a gated fake model, a
per-run generated key) and `ng serve`, so ports 4200, 4300, 4301, 5080 and 5099 must be free and no
regular API may be running.

For the integrated development topology, start Docker and run the AppHost from the repository root:

```bash
dotnet run --project src/bOps.AppHost/bOps.AppHost.csproj
```

It starts UI `4200`, API `5080`, SearXNG `8081` and the Linux test target. SearXNG is development
infrastructure only: the AppHost supplies `Web__Search__BaseUrl` to the API and waits for its
`/healthz`; direct API/CLI execution has no Docker requirement. Set `Searxng__Enabled=false` to omit
it from the AppHost, or set `Web__Search__BaseUrl` on the API/CLI process to use an external trusted
instance. Verify the local JSON API with
`Invoke-RestMethod 'http://localhost:8081/search?q=bOps&format=json'` on PowerShell or
`curl -fsS 'http://localhost:8081/search?q=bOps&format=json'` on bash. Full failure meanings and
network-namespace guidance are in [the web network policy](security/web-network-policy.md).

## 11. Production notes

- Bind the API to loopback, or put TLS and an authenticated reverse proxy in front of it. The launch
  profile's `localhost:5080` is a development setting only.
- The API and CLI run with the privileges of their host account; bOps does not elevate.
- Credentials travel only in the `Authorization: Bearer` header or, for the web UI, once in the
  `POST /api/session` JSON body and then as the `HttpOnly` session cookie; never in a query string.
- When the API is started by a service manager or container launcher, inject the environment variables
  there.

See the [threat model](security/threat-model.md), [web network policy](security/web-network-policy.md)
and [SECURITY.md](../SECURITY.md).

### Known validation limits

On Linux, CI exercises `systemctl` for real through `service.list`/`service.status`. For
`service.start`/`stop`/`restart`, CI validates the tool contract, verification wiring and rejected-input
paths, but does not execute a real privileged service lifecycle. A real systemd lifecycle test exists for
enable/disable and runs only when `BOPS_RUN_REAL_SYSTEMD_TESTS=1` under root, so the normal unprivileged
CI runner skips it. Windows CI runs elevated. See
`tests/bOps.Packages.Service.Linux.Tests/LinuxServiceActionToolsTests.cs`.

## 12. Prerequisite readiness and system messages

Some tools and Skill Capabilities need something outside bOps: the Docker daemon, a SearXNG endpoint, a debugger executable,
a plugin's own service. bOps tells you — in the **System messages** page, `GET /api/system-messages` and nowhere else you
have to dig — when one of those is missing, why, and how to fix it ([ADR-0049](architecture/adr/0049-unified-prerequisite-readiness-and-system-messages.md)).

**Registered versus available.** A tool or Capability bOps loaded and validated is *registered*. It is *available* only
while its **required** prerequisites are satisfied. A registered component that is not available is never offered to a
model and never executes; it is simply not in the catalog until the prerequisite recovers (no restart needed).

| Declared as | Prerequisite is… | The component is… |
|---|---|---|
| Required (`Requires`) | `Available` | available |
| Required | `Degraded` | available, **degraded** |
| Required | `Unavailable`, `Error` or `Unknown` | **not available** |
| Optional (`OptionalRequires`) | `Available` | available |
| Optional | anything else | available, **degraded** (never hidden) |

The four reported states are *Available*, *Degraded*, *Unavailable* (the check ran and the thing is absent) and *Error*
(the check itself failed, timed out, returned something invalid, or could not be recorded — see below).

**When readiness is checked.**

- **At start.** Every registered check runs once before the API accepts work, and the result decides the first catalog.
  A missing prerequisite never stops the host; it produces a message.
- **Periodically.** The API re-checks every `Prerequisites:RefreshIntervalSeconds` (default 30, 5–3600), at most
  `Prerequisites:MaxConcurrency` (default 4, 1–32) checks at a time. Each check runs under its own timeout (1–60 s,
  default 10 s). The CLI checks once at start and has no timer.
- **After a plugin lifecycle change.** A successful `POST /api/plugins/{id}/enable` or `/disable` runs one readiness cycle
  *before* it answers, so the catalog and the System Messages page already reflect the change. The lifecycle result is
  authoritative: if that follow-up refresh fails it is logged, the enable/disable still stands, and the periodic refresh
  retries. A rejected, stale, unconfirmed, replayed or failed operation changes nothing and refreshes nothing.

**Declared but not registered.** A component that names a prerequisite no installed package registered a check for
cannot determine its readiness. It is fail-closed (required → not available, optional → degraded) and is announced once as
`prerequisite.not-registered` — *"No package registered a check for prerequisite 'x'. Components depending on it cannot
determine readiness."* — with no invented remediation. When a package later registers the check, its next result
transitions normally and a recovery message follows. `GET /api/prerequisites` reports the same `Error`/`not-registered`
state.

**Fail-closed persistence.** An observation is recorded durably (state and message in one transaction) *before* any
component may rely on it. If recording fails, that prerequisite is treated as `Error` with the stable reason
`state-record-failed` — required dependents are not available, optional ones degraded — until a later observation can be
recorded. Raw database errors are only in the server log, never in a message or API field. Other prerequisites are still
recorded. If the system-message store itself is unavailable no message can be written, and nothing gains authority.

**What a message contains.** One message per prerequisite *transition* (fingerprint `state|code`), never per refresh and
never per affected tool: repeated identical observations, and a process restart that observes the same state, write
nothing. A first `Available` is quiet; `Unavailable`/`Degraded` are a `Warning` when a component requires the
prerequisite (otherwise `Information`); a failing check is an `Error`; recovery is `Information`. The text is
*"<display name> is unavailable. <remediation>"* with the package-supplied remediation, and metadata lists at most 16 affected components plus
their full count. Metadata is bounded (32 entries, 4,096 bytes) and refuses secret-looking keys and values.

**System messages are not logs, audit or model input.** Logs are for developers and are not copied into messages. The
audit chain (`audit.jsonl`) records actions and proves them; system messages prove nothing, are not hash-chained and can be
purged. Neither messages nor prerequisite state ever enter a model context.

**Configuration.**

| Key | Default | Meaning |
|---|---|---|
| `SystemMessages:FilePath` | `system-messages.db` | SQLite database for messages and prerequisite state. A relative path is resolved from the **process working directory**, like the other relative store paths (`Memory:FilePath`, `Delegation:FilePath`). The API and the CLI each read this key independently; point both at the same absolute path to share one inbox. |
| `SystemMessages:RetentionDays` | 90 | Messages older than this are deleted (1–3650). |
| `SystemMessages:RetentionIntervalMinutes` | 360 | One bounded purge when the API starts, then every interval (5–10080); never on insert. |
| `Prerequisites:RefreshIntervalSeconds` | 30 | API refresh period. |
| `Prerequisites:MaxConcurrency` | 4 | Parallel checks per refresh. |
| `Agent:ReplanWarningThreshold` | 3, capped by `MaxLifetimeReplans` | See below. |

Out-of-range values stop the host at start. There is no migration of `system-messages.db` to a per-user location.

**The `agent.replan.threshold` message.** When an accepted replan brings a task's *lifetime* replan count (across every
execution attempt) to `Agent:ReplanWarningThreshold`, bOps writes one `Warning` from source `runtime/agent` with the task id,
the provider and model ids, the lifetime count, the threshold, the limit and the execution attempt. It is written once per task
(a resume already past the threshold, or later replans, write nothing), never for a replan attempt that was not accepted, and
never contains the goal, prompt, model reply or tool output. Unset, the threshold is 3, lowered to `MaxLifetimeReplans` when that is
smaller so existing configurations keep working; when set it must be at least 1 and at most `Agent:MaxLifetimeReplans`. Failing to
write the message is logged and never fails or changes the task.

**Reading messages.**

- **API** (Viewer role): `GET /api/system-messages?fromUtc=&toUtc=&severity=&contains=&pageSize=&cursor=`. `fromUtc` and `toUtc` are
  inclusive ISO 8601 instants; `severity` is exactly `Information`, `Warning`, `Error` or `Critical`; `contains` is a
  case-insensitive substring of the message. Every supplied filter must match (AND). Newest first; `pageSize` defaults to 50
  (max 200); `nextCursor` is an opaque keyset cursor — pass it back as `cursor` for the next, older page (there is no
  offset). Invalid input is a 400 naming the parameter. `GET /api/prerequisites` (Viewer) shows the *current* state of every
  prerequisite, which components need it, and the remediation.
- **UI**: **System messages** (`/system-messages`; *Messaggi di sistema* in Italian). Filters From, To, Severity and *Text contained
  in message* are sent to the server when you press **Apply** (not while typing); **Reset** clears them. Rows show date/time,
  severity (word, glyph and accessible label — never colour alone), source and message, 50 per page with Previous/Next; expand a
  row for code, source, timestamp, severity, task, component and the metadata as escaped JSON text.

## Related documents

[Plugins](plugins/getting-started.md) · [Delegation](agents/delegation.md) ·
[Delegation policy](agents/delegation-policy.md) · [Architecture decisions](architecture/adr/) ·
[Vault ADR-0029](architecture/adr/0029-encrypted-local-vault-and-master-key.md)
