# TLS and certificate diagnostics

V1.3-J adds the BCL-only `bOps.Packages.Security` package. Every tool is `Read` risk and is
available on Windows and Linux. The package never installs, removes, exports, or reads certificate
files, and it never exposes private key material.

`network.tls_probe` resolves the supplied host, opens one bounded TCP connection, and performs a
TLS handshake without sending application data. `sniHost` (or `host` when omitted) is used for both
SNI and certificate hostname validation; the resolved address is not substituted for that name.
TCP and handshake failures are tool failures. A negotiated connection with an untrusted, expired,
not-yet-valid, or hostname-mismatched certificate is successful diagnostic evidence with
`chainValid:false`, `policyErrors`, and bounded chain statuses.

`certificate.list` and `certificate.inspect` only read `CurrentUser`/`LocalMachine` stores `My`,
`Root`, or `CertificateAuthority`. Inspection normalizes thumbprints by removing whitespace and
ignoring case. Chain building uses `X509RevocationMode.NoCheck`, so it does not introduce online
revocation traffic. Results contain public certificate metadata and a boolean `hasPrivateKey` only.
