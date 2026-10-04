# ADR-0043 — Browser web session: a bounded, revocable server-side session behind a hardened cookie

Status: Proposed — awaiting independent architecture/security review
Date: 2026-10-05

Amends [ADR-0018](0018-bops-api-minimal-surface.md) (the `bOps.Api` surface) for **authentication only**: it adds a second
authentication scheme for browsers, three session endpoint behaviours and a CSRF gate. ADR-0018's text is not edited; it
carries an `Amended by: ADR-0043` pointer. Leaves [ADR-0040](0040-resume-state-machine.md) (task view, resume HTTP
contract) and [ADR-0037](0037-safe-local-plugin-lifecycle.md) (plugin lifecycle; "API owns … CSRF handling for browser
mutations") unchanged in meaning: §9 below *is* the CSRF handling ADR-0037 delegates to the API. No `bOps.Abstractions`
type changes. Governs HARDEN-10 of the V1.3.x reliability train
([packet](../../../agentic/_tasks/2026-09-25-v1.3x-harden-10-browser-session.md);
[plan](../../../agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md) §9, finding F-16, defect C-21; documents the
F-26 constraint). **No implementation may start before this ADR is Accepted by the operator** (packet ADR gate).

## Context

Read at `main` `0e006c47f73013065316632c6e717bfff84c7a2c` (HARDEN-8 merged via PR #75):

1. **Authentication is Bearer-only.** `ApiKeyAuthenticationHandler` (`src/core/bOps.Api/ApiKeyAuthenticationHandler.cs`)
   reads `Authorization: Bearer <key>`, trims it, and walks `Authentication:ApiKeys` in configuration order; for each
   entry with a non-empty `Id` it resolves the `SecretReference` through `ISecretProvider` and compares SHA-256 of both
   values with `CryptographicOperations.FixedTimeEquals`. The first match produces claims `NameIdentifier = Id`,
   `Name = DisplayName ?? Id` and one `Role` claim per distinct comma-separated role. A non-Bearer `Authorization` value
   is `NoResult`; an empty Bearer value or no match is `Fail`. Options are `IOptions<ApiAuthenticationOptions>`
   (snapshotted at first use): a key or role change takes effect at the next host start. This was introduced by
   `b2f4fbf` after ADR-0018 (whose "no authentication" paragraph it superseded in practice); the threat model
   (`docs/security/threat-model.md`, "API spoofing and privilege escalation") documents it.
2. **Authorization** uses four role policies (`ApiAuthorization`: `bops.viewer`, `bops.operator`, `bops.approver`,
   `bops.administrator`), each `RequireRole(...)` against the default scheme. Actor identity everywhere is
   `ClaimTypes.NameIdentifier` (`AgentsEndpoints.cs:201`, the rate limiter in `Program.cs`).
3. **Rate limiting** is one global `PartitionedRateLimiter` (`Program.cs`): authenticated GET/HEAD get a 600-token bucket
   per credential id; everything else (writes and anything unauthenticated) a 120-token bucket per credential id or
   remote IP; `QueueLimit = 0`; rejection is a bare `429` with no `Retry-After`. Middleware order is
   `UseAuthentication → UseRateLimiter → UseAuthorization`.
4. **The UI** (`web/bops-ui`) keeps the raw API key in `AuthService.token` (an in-memory signal) and the interceptor adds
   `Authorization: Bearer <key>` to every request. `App` renders `<bops-login>` instead of the routed shell while not
   authenticated (there is no `/login` route and no guard). Root stores (`TasksStore`, `ApprovalsStore`,
   `DelegationsStore`) poll every 2–3 s and check `auth.authenticated()` before each poll. The task watcher
   (`core/streaming/task-events.ts`, HARDEN-4) ends on 401 with `sessionExpired`; `TasksStore` keeps a memory-only
   `pendingWatch` and restores it after re-login. The selected task id is memory-only: **after F5 nothing knows which
   task was being watched**, and the key must be re-entered (C-21, operator Cases C and E).
5. **Persistence convention.** Each durable concern is its own SQLite file next to the others (`tasks.db`,
   `delegations.db`), opened with WAL and `busy_timeout`, schema by `CREATE TABLE IF NOT EXISTS` plus additive checks,
   owner-only file mode on Unix, `synchronous=FULL` where a write must be durable before the response.
6. **No request logging middleware** (`AddHttpLogging`/W3C) is registered; OpenTelemetry traces only the bOps activity
   source (no ASP.NET Core instrumentation). The launch profile runs in `Development`, where ASP.NET Core adds the
   developer exception page automatically; that page renders request headers and cookies.
7. **The plugin lifecycle endpoints** state in a remark (`PluginLifecycleEndpoints.cs`) and the threat model states in
   prose that "ambient cookies never authenticate a mutation" because no cookie scheme exists.
   `PluginLifecycleEndpointsTests.AmbientCookieCredentials_NeverAuthenticate_AMutation` sends a cookie named
   `.bops.session`. This ADR changes the premise of that prose (§9, §19), not the test.

### Supported development topology — verified, not assumed

The only supported browser topology is the Angular dev server with its same-origin proxy (production SPA hosting is
deferred, plan §15 / F-26):

| | |
|---|---|
| Browser-visible origin | `http://localhost:4200` (`ng serve`, `@angular/build:dev-server`) |
| API | Kestrel `http://localhost:5080` (`Properties/launchSettings.json`, `Development`) |
| Proxy | `web/bops-ui/proxy.conf.json`: `/api` → `http://localhost:5080`, `changeOrigin: true`, `secure: false` |
| What the API receives (measured) | `Host: localhost:5080` (rewritten by `changeOrigin`), `Origin: http://localhost:4200`, `Referer: http://localhost:4200/…`, **no** `X-Forwarded-*` headers |

Probe run on 2026-10-04 with the repository's real `ng serve` and unchanged `proxy.conf.json` in front of a scratch API
that emitted exactly the `Set-Cookie` strings ASP.NET Core produces for §7, driven headless by Google Chrome
154.0.8037.97 (installed stable) and Firefox 150.0.2 (Playwright's Gecko build). Both browsers, identically:

| Check | Result |
|---|---|
| `__Host-bops_session=…; path=/; secure; samesite=strict; httponly` set from `http://localhost:4200` | accepted; jar row `domain=localhost`, `path=/`, `secure`, `httpOnly`, `sameSite=Strict`, `expires=-1` (session cookie) |
| `document.cookie` | does not contain it |
| Sent on same-origin `fetch('/api/…')` through the proxy; after `page.reload()` | yes; yes |
| Persistent variant `…; max-age=43200; …` | accepted; expiry = now + 43 200 s |
| Deletion `__Host-bops_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure; samesite=strict; httponly` | cookie removed |
| Cross-**site** page `http://127.0.0.1:5091` (no-cors `text/plain` POST, form POST, `fetch` with `X-bOps-Request`) | cookie **not** sent; the custom-header request is held at a preflight `OPTIONS` that gets no CORS answer and never sent |
| Same-**site** page `http://localhost:5091` (another port) — same three requests | no-cors `text/plain` POST and form POST **arrive with the cookie** (`Sec-Fetch-Site: same-site`); the custom-header request is held at the preflight |

Conclusions that bind this ADR: (a) hardened `Secure` + `__Host-` cookies work in the supported topology in both
browsers — **the packet's hard stop does not trigger**; (b) the API cannot derive the browser origin from `Host`, so
same-origin validation compares against **configured** UI origins (§9), never `Host` and never forwarded headers;
(c) cookies are not port-isolated, so `SameSite=Strict` does not stop a page served on another `localhost` port — the
custom header plus `Origin` validation (§9) is load-bearing, not defence in depth.

## Decision

### 1. Trust boundary

A browser exchanges an API key once (`POST /api/session`) for an opaque, server-side session represented by the cookie
`__Host-bops_session`. The session is bounded (idle and absolute expiry), revocable (logout, credential change), bound
to the credential **and** the credential's current secret value, and stored only as digests. Bearer authentication for
the CLI and API clients is unchanged. Neither CORS, OAuth/OIDC/SSO, user accounts, refresh tokens nor production SPA
hosting is introduced.

### 2. One credential authority

The credential loop moves out of `ApiKeyAuthenticationHandler` into one internal singleton in `bOps.Api`,
`ApiCredentialAuthority` (dependencies: `IOptions<ApiAuthenticationOptions>`, `ISecretProvider`). It owns:

- `FindByPresentedKey(string presented)` — **the existing algorithm verbatim**: configuration order, skip empty `Id`,
  resolve the secret, skip empty secrets, SHA-256 both, `FixedTimeEquals`, first match wins. Both the Bearer handler
  (after its unchanged prefix/trim/empty handling) and `POST /api/session` (after the same trim) call it.
- `FindBySessionBinding(string credentialId, ReadOnlySpan<byte> token, ReadOnlySpan<byte> storedBinding)` — §4,
  including the Bearer-equivalence check of §4, which is evaluated with the **same** ordered resolution as
  `FindByPresentedKey`, not with a second, independently written loop. How the shared resolution is factored is an
  implementation choice; there must be one ordered credential-resolution semantic, so that the same configured
  credential set and the same secret always yield the same effective credential under Bearer and under a session.
- `CreatePrincipal(match, scheme)` — the existing claim construction verbatim (`NameIdentifier`, `Name`, distinct
  case-insensitive `Role` claims); only the identity's authentication type differs (`bops-api-key` or
  `bops-browser-session`).

A match exposes `Id`, `DisplayName`, the role list and `SecretDigest = SHA-256(UTF-8(resolved secret))`; it never
carries the secret itself. The Bearer handler's observable behaviour (status codes, `NoResult`/`Fail` cases, claims,
challenge) must be identical before and after the extraction; existing Bearer tests plus the regression tests of §18
guard it.

### 3. Session token and digest

- **Token:** 32 bytes from `RandomNumberGenerator` (256 bits of CSPRNG entropy), generated per login, never derived.
- **Cookie value:** the token as unpadded base64url (RFC 4648 §5): exactly 43 characters of `[A-Za-z0-9_-]`. A presented
  value is accepted for lookup only if it is exactly 43 characters of that alphabet, decodes to 32 bytes and re-encodes
  to the identical string (canonical form); anything else is an invalid session (§8). Raw token bytes are zeroed with
  `CryptographicOperations.ZeroMemory` after use; the encoded string is not retained beyond the request.
- **Stored digest:** `token_digest = SHA-256(token bytes)`, stored as a 32-byte BLOB. **Plain SHA-256, no HMAC
  pepper.** Rationale: the token is uniformly random 256-bit material, so a digest reader cannot invert it, and a
  keyed digest would add a new server secret (storage, rotation, loss = mass logout) with no gain: the vault master
  key is optional and Data Protection is not configured. Database *write* access does not let an attacker mint a
  session either, because a usable row also needs the credential binding of §4, which requires the API key.
- **Lookup:** by digest equality on the primary key. No constant-time comparison is needed for the lookup itself: an
  attacker controls the token preimage, not the digest, so index timing reveals nothing usable. Constant-time
  comparison is required where a secret-derived value is compared (§4).
- **Collision:** the primary key rejects a duplicate digest. On a unique-constraint violation during insert the
  server generates a new token once and retries; a second violation is a server error (`500`, no session). The
  probability is 2⁻²⁵⁶ per pair; the rule exists only so the path is defined.
- **No rotation during a session.** A token lives from login to expiry, logout or credential change. Every successful
  login issues a fresh token (§10.1); privilege changes need no rotation because roles are re-resolved per request
  (§4).

### 4. Credential binding (key-version fingerprint) and roles

Each session row stores the credential id and a **binding**:

```text
message = ASCII("bops-session-binding-v1")
          || UInt32BigEndian(byteLength(UTF-8(credentialId)))
          || UTF-8(credentialId)
          || SHA-256(UTF-8(resolved secret of that credential))
binding = HMAC-SHA256(key = token bytes (32), message)
```

- The binding is keyed by the **session token**, which the server never stores. The database therefore holds no
  value that lets anyone test guesses of the API key offline — an unkeyed `SHA-256(apiKey)` fingerprint would, and
  operator-chosen keys can be low-entropy.
- **Candidate (every cookie-authenticated request):** walk `Authentication:ApiKeys` in configuration order; for each
  entry whose `Id` equals the stored `credential_id` (ordinal) and whose secret resolves non-empty, compute the
  binding from the presented token and compare it with the stored binding using `FixedTimeEquals`. The first entry
  whose binding matches is the **candidate** `E`. No candidate → the session is invalid and its row is deleted (§8).
- **Bearer equivalence (mandatory, after the binding matches).** A valid binding proves only that `E`'s current secret
  is the one the session was created with; it does not prove that `E` is the credential Bearer would select for that
  secret, because neither `Id` nor secret is required to be unique across entries. The handler must therefore also
  prove

  ```text
  FindByPresentedKey(E.resolvedSecret) == E
  ```

  where `FindByPresentedKey` is the ordered Bearer resolution of §2 (configuration order, skip empty `Id`, skip empty
  secret, SHA-256 + `FixedTimeEquals`, first match wins) and `==` means the **same configuration entry** (same
  position in the ordered list), not merely an entry with the same `Id`. Equivalently: the first entry in Bearer order
  whose `SecretDigest` equals `E.SecretDigest` is `E` itself. (A session can exist only for a secret that a trimmed
  login key matched exactly, so `E`'s secret has no surrounding whitespace while its binding matches, and the digest
  form and the presented-key form select the same entry.) The check runs on resolved secrets in memory; nothing new is
  stored. If an **earlier** entry with **any** `Id` resolves to the same secret, Bearer would select that entry, and
  the session is invalid: `401`, row deleted, deletion cookie, revocation category `credential_changed` (§13). There
  is no fallback: the session neither continues as `E` nor becomes the earlier entry's identity, and neither entry's
  roles are used. A session stays bound to its original credential identity; when current Bearer resolution no longer
  maps its secret to that identity, the session is over.
- **Validation order (normative)** — §8 steps 1–6 expand to:
  1. parse and canonicalise the session token (§3);
  2. find the persisted row by digest;
  3. idle and absolute expiry (§6);
  4. locate the candidate `E` by stored `credential_id` among current configuration entries;
  5. recompute and verify the binding against `E`'s current secret (`FixedTimeEquals`);
  6. resolve `E`'s secret through the current Bearer ordering;
  7. require the Bearer-selected entry to be `E`;
  8. derive the **current** claims and roles from `E` (`CreatePrincipal`);
  9. authenticate.

  Any failure in steps 4–7 → `401`, row deleted, deletion cookie (§8). Authorization policies then run normally on
  the principal. Hence, for every valid browser session, session identity and roles equal the current Bearer identity
  and roles for the same key — in particular session authority never exceeds current Bearer authority for that key.
- **Deterministic cases** (`id / secret / roles`, configuration order top to bottom; session created for `ops` with
  `K1`):

  | Case | Configuration now | Bearer `K1` selects | Session result |
  |---|---|---|---|
  | A — normal | `ops / K1 / administrator` | `ops` | valid; roles = current roles of `ops` |
  | B — rotated | `ops / K2 / administrator` | — (no `ops` match) | binding mismatch → `401`, row deleted |
  | C — removed | no `ops` entry | — | no candidate → `401`, row deleted |
  | D — same secret earlier under another id | `readonly / K1 / viewer`, then `ops / K1 / administrator` | `readonly` | binding matches `ops`, Bearer selects `readonly` → `401`, row deleted; never `ops`, never `readonly` |
  | E — duplicate secret later | `ops / K1 / administrator`, then `readonly / K1 / viewer` | `ops` | valid; roles = current roles of `ops` |

  Duplicate `Id`s and duplicate secrets remain permitted configuration (no new uniqueness rule, Bearer semantics
  unchanged); the equivalence check alone guarantees the invariant.
- **Credential id no longer configured** → no candidate → invalid, row deleted, `401`.
- **Same id, secret changed (rotation)** → binding mismatch → invalid, row deleted, `401`. Changing only the
  `SecretReference` to a variable holding the *same* value is not a rotation and keeps the session.
- **Secret resolves empty** (variable missing) → no candidate → invalid, row deleted, `401` (fail closed).
- **Roles are re-resolved, never snapshotted.** Claims come from the candidate entry `E` that passed the binding and
  Bearer-equivalence checks, on every request,
  exactly as for Bearer. A role granted or removed takes effect at the same moment it would for a Bearer request with
  that key (the next host start, since options are snapshotted); a removed role can never survive in a session.
  A session therefore can never be more privileged than the same key presented as Bearer at that moment.
- Because key and role changes require a host restart and sessions persist across restarts, invalidation happens
  lazily on the first request after the restart that presents the session; a startup sweep (§5.6) removes rows whose
  credential id is no longer configured. Rotation cannot be detected without the token, so rotated-key rows are
  removed at their next presentation or by expiry cleanup — they are unusable either way.

### 5. Persisted record and store

#### 5.1 Placement

`bOps.Api` (host), not `bOps.Memory`: the session is an HTTP-host concern of the browser channel, like
`TaskIdempotencyStore` and `ApiApprovalProvider`. An internal interface `IBrowserSessionStore` with one implementation
`SqliteBrowserSessionStore` lives in `bOps.Api`; tests may substitute it. `bOps.Api` already reaches
`Microsoft.Data.Sqlite` through `bOps.Memory`; the implementation adds a direct `PackageReference` only if the build
requires it. No generic SQL is exposed: the interface has exactly these operations.

| Operation | Semantics |
|---|---|
| `CreateAsync(record, supersededDigest?)` | One `BEGIN IMMEDIATE` transaction: `INSERT` the new row; if `supersededDigest` is given, `DELETE` that row; commit. Duplicate digest → `DuplicateSessionDigestException` (caller retries once, §3). |
| `FindAsync(digest)` | One `SELECT` of the row or `null`. No write. |
| `TouchAsync(digest, now, idle, absolute)` | Conditional `UPDATE` of §6.3; returns whether a row changed. |
| `DeleteAsync(digest)` | `DELETE` by digest; idempotent. |
| `DeleteExpiredAsync(now, idle, absolute)` | `DELETE` every row expired under §6.2; returns the count. |
| `DeleteUnknownCredentialsAsync(configuredIds)` | `DELETE` rows whose `credential_id` is not in the configured id set (startup sweep). |

#### 5.2 File and schema

Its own file, `BrowserSession:FilePath` (default `sessions.db`, relative to the working directory like the other
stores). WAL, `busy_timeout=5000`, `synchronous=FULL` (a logout `204` must not be undone by a crash), owner-only file mode
on Unix (Windows inherits the directory ACL, as the other stores do). Deleting `sessions.db` while the API is stopped
revokes every browser session — a documented operator kill switch.

```sql
CREATE TABLE IF NOT EXISTS browser_sessions (
    token_digest                BLOB    NOT NULL PRIMARY KEY CHECK (length(token_digest) = 32),
    credential_id               TEXT    NOT NULL CHECK (length(credential_id) BETWEEN 1 AND 256),
    credential_binding          BLOB    NOT NULL CHECK (length(credential_binding) = 32),
    created_at_unix_ms          INTEGER NOT NULL,
    last_seen_at_unix_ms        INTEGER NOT NULL,
    absolute_expires_at_unix_ms INTEGER NOT NULL CHECK (absolute_expires_at_unix_ms > created_at_unix_ms)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS ix_browser_sessions_absolute ON browser_sessions(absolute_expires_at_unix_ms);
CREATE INDEX IF NOT EXISTS ix_browser_sessions_last_seen ON browser_sessions(last_seen_at_unix_ms);
```

Timestamps are UTC Unix milliseconds from `TimeProvider.GetUtcNow()`, so every expiry comparison is exact integer
arithmetic in C# and SQL (the other stores' ISO-text columns are not compared arithmetically). **Not stored:** the raw
token, the API key, any secret digest, the `Authorization` header, the cookie string, the "keep signed in" choice (the
cookie attribute is decided once at login and the server's expiry rules do not depend on it), a revocation flag
(revocation is deletion), user agent or IP address.

#### 5.3 Versioning and migration

`PRAGMA user_version`: `0` on a new file → create the schema and set `1` in one transaction; `1` → open; greater than
`1` (written by a newer bOps) → refuse to start with a message naming the file. This is the only schema version
HARDEN-10 defines; there is nothing to migrate from (no browser secret exists today). Fresh installs, existing
installations without `sessions.db`, restarts with live sessions and upgrades are all the same path. No destructive
migration; backup guidance: `sessions.db` holds no secret usable without a cookie, and losing it only signs browsers
out.

#### 5.4 Concurrency (fail closed)

- **Simultaneous requests on one session:** each reads independently (WAL readers do not block); touches are
  monotonic (§6.3), so concurrent touches cannot move `last_seen` backwards.
- **Logout racing validation:** the linearization point is the validation read. A request whose read completed before
  the logout's `DELETE` committed finishes as authenticated; any read that starts after the commit finds no row and
  gets `401`. No cache exists, so there is no window beyond in-flight requests.
- **Key rotation racing validation:** options are snapshotted per process; rotation requires a restart, so no
  in-process race exists. After restart every validation uses the new secret.
- **Expiry racing touch:** the touch `UPDATE` re-checks expiry in its `WHERE` clause, so a touch can never resurrect a
  session that expired between read and write.
- **Login supersede:** insert-new and delete-old are one transaction.
- **Store failure:** an exception from `FindAsync` propagates (the request fails as a server error through §13's
  exception handler); it never authenticates. A failure of `TouchAsync`, of a best-effort `DeleteAsync` on an already
  invalid session, or of cleanup is logged without secrets and does not change the authentication outcome.

#### 5.5 No caching

Every cookie-authenticated request reads the store. Revocation is therefore effective at the next request.

#### 5.6 Cleanup (bounded)

- **On store open (startup):** `DeleteExpiredAsync`, then `DeleteUnknownCredentialsAsync` with the configured ids.
- **Opportunistic:** after a successful login, if at least 5 minutes have passed since the last cleanup run in this
  process (in-memory timestamp from `TimeProvider`), run `DeleteExpiredAsync`.
- **Per session:** a session found expired or invalid during validation is deleted immediately.
- **Bound:** rows are created only by logins, which are rate-limited (§11); every created row becomes removable after at
  most the absolute lifetime and is removed within 5 minutes of the next login after that, or at the next start.
- **Failure:** logged, retried at the next trigger; it never authenticates an expired row because validation checks
  expiry explicitly (§6.2). No hosted background service is added.

### 6. Lifetime

#### 6.1 Defaults (configurable, §12)

Idle timeout **60 minutes**; absolute lifetime **12 hours**; touch granularity **60 seconds** (fixed).

#### 6.2 Exact rules

At login, with `now = TimeProvider.GetUtcNow()` in Unix ms: `created_at = last_seen_at = now`,
`absolute_expires_at = now + AbsoluteTimeout`.

A session is **expired** at instant `now` if **any** of:

```text
now >= absolute_expires_at
now >= created_at   + AbsoluteTimeout(current configuration)
now >= last_seen_at + IdleTimeout(current configuration)
```

Equality is expired. The second rule means lowering `AbsoluteTimeout` shortens existing sessions after a restart;
raising it never lengthens them (the stored `absolute_expires_at` still applies). **Absolute expiry never slides.**

#### 6.3 Idle sliding — one rule

`last_seen_at` advances only when a request (a) authenticated with this session, (b) passed the CSRF gate (§9),
(c) was not rejected by the rate limiter, and (d) passed authorization — i.e. it reached the endpoint. Then, if
`now − last_seen_at >= 60 000` ms:

```sql
UPDATE browser_sessions SET last_seen_at_unix_ms = $now
WHERE token_digest = $digest
  AND last_seen_at_unix_ms < $now
  AND last_seen_at_unix_ms + $idleMs > $now
  AND absolute_expires_at_unix_ms > $now
  AND created_at_unix_ms + $absoluteMs > $now;
```

Consequences, stated so nobody infers otherwise: safe (GET) and unsafe requests both extend idle; requests rejected
with `401`, `403` (CSRF gate or role policy) or `429` never extend it — in particular a CSRF-rejected request never
does; a request that reached the endpoint extends it even if the endpoint then answers with an error (`400`, `404`,
`409`, …), because the operator did act with a valid session. The effective idle window is between `IdleTimeout − 60 s` and `IdleTimeout`. **The UI's background
polling is an authorized request and keeps an open, polling tab alive up to the absolute lifetime.** Idle expiry
therefore protects closed or abandoned browsers and restored-but-unused tabs, not an unattended open dashboard; the
absolute lifetime bounds the latter. (Rejected alternative: a client-declared "background request" header — advisory,
unverifiable by the server, and it would only shorten the UI's own session.)

### 7. Cookie

Issued only by a successful `POST /api/session`:

| Attribute | Value |
|---|---|
| Name | `__Host-bops_session` |
| Value | 43-character base64url token (§3) |
| `HttpOnly` | yes |
| `Secure` | yes — always, in every environment; there is no switch to disable it |
| `SameSite` | `Strict` |
| `Path` | `/` |
| `Domain` | absent (host-only; required by the `__Host-` prefix) |
| Default (`keepSignedIn` false or absent) | no `Max-Age`, no `Expires` → browser-session cookie |
| `keepSignedIn: true` | `Max-Age = floor((absolute_expires_at − now) / 1000)` seconds, which at issuance equals `AbsoluteTimeout` in whole seconds; no `Expires` |

Built with ASP.NET Core `CookieOptions` (`HttpOnly = true`, `Secure = true`, `SameSite = Strict`, `Path = "/"`,
`Domain = null`, `MaxAge` only for persistent). Measured serialization: `__Host-bops_session=<v>; path=/; secure;
samesite=strict; httponly` and, persistent, `…; max-age=43200; path=/; …`.

**Deletion** uses `Response.Cookies.Delete("__Host-bops_session", options)` with the same `Path`, `Secure`, `HttpOnly`,
`SameSite` and no `Domain` (measured: `__Host-bops_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure;
samesite=strict; httponly`, removed by both browsers). It is emitted (a) by `DELETE /api/session` when the request was
cookie-authenticated or presented an invalid cookie, and (b) on every `401` challenge issued by the session scheme for a
request that presented a session cookie. It is never emitted on other responses, so a login response never carries
two `Set-Cookie` lines for the name.

Even a browser-session cookie can be restored by a browser's "continue where you left off"; the server-side idle and
absolute limits are what bound it.

### 8. Authentication composition

Three schemes:

| Scheme | Role |
|---|---|
| `bops` (new default; ASP.NET Core *policy scheme*) | Forwards every operation (authenticate, challenge, forbid) to the scheme chosen by `ForwardDefaultSelector`. |
| `bops-api-key` (existing) | Unchanged Bearer handler, now calling `ApiCredentialAuthority`. |
| `bops-browser-session` (new) | Cookie session handler. |

**Selector — fixed precedence, decided from the request, before any credential is checked:**

```text
if the request has an Authorization header (any value, including empty)   -> bops-api-key
else if the request's Cookie header(s) contain the name __Host-bops_session -> bops-browser-session
else                                                                         -> bops-api-key   (unchanged anonymous behaviour)
```

An explicitly supplied `Authorization` header **always** decides: a valid Bearer wins over any cookie; an invalid,
empty or non-Bearer `Authorization` value with a valid cookie is `401` — **never a silent downgrade to the cookie**.
When Bearer is selected the cookie is neither looked up, touched, deleted nor CSRF-checked.

**Session handler (`HandleAuthenticateAsync`)**, in order; every failure returns `Fail` with one constant message
(`"The browser session is not valid."`) whatever the reason:

1. Parse the raw `Cookie` header value(s). Exactly one `__Host-bops_session` pair must exist; zero cannot happen
   (selector), two or more → fail.
2. Canonical 43-character base64url → 32 bytes (§3), else fail.
3. `digest = SHA-256(token)`; `FindAsync(digest)`; no row → fail.
4. Expiry (§6.2) → `DeleteAsync`, fail.
5. Credential (§4, validation order steps 4–7): candidate by stored id, binding, Bearer equivalence → no candidate,
   binding mismatch, or Bearer resolution of the candidate's secret selecting a different entry → `DeleteAsync`, fail.
6. Success: `ApiCredentialAuthority.CreatePrincipal(match, "bops-browser-session")`; attach an internal request feature
   carrying the digest and `last_seen_at` (used by the CSRF gate to recognise cookie authentication, by the touch, and
   by logout).

`HandleChallengeAsync` returns `401` with an empty body and, when a session cookie was presented, the deletion cookie
of §7. `HandleForbiddenAsync` is the default `403`.

**Authorization is untouched:** the four policies stay `RequireRole(...)` on the default scheme and therefore apply
identically whichever scheme produced the principal. No policy, role constant or endpoint authorization changes, and
no endpoint names a scheme. The global rate limiter keys on `NameIdentifier`, so Bearer and cookie use of the same
credential share one bucket.

**Pipeline order:**

```text
UseExceptionHandler      (new, all environments, §13)
UseAuthentication        (default scheme "bops")
UseBrowserSessionCsrf    (new, §9; acts only on cookie-authenticated unsafe requests)
UseRateLimiter
UseAuthorization
UseBrowserSessionTouch   (new, §6.3; reached only when authorization succeeded)
endpoints
```

### 9. CSRF gate

Applies **only** when the effective authentication is `bops-browser-session` (the request feature of §8 step 6 is
present) **and** the method is unsafe. Unsafe = every method other than `GET`, `HEAD`, `OPTIONS`, `TRACE` (this covers
`POST`, `PUT`, `PATCH`, `DELETE` and any extension method). Bearer requests are never checked. The gate runs before
rate limiting, authorization, model binding and any body read, so a rejected request has no effect and no body (for
example a plugin archive) is consumed.

Both conditions are required; failure of either → `403` with body `{"code":"csrf_rejected","message":"The request
was refused by the browser-session origin check."}`, `Cache-Control: no-store`, no cookie change, no idle extension.

**(a) Custom header.** The request carries exactly one `X-bOps-Request` header field value, and it is exactly the
string `1` (ordinal). Missing, empty, `true`, ` 1`, `1, 1` or two fields → reject. Because the header is not
CORS-safelisted, a cross-origin page cannot send it without a preflight, and no CORS policy exists to answer one.

**(b) Same origin, against configured UI origins.** The trusted set is `BrowserSession:Origins` (§12), canonicalized at
startup to tuples `(scheme, host, port)`.

1. If the request has **at least one** `Origin` header field: there must be exactly one field whose value contains no
   comma; the value must not be `null` (any case); it must parse (`Uri.TryCreate`, absolute) as `scheme://host[:port]`
   with scheme `http` or `https`, no user-info, no path other than empty, no query, no fragment. Its tuple must equal a
   trusted tuple. `Referer` is **not** consulted when an `Origin` field is present, even if `Origin` fails.
2. Only if **no** `Origin` field is present: exactly one `Referer` field; it must parse as an absolute `http`/`https`
   URL without user-info; its `(scheme, host, port)` tuple (path and query ignored) must equal a trusted tuple.
3. Neither present → reject.

**Canonical comparison:** scheme compared lower-case ordinal; host = `Uri.IdnHost` lower-cased invariant, compared
ordinal (no suffix, prefix, substring, wildcard or DNS resolution; IPv6 literals keep their brackets); port =
`Uri.Port`, so an omitted port equals the scheme default (`http` 80, `https` 443) and `http://localhost:80` equals
`http://localhost`. `localhost`, `127.0.0.1` and `[::1]` are **different** origins, as they are for the browser; a
trailing-dot host is a different host. `Host`, `X-Forwarded-Host`, `X-Forwarded-Proto` and `Forwarded` are never read:
the app has no trusted-proxy boundary, and the dev proxy rewrites `Host` (measured).

Measured browser behaviour that makes this sufficient: both browsers send `Origin` on same-origin non-GET `fetch`; the
bOps UI sets no `Referrer-Policy` (a `no-referrer` policy would make the browser send `Origin: null`, which this gate
rejects — the UI must not adopt one).

### 10. Endpoints

All three responses carry `Cache-Control: no-store`. Error bodies use the existing `TaskErrorResponse`-shaped
`{ "code", "message" }` with fixed messages.

#### 10.1 `POST /api/session` — login

Anonymous (the request's own principal, if any, is ignored), rate-limited by `bops.session-login` (§11). Steps, in
order:

1. **Origin gate (always, whatever the scheme):** §9 (a) and (b). Failure → `403 csrf_rejected`. The body is not read
   and the key is not validated, so a foreign origin learns nothing about any key. (If a valid session cookie was
   presented, the global gate already rejected the request identically.)
2. **Media type:** `Content-Type` must be `application/json` (parameters allowed; `charset`, if present, must be
   `utf-8`). Otherwise `415 unsupported_media_type`. A plain HTML form or `text/plain` request can never reach step 4.
3. **Size:** at most 4 096 bytes, enforced while reading (`BoundedRequestStream`). Larger → `413 payload_too_large`.
4. **Shape:** a JSON object `{ "apiKey": string, "keepSignedIn": boolean? }` parsed with source-generated
   `System.Text.Json` metadata, unknown members disallowed, duplicate properties disallowed, trailing content
   disallowed. `apiKey` is trimmed exactly as the Bearer handler trims; missing, non-string or empty after trimming →
   `400 invalid_request`. `keepSignedIn` absent → `false`; non-boolean → `400`. Parse failures return the fixed body and
   never log the payload or the exception message.
5. **Credential:** `ApiCredentialAuthority.FindByPresentedKey`. No match → `401 invalid_credential` ("The API key is not
   valid."). Nothing else changes: an already presented session stays valid, no cookie is set or deleted.
6. **Role floor:** the matched credential must hold `viewer` (the UI's minimum and `/api/session/me`'s policy).
   Otherwise `403 viewer_role_required`, no session. Bearer use of such a key is unaffected.
7. **Create:** new token (§3), digest, binding (§4), row (§6.2). If the request presented exactly one syntactically
   valid (§3 canonical) `__Host-bops_session` value, its digest is passed as `supersededDigest`, so the new row is inserted and the presented
   session deleted in one transaction — whether the presented session was valid, expired or belonged to another
   credential. A failed login (any step above) never revokes anything.
8. **Opportunistic cleanup** (§5.6), after commit; failure ignored after logging.
9. **Response:** `200` with `{ "id", "displayName", "roles" }` (the same shape as `GET /api/session/me`) and the cookie of
   §7. The API key, token, digest and binding never appear in it.

Why login needs the origin gate even though it does not authenticate with the cookie: the `SameSite` measurement shows a
same-site page on another `localhost` port can make a simple POST; without the gate such a page could try keys
(responses are opaque to it, but the attempt would create a session for a valid key and log the operator into
whatever key the page chose). With the gate, the media-type check and the absence of CORS, a hostile origin can neither
submit a valid request shape nor read any response. There is no CORS policy, and none may be added.

#### 10.2 `GET /api/session/me`

Unchanged route, policy (`bops.viewer`) and body `{ "id", "displayName", "roles" }`; now reached through the `bops`
policy scheme, so it works with Bearer or cookie. Authenticated → `200`. Expired, revoked, rotated, removed, malformed
or absent credential → `401` with an empty body (plus cookie deletion when a session cookie was presented). The response
never says *why* a session is invalid and never contains the key, token, digest or binding. A credential without
`viewer` → `403` (unchanged).

#### 10.3 `DELETE /api/session` — logout

Requires an authenticated principal (default policy; no role).

| Effective scheme | Behaviour |
|---|---|
| Browser session (CSRF gate passed) | `DeleteAsync(digest)` (idempotent), deletion cookie, `204`. Replaying the old cookie afterwards → `401`. |
| Bearer | `204`, no server change, no `Set-Cookie`. A Bearer request never revokes a browser session, even if it also carried a cookie (Bearer precedence, §8). |
| Unauthenticated (including an invalid/expired cookie) | `401` from the challenge, which deletes a presented cookie (§7). |

The UI never sends `Authorization`, so its logout is always cookie-authenticated; `204` and `401` both mean "signed
out" to it (§14.5).

### 11. Login rate limiting

Reuses the existing ASP.NET Core rate limiter; the global limiter stays as is and still applies first.

| | |
|---|---|
| Policy name | `bops.session-login`, attached to `POST /api/session` only (`RequireRateLimiting`) |
| Implementation | an `IRateLimiterPolicy<string>` so it carries its own `OnRejected` |
| Partition | `HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"` (never a credential, never body content) |
| Limiter | fixed window: `PermitLimit = 10`, `Window = 1 minute`, `QueueLimit = 0`, `AutoReplenishment = true` |
| Counting | every request that reaches the rate-limiting middleware for this endpoint, successful or not, including those the login's own origin gate then rejects; a request already rejected by the global CSRF gate (it presented a valid session cookie without the header/origin, §8 order) is answered `403` before it is counted |
| Rejection | `429`, `Retry-After: <whole seconds, rounded up>` from the lease's `MetadataName.RetryAfter` (omitted only if the lease has none), `Cache-Control: no-store`, body `{"code":"rate_limited","message":"Too many sign-in attempts. Try again later."}` |

Fixed constants, not configuration (no existing limit is configurable). The response is identical for valid and
invalid keys, and an invalid key always yields the same `401`, so neither path enumerates credentials (there are no
usernames to enumerate; only key guessing, which this bounds to 10/minute per address in addition to the global
bucket — no weaker than Bearer guessing today). Behind the dev proxy every browser shares the loopback partition,
which is acceptable for a single-operator local tool.

### 12. Configuration

Additive section; invalid values **fail startup** with a message naming the key (never clamped). Validation runs at
startup (`ValidateOnStart` or equivalent eager check).

| Key | Type | Default | Valid |
|---|---|---|---|
| `BrowserSession:Origins` | string, comma-separated (scalar, like `ApiKeys:*:Roles`, so hierarchical configuration cannot merge array indices and silently retain an extra trusted origin) | `http://localhost:4200` | 1–8 entries after trimming; each `scheme://host[:port]` exactly (no path, query, fragment, user-info, wildcard); `https`, or `http` only when the host is `localhost`, `127.0.0.1` or `[::1]` (the only `http` hosts where browsers accept `Secure` cookies); no duplicates after canonicalization |
| `BrowserSession:IdleTimeout` | `TimeSpan` | `01:00:00` | 00:05:00 – 1.00:00:00, whole seconds, ≤ `AbsoluteTimeout` |
| `BrowserSession:AbsoluteTimeout` | `TimeSpan` | `12:00:00` | 00:15:00 – 7.00:00:00, whole seconds |
| `BrowserSession:FilePath` | string | `sessions.db` | non-empty |

The persistent-cookie maximum is not a separate key: it is always the remaining absolute lifetime (§7). There is no key
to disable `Secure`, `HttpOnly`, `SameSite=Strict`, the `__Host-` prefix or the CSRF gate. The shipped
`appsettings.json` carries the section with the defaults; the code defaults are identical. Login-limit numbers, token
size, touch granularity and cleanup interval are constants (§6, §11).

### 13. Negative-security contract (secret leakage)

The raw API key and the raw cookie value (and the token bytes) must not appear in: any API response body or header
(other than the cookie's own `Set-Cookie` at login), application logs, structured logs, the audit log, exception
messages, `sessions.db`, browser `localStorage`/`sessionStorage`/IndexedDB, URLs or history, or Angular state after the
login request completes. The token digest and binding are not logged either (no operational need).

Mechanisms the implementation must provide:

- **No request logging** of bodies, `Authorization` or `Cookie`; HARDEN-10 adds none. If request logging is ever added it
  must exclude these (out of scope here, recorded as a constraint).
- **Fixed messages** for every `AuthenticateResult.Fail` and every error body in §9–§11; no exception text reaches a
  response or a log line on the login path.
- **Explicit exception handler in every environment** (`AddProblemDetails` + `UseExceptionHandler` as the outermost
  middleware): an unhandled exception yields a problem body with no request data. This prevents the developer
  exception page — automatic in `Development`, the supported topology — from rendering `Cookie` and `Authorization`
  headers into a body that page script could read (which would defeat `HttpOnly`).
- **Log events (no secrets):** session created (credential id, persistent yes/no), login rejected (category only:
  `invalid_credential`, `viewer_role_required`, `csrf_rejected`, `invalid_request`), session revoked (category:
  `logout`, `superseded`, `expired`, `credential_changed`, `credential_removed`; credential id when known).
  Authentication events are host channel events, not runtime actions; they are logged, not added to the audit schema
  (an audit-schema change is an `bOps.Abstractions` change with its own ADR; Bearer authentication is not audited
  today either).

### 14. Angular

#### 14.1 State machine (`AuthService`)

```text
status: 'initializing' | 'authenticated' | 'unauthenticated' | 'session-expired' | 'unavailable'
identity: CurrentIdentity | null           (non-null only in 'authenticated')
authenticated = computed(status === 'authenticated')
sessionExpired = computed(status === 'session-expired')
```

The `token` signal is **removed**; `AuthService` never holds a key.

| From | Event | To |
|---|---|---|
| `initializing` | boot `GET /api/session/me` → 200 | `authenticated` (identity from body) |
| `initializing` | → 401 | `unauthenticated` (no "expired" message) |
| `initializing` | → status 0, 429, 502/503/504, other 5xx, or 10 s timeout | `unavailable` |
| `unavailable` | retry (manual button, `online`, `visibilitychange`, or HARDEN-4 `backoffMs` schedule) → 200 / 401 / failure | `authenticated` / `unauthenticated` / stays |
| `unauthenticated`, `session-expired`, `unavailable` | `signIn` → 200 | `authenticated` |
| `authenticated` | any bOps API `401` (§14.4) | `session-expired` (identity cleared) |
| `authenticated` | `signOut` → 204 or 401 | `unauthenticated` |
| `authenticated` | `signOut` → anything else | stays `authenticated`, error shown ("Sign-out could not be confirmed — try again"); the server session may still be valid, so the UI must not pretend otherwise |

#### 14.2 Boot without races

`provideAppInitializer(() => inject(AuthService).restore())`: bootstrap of the root component waits for the boot
`GET /api/session/me` (10 s timeout). `restore()` never rejects. Root stores are created when `App` is constructed,
i.e. after the initializer, so their `onInit` sees the final status; they already gate every poll on
`auth.authenticated()`. `App` renders by status: `authenticated` → the shell with `<router-outlet>`; every other
status → the login screen (with the expired banner for `session-expired`, an "API unreachable — Retry" banner for
`unavailable`); `initializing` renders nothing (defensive; not reachable after the initializer). Login stays a
component, not a route, so the URL the operator was on is preserved through login.

#### 14.3 Login

1. The operator enters the key; a checkbox "Keep me signed in on this device" (unchecked by default, not remembered)
   maps to `keepSignedIn`.
2. `AuthService.signIn(apiKey, keepSignedIn)` sends `POST /api/session` with a JSON body; the key is a function
   argument, never assigned to a signal or service field.
3. The `Login` component clears its key field signal when the request completes, **whatever the outcome**.
4. On `200`, identity comes from the response body; status `authenticated`. No storage API is called.
5. Failure messages: `401` → existing "Authentication failed…"; `403 viewer_role_required` → "This key cannot open the
   web UI (viewer role required)"; `403 csrf_rejected` → "Open bOps at its configured address"; `429` → "Too many
   sign-in attempts; try again in N s" (from `Retry-After`); 0/502/503/504 → existing "Cannot reach the bOps API";
   other → existing HTTP error text.
6. The login note (`login.keyNote`) changes to: the key is exchanged once for a browser session and is not kept by the
   page or stored in the browser; plus a hint for the checkbox. English and Italian.

#### 14.4 Interceptor

- A request is a **bOps API request** iff its URL is relative and starts with `/api/`. Absolute URLs never get bOps
  headers.
- Never sets `Authorization`.
- For bOps API requests with an unsafe method (§9 definition) adds `X-bOps-Request: 1`.
- Relies on same-origin `fetch` (`withFetch()`, default `credentials: 'same-origin'`) to attach the cookie; no
  `withCredentials`.
- On an `HttpErrorResponse` with status `401` from a bOps API request **while `status === 'authenticated'`**, calls
  `auth.expireSession()` (idempotent; no-op in any other status), then rethrows. Boot, login and logout 401s happen in
  other statuses or are handled by their callers, so they never produce "session expired".

#### 14.5 Logout

`signOut()` sends `DELETE /api/session` (with `X-bOps-Request: 1`) and follows §14.1. A deliberate sign-out ends the
watch (existing behaviour); it never leaves a pending watch for the next identity.

#### 14.6 One session-expired flow (with HARDEN-4)

The interceptor is the single place that turns a `401` into `session-expired`. Consequences for existing code:

- Polling stores stop by their existing `auth.authenticated()` checks; no new loop logic.
- The watcher keeps its own HARDEN-4 behaviour (stop, `sessionExpired`). `TasksStore.onConnection` records
  `pendingWatch` unless `auth.status() === 'unauthenticated'` (a deliberate sign-out) — the current
  `!auth.authenticated()` guard would discard the pending watch when the interceptor has already switched to
  `session-expired`, so it changes to the explicit status check.
- The "clear selection on sign-out" effect fires on a transition to `unauthenticated`, never on `session-expired`.
- After re-login, the existing `pendingWatch` effect restarts the watch with its `expectedAttempt`; the selection and URL
  (§15) are intact because the dashboard store is a root singleton and login is not a route.
- Text: the existing `login.sessionExpired` / `dashboard.connection.sessionExpired` messages already say "Session
  expired — sign in again …"; they stay.

### 15. Refresh and running-task watch restoration

The selected task becomes durable UI state in the **URL** of the dashboard: `/dashboard?task=<uuid>`. A task id is not
a secret (it already appears in API URLs; every read is authorized server-side).

- **Writing it:** `selectTask`, a successful `start` and a successful `resume` navigate to the current route with
  `queryParams: { task: <id> }`, `queryParamsHandling: 'merge'`, `replaceUrl: true`. `clearSelection` removes it. A
  watch ending in `notFound` (404) removes it.
- **Reading it after F5:** boot restores the session (§14.2); the shell renders the dashboard; the dashboard reads
  `task` once on init. If it is a canonical UUID and differs from the store's `selectedTaskId`, it calls
  `TasksStore.selectTask(id)`, which fetches the task and, if `status === Running`, starts the HARDEN-4 watch with
  `expectedAttempt = snapshot.executionAttempt`. A malformed value is removed from the URL and ignored.
- **Task finished during the reload:** the fetched snapshot is terminal; it is shown, no watch starts.
- **Task interrupted** (`Running`, `executing: false`): the watch runs and the HARDEN-4 Interrupted presentation
  applies.
- **Task gone:** 404 → the existing "task gone" error, parameter removed.
- **Session expired, then re-login without reload:** §14.6 (`pendingWatch`).
- **Session expired, then reload, then login:** the reload shows the login screen at `/dashboard?task=…`; after login
  the dashboard reads the parameter and restores the watch as above.
- **Scope, stated explicitly:** the watch is a dashboard view. F5 on the dashboard with a selected task restores the
  watch; F5 on another page restores the session and that page, and the dashboard opens without a selection (its URL
  carried none). This is the acceptance criterion's meaning for HARDEN-10 and E2E-8.

### 16. Security matrix

| Threat / property | Mitigation (section) |
|---|---|
| API-key theft from browser persistence | Key never stored; passed as an argument and dropped when the login request completes; `token` signal removed (§14.3) |
| API key in URL/history/logs | JSON body only, never a query string; no request logging; fixed error texts (§10.1, §13) |
| Session token theft by page script | `HttpOnly` (§7); developer exception page cannot echo cookies (§13) |
| Session token at rest | Only `SHA-256(token)` and a token-keyed binding stored (§3, §4) |
| API key guessable from the database | Binding keyed by the never-stored token; no unkeyed key fingerprint (§4) |
| Database write → forged session | Requires the binding, i.e. the API key (§3, §4) |
| Session fixation | Fresh CSPRNG token per login; presented session superseded atomically; tokens never accepted from the client as new (§3, §10.1) |
| CSRF (cross-site) | `SameSite=Strict` + custom header + configured-origin check (§7, §9) |
| CSRF (same-site, other `localhost` port — measured to receive the cookie) | Custom header + configured-origin check; JSON-only login (§9, §10.1) |
| Cross-origin API use / reading responses | No CORS policy; custom header forces an unanswered preflight (§9) |
| Login CSRF / forced login | Origin gate and media type on `POST /api/session` (§10.1) |
| Brute force / key guessing | `bops.session-login` 10/min per address + global limiter; uniform `401` (§11) |
| Session replay after logout | Server-side deletion; no cache (§5.5, §10.3) |
| API-key removal | Credential id absent → `401`, row deleted; startup sweep (§4, §5.6) |
| API-key rotation | Binding mismatch → `401`, row deleted (§4) |
| Stale or escalated privileges | Roles re-resolved from configuration on every request; never snapshotted (§4) |
| Session authority diverging from Bearer (duplicate secret under an earlier entry) | Bearer-equivalence check: the candidate must be the entry Bearer selects for its secret, else `401`, row deleted (§2, §4) |
| Unlimited session lifetime | Idle 60 min + absolute 12 h, absolute never slides, equality expires (§6) |
| Persistent cookie outliving the session | `Max-Age` = remaining absolute lifetime; server checks regardless (§7) |
| Database growth | Rate-limited creation, startup/opportunistic/per-session cleanup (§5.6) |
| Bearer regression | Bearer handler behaviour identical; Bearer precedence; no CSRF on Bearer (§2, §8) |
| Bearer→cookie downgrade with a bad header | Any `Authorization` header selects Bearer (§8) |
| Cookie tossing / duplicate cookies | `__Host-` prefix; exactly one cookie pair or fail (§7, §8) |
| Host-header / forwarded-header spoofing in the origin check | Configured origins only; `Host`/`X-Forwarded-*` never read (§9) |
| Secret logging | Negative contract and tests (§13, §18) |
| Store unavailable | Lookup failure never authenticates (§5.4) |
| Shared `localhost` cookie jar (residual) | Any web server the operator's browser visits on `http://localhost:<any port>` receives the cookie; CSRF from such pages is blocked (§9), token disclosure to a malicious local listener is not. Documented residual risk; operators should not browse untrusted local web apps in the same browser profile. A dedicated host such as `bops.localhost` would isolate the jar — possible follow-up, not in this ADR. |
| XSS in the UI (residual) | Unchanged class of risk: script in the page can act as the operator while the session lives, but can no longer read or exfiltrate the long-lived API key (an improvement over today's in-memory key) |

### 17. Authentication / CSRF matrix (normative)

"Valid origin" = `X-bOps-Request: 1` and an `Origin` in `BrowserSession:Origins`. Protected endpoint = any endpoint
with a role policy (results assume the role is held).

| # | `Authorization` | Cookie | Method | Headers / origin | Effective scheme | Result |
|---|---|---|---|---|---|---|
| 1 | valid Bearer | none | GET | none | Bearer | 200 |
| 2 | valid Bearer | valid | POST | none | Bearer | endpoint result; no CSRF check; cookie not read or touched |
| 3 | valid Bearer | invalid/expired | GET | n/a | Bearer | 200; cookie not read, not deleted |
| 4 | invalid Bearer | valid | GET | n/a | Bearer | **401**; no downgrade; cookie not read |
| 5 | `Basic …`, empty value, or `Bearer` with no credential | valid | GET | n/a | Bearer | **401** |
| 6 | none | valid | GET | n/a | Session | 200; idle touch per §6.3 |
| 7 | none | valid | POST/PUT/PATCH/DELETE | valid origin | Session | endpoint result |
| 8 | none | valid | POST | `X-bOps-Request` missing | Session | **403** `csrf_rejected`; no effect; no touch |
| 9 | none | valid | POST | `X-bOps-Request` ≠ exactly `1`, or repeated | Session | **403** |
| 10 | none | valid | POST | foreign `Origin` (incl. another `localhost` port, `127.0.0.1` vs `localhost`) | Session | **403** |
| 11 | none | valid | POST | `Origin: null` | Session | **403** (Referer not consulted) |
| 12 | none | valid | POST | malformed or multiple `Origin` | Session | **403** |
| 13 | none | valid | POST | no `Origin`, same-origin `Referer` | Session | endpoint result |
| 14 | none | valid | POST | no `Origin`, foreign or malformed `Referer` | Session | **403** |
| 15 | none | valid | POST | neither `Origin` nor `Referer` | Session | **403** |
| 16 | none | valid | HEAD/OPTIONS | none | Session | no CSRF requirement |
| 17 | none | expired (idle or absolute) | GET | n/a | Session | **401**, row deleted, deletion cookie |
| 18 | none | revoked (logged out / superseded) | GET | n/a | Session | **401**, deletion cookie |
| 19 | none | credential removed or rotated | GET | n/a | Session | **401**, row deleted, deletion cookie |
| 20 | none | malformed value or duplicated cookie | GET | n/a | Session | **401**, deletion cookie |
| 21 | none | none | GET protected | n/a | Bearer (anonymous) | **401** (unchanged) |
| 22 | any | any | `POST /api/session` | origin gate fails | — | **403**, key not validated |
| 23 | any | any | `POST /api/session` | valid origin, wrong media type | — | **415** |
| 24 | any | any | `POST /api/session` | valid origin, invalid key | — | **401**, presented session untouched |
| 25 | any | valid or none | `POST /api/session` | valid origin, valid key | — | **200**, new cookie, presented session superseded |
| 26 | any | any | `POST /api/session` | 11th request in the window from the address | — | **429** + `Retry-After` |
| 27 | none | valid | `DELETE /api/session` | valid origin | Session | **204**, row deleted, deletion cookie |
| 28 | valid Bearer | valid | `DELETE /api/session` | n/a | Bearer | **204**, browser session untouched |
| 29 | none | bound to credential B; its secret now resolves via Bearer to an earlier credential A | GET | n/a | Session | **401**, row deleted, deletion cookie; no authority from A or B (§4 case D) |
| 30 | none | bound to credential B; a later credential shares the secret, Bearer still resolves it to B | GET | n/a | Session | 200 as B with B's current roles (§4 case E) |

Authorization policies (`bops.viewer`, `bops.operator`, `bops.approver`, `bops.administrator`) produce the same
200/403 under rows 1 and 6–7 for the same credential and roles.

### 18. Required tests (implementation)

TDD applies to the store, the authority, the gate and the handler; the API is exercised through `TestAppFactory`
(`WebApplicationFactory<Program>`), with a fake `TimeProvider` registered in place of `TimeProvider.System`. The test
client sends `Cookie`, `Origin` and `X-bOps-Request` headers explicitly (the in-memory `TestServer` is `http`, so a
`CookieContainer` would not replay a `Secure` cookie).

- **Session creation:** valid key → 200 + identity + exact `Set-Cookie` attributes (name, `HttpOnly`, `Secure`,
  `SameSite=Strict`, `Path=/`, no `Domain`, no `Max-Age`/`Expires`); `keepSignedIn: true` → `Max-Age` equals
  `AbsoluteTimeout` seconds; invalid key → 401, no `Set-Cookie`; missing viewer → 403; 415/413/400 cases incl. unknown
  and duplicate members; two logins → different tokens; login with a presented session supersedes it (old → 401); failed
  login leaves the presented session valid; origin-gate failures never validate the key (a test double counts
  authority calls).
- **Rate limiting:** 10 requests pass, the 11th → 429 with `Retry-After`, body without the key; window reset with the
  limiter's clock or a fresh partition.
- **Authentication:** every row of §17; every authorization policy under both schemes with the same roles (parameterized
  over the four policies and a representative endpoint each); `GET /api/session/me` under both schemes.
- **Bearer regression:** the existing Bearer tests unchanged and green; new explicit cases for case-insensitive
  `bearer ` prefix, trimming, empty Bearer, non-Bearer scheme, role de-duplication, first-matching entry — all identical
  to pre-HARDEN-10 behaviour.
- **Expiry (fake clock):** idle at `IdleTimeout − 1 ms` valid / at `IdleTimeout` expired; absolute at the boundary
  expired; touch extends idle; touch does not happen within 60 s; absolute never slides across many touches; requests
  rejected with 401/403/429 do not touch; lowering `AbsoluteTimeout` on restart shortens live sessions; raising it does
  not lengthen them.
- **Revocation:** logout → replay 401; credential removed (restart with a configuration lacking the id) → 401 and row
  gone; rotated secret (restart with a new value) → 401 and row gone; same value under a different variable name →
  still valid; Bearer `DELETE /api/session` leaves the session valid.
- **Bearer equivalence (§4, §17 rows 29–30):**
  `Session_IsInvalidated_WhenItsSecretNowResolvesToAnotherBearerCredential` — configuration `ops / K / administrator`,
  create a session; restart with `readonly / K / viewer`, `ops / K / administrator`; Bearer `K` → authenticated as
  `readonly` with only `viewer`; the old session → `401`, deletion cookie, row gone, and a subsequent request with it
  is still `401`. Complementary still-valid case — restart instead with `ops / K / administrator`,
  `readonly / K / viewer`: Bearer `K` → `ops`; the old session → `200` as `ops` with `administrator`, row kept.
- **CSRF:** the full §17 rows 7–16 for each of POST, PUT, PATCH, DELETE (parameterized), including a plugin lifecycle
  mutation and a settings mutation with a valid administrator session → 403 without the header and success with it;
  the rejected request has no side effect and its body is not read.
- **Persistence:** fresh file; existing installation without `sessions.db`; restart with `KeepTempDirectory` keeps a
  valid session valid; `user_version` greater than 1 refuses to start; startup sweep removes unknown credential ids;
  cleanup removes exactly the expired rows; table contents contain no key, no token and no cookie string (scan all
  columns for the raw values).
- **Secret leakage:** for every session endpoint and error path, response bodies and headers (except the login
  `Set-Cookie`) contain neither the key nor the token; captured logs (test logger provider) contain neither; an induced
  exception in `Development` returns a body without the cookie value or `Authorization` value.
- **Configuration:** each invalid value of §12 fails startup; defaults applied when the section is absent.
- **UI (Karma, headless):** boot restore (200 → shell; 401 → login without expired banner; 0/5xx/timeout →
  unavailable + retry); login posts JSON with `keepSignedIn`, clears the field on every outcome, never touches
  `localStorage`/`sessionStorage`/IndexedDB (spies) and leaves no key in `AuthService`; failure messages per status;
  interceptor adds `X-bOps-Request` only to relative `/api/` unsafe requests and never `Authorization`; 401 while
  authenticated → `session-expired` exactly once, polls stop; boot/login/logout 401s do not; logout 204/401 →
  unauthenticated, other failure stays authenticated with an error; `pendingWatch` survives the interceptor-first
  expiry and the watch restarts after re-login; selection URL written/cleared; dashboard init with `?task=` restores the
  watch with the snapshot's attempt, shows a terminal task without a watch, strips a malformed id.
- **E2E-8 and manual evidence:** §18.1 and §18.2.

#### 18.1 E2E-8 (Playwright, executable)

Harness: `@playwright/test` as a `web/bops-ui` dev dependency; Chromium and Firefox projects; tracing, video and HAR
**off** for these specs (Playwright traces record request headers, i.e. the cookie); screenshots allowed. Topology =
the supported one: the repository's `ng serve` with `proxy.conf.json` at `http://localhost:4200`, and the real `bOps.Api`
composition (`Program`) on Kestrel at `http://localhost:5080`, started by a test-only host (for example
`WebApplicationFactory<Program>` with `UseKestrel`) that substitutes a deterministic gated fake `IChatModel` — each model
call waits for a release signal from a loopback control channel that exists only in that test host assembly, never in
`bOps.Api`. The API key is generated per run, passed through the environment to the host and the test process, and
never printed.

1. Start API host and `ng serve`; open `http://localhost:4200/`.
2. Sign in with the key (checkbox off).
3. Assert `localStorage`, `sessionStorage` and IndexedDB database names contain neither the key nor the cookie value;
   the page URL and `history` entries contain neither; `document.cookie` lacks `__Host-bops_session`.
4. Start a task from the dashboard; the fake model holds it `Running`; URL has `?task=<id>`.
5. Release one model step; the UI shows step 1.
6. `page.reload()`.
7. No login screen appears; network shows `GET /api/session/me` → 200.
8. The same task is selected and its watch polls (`GET /api/agents/tasks/<id>` requests resume).
9. Release another step; the UI shows step 2 without interaction; release to completion; the UI shows the terminal
   status and the watch stops.
10. Read the cookie value from `context.cookies()` into a local variable (never logged), sign out; the UI shows the
    login screen; the cookie is gone from the jar.
11. Replay: `request.get('/api/session/me', { headers: { Cookie: '__Host-bops_session=' + old } })` → 401.
12. CSRF: sign in again; from a page on `http://localhost:4300` (same site, other port) and one on `http://127.0.0.1:4301`
    (cross site), attempt `POST http://localhost:4200/api/agents/tasks` as a no-cors `text/plain` fetch, a form post and a
    `fetch` with `X-bOps-Request`; afterwards the API's task list is unchanged (no task created).
13. Expired-session UI path: revoke the session server-side through the test host's control channel (deleting its row),
    then trigger a poll; the UI shows "Session expired — sign in…"; sign in; the watch restarts.

Time-based expiry is **not** tested by waiting in E2E; it is covered at API level with the fake clock.

#### 18.2 Manual browser evidence

In Chrome (and Firefox if available) against the supported topology, DevTools → Application/Storage → Cookies →
`http://localhost:4200`, capture with the **value redacted**: name `__Host-bops_session`; `HttpOnly` ✓; `Secure` ✓;
`SameSite` `Strict`; `Path` `/`; `Domain` `localhost` (host-only, no `Domain` attribute); expiry "Session" by default;
with "Keep me signed in", an expiry ≈ 12 h ahead. Plus Local Storage and Session Storage panes showing only the
existing non-secret `language`/`theme` entries. No screenshot may show a cookie value or the API key.

### 19. Documentation the implementation must update

`docs/operator-configuration.md` (sign-in, session behaviour, configuration keys, the dev-proxy/same-origin requirement,
opening the UI at exactly a configured origin, the `sessions.db` kill switch, absence of production SPA hosting —
F-26), `docs/security/threat-model.md` ("All `/api` operations require an authenticated bearer key" → bearer key or
browser session; the plugin-lifecycle sentence "ambient cookies never authenticate a mutation" → "the browser-session
cookie authenticates a mutation only through the CSRF gate of ADR-0043"; residual risks of §16), the
`PluginLifecycleEndpoints` CSRF remark (same correction), and the login-page texts (§14.3).

## Alternatives considered

- **Keep the key in `sessionStorage`/`localStorage`.** Rejected by the packet and the plan: any script on the origin
  reads it, and it is the long-lived credential.
- **ASP.NET Core cookie authentication (`AddCookie`) with an `ITicketStore`.** Rejected: the cookie would be a Data
  Protection-encrypted ticket (new key ring to persist and protect), sliding expiration and redirect semantics do not
  match §6 and §10, and binding to the credential's secret would be bolted on anyway. A plain opaque token with a digest
  lookup is smaller and fully specified.
- **ASP.NET Core antiforgery tokens.** Rejected: needs a token endpoint and a script-readable token; for a JSON API with
  no CORS, a non-safelisted custom header plus `Origin` validation is the standard, simpler control.
- **Compare `Origin` with `Host` (or `X-Forwarded-Host`).** Rejected: measured — the dev proxy rewrites `Host` to
  `localhost:5080` while the browser sends `Origin: http://localhost:4200`; trusting forwarded headers needs a
  trusted-proxy boundary the app does not have.
- **No `Referer` fallback.** Considered; the fallback is kept as the packet specifies, but only when `Origin` is entirely
  absent, never for `Origin: null`.
- **HMAC-peppered token digest.** Rejected (§3): no gain for 256-bit random tokens, new secret lifecycle.
- **Unkeyed `SHA-256(apiKey)` (or PBKDF2) key-version fingerprint.** Rejected (§4): offline-guessable from the database
  for low-entropy keys; PBKDF2 per request is costly and still offline-attackable.
- **Invalid Bearer falls back to a valid cookie.** Rejected: silent downgrade, ambiguous identity.
- **Snapshot roles at login.** Rejected: a removed role would survive until expiry.
- **Session table inside `tasks.db`.** Rejected: separate concern and lifecycle; a separate file follows the repository
  convention and gives the operator a revoke-all switch.
- **Token rotation on every request or on an interval.** Rejected: concurrent requests race the rotation; with
  re-resolved roles, `HttpOnly`, `SameSite=Strict` and server-side expiry it adds failure modes without closing a
  threat in this topology.
- **Restoring the watched task from `sessionStorage` or by server-side "most recent running task" discovery.**
  Rejected: storage invites confusion with the no-storage rule; discovery is non-deterministic. The URL is explicit,
  testable and survives re-login.
- **Audit events for login/logout.** Deferred: an audit-schema change is a `bOps.Abstractions` change needing its own
  ADR; structured logs without secrets cover operations (§13).

## Consequences

- Additive public contract: `POST /api/session`, `DELETE /api/session`, `GET /api/session/me` now also cookie-authenticated
  (same body), the `bops-browser-session` scheme, the `BrowserSession` configuration section, `sessions.db`. No change
  for Bearer clients; no breaking change.
- Behavioural changes visible to browsers only: unsafe cookie-authenticated requests need `X-bOps-Request: 1` and a
  configured origin; the UI must be opened at exactly a configured origin (`http://localhost:4200` by default — not
  `127.0.0.1:4200`).
- Every unhandled exception now yields a problem body without request data in every environment, including
  `Development` (the developer exception page is no longer reachable).
- `bOps.Api` gains a third SQLite file and a small amount of authentication middleware; the core projects are untouched
  (A1 holds).
- After acceptance, record a `06-decisions.md` entry as for earlier HARDEN ADRs.

### Residual risks

- Shared `localhost` cookie jar across ports (§16).
- An open, polling dashboard tab stays signed in up to the absolute lifetime (§6.3).
- In-flight requests validated just before logout complete (§5.4).
- Rotated-key rows persist (unusable) until presented or expired (§4).
- Key or role changes still require a host restart, exactly as for Bearer.
