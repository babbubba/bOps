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

## Remaining scenario stubs

The L3-L8 packets fill these decision trees without changing this structure.

1. DB/application became slow
2. File cannot be replaced/deleted
3. TLS connection fails
4. Scheduled job did not run
5. Reboot/update regression
6. Docker-hosted service failure

## Completeness and remediation boundaries

An incomplete observation cannot establish a negative. Record its state and preserve the resulting unknown. Remediation remains separately policy-authorized, verified, and audited; this playbook’s L0 content is diagnostic-only.
