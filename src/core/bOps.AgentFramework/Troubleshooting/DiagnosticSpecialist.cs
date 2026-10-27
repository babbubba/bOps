// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// A specialist the workflow may involve, registered by the host with its declared capabilities (C01-R23). The triage agent chooses
/// among the registered, available specialists by what they declare; nothing in the workflow names a specialist, a package or a tool.
/// A specialist's tool authority is not stated here as a list: <see cref="ToolSelector"/> is applied to the read-only tools visible on
/// the node when a run starts, and the resulting exact set is frozen for the run and audited.
/// </summary>
/// <param name="Name">The unique, short, lowercase name the triage agent and the reviewer use (for example <c>system</c>).</param>
/// <param name="Description">One sentence on what the specialist investigates, shown to the triage agent.</param>
/// <param name="Capabilities">The areas it can investigate, shown to the triage agent.</param>
/// <param name="Instructions">The specialist's standing instructions: its domain and how to investigate it.</param>
/// <param name="ToolSelector">Chooses, among the read-only tools visible on the node, the ones this specialist may call.</param>
public sealed record DiagnosticSpecialist(
    string Name,
    string Description,
    IReadOnlyList<string> Capabilities,
    string Instructions,
    Func<ToolManifest, bool> ToolSelector);
