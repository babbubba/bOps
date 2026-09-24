# Troubleshooting playbook

## Shared decision-tree rules

Each scenario records **Evidence** (tool observation), **Inference** (bounded conclusion), and **Unknown** (missing, partial, unavailable, or truncated evidence). Diagnose before remediation. No scenario authorizes generic execution, automatic termination, restart, SQL, or unbounded collection.

## DB port 5432/1433 unreachable

Use this tree for host and TCP evidence only; ports 5432 and 1433 are endpoint examples, not PostgreSQL or SQL Server diagnosis.

```text
network.dns_query
  unavailable or no resolution -> Evidence: hostname resolution was not established.
                                Inference: no IP route is known.
                                Unknown: route, listener, firewall, service, and TCP path.
  resolved -> network.routes
    no usable route -> Evidence: route/path evidence is absent or unusable.
                       Unknown: listener, service, local/remote firewall state.
    usable route -> network.sockets
      no local listener -> Evidence: no local listener observed; continue to service.status/config.
      listener at unexpected/local-only bind -> Evidence: listener exists but not on the intended exposure.
      then firewall.status + firewall.rules -> explicit block is LOCAL firewall evidence only.
      then service.status + service.config -> stopped/failed is service-state evidence, not route proof.
      then system.events -> matching event is correlation, not DB root cause.
      then network.port_check
        reachable -> Evidence: TCP endpoint reachable at this observation time.
                     Unknown: PostgreSQL/SQL Server protocol and database health.
        unreachable -> retain the specific observed listener/bind/service/local-firewall evidence;
                       remote path remains unknown unless independently observed.
```

Partial, unavailable, or truncated firewall evidence is an evidence gap, never proof that the firewall is open. bOps cannot infer remote firewall state from local host evidence. A reachable TCP port does not prove DB health; when the symptom persists, a future commercial DB Skill must collect DB-native evidence such as PostgreSQL `stat_activity`/locks/vacuum or SQL Server `wait_stats`/blocking/query_store.

## Service keeps crashing

Use this tree to diagnose the operational symptom. It does not authorize a restart, process termination,
or dump-content inspection. Keep **CAUSE**, **CORRELATION**, and **UNKNOWN** separate in the verdict.

```text
service.status -> service.config -> service.dependencies
  target failed/stopped -> OBSERVED: current service state/configuration.
  required dependency failed/stopped/unavailable -> OBSERVED: dependency state;
    INFERENCE: it may explain target startup/runtime failure.
    UNKNOWN: target binary crash unless target-specific event/crash evidence exists.
  dependency running -> no dependency-failure inference.
  then system.events -> system.crashes
    complete sources with time/process/service identity match -> CORRELATION:
      strong evidence of abnormal target termination; internal application cause remains unknown.
    complete sources without a specific cause -> UNKNOWN: host evidence does not establish root cause.
    partial/unavailable/truncated source -> UNKNOWN: confirmation is incomplete;
      never treat as empty-but-complete or conclude no crash occurred.
  then process.tree -> process.metrics
    prior process not found/exited, or metrics unavailable -> UNKNOWN: live metrics unavailable
      because process is no longer observable; missing values are not zero or normal.
      Preserve earlier service/event/crash evidence.
  then storage.io
    elevated latency/queue/pressure in the observation window -> CORRELATION / possible
      contributing condition only. Timing does not establish storage as crash cause.
  then system.memory -> system.swap
    pressure -> CORRELATION / possible contributing host condition only.
      Do not claim OOM-kill without explicit matching event/crash evidence.
  no specific host cause after complete evidence -> UNKNOWN: hand off to application/domain-specific
    logs or Skills; do not invent an exception or internal cause.
```

**CAUSE** requires a source that explicitly establishes causality. Matching event/crash records support
correlation when their identity and time align; they do not by themselves identify the internal cause.
Storage and memory/swap pressure remain correlated host evidence even when observed in the same window
as a failure. Missing process samples, missing crash records, and incomplete event/crash sources remain
UNKNOWN. An unavailable source is never equivalent to an empty complete source.

## DB/application became slow

This is a host-pressure decision tree, not a database diagnosis. Host telemetry can identify CPU,
memory, storage, or interface pressure, but cannot by itself diagnose DB-native waits, locks,
blocking, query plans, vacuum activity, or application-specific causes.

```text
system.cpu
  sustained/high pressure -> OBSERVED: host CPU pressure; possible host-side contributor, not proven cause.
  then system.memory -> system.swap
    low available memory, pressure, or swap activity -> OBSERVED: memory/swap pressure;
      possible contributor only. Missing swap data remains UNKNOWN, not unused swap.
  then process.metrics
    relevant high CPU/memory -> CORRELATED: strengthens the matching host-pressure evidence.
    process exited/unavailable -> UNKNOWN: live process data is not zero or healthy.
  then storage.io -> storage.health
    latency/queue pressure -> OBSERVED: storage performance pressure; possible contributor only.
    healthy/no-fault health evidence -> device health observation; it does not erase I/O pressure.
    unavailable health -> UNKNOWN: device health cannot be ruled out.
  then system.events
    relevant matching host event -> CORRELATED: can strengthen timing/identity evidence, never causal proof.
    empty complete result != unavailable events source.
  then network.interface_stats
    local errors/drops -> OBSERVED: interface degradation; correlated/possible contributor only.
    incomplete counters -> UNKNOWN: network-interface degradation cannot be ruled out.
  complete evidence with no material pressure across all domains ->
    INFERENCE: No host-side bottleneck was established by the available evidence.
    HANDOFF: do not call the host healthy or identify DB/application root cause.
```

The final handoff is to a future commercial DB Skill using typed DB-native evidence: PostgreSQL
`postgres.stat_activity`, `postgres.locks`, and `postgres.vacuum`; or SQL Server
`sqlserver.wait_stats`, `sqlserver.blocking`, and `sqlserver.query_store`. These are future-boundary
examples only: bOps registers no SQL, DB credentials, DB connection logic, or DB tool here.

## File cannot be replaced/deleted

This is diagnostic-only. It does not delete or replace a file, close handles, terminate a process,
alter permissions or ownership, or unload a module. Keep permission evidence, lock evidence,
process correlation, module context, state races, and unknowns separate.

```text
fs.stat
  absent initially -> OBSERVED: target is absent at this instant.
                      UNKNOWN: why it is absent; do not invent a prior lock or permission cause.
  present -> fs.permissions
    later target absent/changed -> OBSERVED: state changed/raced after initial stat.
                                  Reconsider the requested operation against current state.
                                  Do not call this bOps remediation or say the target never existed.
    access/ACL/mode evidence denies the current identity -> INFERENCE: permission/access may
      explain the symptom. This is not a file-lock conclusion; effective access limitations stay visible.
    otherwise -> fs.locks
      partial/unavailable/truncated -> UNKNOWN: lock visibility is incomplete. Do not conclude
        that the file is unlocked or that no process is using it.
      complete, no relevant holder -> OBSERVED: no relevant lock was observed by this source.
        UNKNOWN: replacement/deletion can still fail because of a race or filesystem semantics.
      complete holder PID -> OBSERVED: lock evidence associates that PID with this target.
        process.inspect
          PID absent/unavailable -> OBSERVED: earlier lock evidence remains.
            UNKNOWN: the process exited before inspection; current identity cannot be confirmed.
          PID present -> process.tree -> process.modules
            tree -> contextual ancestry only; incomplete visibility remains unknown.
            target/related mapped module -> OBSERVED: module context.
              A mapped module is not equivalent to a proven write/delete lock; only fs.locks
              establishes the holder association.
```

Do not turn a lock-holder observation into a claim that the process is malicious, broken, or must
be terminated. An operator may investigate the observed process identity, ancestry, and module
context, but bOps does not automatically terminate processes or alter permissions. A complete empty
lock query is materially different from an incomplete query: the former reports no relevant lock
observed by that source; the latter leaves lock ownership unknown. If no host-side explanation is
established, retain that unknown rather than fabricating a permission or process cause.

## TLS connection fails

Use the requested hostname and port throughout the observation. Keep name resolution, routing,
transport, TLS negotiation, trust, endpoint identity, validity dates, and local time as separate
evidence layers. The sequence is:

```text
network.dns_query
  failed / unavailable -> OBSERVED: name resolution was not established.
                          UNKNOWN: route, TCP, TLS, certificate, and application state.
  resolved -> network.routes
    no usable route / incomplete -> OBSERVED: routing evidence is absent, unusable, or incomplete.
                                    UNKNOWN: endpoint reachability and later layers.
    route evidence usable -> network.port_check
      refused / unreachable -> OBSERVED: TCP transport connection failed.
        TLS handshake was not established. Do not report certificate, trust, or hostname failure.
      reachable -> OBSERVED: TCP is reachable at this observation time.
        network.tls_probe
          handshake failure -> TLS negotiation failed after TCP success.
            A generic handshake failure does not establish trust failure.
          handshake success -> inspect reported trust and identity independently.
            chainValid=false / chain status -> TRUST FAILURE evidence; TCP remains reachable.
            identity mismatch -> HOSTNAME / endpoint identity mismatch for the requested hostname.
              This does not mean the certificate fails for every hostname.
            then certificate.inspect (when certificate can be identified in an allowed local store)
              partial / unavailable / not found -> UNKNOWN: certificate-specific metadata is incomplete.
              complete validity dates + system.time UTC time
                NotAfter < observed time -> EXPIRED validity-window evidence.
                NotBefore > observed time -> NOT-YET-VALID validity-window evidence.
                dates cover observed time -> no date-based validity symptom observed.
              Dates alone do not prove the local clock is wrong.
            then system.time -> network.ntp_probe
              incomplete / unavailable -> UNKNOWN: clock concern cannot be ruled in or out.
              material offset or unsynchronized status -> OBSERVED: local time-sync concern.
                Only when consistent with an observed validity symptom, time evidence may explain
                or contribute to it; it does not prove that the clock caused the TLS failure.
              normal synchronized time + validity symptom -> do not conclude clock caused it.
        TLS success + acceptable reported trust/identity + dates valid at observed time ->
          INFERENCE: TLS connectivity is established at observation time.
          UNKNOWN: application, DB, HTTP/API request, and authentication health.
```

**TRUST FAILURE != TCP FAILURE.** A successful `network.port_check` followed by a TLS-reported
chain validation failure means TCP is reachable and trust validation failed. A handshake failure
without explicit trust evidence is not a trust conclusion. `certificate.inspect` reads public
metadata and local chain evidence for a thumbprint in an allowed local store; the TLS probe itself
provides peer metadata and trust evidence. Do not invent chain details, revocation status, local
trust-store contents, or private-key facts that the typed result did not expose.

Time evidence is explanatory context, never a root-cause oracle. Expired or not-yet-valid dates do
not by themselves prove a bad clock. NTP drift does not by itself prove it caused TLS failure; require
a matching certificate validity symptom and describe the time evidence as a possible explanation or
contributor. No certificate, trust-store, private-key, or system-clock mutation is part of diagnosis.
The TLS probe sends no application data, so successful TLS is not evidence of application health.
Partial, unavailable, or truncated certificate/time evidence remains UNKNOWN, not a complete normal
result.

## Remaining scenario stubs

The L6-L8 packets fill these decision trees without changing this structure.

1. Scheduled job did not run
2. Reboot/update regression
3. Docker-hosted service failure

## Completeness and remediation boundaries

An incomplete observation cannot establish a negative. Record its state and preserve the resulting unknown. Remediation remains separately policy-authorized, verified, and audited; this playbook’s L0 content is diagnostic-only.
