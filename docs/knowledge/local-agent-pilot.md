# Knowledge in the CLI — Local Agent pilot

Status: **experimental pilot, opt-in, off by default** ([ADR-0058](../architecture/adr/README.md#adr-0058), proposed as D-050).
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

**What a refused read does (ADR-0059, Proposed).** A read is optional, so a refused one ends the *reading*, not the task. Mistakes about
a ref, an argument, a page or a size, or a failure of the framework function (`Malformed`, `Unsupported`, `UnknownSkill`, `UnknownResource`,
`OutOfRange`, `TooLarge`, `InfrastructureFailure`) are recoverable: each is audited and answered with an actionable sentence; after more than `MaxRejections` of
them the function is **withdrawn for the rest of the attempt** (the audit event carries `Withdrawn: true`) and the step goes on with its
operational tool. Calls to a function that is spent or withdrawn (`LimitExceeded`, `NotOffered`) are tolerated twice and the third ends
the task. Security refusals (`NotAuthorized`, `SnapshotUnavailable`, a content that differs from the pin) are never recoverable: more than
`MaxRejections` of them ends the task as before. `InfrastructureFailure` (the framework function threw, nothing was returned) is
audited apart from `IntegrityMismatch`; it is not a security signal, but nothing is ever served from it. A duplicate read is served and audited with `Repeat`; the read budget bounds the loop.

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
* **Timeouts for a slow local model.** The CLI ships no model-call overrides: attempt timeout 120 s, HTTP timeout 150 s, call budget 300 s. A
  reasoning model at ~60 tokens/s needs ~137 s for an 8192-token output plus prefill, so the 120 s attempt timeout can cut a legitimate
  answer. Minimal operator change (keep 32K/8K): `ModelProvider:RequestTimeout=00:05:00`, `Agent:ModelCallAttemptTimeout=00:04:00`,
  `Agent:ModelCallBudget=00:08:00` (attempt < HTTP timeout is validated; the budget must cover two attempts at `ModelCallMaxAttempts`
  as you set it). The API's shipped profile (300 s / 360 s / 660 s) is already coherent. Retry behaviour and token limits are unchanged.
* The index adds tokens to every step request even if nothing is read.

## 7. Turning it off, and what is not here

Set `Knowledge:Enabled` to `false` (or remove the section). Installed packs, snapshots and the trust store are left alone.
Not implemented: binding a pack by capability or goal, several packs per task, Experience packs, a `bops knowledge` command, hot reload
of grants and trust, and any API or UI composition (ADR-0058 §6–§7).

## Admission contract and marker neutralization (technical specification)

*From ADR-0057 §U.1 and §U.2 (restated in full during the GATE-02 corrections of the architecture consolidation). This section is the technical detail that rule
`C05-R13` of `docs/architecture/current/05-interfaces-knowledge-and-hosts.md` needs for its Knowledge Discovery M1 scope (ADR-0057 rev. 2, D-049). It is not a
new architectural authority: it carries no decision beyond that rule, and it does not make ADR-0054/0055/0056/0058/0059 Accepted.*

### Admission constraints on names, descriptions and identifiers (U1–U10)

Applied in `KnowledgeTextPolicy.ValidateDescription` (`bOps.Knowledge`, pure), called by `KnowledgeReadBridge.ValidateAdvertisement` in place of the earlier
conditions on `description`. A violation raises `KnowledgePackIntegrityException(SkillInvalid)`, so the **whole pack is refused** and audited. The checks are
deterministic, reject and **never rewrite**.

| # | Rule | Detail |
|---|---|---|
| U1 | Well-formed UTF-16 | enumeration with `Rune.DecodeFromUtf16`; an unpaired surrogate is rejected |
| U2 | Normalization | `IsNormalized(NormalizationForm.FormC)`; text that is not NFC is rejected (no silent normalization: what is reviewed, pinned and shown is the same text) |
| U3 | Category allow-list (`Rune.GetUnicodeCategory`, internal .NET tables, not ICU) | allowed: `Lu Ll Lt Lm Lo Nd Nl No Pc Pd Ps Pe Pi Pf Po Sm Sc Sk So` and **only U+0020** among the `Zs`. Everything else is rejected: `Cc` (including TAB, CR, LF), `Cf` (bidi U+061C, U+200E, U+200F, U+202A–U+202E, U+2066–U+2069; zero-width U+200B–U+200D, U+2060, U+FEFF; soft hyphen U+00AD; tags U+E0001, U+E0020–U+E007F), `Cs`, `Co`, `Cn`, `Zl`, `Zp`, other `Zs` (U+00A0, U+3000, …), `Mn`, `Mc`, `Me` (including variation selectors U+FE00–U+FE0F, U+E0100–U+E01EF, stacked combinations) |
| U4 | Forbidden ASCII | `<` `>` `[` `]` `{` `}` `` ` `` `\|` `\` `"` |
| U5 | Lengths and spaces | 1–256 UTF-16 units **and** ≤ 768 UTF-8 bytes; no leading or trailing space; no run of 2 or more spaces |
| U6 | Forbidden content (on the NFKC-folded, `ToLowerInvariant` form) | substrings `runtime.`, `load_skill`, `read_skill_resource`, `run_skill_script`, `knowledge-index`, `skill-data`, `bops_tool_output`, `im_start`, `im_end`, `endoftext`; regex `(^|[^\p{L}\p{N}])(system|developer|user|assistant|tool|operator|human)\s*:` (role labels, also in fullwidth form thanks to NFKC) |
| U7 | Skill name | unchanged `^[a-z0-9][a-z0-9-]{0,63}$` (ASCII; it cannot contain `.` or spaces) |
| U8 | Resource path | unchanged `PackPathPolicy` at admission: segments `^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$` (no `..`, no empty segments), ≤ 128, reserved device names, links and reparse points rejected. In the index it is display only; resolution is by exact ordinal key in the pin or by ref; bOps never composes a path at run time |
| U9 | Identifiers | `packId`, `version` and `publisher` are already shape-checked (`PackReference.IsPackId/IsVersion`, `KnowledgeTrustStore.IsIdentifier`) |
| U10 | Other frontmatter fields | unchanged (`FrontmatterWithinBounds`); they are **never** rendered in the index, in the control text or in `discover` |

The `query` argument of `discover` uses U1–U3 plus the allow-list "letters, digits, U+0020, `-`".

Accepted limits (ADR-0057 §U.4): confusables across scripts (UTS #39) are not verified (a description confers no identity and names are ASCII); scripts that
need combining marks (Devanagari, Thai, Arabic diacritics) are not admitted in descriptions in M1; `Rune.GetUnicodeCategory` is deterministic per .NET version
while `IsNormalized` can depend on ICU for recent code points, mitigated by U3 (`Cn` rejected) and by tests on Windows and Linux.

### Marker neutralization (§U.2)

`[knowledge-index`, `[/knowledge-index`, `[knowledge-index-control` and `[/knowledge-index-control` are neutralized **case-insensitively** in `WrapToolOutput`
(`AgentRunner`, which used `Ordinal` for the earlier markers: the new markers use `OrdinalIgnoreCase`) and in the bridge's `Neutralize`, which also applies to
`discover` pages.

## Globalization requirement (technical specification)

*From ADR-0057 §U.5 (consolidated during the architecture consolidation, DOC-02; the M1 development scope is rule `C05-R13` and the remaining knowledge-engine mechanism is a pending decision, see
`docs/architecture/current/05-interfaces-knowledge-and-hosts.md`).*

Unicode normalization (NFC check of descriptions, NFKC fold of the forbidden-content test and of the lexical scoring) needs the .NET normalization
support, which does not exist under `InvariantGlobalization=true` (`Normalize` returns the text unchanged and `IsNormalized` answers `true` even for
non-normalized text). Availability is decided by the hosting process (runtimeconfig), not by the `bOps.Knowledge` library. The repository default
(`Directory.Build.props`) stays invariant; `InvariantGlobalization=false` is set only in the hosts that run discovery (`bOps.Api`, `bOps.Cli`,
`bOps.Knowledge.Tests`), with the invariant culture pinned so nothing else changes. On Linux the process needs `libicu`, and
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` overrides the runtimeconfig and returns the process to invariant mode. In invariant mode the policy
closes safely: `KnowledgeTextPolicy.NormalizationAvailable` (probed once by normalizing U+212B and U+FF21) is `false`, every non-ASCII description or
query is refused (`NormalizationUnavailable`), ASCII text is judged exactly, and the selector and the `discover` query raise
`KnowledgeNormalizationUnavailableException` (code `KNOW-NORM-UNAVAILABLE`, a fixed message without text) before scoring; no fallback accepts
unverified metadata and no degraded score is produced. The `sha256=` value in the index marker is the first 12 hex characters of the SHA-256 of the
entry lines only, while `IndexHash` is the SHA-256 of the whole index text.
