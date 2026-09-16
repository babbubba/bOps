# ADR-0011 — Local-only execution now, with a contract shaped for remote agents later

Status: Accepted

**Backfill note.** Written at V0.9.1 to record the decision made at V0.1 (`agentic/06-decisions.md`
D-001, which this ADR restates in ADR form). `agentic/05-workflow.md` has listed this ADR as
owed since the project's first commit. The consolidated roadmap's V1.4 plans the actual
remote transport; this ADR is not superseded by that plan — it is the reason V1.4 does not need
to touch `NodeId`, the serializable tool boundary, or per-node registries when it arrives.

## Context

bOps administers a live machine. Two topologies were possible from the first commit: bOps runs
on the machine it administers (local-only), or a controller dispatches work to remote agents.
The second is the more ambitious long-term shape the project's commercial plan wants (a Control
Plane coordinating many nodes), but building it at V0.1 — inter-node authentication,
certificates, protocol versioning, fleet management — would have pushed the first working
version out by months for a capability nothing yet needed.

## Decision

bOps runs locally, on the machine it administers, through V0.9 (and beyond, until V1.4). The
*contract* is shaped from V0.1 so that adding remote agents later is a DI swap, not a rewrite:
`NodeId` on every `TaskState`, `PlanStep` and `AuditEvent` (rule A3) even though today there is
exactly one node and it is always `NodeId.Local`; `IToolRegistry`/`ICapabilityProbe` resolved
per-node rather than injected as process-wide singletons (rule A4); policy evaluated on the node
that will execute (rule A5), not only where a plan is made; `IAuditSink` writing to node-local
storage first, with aggregation as a separate, later concern (rule A6); and the entire tool
boundary crossing only as a serializable `ToolCallResult` (rule A2) — never a `Stream`, a live
`Process`, or any type a remote transport could not carry.

## Alternatives considered

- **Controller + remote agents from V0.1.** Rejected: doubles the project's surface before the
  core loop was even validated, for a capability with no operator asking for it yet.
- **SSH-based remote execution**, layering a transport onto the existing local tools. Rejected
  outright: it would demolish typed platform access and the safety model this project's whole
  premise rests on — a "run this over SSH" wrapper is, in effect, the generic execution tool
  rule S1 permanently forbids, wearing a different name.

## Consequences

Rules A2 through A6 cost close to nothing today — there is one node, `NodeId.Local`, and no
remote path exercises any of this. They are the entire difference between V1.4 adding remote
execution as new transport code versus rewriting the runtime to make remote execution possible
at all. The consolidated roadmap's V1.4 plan explicitly repeats the same constraint in its own
words: the Control Plane may send a node only typed objectives, never raw commands, and the node
re-checks policy, approval and entitlement locally regardless of what a remote controller says.
