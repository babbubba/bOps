// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace Acme.SamplePlugin;

/// <summary>
/// The plugin's entry type, as named by <c>EntryType</c> in <c>bops-plugin.json</c>. The loader
/// activates exactly one instance of this via <c>ActivatorUtilities</c>, against a container
/// exposing only the A10 list — this constructor takes nothing, so it works unchanged whether
/// that list ever grows.
/// </summary>
public sealed class SampleToolProvider(TimeProvider timeProvider) : ISkillProvider
{
    public string SkillId => "sample.echo-marker-skill";

    public IReadOnlyList<ICapability> GetCapabilities() => [new SampleEchoMarkerCapability(timeProvider)];

    public IEnumerable<ITool> GetTools() =>
        [new SampleEchoTool(), new SampleMarkerStatusTool(), new SampleMarkerCreateTool()];
}
