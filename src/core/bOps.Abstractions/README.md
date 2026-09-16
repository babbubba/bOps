# bOps.Abstractions

`bOps.Abstractions` is the dependency-free public SDK for bOps tool, model-provider and Skill
packages. The current package is a V1.1 preview: the stable 1.0 tool/provider surface remains
SemVer-compatible while the Skill/Capability execution boundary is completed. Reference this
package—not Runtime, Policy, Memory or another host project—and expose an entry type implementing
one of its supported package-provider contracts.

Each `ITool` supplies a typed `ToolManifest`. A side-effecting tool must implement
`IVerifiableTool` and declare post-action verification. Package trust is assigned by the host from
verified publisher provenance; a package cannot grant itself trust.

Credentials use `SecretReference`. The host resolves a selected reference and supplies only the
credential required by the configured model adapter. Plugins do not receive a general secret
resolver.

The 1.x public API follows Semantic Versioning. See the repository plugin guide and ADR-0022 for
the package manifest, signing workflow, compatibility rules and security boundary. ADR-0023 and
ADR-0024 define the V1.1 Evidence and immutable ExecutionPlan additions; executable Skill provider
interfaces remain preview work until the V1.1 Definition of Done is met.
