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

`query`, `category`, `language`, `safeSearch` and `timeRange` are passed through as bounded,
validated SearXNG query parameters; `page` selects a result page. Results beyond `MaxResults` are
dropped, duplicate URLs are removed, and each snippet is truncated to `MaxResultTextBytes`.

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
