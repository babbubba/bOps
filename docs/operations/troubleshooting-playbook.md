# Troubleshooting playbook

## Shared decision-tree rules

Each scenario records **Evidence** (tool observation), **Inference** (bounded conclusion), and **Unknown** (missing, partial, unavailable, or truncated evidence). Diagnose before remediation. No scenario authorizes generic execution, automatic termination, restart, SQL, or unbounded collection.

## Scenario stubs

The L1-L8 packets fill these decision trees without changing this structure.

1. DB port 5432/1433 unreachable
2. Service keeps crashing
3. DB/application became slow
4. File cannot be replaced/deleted
5. TLS connection fails
6. Scheduled job did not run
7. Reboot/update regression
8. Docker-hosted service failure

## Completeness and remediation boundaries

An incomplete observation cannot establish a negative. Record its state and preserve the resulting unknown. Remediation remains separately policy-authorized, verified, and audited; this playbook’s L0 content is diagnostic-only.
