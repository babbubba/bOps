# KNOW-FIX-01 — Knowledge observability and prompt consistency

Follows KNOW-ARCH-REVIEW-01. No change to trust, grants, signatures, entitlements, the Knowledge Pack, MAF/NuGet packages,
budgets, planning or completion criteria. Deterministic discovery (KNOW-DISC-01) is not implemented.

1. **Export discriminator.** `SafeAudit` allowlisted `$type`, but the persisted discriminator is `eventType`
   (`JsonPolymorphic` on `AuditEvent`), so every `audit.jsonl` line lost its type. It now exports `eventType` when the value
   is one the audit contract declares (`JsonDerivedType`), otherwise the redaction marker. A knowledge read's `Operation` is
   exported only as `discover`/`load`/`read_resource`. Export schema stays v2; both fields are additive.
2. **Routing telemetry.** `bops.tool_routing.events_total` (stage `announced`/`selected`/`executed`/`refused`, `tool`, closed
   `reason`) and matching activity events. Announced = the request's tool view, selected = proposed calls (an unoffered or
   unmapped name is `[unoffered]`), executed = dispatched by the runtime, refused = rejected before anything ran. It writes no
   audit event. `result.json` gains `toolRouting`, which holds this task's events only, reduced to approved names and closed codes.
3. **Prompt coherence.** "The single offered native tool" and "No native tool is authorized" are used only when no runtime
   control function is offered beside the step view. Otherwise the prompt names the step tool as the only *operational* tool.
   When `runtime.knowledge_read` is offered it is described as optional, as data not instruction, and as granting no authority.
   The name comes from the session manifest, and no catalog is added.

Offline evidence: `KnowledgeObservabilityTests` (real LlamaCpp adapter, in-memory HTTP, native and JSON fallback).

Known limits: calls after the first runtime control call in one reply are `selected` but neither `executed` nor `refused`
(first-call-wins). Counters are process-wide; only the activity-event path is attributable to one task. Native wire aliases
are inferred from the offered list, not from a provider capture.
