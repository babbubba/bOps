# bOps.Abstractions

`bOps.Abstractions` is the dependency-free public SDK for bOps 1.0 tool and model-provider
packages. Reference this package—not Runtime, Policy, Memory or another host project—and expose an
entry type implementing `IToolProvider` or `IModelProviderPackage`.

Each `ITool` supplies a typed `ToolManifest`. A side-effecting tool must implement
`IVerifiableTool` and declare post-action verification. Package trust is assigned by the host from
verified publisher provenance; a package cannot grant itself trust.

Credentials use `SecretReference`. The host resolves a selected reference and supplies only the
credential required by the configured model adapter. Plugins do not receive a general secret
resolver.

The 1.x public API follows Semantic Versioning. See the repository plugin guide and ADR-0022 for
the package manifest, signing workflow, compatibility rules and security boundary.
