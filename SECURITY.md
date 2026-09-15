# Security Policy

## Reporting a vulnerability

**Please do not open a public GitHub issue for a security vulnerability.** Report it privately:

1. **Preferred:** use GitHub's private vulnerability reporting for this repository (the
   "Report a vulnerability" button under the Security tab). This keeps the report and any
   discussion out of public view until a fix is ready.
2. **Fallback**, if private reporting is not enabled or reachable: email
   `fcavallari@bsoftsolutions.it` with a clear subject line starting `[bOps security]`.
   *(Maintainer note: replace this with a dedicated security contact address once one exists —
   this is the project owner's own address as a starting point, not a permanent security
   mailbox.)*

Please include: the version or commit you tested, reproduction steps, the affected component
(runtime, a specific package, the CLI, `bOps.Api`, or the UI), and what you believe the impact
is. A proof of concept is welcome but not required to file a report.

## What to expect

This is a pre-alpha, single-maintainer project as of this writing. We will acknowledge a report
on a best-effort basis and work on a fix at a priority matching its severity — we are not yet in
a position to promise a specific response-time SLA, and we would rather say that plainly than
commit to a number we cannot keep. Once a fix is available, we will coordinate a disclosure
timeline with the reporter before any public write-up.

## Supported versions

| Version | Supported |
|---|---|
| `main` (latest) | Yes — this is the only line actively maintained before `V1.0` |
| Anything pre-`V0.9` | No — treated as historical; upgrade to `main` before reporting |

There is no long-term-support branch yet. This will be revisited once `V1.0` ships a frozen
`bOps.Abstractions` surface (`agentic/06-decisions.md`, D-012).

## Scope

In scope: the public `bOps` repository — `bOps.Abstractions`, `bOps.Runtime`, `bOps.Policy`,
`bOps.Memory`, `bOps.Audit`, the CLI, `bOps.Api`, `web/bops-ui`, and the first-party packages
under `src/packages/`.

Out of scope for this file: the private `bOps.Commercial` repository (once it exists) has its
own, separate disclosure process — do not use this channel for it.

**Known, structural limitations that are not themselves vulnerabilities** — reported findings
that restate these will be closed as expected behavior, documented in
`agentic/03-security-rules.md`:

- An installed package runs with the host process's full privileges. `AssemblyLoadContext`
  isolation (used by the plugin loader from V0.10) isolates dependencies, not permissions — see
  rule S8. Installing a package is equivalent to installing software with bOps's own privileges.
- The audit log (`JsonLinesAuditSink`) is tamper-*evident* (SHA-256 hash-chained, independently
  checkable with `AuditChainVerifier`), not tamper-*proof* — someone with write access to the log
  file can rewrite it from a point forward and recompute every hash after it. Do not report "the
  audit log can be edited by an attacker with local write access" as a new finding; it is a
  documented limitation (rule S9).
- `bOps.Api` has no authentication yet (as of `V0.9`) — this is a tracked, known gap, not a
  vulnerability to report; authentication is scoped for `V1.0`
  (`piano-bops-v0.9.1-v2.0.md` §7).
