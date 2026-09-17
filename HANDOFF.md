# Handoff — V1.1-E complete; V1.1-F active

V1.1-A through V1.1-E are complete on public `bOps` `main`. V1.1-E adds `bOps.Packages.Web`: a
`web.search` tool against one operator-configured SearXNG instance and a hardened `web.fetch` tool
that resolves and validates every connection's destination address at connect time, closing the
DNS-rebinding TOCTOU window by construction. GitHub Actions run `35245357524` passed on Windows and
Linux after Ubuntu CI caught that the package's local test server (`System.Net.HttpListener`) does
not reliably dispatch requests on Linux — fixed by switching the test server to Kestrel, the same
server `bOps.Api` itself runs in production.

The formal V1.0 release gate remains independently operator-gated. No tag, package publication,
release workflow or commercial-repository product change was made.

## V1.1-E delivered

- `web.search` queries one operator-configured SearXNG instance's JSON API. No API key is used or
  accepted; a JSON-disabled instance is reported as an actionable failure, never scraped as HTML.
  It is invisible to the model until `Web:Search:BaseUrl` is configured (capability `web.searxng`,
  the same gating mechanism `docker.*` uses for an absent daemon).
- `web.fetch` resolves and validates the target address inside
  `SocketsHttpHandler.ConnectCallback`, immediately before connecting — denying loopback,
  link-local (including the cloud-metadata address), RFC 1918 private ranges, RFC 6598
  carrier-grade-NAT space, IPv6 unique-local/link-local, multicast and unspecified addresses by
  default. Because validation happens inside the same callback that performs the connection, a
  redirect to a different host is revalidated the same way with no TOCTOU window (ADR-0028,
  decision D-023).
- Its own bounded redirect loop (`AllowAutoRedirect` is off) caps redirect count and rejects an
  `https`-to-`http` scheme downgrade by default.
- Decompression is manual so the byte cap applies to *decompressed* output, not wire bytes — the
  decompression-bomb defense. Only an allowlisted set of textual content types is decoded; charset
  is trusted only from a small allowlist and otherwise falls back to UTF-8.
- No arbitrary headers, credentials, cookies or request body — both tools accept only their
  documented, bounded arguments and return requested/final URL, status, content type, retrieval
  timestamp, truncation and bounded text.

See `docs/security/web-network-policy.md` and ADR-0028 for operator and normative details.

## Validation on 2026-09-17

- `dotnet restore bOps.slnx --locked-mode` — passed.
- `dotnet build bOps.slnx --configuration Release --no-restore` — 0 warnings, 0 errors.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"` — all
  executed assemblies passed locally, including the new `bOps.Packages.Web.Tests` (61/61: IP-range
  policy unit tests, recorded SearXNG contract tests, and local-Kestrel-server attack tests for
  redirect-to-loopback/private/metadata, oversized body, decompression bomb, invalid charset and
  excess redirects).
- `npm run build` and `npm test -- --watch=false` — passed; Angular 18/18 (no UI surface change —
  both tools are Read-risk with no approval workflow).
- GitHub Actions run `35245357524` — Windows and Ubuntu restore/build/non-live tests and Angular
  checks passed.

## Next action and boundaries

Implement `agentic/_tasks/2026-09-16-v1.1-f-plugin-catalog-ui.md` with effort **medio**: read-only
plugin catalog API endpoints and an Angular page projecting `PluginManager` state (installed/active
plugins, versions, publisher/signature/trust state, declared capabilities, effective risk ceiling,
compatibility, sanitized load errors). Enable/disable/upload stay out of scope for V1.1-F.

Do not start V1.1-G or later work until V1.1-F closes. Do not create a release tag or publish
packages without separate operator authorization. The private commercial repository remains
product-gated and unchanged; only the workspace submodule pin advances after this public closure.
