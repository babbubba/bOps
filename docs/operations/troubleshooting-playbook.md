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

## Scenario stubs

The L2-L8 packets fill these decision trees without changing this structure.

1. Service keeps crashing
2. DB/application became slow
3. File cannot be replaced/deleted
4. TLS connection fails
5. Scheduled job did not run
6. Reboot/update regression
7. Docker-hosted service failure

## Completeness and remediation boundaries

An incomplete observation cannot establish a negative. Record its state and preserve the resulting unknown. Remediation remains separately policy-authorized, verified, and audited; this playbook’s L0 content is diagnostic-only.
