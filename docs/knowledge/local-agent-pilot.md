# Knowledge in the CLI — Local Agent pilot

Status: **experimental pilot, opt-in, off by default** ([ADR-0058](../architecture/adr/0058-local-agent-knowledge-pilot.md), proposed as D-050).
Scope: `bops "<goal>"` and `bops resume` on one machine, for the operating-system user running it. Not the API, not the UI, not `bops delegate`.

## 1. What it does

With the pilot on, a task is bound to one signed *Knowledge pack* you installed and granted. On every agentic step (not on planning or
replanning) the model receives a short, host-generated **index** of the pack's skills (`K1`, `K1.R1`, …) as attributed reference data, and a
single read function, `runtime.knowledge_read`. The model decides whether to read a procedure; bOps serves it from a verified, hash-pinned
snapshot and audits the read. Nothing is forced into the prompt, a read grants no tool and changes no policy, and the tool surface of
every step is the one it was without Knowledge.

Three words, three different things:

| | Where it is decided | What it means |
|---|---|---|
| **Installed** | a directory under `Knowledge:PacksDirectory` | The bytes are on disk. Authorises nothing. |
| **Authorised** | `Knowledge:Grants` + the trust store + the signature | You listed this exact id, version and kind, and a key you trust signed it. |
| **Used** | the model's own `runtime.knowledge_read` calls | Audited per read (`KnowledgeRead` events). A task may use none. |

## 2. Requirements

* PowerShell 7 (`pwsh`) only for the publisher scripts; the CLI itself needs nothing extra on Windows. On Linux the CLI needs `libicu`
  (Knowledge normalises text; `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` makes Knowledge report itself unavailable rather than run degraded).
* A model server whose chat template accepts **two consecutive `user` messages** — see §6.

## 3. Get a real signed pack (publisher side — never on the machine that runs bOps)

The runtime holds no signing key and has no signing code. Keep the private key offline. (The pack format here is the directory format of
`bops-pack.json` + `bops-pack.sig.json`; it is not the `.bopsknowledge` archive of [knowledge-pack-format.md](knowledge-pack-format.md), which is a
V1.4-D Coordinator design.)

```powershell
# 1. Once per publisher key: a private key (keep offline) and a public trust-store entry.
pwsh scripts/New-KnowledgeSigningKey.ps1 -Publisher acme -KeyId acme-2026 -OutputDirectory D:\keys\acme

# 2. Sign the example pack. The source holds one directory per skill, each with a SKILL.md.
pwsh scripts/Publish-KnowledgePack.ps1 `
    -SourceDirectory docs/knowledge/examples/windows-event-triage-skills `
    -OutputRoot D:\bops-publish -PackId bsoft.windows-event-runbooks -Version 1.0.0 `
    -Publisher acme -KeyId acme-2026 -PrivateKeyPath D:\keys\acme\acme-acme-2026.private.pem
```

`docs/knowledge/examples/windows-event-triage-skills/` is the `windows-event-triage` skill (a procedure for separating observed
evidence from possible causes in Windows event data). Use your own publisher name and key id; the benchmark's `acme-2026` key and
packs under `docs/benchmarks/` are test artefacts whose private key does not exist in the repository, and must not be trusted.

The script refuses what bOps would refuse (non-text files, links, files outside a skill directory, key material), never rewrites an
existing version, and writes `bops-pack.json` (the signed manifest), `bops-pack.sig.json` (RSA-PSS-SHA256) and the payload under
`<OutputRoot>/<packId>/<version>/`.

## 4. Install on the machine that runs bOps

1. Copy `D:\bops-publish\bsoft.windows-event-runbooks\1.0.0\` to `<packs>\bsoft.windows-event-runbooks\1.0.0\` (e.g. `C:\ProgramData\bops\packs`).
   Restrict write access to administrators: the packs and the snapshots are only as safe as their permissions (S8).
2. Copy the **public** trust store (`acme-acme-2026.trust.json`) to e.g. `C:\ProgramData\bops\publisher-trust.json`. To trust a second
   publisher, add its entry to the `keys` array; to revoke, set `"revoked": true` on a key or list the publisher in `revokedPublishers`.
3. Create an empty directory for snapshots, e.g. `C:\ProgramData\bops\snapshots` (not inside the packs directory).
4. Configure it in the `appsettings.json` of the CLI's working directory (the shipped file next to `bops` carries only `Enabled: false`), or
   with environment variables (`Knowledge__Enabled=true`, `Knowledge__Grants__0__PackId=…`):

```json
{
  "Knowledge": {
    "Enabled": true,
    "PacksDirectory": "C:\\ProgramData\\bops\\packs",
    "SnapshotsDirectory": "C:\\ProgramData\\bops\\snapshots",
    "TrustStorePath": "C:\\ProgramData\\bops\\publisher-trust.json",
    "Grants": [
      { "PackId": "bsoft.windows-event-runbooks", "Version": "1.0.0", "Kind": "Knowledge" }
    ],
    "Binding": { "PackId": "bsoft.windows-event-runbooks", "Version": "1.0.0" },
    "Budget": { "MaxReads": 4, "MaxRejections": 4, "MaxCharactersPerRead": 8000 }
  }
}
```

| Key | Meaning |
|---|---|
| `Enabled` | `false` (shipped) = the CLI behaves exactly as without the feature. `true` = everything below is validated; any error ends the command with exit code 2. |
| `PacksDirectory` | Must exist. Layout `<packId>/<version>/`. |
| `SnapshotsDirectory` | bOps writes verified copies here, per tenant, by content hash. Separate from the packs directory. |
| `TrustStorePath` | JSON with public keys only. A file that contains a private key, or no key, is rejected. |
| `Grants` | The exact versions the Local Agent may use. `Kind` must be `Knowledge`. There is no tenant key and no wildcard. |
| `Binding` | The pack new tasks are bound to (one per task). It must also be granted, or the task is refused (`NotEntitled`). |
| `Budget` | Per attempt. Ceilings: reads 8, rejections 8, characters per read 16000. |

An unknown or misspelt key under `Knowledge` is an error even when `Enabled` is `false`.

## 5. Try it

```powershell
bops "Analizza gli eventi Windows delle ultime 24 ore, identifica eventuali problemi di stabilità e distingui le evidenze dalle possibili cause."
```

On start the CLI logs `Knowledge is enabled for the Local Agent: tasks are bound to '…'`. After the run:

* `bops audit verify` — the hash chain must be valid.
* `audit.jsonl` holds, per task: one `KnowledgePackAdmission` (code `Admitted` or the refusal cause), one `KnowledgeIndex` per
  publication (references and counts, no text), and one `KnowledgeRead` per attempted read (reference, hash, result — never content).
  If there is no `KnowledgeRead`, the model chose not to read; that is a valid outcome and is reported, not corrected.
* A refused admission ends the task `Failed` before any model call; the transcript says `Knowledge unavailable: <code>`:

| Code | Usual cause |
|---|---|
| `NotEntitled` | The bound pack/version/kind is not in `Grants`. |
| `UnknownKey` / `KeyRevoked` / `PublisherRevoked` / `InvalidSignature` | The signing key is not in the trust store, was revoked, or the signature does not verify. |
| `SourceUnavailable` | Nothing at `<packs>/<id>/<version>`. |
| `HashMismatch` / `UnexpectedContent` / `MissingResource` | The installed files differ from the signed manifest. |
| `EngineIncompatible` | The pack's engine range does not contain this bOps version. |
| `PinMismatch` / `SnapshotTampered` | On resume: the bytes or the snapshot changed since the task started. |

`bops resume <task-id>` re-admits the pinned content under today's configuration. A task bound to knowledge is never resumed without it
(disable the section and the resume fails closed with `KnowledgeUnavailable`).

## 6. Before you rely on it

* **Server compatibility (ADR-0057 B2) is unverified.** With Knowledge on, every step request starts with the index as a `user` message
  followed by the goal as another `user` message. Servers/templates that require strict user/assistant alternation can reject it. Check
  against your model server first; the pilot does not merge the index into the goal.
* **Revocation is seen at the next resume or admission** (B1), not inside a running attempt, except provider/tenant/snapshot changes.
* **No quality claim.** Whether the pack improves a diagnosis is not measured here; no benchmark was run for this pilot.
* The index adds tokens to every step request even if nothing is read.

## 7. Turning it off, and what is not here

Set `Knowledge:Enabled` to `false` (or remove the section). Installed packs, snapshots and the trust store are left alone.
Not implemented: binding a pack by capability or goal, several packs per task, Experience packs, a `bops knowledge` command, hot reload
of grants and trust, and any API or UI composition (ADR-0058 §6–§7).
