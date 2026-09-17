# ADR-0028 — Outbound web search and hardened fetch

Status: Accepted
Date: 2026-09-17

## Context

V1.1-E adds `bOps.Packages.Web`, the first package whose tools reach network destinations the
operator did not individually enumerate in advance. Every prior outbound caller in this codebase
targets operator-fixed configuration — the configured LLM provider endpoint, the Docker daemon
socket — never a host chosen at request time. `web.search` sends a query to one operator-configured
SearXNG instance; `web.fetch` retrieves whatever URL the model supplies, including URLs discovered
inside `web.search` results or a previously fetched page. That URL is adversarial input in the same
sense a container log line is under S5: it can name `http://169.254.169.254/latest/meta-data/`, a
loopback admin panel, or a host that resolves differently on each lookup.

Both tools are `RiskLevel.Read` — they mutate nothing on the managed node — but "no side effect on
the node" is not "no side effect at all". A `Read` classification only exempts a tool from policy
approval and `IVerifiableTool`; it does not relax S5 (output is data, never instruction) or S6/S10.
The risk this ADR manages is entirely outbound: reaching a network destination the operator did not
intend, or letting response content and its own resource cost cause harm (SSRF, DNS rebinding,
decompression bombs, unbounded memory).

`bOps.Abstractions` already has everything this needs: `RiskLevel.Read` needs no
`VerificationSpec`, and `ToolManifest.Requires` (rule A8/ADR-0006) already gates tool visibility on
a capability probe, exactly matching `web.search`'s dependency on operator-configured SearXNG. No
SDK change is required.

## Decision

### Two tools, two trust boundaries

`web.search` calls exactly one address: `Web:Search:BaseUrl`, operator configuration read at
startup, never a value the model can influence. This is the same trust level as
`ModelProvider:BaseUrl` or `Docker:Endpoint` elsewhere in this host — an operator already decided
to trust that endpoint — so `web.search` does not route through the SSRF-defense path below. It
still runs through a size/timeout-bounded `HttpClient` as defense-in-depth against a misbehaving or
compromised instance, and it never falls back to scraping HTML: SearXNG is configured with
`format=json` on the request and a non-JSON response is reported as an actionable structured
failure ("SearXNG JSON output is disabled on this instance"), never parsed as a page.

`web.fetch` accepts a URL from tool arguments — ultimately model-chosen, ultimately adversarial —
and is the tool this ADR's SSRF/DNS-rebinding/redirect/decoding rules govern.

### `web.fetch`: SSRF and DNS-rebinding defense via `SocketsHttpHandler.ConnectCallback`

`web.fetch`'s `HttpClient` is built on one `SocketsHttpHandler` per package instance with
`AllowAutoRedirect = false`, `UseCookies = false`, `Credentials = null`,
`AutomaticDecompression = DecompressionMethods.None`, and a custom `ConnectCallback`. On every new
connection the callback:

1. resolves the target host through `IDnsResolver` (production: `System.Net.Dns`; injectable for
   tests — see *Testing seam* below);
2. validates **every** returned address against `IpAddressPolicy`: denies loopback, link-local
   (including the `169.254.169.254` cloud-metadata address, which is link-local), RFC 1918 private
   ranges, IPv6 unique-local (`fc00::/7`), multicast, unspecified (`0.0.0.0`/`::`) and IPv4-mapped
   IPv6 wrapping any denied address, by default;
3. connects a raw `Socket` to the first address that survives validation, or fails the request with
   an actionable "destination denied by network policy" outcome if none does;
4. returns a `NetworkStream` over that socket. `SocketsHttpHandler` layers TLS on top itself for
   `https://`, using the original request hostname for SNI and certificate validation — the
   callback never touches TLS, so certificate checking is unaffected by this rule.

An explicit, narrow operator allowlist (`Web:Fetch:AllowedHosts` / `AllowedNetworks`) is the only
override, checked before the deny rules so an operator can deliberately permit one internal service
without disabling the defense generally.

Because the callback re-resolves and re-validates on **every new connection**, and a redirect to a
different scheme/host/port is, by definition, a new connection, this single mechanism also closes
the TOCTOU DNS-rebinding gap (validate-then-connect against a hostname that resolves differently
between the two steps): there is no "check now, connect later" window — the address that is
validated is the address that is connected to, atomically, inside one callback invocation.

Redirects themselves are handled by `WebFetchService`, not by `SocketsHttpHandler`
(`AllowAutoRedirect = false`), as an explicit, independently testable loop: each hop is capped by
`Web:Fetch:MaxRedirects` (default 5, hard ceiling 10), and a redirect that downgrades scheme
(`https` → `http`) is rejected by default. Exceeding the cap or a rejected downgrade ends the fetch
with a bounded diagnostic, never a partial silent success.

### Bounded, decompression-bomb-safe reading

`AutomaticDecompression` is off so the package can enforce a byte budget on the *decompressed*
stream, not just the wire bytes. `WebFetchService` reads the response body itself: if
`Content-Encoding` names a supported codec (gzip, deflate, br), it wraps the raw stream in the
matching decompression stream and copies through a counter that aborts once
`Web:Fetch:MaxDecompressedBytes` is exceeded. The raw (pre-decompression) stream is independently
capped at `Web:Fetch:MaxResponseBytes`. Aborting produces a truncated result (`truncated: true`),
never an unbounded allocation or an unhandled exception.

### Content-type and charset handling

Only a fixed allowlist of textual content types is decoded to text: `text/plain`, `text/html`,
`text/markdown`, `application/json`, `application/xml`, `text/xml`, `text/csv`
(`Web:Fetch:AllowedContentTypes`, operator-narrowable, never wider than this default set without an
explicit configuration change). Anything else returns content-type, byte length and a bounded
diagnostic — never raw bytes, since this package has no use for arbitrary binary payloads and
returning them would bypass every size/content control above.

Charset comes from a small trusted allowlist (`utf-8`, `us-ascii`, `iso-8859-1`) read from the
`Content-Type` header's `charset` parameter — every one natively supported by the runtime, so this
adds no encoding-provider dependency; anything unspecified or unrecognized decodes as UTF-8, whose
default fallback behavior never throws on invalid byte sequences.
This is a deliberate refusal to trust an attacker-controlled charset declaration for anything beyond
selecting among a few well-understood, unambiguous encodings.

### Output shape and S5

Both tools return requested URL, final URL (after any followed redirects), HTTP status, content
type/encoding, retrieval timestamp, a `truncated` flag and the bounded text body — never raw
response headers, cookies, or anything beyond this fixed shape. Per S5, this text is untrusted
external data: it is returned as ordinary tool output, subject to the same delimiter/neutralization
handling every tool result already receives before it reaches model context, and this ADR does not
add prompt-injection-specific text transformation — the existing S5 boundary is the correct and
sufficient control, exactly as it is for `fs.read` or `docker.logs`.

### Capability gating

`web.search`'s `ToolManifest.Requires = ["web.searxng"]`. A capability probe checks only that
`Web:Search:BaseUrl` is a well-formed absolute `http`/`https` URI — a local, synchronous check, not
a live network probe (registered through `CachingCapabilityProbe.RegisterCheck`, the same mechanism
`DockerCapability` already uses for the Docker daemon). An unconfigured instance means the tool is
absent from what the model sees, not present-and-failing. `web.fetch` has `Requires = []`: it
depends on no operator-provided endpoint and is always available where the package loads.

### Testing seam: `IDnsResolver`

`IpAddressPolicy` enforcement is deterministic and directly unit-testable. The redirect loop,
decompression-bomb handling and SSRF connect-time rejection need a real HTTP server and a way to
make "this hostname resolves to that (attacker-chosen) address" true without controlling real DNS
in CI. `IDnsResolver` (`Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)`) is
injected into the package's `HttpClient` construction; production uses `SystemDnsResolver`
(`Dns.GetHostAddressesAsync`), tests use a fake that maps chosen hostnames to addresses such as
`127.0.0.1` or a documented metadata-service address. A local `HttpListener` on `127.0.0.1:0`
supplies the redirect chain and oversized/compressed bodies under test; happy-path tests explicitly
add that address to `AllowedHosts` so the default-deny path and the "explicit allowlist works" path
are proven as two separate assertions, not conflated.

## Alternatives considered

- **Validate the hostname's resolved address once before calling `HttpClient.SendAsync`, then let
  the client connect normally.** Rejected: this is exactly the TOCTOU window DNS rebinding exploits
  — nothing stops the name resolving to a different, disallowed address between the check and the
  actual connect performed later inside the client.
- **Rely on `HttpClient`'s built-in `AllowAutoRedirect` and inspect the final response's
  `RequestMessage.RequestUri` afterward.** Rejected: by the time a disallowed redirect target is
  observable, the request has already been sent to it. Detection after the fact is not prevention.
- **Block by hostname/domain blocklist (e.g. deny `*.internal`, `metadata.google.internal`).**
  Rejected: a blocklist of names is trivially bypassed by any address that resolves to a denied
  range under a permitted-looking name; only IP-range validation at connect time is complete.
- **Let `SocketsHttpHandler.AutomaticDecompression` handle compression and cap only the final text
  length.** Rejected: automatic decompression happens transparently before any size check runs,
  which is exactly the decompression-bomb shape this ADR defends against.
- **Return raw bytes for any content type and let the model/caller decide what to do with them.**
  Rejected: unbounded and untyped content defeats the size/content controls and gives no reason to
  bound anything else in this design.
- **Give `web.fetch` an operator-configurable request-body/header passthrough for flexibility.**
  Rejected by the task's own contract: no arbitrary headers, credentials, cookies or request bodies
  — a fetch tool that can carry caller-chosen headers is one step from an SSRF-driven credential
  relay.

## Consequences

- `bOps.Packages.Web` is a new first-party package following the existing package boundary (rule
  A8/ADR-0006): no Web identity appears in `bOps.Runtime`, `bOps.Policy`, `bOps.Memory` or
  `bOps.Audit`. No change to `bOps.Abstractions` is required — `RiskLevel.Read`, `ToolManifest`,
  and the existing capability-probe mechanism already cover this package's needs.
- `web.fetch` is, by construction, unable to complete a connection to a loopback, link-local,
  private, unique-local, multicast or unspecified address unless an operator has explicitly
  allowlisted it — including via a redirect or a DNS answer that changes between calls.
- A misconfigured or malicious SearXNG instance, a decompression bomb, or an oversized/slow response
  can degrade to a bounded failure (timeout, truncation, actionable diagnostic) but cannot exhaust
  unbounded memory or hang the calling task past its configured timeout (S7).
- Both tools' output remains ordinary S5 tool-result data: bounded, attributed with final URL and
  retrieval metadata, and never capable of altering runtime state, policy, or the tool list on its
  own.
- Attack tests (redirect-to-loopback/private/metadata, DNS-rebinding via a swapped resolver answer,
  oversized body, decompression bomb, invalid charset, slow stream, excess redirect count, scheme
  downgrade) run against a real local `HttpListener`, not only recorded fixtures, matching the
  precedent set by V1.1-D's real-filesystem attack tests.
