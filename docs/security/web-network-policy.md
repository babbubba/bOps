# Web search and fetch network policy

`bOps.Packages.Web` adds two Read-risk tools: `web.search`, which queries one operator-configured
SearXNG instance, and `web.fetch`, which retrieves one HTTP/HTTPS URL under a deny-by-default
outbound network policy. Neither tool has a side effect on the managed node; the risk they manage
is entirely outbound (ADR-0028).

## `web.search`: enabling SearXNG JSON search

`web.search` is invisible to the model until `Web:Search:BaseUrl` is configured — it declares the
`web.searxng` capability and the tool registry hides it, exactly like `docker.*` hides itself when
no daemon is reachable. There is no default public instance and none will be added; point this at
an instance you operate or otherwise trust.

```json
"Web": {
  "Search": {
    "BaseUrl": "https://searxng.internal.example.com",
    "Timeout": "00:00:10",
    "MaxResults": 10,
    "MaxResultTextBytes": 1024,
    "MaxResponseBytes": 2097152
  }
}
```

The instance must serve JSON results. Enable `json` under `search.formats` in its `settings.yml`
and restart it — see <https://docs.searxng.org/dev/search_api.html>. If JSON output is disabled,
`web.search` reports an actionable failure naming the setting to change; it never scrapes the HTML
results page as a fallback, and no API key is used or accepted.

### Local development with Aspire

`bOps.AppHost` supplies a ready-to-use development instance by default. It uses the pinned image
`docker.io/searxng/searxng:2026.10.4-d48c4b555`, mounts
`src/bOps.AppHost/searxng/settings.yml` read-only, generates the SearXNG server secret at AppHost
startup, exposes only the Aspire loopback proxy on `http://localhost:8081`, and waits on
`/healthz` before starting the API. The mounted settings explicitly include both `html` and `json`
under `search.formats`; the limiter is off because this is a loopback-only development instance,
not an Internet-facing deployment.

```bash
dotnet run --project src/bOps.AppHost/bOps.AppHost.csproj
curl -fsS "http://localhost:8081/healthz"
curl -fsS "http://localhost:8081/search?q=bOps&format=json"
```

```powershell
dotnet run --project src/bOps.AppHost/bOps.AppHost.csproj
Invoke-WebRequest -UseBasicParsing http://localhost:8081/healthz
Invoke-RestMethod 'http://localhost:8081/search?q=bOps&format=json'
```

The AppHost injects an Aspire endpoint reference into the **host process** running `bops-api`; do
not replace it with a guessed container address. Host `localhost`, a container's own `localhost`
and the Aspire resource name are different network namespaces. If you containerize the API
outside this development topology, configure `Web__Search__BaseUrl` with an address resolvable
from that API container.

Set `Searxng__Enabled=false` before starting the AppHost to omit the resource and its API
configuration. This does not affect `web.fetch` or other tools. Starting `bOps.Api` or `bOps.Cli`
directly also remains Docker-independent.

### Readiness and failure diagnosis

- `GET /healthz` returning `200 OK` proves the SearXNG web application is ready.
- `/search?q=bOps&format=json` returning a JSON object proves JSON output is enabled and at least
  one real search request completed. An empty `results` array is still valid JSON readiness.
- A connection or DNS error means the configured endpoint is unreachable from the bOps process.
- An HTTP error is reported with its status code.
- A successful HTML response means `json` is absent from `search.formats`.
- An `application/json` response that cannot be parsed is reported as malformed; it is never
  accepted as search evidence.

The API's authenticated `GET /api/tools` is the catalog the model actually sees. `web.search`
present there means a syntactically valid endpoint was configured; it does not replace the live
readiness checks above. `web.search` absent while `web.fetch` remains present means
`Web:Search:BaseUrl` is empty or invalid.

`query`, `category`, `language`, `safeSearch` and `timeRange` are passed through as bounded,
validated SearXNG query parameters; `page` selects a result page. Results beyond `MaxResults` are
dropped, duplicate URLs are removed, and each snippet is truncated to `MaxResultTextBytes`.

## `EvidenceRead/v1` is internal, not web access

Bounded task history may tell the model to return one exact `EvidenceRead/v1` JSON object as plain
assistant text. The runtime recognizes that object, validates its current-task evidence id, source,
offset and length, and returns a bounded continuation. It is not a registered tool, URL, search
query or fetch target. The model must not encode it inside `web.fetch` or `web.search` arguments;
their manifests and runtime prompt state this explicitly.

A malformed internal directive is rejected by the runtime. A model that instead proposes an
ordinary web tool call still goes through that tool's normal validation: `data:` and `file:` are
not HTTP(S), fabricated names fail DNS, and none of those failures relax policy or reveal internal
evidence. Internal reads are capped at four attempts per logical model call; exceeding the cap ends
the task with `EvidenceRead/v1 limit exceeded` rather than looping without explanation.

## `web.fetch`: what it will and will not reach

`web.fetch` always denies loopback, link-local (including the `169.254.169.254` cloud-metadata
address), RFC 1918 private ranges, RFC 6598 carrier-grade-NAT space, IPv6 unique-local/link-local,
multicast and unspecified addresses — checked against the address actually connected to, not the
hostname, so a DNS answer that changes between calls or a redirect to a different host is
revalidated the same way (ADR-0028, decision D-023). There is no way to disable this by tool
argument; it is enforced under the connection itself.

An operator may narrow an explicit exception with IP literals or CIDR ranges — never hostnames,
since allowlisting a name would reopen the exact DNS-rebinding gap this policy closes:

```json
"Web": {
  "Fetch": {
    "ConnectTimeout": "00:00:05",
    "OverallTimeout": "00:00:15",
    "MaxRedirects": 5,
    "MaxResponseBytes": 1048576,
    "MaxDecompressedBytes": 4194304,
    "AllowSchemeDowngradeOnRedirect": false,
    "AllowedContentTypes": ["text/plain", "text/html", "text/markdown", "application/json", "application/xml", "text/xml", "text/csv"],
    "AllowedAddresses": ["10.20.1.5"],
    "AllowedNetworks": ["10.20.0.0/16"]
  }
}
```

Redirects are followed up to `MaxRedirects`, each one re-resolved and revalidated exactly like the
original request; a redirect from `https` to `http` is rejected unless
`AllowSchemeDowngradeOnRedirect` is explicitly set. Response bytes are capped at
`MaxResponseBytes`; when the server compresses its response, the *decompressed* byte count is
independently capped at `MaxDecompressedBytes` regardless of how small the compressed payload is —
the defense against a decompression bomb. Exceeding either cap truncates the result rather than
failing it; the tool output reports `truncated: true` so the model does not mistake a cut body for
a complete one.

Only content types in `AllowedContentTypes` are decoded to text; anything else comes back as
content-type and byte-length metadata with no body. Charset is read from the response and trusted
only when it names `utf-8`, `us-ascii` or `iso-8859-1`; anything else decodes as UTF-8, which never
throws on invalid byte sequences.

## What this does not protect against

This is a network-destination and resource-cost control, not a content filter: a public, routable
URL that is merely unwanted (rather than internal) is not blocked, and an operator-added allowlist
entry is trusted as configured — allowlisting a sensitive internal host is a deliberate operator
decision this policy does not second-guess. Fetched and searched content is untrusted external data
under S5 like any other tool output: it is delimited and neutralized before it reaches model
context and can never itself alter policy, the tool list or runtime state.

See ADR-0028 and `docs/security/threat-model.md` for the full threat model and rejected
alternatives.
