# Active task index

This directory contains executable public-repository work. Read the consolidated roadmap first,
then open only the first task whose dependencies and operator gates are satisfied. Do not use task
snapshots under `agentic/obsolete/`.

| Order | Task | Status | Effort |
|---:|---|---|---|
| independent | [`Formal V1.0 release gate`](2026-09-16-release-v1.0-formal-gate.md) | Done for `v1.1.0-preview.2` | medio |
| parallel admin | [`Repository topology bootstrap`](2026-09-16-repository-topology-bootstrap.md) | Complete (accepted deviations recorded); gate for V1.3 satisfied | alto |
| 1 | [`V1.1-A Skill SDK completion`](2026-09-16-v1.1-a-skill-sdk-completion.md) | Complete | molto alto |
| 2 | [`V1.1-B system inventory`](2026-09-16-v1.1-b-system-inventory.md) | Complete | alto |
| 3 | [`V1.1-C filesystem inventory`](2026-09-16-v1.1-c-filesystem-inventory.md) | Complete | alto |
| 4 | [`V1.1-D governed recursive delete`](2026-09-16-v1.1-d-governed-recursive-delete.md) | Complete | molto alto |
| 5 | [`V1.1-E Web capabilities`](2026-09-16-v1.1-e-web-capabilities.md) | Complete | molto alto |
| 6 | [`V1.1-F plugin catalog UI`](2026-09-16-v1.1-f-plugin-catalog-ui.md) | Complete | medio |
| 7 | [`V1.1-G secure Settings`](2026-09-16-v1.1-g-secure-settings.md) | Complete | molto alto |
| 8 | [`V1.1-H integration/release`](2026-09-16-v1.1-h-integration-release.md) | Complete | alto |
| 9 | [`V1.2 multi-agent`](2026-09-16-v1.2-multi-agent.md) | Complete — A to M implemented and release gate closed | molto alto, split A–M |
| 10 | [`V1.3-A system events`](2026-09-21-v1.3-a-system-events.md) | **Future — next** | alto |
| 11 | [`V1.3-B Docker management`](2026-09-21-v1.3-b-docker-management.md) | Future | molto alto |
| 12 | [`V1.3-C OSS entitlement/plugin lifecycle`](2026-09-16-v1.3-oss-entitlement-plugin-lifecycle.md) | Future | molto alto |
| 13 | [`V1.4 node/Control Plane protocol`](2026-09-16-v1.4-node-control-plane-protocol.md) | Future | molto alto |
| 14 | [`V2.0 OSS GA readiness`](2026-09-16-v2.0-oss-ga-readiness.md) | Future | molto alto |
| 15 | [`Post-V2.0 bilingual README`](2026-09-16-post-v2.0-bilingual-readme.md) | Future | medio |
| independent | [`Multilingual UI (Italian and English)`](2026-09-19-ui-multilanguage-it-en.md) | **Implemented** | medio |

Private companion implementation from V1.3 onward belongs in `bOps.Commercial`. The private coordination root
tracks cross-repository sequencing after it exists; it must not duplicate these task bodies.

## Execution rules

- Never start a later ordered task while an earlier dependency remains open.
- Status checkboxes describe verified work only; do not check them prospectively.
- A required ADR is written and accepted before the implementation it governs.
- External mutations — repository creation, push, tag, publication or release — require explicit
  operator authorization at execution time.
- Keep README, CHANGELOG, HANDOFF and the selected task aligned with validated results.
