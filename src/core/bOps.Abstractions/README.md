# bOps.Abstractions

`bOps.Abstractions` is the dependency-free public SDK for bOps Tool, model-provider and Skill
packages. The current package is a V1.1 preview: the stable 1.0 surface remains SemVer-compatible
and the executable Skill/Capability boundary is now available for integration validation.
Reference this package—not Runtime, Policy, Memory or another host project—and expose an entry
type implementing one of its supported package-provider contracts.

Each `ITool` supplies a typed `ToolManifest`. A side-effecting tool must implement
`IVerifiableTool` and declare post-action verification. Package trust is assigned by the host from
verified publisher provenance; a package cannot grant itself trust.

Credentials use `SecretReference`. The host resolves a selected reference and supplies only the
credential required by the configured model adapter. Plugins do not receive a general secret
resolver.

An `ISkillProvider` contributes deterministic `ICapability` implementations and their Tools.
During preparation the host passes an invocation-scoped `IToolInvoker` that can reach only visible
`Read` tools from that same package. A non-Read prepared plan requires approval of its exact
canonical hash, and every step still crosses ordinary policy, approval and verification. V1.1
Skill runs are terminal and non-resumable; interruption requires a fresh preparation and approval.

The 1.x public API follows Semantic Versioning. See the repository plugin guide and ADR-0022 for
the package manifest, signing workflow, compatibility rules and security boundary. ADR-0023,
ADR-0024 and ADR-0025 define Evidence, immutable ExecutionPlans and the executable Skill boundary.
