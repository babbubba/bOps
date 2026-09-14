using System.Runtime.CompilerServices;

// ToolManifest.Package (agentic/01-architecture-rules.md, rule A11) is stamped by the registry
// at registration time and must never be set by a package. bOps.Runtime owns the registry
// implementation and is the only assembly allowed to write it.
[assembly: InternalsVisibleTo("bOps.Runtime")]
[assembly: InternalsVisibleTo("bOps.Runtime.Tests")]
