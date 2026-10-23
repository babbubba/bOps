# Historical archive (no authority)

This directory holds the 55 legacy ADRs and the previous full versions of `agentic/01-architecture-rules.md` and `agentic/06-decisions.md`, packed unmodified in one ZIP. It is provenance only: **nothing here is normative and no agent or bootstrap should read it** unless the operator explicitly asks for historical research.

The current architecture is in [`docs/architecture/current/`](../current/). To find where a historical identifier went, use the compatibility indexes ([ADR index](../adr/README.md), `agentic/01-architecture-rules.md`, `agentic/03-security-rules.md`, `agentic/06-decisions.md`), not this archive.

| File | Purpose |
|---|---|
| `legacy-adr-and-decisions-2026-10-22.zip` | The 57 originals, original repository-relative paths |
| `legacy-adr-and-decisions-2026-10-22.zip.sha256` | SHA-256 of the ZIP (sidecar) |
| `MANIFEST.md`, `MANIFEST.csv` | Path, size, content SHA-256, status, destination and the identifier map, kept outside the ZIP |

Recovering one original, only on explicit request:

```bash
unzip -d /tmp/legacy-adr docs/architecture/archive/legacy-adr-and-decisions-2026-10-22.zip docs/architecture/adr/0014-*.md
sha256sum /tmp/legacy-adr/docs/architecture/adr/0014-*.md   # compare with MANIFEST.csv
```

Verify the archive: `sha256sum -c legacy-adr-and-decisions-2026-10-22.zip.sha256` (run in this directory). The Git history is not rewritten; the same files remain in commit `e0e9de7a74b8bb33e07c7b1f8c92ab87832986de`.
