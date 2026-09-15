// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

// ToolManifest.Package (agentic/01-architecture-rules.md, rule A11) is stamped by the registry
// at registration time and must never be set by a package. bOps.Runtime owns the registry
// implementation and is the only *production* assembly allowed to write it. The corresponding
// *.Tests assemblies also need access, not to stamp anything in production, but to construct a
// ToolManifest fixture with a specific Package value directly, without routing every unit test
// through a full IToolRegistry.Register call.
[assembly: InternalsVisibleTo("bOps.Runtime")]
[assembly: InternalsVisibleTo("bOps.Runtime.Tests")]
[assembly: InternalsVisibleTo("bOps.Policy.Tests")]
