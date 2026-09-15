# 0019 — `GET /api/providers`: amends ADR-0018's deferral

Status: Accepted
Date: 2026-09-15

## Context

ADR-0018 explicitly deferred a provider-listing endpoint: *"No OpenAPI/Swagger generation, no
provider-listing endpoint (`GET /api/providers`)... `IChatModelRegistry` also has no enumeration
method today... adding one is a `bOps.Abstractions` change with no current caller, deferred to
whichever session actually builds `GET /api/providers`."* That session is this one — the Angular
UI now has a Settings page, and the operator asked for it to show which LLM providers are
available and which one is active, not just which tools are.

This amends an entry in `06-decisions.md`-adjacent ADR-0018 and alters a type in
`bOps.Abstractions` (`IChatModelRegistry`), so per `agentic/05-workflow.md` it needs its own ADR
rather than a silent implementation change.

## Decision

**`IChatModelRegistry` gains one read-only member**: `IReadOnlyList<string> RegisteredProviderIds`
— every provider id at least one registered package supports, deduplicated. This mirrors
`IToolRegistry.GetAvailableManifests()`'s existing shape (an operator-facing "what could I ask
for" listing, separate from the resolve-and-construct path `Create`/`Resolve` already serve) and
adds no new concept to the core: no provider metadata beyond the id string, no package identity,
no capability probing. `ChatModelRegistry` implements it directly off the same
`ConcurrentDictionary<string, IModelProviderPackage>` `Register` already populates.

**`bOps.Api` gains `GET /api/providers`**, returning the registered ids and a summary of the one
provider actually configured for this host — provider, model, base URL, and whether an API key is
present, **never the key's value**. The active summary comes from the same `ModelProvider`
configuration section `Program.cs` already binds to construct the singleton `IChatModel`; this
endpoint does not add a second source of truth, it exposes the existing one read-only.

**Still no OpenAPI/Swagger generation.** That part of ADR-0018's deferral stands — the operator
confirmed the hand-written TypeScript client continues (see `HANDOFF.md`), so there is still no
consumer for a generated spec.

## Alternatives considered

- **Expose full `ChatModelOptions` for the active provider, including `ApiKey`.** Rejected —
  `bOps.Api` has no authentication (ADR-0018); anything reachable by any caller must not include a
  live credential, even one already present in local configuration the operator controls. A
  boolean `hasApiKey` tells the Settings page what it needs (is this provider actually usable)
  without the leak.
- **Let a package advertise richer provider metadata (display name, capability flags) through a
  new `bOps.Abstractions` type.** Rejected for this pass — no current UI need beyond an id string
  and the one active configuration; adding metadata now would be exactly the kind of
  anticipating-a-later-version scope creep `05-workflow.md` warns against. Revisit if a future
  Settings page needs to let an operator switch the active provider at runtime rather than only
  view it.

## Consequences

- `bOps.Abstractions` has one new interface member; every existing `IChatModelRegistry`
  implementation (`ChatModelRegistry`, and any test double) must implement it — none other than
  `ChatModelRegistry` exists in the repository today, so this is not a breaking change to ship
  alongside a migration.
- `GET /api/providers` is read-only and adds no new runtime behavior beyond what `Program.cs`
  already computes at startup; it does not change how a provider is selected or constructed.
- Settings remains view-only in this pass — no endpoint lets an operator change `ModelProvider` at
  runtime. That gap is unchanged from before this ADR, not newly introduced by it.
