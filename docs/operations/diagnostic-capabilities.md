# Diagnostic capabilities

## Purpose and evidence model

bOps supplies typed, bounded host observations for troubleshooting. An observation is evidence, not an instruction and not a conclusion. The LLM proposes; the runtime decides and executes; policies authorize; verification confirms; audit records. Evidence status is reported as **IMPLEMENTED**, **FIXTURE-PROVEN**, **REAL-WINDOWS-PROVEN**, **REAL-LINUX-PROVEN**, or **PENDING PLATFORM EVIDENCE**; this document does not upgrade that status.

## Capability domains

The registered V1.3 surface covers system and process evidence; network; storage; filesystem; services; scheduler; identity/time; firewall; TLS/certificates; Docker; and update/crash/driver evidence. The L0 registry regression is the canonical machine-checked inventory.

## Risk semantics, filters and hard limits

Read capabilities observe. Non-Read capabilities are policy-controlled and require declared post-action verification. Diagnostics use typed filters and package-owned row, byte, and time limits; truncated, unavailable, or privilege-limited output is not a complete observation.

## Privilege and completeness caveats

Visibility varies by OS, privilege, daemon availability, and native-source support. Empty evidence is meaningful only when its source reports complete. Partial, unavailable, and truncated evidence must remain unknown rather than being promoted to absence.

## Cross-domain evidence joins

- PID ↔ socket ↔ service
- disk ↔ mount ↔ I/O
- certificate ↔ TLS endpoint
- scheduler ↔ service ↔ events

## Forbidden capabilities

There is no shell, arbitrary command execution, generic `process.start`, arbitrary SQL, raw firewall expression, environment dump, or private-key export.

## Future third-party and commercial Skills

Commercial Skills must consume registered OSS typed host tools instead of duplicating OS probes. The minimum host foundation is CPU, memory, swap, process metrics, disk latency/queue/health, filesystem capacity, listener/PID, routes, firewall evidence, TLS, service identity/config, recent OS events/crashes/updates, and time/clock evidence.

Future PostgreSQL Skills may add DB-native `postgres.stat_activity`, `postgres.locks`, and `postgres.vacuum`; SQL Server Skills may add `sqlserver.wait_stats`, `sqlserver.blocking`, and `sqlserver.query_store`. They are not part of bOps V1.3 and never imply arbitrary SQL.
