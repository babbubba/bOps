// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>A registered specialist together with the exact read-only tools it may call in this run.</summary>
/// <param name="Specialist">The registration.</param>
/// <param name="Tools">The manifests of the tools it may call, frozen when the run started; never empty.</param>
public sealed record AvailableSpecialist(DiagnosticSpecialist Specialist, IReadOnlyList<ToolManifest> Tools);
