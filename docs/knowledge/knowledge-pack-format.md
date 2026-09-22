# Knowledge / Experience Pack format

Status: **normative for V1.4-D design**
Implementation owner: `bOps.Commercial` Coordinator

## 1. Purpose

A `.bopsknowledge` pack is a signed, versioned, read-mostly content artifact imported by the Coordinator into the Semantic Knowledge Store.

Allowed kinds:
- `knowledge`: documentation, runbooks, known issues, technical references and best practices;
- `experience`: curated or synthetic troubleshooting incidents and operational patterns;
- `mixed`: allowed only when a Skill deliberately ships both; prefer separate packs.

A pack contains no executable code, scripts, migrations, system prompts, customer Operational Memory, credentials or raw customer data.

## 2. Deterministic container

Physical format: deterministic ZIP with UTF-8 names, forward slashes, lexicographic entry order, fixed timestamps, no absolute paths, no `..`, no symlinks.

Required layout:

```text
manifest.json
entries/<entry-id>.yaml
eval/queries.yaml
LICENSES/NOTICE.md
SIGNATURE/manifest.sig
SIGNATURE/publisher.json
embeddings/<embedding-profile-id>/index.json     # optional
embeddings/<embedding-profile-id>/vectors.bin   # optional
```

Default importer ceilings: 512 MiB uncompressed and 20,000 files; both are configurable downward/upward by the product but hard ceilings must exist.

## 3. Manifest

Required fields:

```json
{
  "schemaVersion": 1,
  "packId": "bops.core.experience",
  "version": "1.2.0",
  "kind": "experience",
  "publisher": "bSoft",
  "skillId": null,
  "minCoordinatorVersion": "1.4.0",
  "maxCoordinatorVersionExclusive": "2.0.0",
  "createdAtUtc": "2026-09-21T00:00:00Z",
  "entries": [],
  "tombstones": [],
  "embeddingProfiles": [],
  "contentSha256": "<64 hex>"
}
```

Rules:
- `schemaVersion` is exactly `1` in V1.4-D;
- `packId` is lower-case DNS-like, max 128 characters;
- `version` is SemVer 2.0.0 and is never reused with different bytes;
- `skillId` is null for Core packs and exact Skill id for Skill packs;
- every entry has `entryId`, `entryVersion`, relative path and SHA-256;
- `contentSha256` covers every unsigned file in deterministic path order;
- tombstones are explicit and may remove only entries historically owned by the same pack.

## 4. Common entry metadata

Every YAML entry contains:

```yaml
schemaVersion: 1
entryId: exp.linux.storage.io-wait.001
entryVersion: 1
sourceType: synthetic-incident
trustLevel: synthetic-validated
title: High load with low CPU caused by storage I/O wait
summary: Short reusable summary.
domain: system-performance
skillId:
product:
productVersionRange:
osFamilies: [linux]
tags: [storage, io-wait, performance]
confidence: 0.95
validUntilUtc:
provenance:
  kind: internal-lab
  reference: LAB-2026-001
  license:
review:
  status: approved
  reviewedBy: reviewer
  reviewedAtUtc: 2026-09-21T00:00:00Z
```

Allowed distributable `sourceType`: `official-reference`, `curated-knowledge`, `known-issue`, `runbook`, `synthetic-incident`, `curated-incident`.
Allowed distributable `trustLevel`: `official-reference`, `curated`, `synthetic-validated`.
`observed-tenant` and `inferred` are forbidden in distributable packs.

## 5. Experience entry body

```yaml
incident:
  symptoms:
    - Observable symptom before diagnosis.
  environment:
    product:
    productVersion:
    osFamily: linux
    topology: single-node
    characteristics: []
  evidence:
    - statement: Observable fact that supports the pattern.
      evidenceKind: metric
      expectedTools: [storage.io]
  negativeEvidence:
    - Observable fact that refutes a competing hypothesis.
  rootCause: Concise causal statement.
  remediation:
    - Safe remediation principle using governed capabilities.
  verification:
    - Observable condition proving recovery.
  outcome: confirmed
  antiPatterns:
    - Common unsafe or misleading diagnostic shortcut.
```

Allowed outcomes: `confirmed`, `refuted`, `partial`. `unknown` incidents are not published.

## 6. Knowledge entry body

Knowledge entries contain `problemStatement`, Markdown `content`, `applicableWhen`, `notApplicableWhen`, `evidenceToCollect`, `remediationNotes` and `references`.
Tool names inside knowledge are descriptive only; importing a pack never creates or enables tools.

## 7. Customer-data prohibition

Distributable packs must not contain customer/company/person names; hostnames/FQDN/IP/MAC; usernames/emails; customer database/schema/table names; customer ticket/account ids; internal URLs; credentials/secrets; raw customer logs; business query literals; customer-specific paths; screenshots or binary dumps.

Generic technology identifiers such as `postgresql.service` or `PAGEIOLATCH_SH` are permitted when they are intrinsic to the technology and not copied from a customer environment.

## 8. Precomputed embeddings

Embeddings are optional. `index.json` declares `embeddingProfileId`, model id, dimensions, cosine distance, chunking version, vectors file and exact chunk offsets.
`vectors.bin` is contiguous little-endian float32.
The importer accepts the entire vector set only when profile id, dimensions, chunk ids and byte length match exactly. Otherwise it discards that set and re-embeds locally. Vector spaces are never mixed.

## 9. Evaluation corpus

`eval/queries.yaml` contains realistic queries, metadata filters, `topK`, relevant entry ids and forbidden entry ids.
Do not use only entry titles as queries. Test paraphrases and confusing nearby patterns.
Official packs with 20+ entries require at least 20 release-gate queries.

## 10. Versioning

- PATCH: wording/reference/metadata corrections with no material diagnostic behavior change;
- MINOR: new entries or compatible diagnostic improvements;
- MAJOR: schema/behavior changes or intentional replacement/removal of material knowledge.

`entryVersion` increments whenever an entry changes materially. Released history is immutable.

## 11. Atomic import and rollback

A new pack version is imported side-by-side. Retrieval sees only the active version. Activation changes the active version in one PostgreSQL transaction. Failed staging cannot change the current active version or tenant Operational Memory. Previous active version remains available for rollback.

## 12. Signing

Knowledge packs reuse the Coordinator/update publisher trust introduced by V1.4-E. Do not create a second signing/key infrastructure.

## 13. Mandatory validation

The author/import tooling must reject zip-slip, decompression bombs, duplicate/case-colliding paths, duplicate entry ids, deep/alias YAML abuse, oversized scalars, invalid SemVer/ranges, hash mismatch, malformed vectors, wrong dimensions, illegal tombstones, unsigned/untrusted packs, PII/secret lint failures, and attempts to touch another pack or tenant Operational Memory.

The unsigned build must be byte-for-byte deterministic for identical normalized inputs.