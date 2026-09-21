# Experience Pack authoring manual

Status: **authoring procedure for V1.4-D**
Audience: bSoft maintainers, Skill authors, QA/lab engineers
Goal: turn reusable operational knowledge into distributable Experience Packs **without customer data**.

## 1. What an incident entry is

An incident entry is not a ticket transcript and not a postmortem dump. It is a generalized diagnostic pattern:

```text
symptom
  -> evidence to collect
  -> evidence that confirms/refutes hypotheses
  -> root cause
  -> remediation principles
  -> verification
```

The entry must be useful on a fresh installation and a different environment without knowing the original machine, customer or ticket.

## 2. Preferred sources

Use, in priority order:
1. reproducible lab scenarios created specifically for bOps;
2. synthetic scenarios derived from documented product behavior;
3. official public documentation/known issues whose license permits use;
4. your own technical expertise/playbooks;
5. real-world lessons only after rewriting them from scratch into a generalized case with no customer-derived identifiers or raw data.

Never copy a customer ticket/log into a pack and then merely anonymize fields. Start a new generalized entry.

## 3. Source layout

Core Experience Pack:

```text
knowledge/core/experience/
  entries/
  eval/queries.yaml
```

Skill Experience Pack:

```text
knowledge/skills/<skill>/experience/
  entries/
  eval/queries.yaml
```

Run `knowledge validate <source-directory>` before every commit.

## 4. Incident authoring procedure

### Step 1 — permanent identity

Format: `exp.<domain-or-product>.<pattern>.<nnn>`.
Examples: `exp.linux.storage.io-wait.001`, `exp.postgresql.wal.inactive-slot.001`, `exp.sqlserver.io.pageiolatch.001`.
Use lower-case ASCII, digits, dots and hyphens. Never recycle ids. Start with `entryVersion: 1`.

### Step 2 — title

For confirmed patterns use symptom + cause/pattern.
Good: `WAL growth caused by an inactive replication slot`.
Bad: `Postgres problem`, ticket ids, customer names or host names.
For `refuted` entries title the tested hypothesis.

### Step 3 — symptoms before causes

Symptoms must be observable before diagnosis.
Good: `WAL disk usage increases continuously`.
Bad: `An inactive replication slot retains WAL` because that already states the diagnosis.

### Step 4 — environment applicability

Record only facts that materially affect applicability: product, relevant version range, OS family, topology, storage/network model and required feature/configuration. Omit incidental details.

### Step 5 — evidence

Every evidence item answers: **what measurable/observable fact makes this pattern more or less likely?**
Prefer bOps tool names in `expectedTools` when one exists.

```yaml
evidence:
  - statement: An inactive replication slot retains WAL and its retained position does not advance.
    evidenceKind: database-state
    expectedTools: [postgres.replication_status]
  - statement: WAL storage usage continues to increase.
    evidenceKind: metric
    expectedTools: [storage.mounts]
```

Prefer relationships/ranges to exact customer values.

### Step 6 — negative evidence

Negative evidence is required for `refuted` entries and strongly encouraged otherwise.
Example: `No long-running transaction explains WAL retention.`

### Step 7 — root cause

State one concise causal explanation. Do not restate the symptom. If causality is not sufficiently demonstrated, use `partial`, not `confirmed`.

### Step 8 — remediation

Describe safe remediation principles that map to governed capabilities. Do not embed uncontrolled shell/SQL commands.
Good: confirm an unused replication slot and remove/advance it through the governed PostgreSQL remediation capability.
Bad: raw destructive SQL.

### Step 9 — verification

State what proves recovery using read-only evidence where possible. Example: retained WAL stops growing and normal recycling resumes.

### Step 10 — anti-patterns

Record common unsafe or misleading shortcuts, e.g. `Never delete WAL files manually`.

### Step 11 — confidence

- `1.00`: reproducible repeatedly with clear causality;
- `0.90–0.99`: strong verified evidence;
- `0.75–0.89`: useful pattern with environmental variability;
- below `0.75`: keep as draft unless there is a specific reason to publish.

Never raise confidence merely because an LLM suggested the pattern.

## 5. Required human review

### Technical review
- symptom is distinct from cause;
- evidence supports or refutes the cause;
- remediation follows governed bOps capabilities;
- verification can actually confirm improvement;
- product/version applicability is correct;
- obsolete behavior is not presented as current.

### Privacy review
Search manually and with tooling for person/company names, hostname/FQDN/IP/MAC, usernames/emails, real database/table/schema names, internal URLs, ticket/account/customer ids, customer paths, raw logs, business query data and secret-like strings.
If there is doubt whether a string came from a customer environment, remove or rewrite it.

### Security review
Incident content is data. It must not contain instruction-like payloads such as `ignore system prompt`, `bypass policy`, `run arbitrary shell`, `auto-approve`, or `disable verification`. Malicious text seen in original logs must not be propagated into packs.

## 6. Creating synthetic incidents in the lab

Synthetic incidents are preferred because they are reproducible and free of customer data.

Procedure:
1. define one target failure;
2. create disposable VM/container/test service;
3. introduce one controlled fault;
4. collect bOps evidence before fault;
5. activate fault;
6. collect symptoms/evidence;
7. perform governed remediation;
8. collect verification;
9. reset environment;
10. repeat at least twice.

Good lab candidates: filesystem nearly full, injected I/O latency, service crash loop, expired TLS certificate, DNS fault, firewall block, Docker OOM, PostgreSQL inactive replication slot, PostgreSQL blocked autovacuum, SQL Server blocking chain, SQL Server storage-latency waits.

Fault-injection scripts belong in test infrastructure, never inside the distributable pack.

## 7. From a real incident to generalized experience

There is deliberately no automatic promotion of tenant Operational Memory into global knowledge.

If a real incident reveals a reusable lesson:
1. do **not** export the Operational Memory record;
2. open a blank incident template;
3. rewrite the symptom generically from technical understanding;
4. replace exact values with relationships/ranges;
5. reconstruct evidence as generic statements;
6. remove customer topology not essential to diagnosis;
7. assign a new global `entryId`;
8. use generic provenance such as `internal-curation`, never the customer ticket id;
9. perform independent privacy review;
10. reproduce in lab where possible before assigning `synthetic-validated`; otherwise use `curated` and suitable confidence.

## 8. Retrieval evaluation queries

For every important incident write several realistic phrasings. Do not just paste the title.

```yaml
- id: pg-wal-growing-plain
  query: PostgreSQL WAL keeps growing and disk is filling
- id: pg-wal-growing-operator
  query: pg_wal aumenta continuamente anche se il carico non è cresciuto
- id: pg-wal-growing-symptom
  query: database is healthy but transaction log storage never gets released
```

Also add confusing nearby patterns as negative tests so one incident does not dominate retrieval incorrectly.

## 9. Release process

```text
edit entries
  -> knowledge validate
  -> knowledge build
  -> knowledge inspect
  -> knowledge diff <previous> <new>
  -> knowledge embed --profile <official-profile>
  -> retrieval evaluation
  -> human approval
  -> V1.4-E signing
  -> publish signed .bopsknowledge
```

Schema validation alone is not a release gate. Official pack evaluation must meet the retrieval thresholds defined by the V1.4-D plan.

The human-readable diff must show added/changed entries, old/new `entryVersion`, tombstones, compatibility changes, evaluation changes and embedding-profile changes.

## 10. Independent update channel

Knowledge/Experience Packs version independently from bOps binaries. A pack may be updated without application binaries when its schema, Coordinator compatibility, Skill compatibility and embedding requirements remain supported.

Recommended catalog metadata: pack id, SemVer, SHA-256, signature, minimum Coordinator version and release time. Distribution uses the same trusted update channel established by V1.4-E.

## 11. Updating an incident

Keep `entryId`; increment `entryVersion`. Material changes include root-cause correction, applicability/version range, evidence, remediation, verification, trust or confidence with diagnostic effect.
Released history is immutable. Never reuse a version with different content.

## 12. Removing an incident

Releases remove entries only through explicit manifest tombstones. Use removal for technically dangerous/wrong content, legal/licensing problems, privacy issues or content that would actively mislead retrieval. Age alone is not enough; prefer narrowing applicability or setting `validUntilUtc`.

## 13. Complete example

```yaml
schemaVersion: 1
entryId: exp.postgresql.wal.inactive-slot.001
entryVersion: 1
sourceType: synthetic-incident
trustLevel: synthetic-validated
title: WAL growth caused by an inactive replication slot
summary: PostgreSQL WAL grows continuously when an unused replication slot retains old WAL.
domain: database-storage
skillId: bops.postgresql.dba
product: postgresql
productVersionRange: ">=13 <19"
osFamilies: [linux, windows]
tags: [wal, replication, storage]
confidence: 0.98
provenance:
  kind: internal-lab
  reference: LAB-PG-WAL-001
review:
  status: approved
  reviewedBy: reviewer
  reviewedAtUtc: 2026-09-21T00:00:00Z
incident:
  symptoms:
    - WAL storage usage grows continuously.
    - Normal workload volume does not explain the growth.
  environment:
    product: postgresql
    topology: single-or-replicated
    characteristics: [replication-slots-enabled]
  evidence:
    - statement: At least one replication slot is inactive and retains WAL.
      evidenceKind: database-state
      expectedTools: [postgres.replication_status]
    - statement: WAL filesystem usage grows while the retained position does not advance.
      evidenceKind: metric
      expectedTools: [storage.mounts]
  negativeEvidence:
    - Archive failure is not the primary retention mechanism.
    - No long-running transaction explains retained WAL.
  rootCause: An inactive replication slot prevents normal WAL recycling.
  remediation:
    - Confirm the slot is no longer required.
    - Remove or advance it through the governed PostgreSQL remediation capability.
  verification:
    - The retaining slot no longer exists or advances.
    - WAL filesystem growth stops and normal recycling resumes.
  outcome: confirmed
  antiPatterns:
    - Never delete WAL segment files manually.
```

## 14. Definition of authoring quality

An entry is ready only when another experienced administrator who never saw the source incident can recognize when the pattern may apply, identify what to measure, understand what would refute it, understand likely root cause, select a safe governed remediation path, verify the result, and cannot infer the identity of any customer/person/system.