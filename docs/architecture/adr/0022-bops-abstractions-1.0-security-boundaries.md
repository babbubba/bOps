# ADR-0022: Freeze `bOps.Abstractions` 1.0 around explicit security boundaries

**Status:** Accepted  
**Date:** 2026-09-16

## Context

V1.0 is the first release that promises semantic compatibility for the public plugin SDK. It is
also the gate at which the API and dynamic package loader must stop relying on informal local
trust. The pre-1.0 surface carries raw provider credentials in `ChatModelOptions`, exposes no
host-owned way to refer to a secret, and has a package trust enum without cryptographic
provenance behind it.

The runtime still executes plugins in-process. An `AssemblyLoadContext` isolates dependencies,
not permissions, so no contract can make an installed malicious assembly safe. Authentication,
secret references and signatures must therefore state exactly what they prove and no more.

## Decision

1. `bOps.Abstractions` is published as `1.0.0` after the V1.0 tests and documentation close. From
   that point, its public API follows SemVer.
2. Add `SecretReference` and `ISecretProvider`. A reference contains a provider id and an opaque
   name, never a value. Hosts resolve references at the last responsible moment. Secret providers
   are host infrastructure and are not exposed through the restricted plugin activation
   container; model-provider packages receive only the one credential selected for their own
   configured model through `ChatModelOptions`.
3. API identities come from the authenticated channel. Request DTOs cannot nominate an approver.
   Roles distinguish observation, task operation and approval.
4. Package provenance is a detached signature over a deterministic inventory of the complete
   package directory. A local trust store maps publisher/key ids to public keys. Installation
   records the verification result; enabling fails closed unless provenance is trusted.
5. Package signatures prove that bytes came from a trusted key and were not modified after
   signing. They do not sandbox code, establish code quality, or make publisher claims true.
6. In-flight approvals are deliberately not restored after a crash. A resumed task must request
   a new approval from a currently authenticated approver. Persisting an old approval would turn
   a point-in-time human decision into a reusable bearer capability.
7. Existing local-only execution, node-local policy enforcement and node-local audit semantics
   remain unchanged. No V1.1 Skill/Capability or remote-transport contract is introduced.

## Alternatives considered

### Put secret values directly in plugin configuration

Rejected. It makes accidental serialization/logging easy and gives a package access to more
configuration than the single value the host selected for it.

### Expose `ISecretProvider` in the restricted plugin DI container

Rejected for 1.0. A provider capable of resolving arbitrary names is broader authority than a
package needs. Supplying one selected credential to an already trusted in-process model adapter is
smaller and auditable. A future scoped secret resolver would require a new compatible contract.

### Use assembly strong names as publisher signatures

Rejected. Strong names are identity/versioning machinery, not a publisher trust policy, and do
not cover non-assembly package content such as manifests or native dependencies.

### Automatically trust anything signed

Rejected. A valid signature from an unknown key is only authenticated unknown code. Trust is a
local operator decision, expressed by the trust store.

### Persist completed approval decisions across restarts

Rejected. The original request may no longer be live, the approver's access may have been revoked,
and the target state may have changed. Re-approval on resume is safer and operationally clear.

## Consequences

- Hosts must provide an `ISecretProvider` and must not log resolved values.
- API clients must authenticate and the local UI must carry credentials without placing them in
  URLs. The browser UI uses authenticated polling because native `EventSource` cannot attach a
  bearer header; the authenticated SSE endpoint remains available to header-capable clients.
- Plugin publishers need a signing step and operators need an explicit trust-store entry before
  enablement.
- Unsigned legacy plugins remain installable for inspection but disabled and untrusted.
- Contract changes after 1.0 must be backward-compatible unless the major version changes.
- The precise limitations of these controls are documented in the V1.0 threat model.
